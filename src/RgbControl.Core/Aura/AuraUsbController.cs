using HidSharp;
using HidSharp.Reports;

namespace RgbControl.Core.Aura;

/// <summary>
/// ASUS Aura USB motherboard lighting controller.
/// Protocol reference: OpenRGB's AsusAuraUSBController / AsusAuraMainboardController.
/// Every packet is a 65-byte HID output report with report ID 0xEC.
/// </summary>
public sealed class AuraUsbController : IDisposable
{
    public const int VendorId = 0x0B05;

    // Motherboard controller PIDs known to use this protocol.
    private static readonly int[] ProductIds = [0x18F3, 0x1939, 0x19AF, 0x1AA6, 0x1BED];

    // Vendor usage page 0xFF72, usage 0x00A1 (required to pick the right collection on newer boards).
    private const uint AuraUsage = 0xFF7200A1;

    private const int ReportLength = 65;
    private const byte ReportId = 0xEC;

    private const byte CmdFirmwareVersion = 0x82;
    private const byte CmdConfigTable = 0xB0;
    private const byte CmdEffect = 0x35;
    private const byte CmdEffectColor = 0x36;
    private const byte CmdCommit = 0x3F;
    private const byte CmdDirect = 0x40;

    private const byte ReplyFirmwareVersion = 0x02;
    private const byte ReplyConfigTable = 0x30;

    private readonly HidStream _stream;
    private readonly List<AuraChannel> _channels = [];

    public HidDevice Device { get; }
    public string FirmwareVersion { get; }
    public byte[] ConfigTable { get; }
    public IReadOnlyList<AuraChannel> Channels => _channels;

    private AuraUsbController(HidDevice device, HidStream stream)
    {
        Device = device;
        _stream = stream;
        _stream.ReadTimeout = 1000;
        _stream.WriteTimeout = 1000;

        FirmwareVersion = ReadFirmwareVersion();
        ConfigTable = ReadConfigTable();
        BuildChannels();
    }

    /// <summary>Finds and opens the motherboard controller. Returns null if it isn't present (yet).</summary>
    public static AuraUsbController? TryOpen()
    {
        var candidates = DeviceList.Local.GetHidDevices(VendorId)
            .Where(d => ProductIds.Contains(d.ProductID) && d.GetMaxOutputReportLength() == ReportLength)
            .OrderByDescending(HasAuraUsage)
            .ToList();

        foreach (var device in candidates)
        {
            if (!device.TryOpen(out HidStream stream))
            {
                continue;
            }

            try
            {
                return new AuraUsbController(device, stream);
            }
            catch
            {
                stream.Dispose();
            }
        }

        return null;
    }

    private static bool HasAuraUsage(HidDevice device)
    {
        try
        {
            return device.GetReportDescriptor().DeviceItems
                .Any(item => item.Usages.GetAllValues().Contains(AuraUsage));
        }
        catch
        {
            return false;
        }
    }

    private void BuildChannels()
    {
        int onboardLeds = ConfigTable[0x1B];
        int rgbHeaders = ConfigTable[0x1D];
        int addressableHeaders = ConfigTable[0x02];

        if (onboardLeds < rgbHeaders)
        {
            rgbHeaders = 0;
        }

        byte effectChannel = 0;
        if (onboardLeds > 0)
        {
            _channels.Add(new AuraChannel(AuraChannelType.Onboard, effectChannel++, 0x04, onboardLeds, rgbHeaders));
        }

        for (var i = 0; i < addressableHeaders; i++)
        {
            _channels.Add(new AuraChannel(AuraChannelType.Addressable, effectChannel++, (byte)i, 1, 0));
        }
    }

    /// <summary>Switches the controller into the effect mode OpenRGB uses. Call once before setting effects.</summary>
    public void Initialize()
    {
        var buf = NewPacket(0x52);
        buf[2] = 0x53;
        buf[3] = 0x00;
        buf[4] = 0x01;
        Write(buf);
    }

    /// <summary>
    /// Sets a hardware effect on one channel. With <paramref name="shutdownEffect"/> the effect is
    /// stored as what the board shows while the PC is off (onboard LEDs only); it takes effect after <see cref="Commit"/>.
    /// </summary>
    public void SetEffect(AuraChannel channel, AuraMode mode, Rgb color, bool shutdownEffect = false)
    {
        var effect = NewPacket(CmdEffect);
        effect[2] = channel.EffectChannel;
        effect[3] = 0x00;
        effect[4] = shutdownEffect ? (byte)0x01 : (byte)0x00;
        effect[5] = (byte)mode;
        Write(effect);

        if (mode == AuraMode.Direct)
        {
            return;
        }

        // LED positions are counted across all channels in order.
        var startLed = _channels.TakeWhile(c => c != channel).Sum(c => c.LedCount);
        var mask = (ushort)(((1 << channel.LedCount) - 1) << startLed);

        var colors = NewPacket(CmdEffectColor);
        colors[2] = (byte)(mask >> 8);
        colors[3] = (byte)(mask & 0xFF);
        colors[4] = shutdownEffect ? (byte)0x01 : (byte)0x00;
        for (var i = 0; i < channel.LedCount; i++)
        {
            var offset = 5 + 3 * (startLed + i);
            if (offset + 2 >= ReportLength)
            {
                break;
            }

            colors[offset] = color.R;
            colors[offset + 1] = color.G;
            colors[offset + 2] = color.B;
        }
        Write(colors);
    }

    /// <summary>Sets per-LED colors directly (software-driven; the controller shows them until told otherwise).</summary>
    public void SetDirect(AuraChannel channel, IReadOnlyList<Rgb> colors)
    {
        const int ledsPerPacket = 20;
        var offset = 0;
        do
        {
            var count = Math.Min(ledsPerPacket, colors.Count - offset);
            var apply = offset + count == colors.Count;

            var buf = NewPacket(CmdDirect);
            buf[2] = (byte)((apply ? 0x80 : 0x00) | channel.DirectChannel);
            buf[3] = (byte)offset;
            buf[4] = (byte)count;
            for (var i = 0; i < count; i++)
            {
                buf[5 + 3 * i] = colors[offset + i].R;
                buf[6 + 3 * i] = colors[offset + i].G;
                buf[7 + 3 * i] = colors[offset + i].B;
            }
            Write(buf);

            offset += count;
        }
        while (offset < colors.Count);
    }

    /// <summary>Saves the current effects (and shutdown effects) to the controller's flash. Use sparingly.</summary>
    public void Commit()
    {
        var buf = NewPacket(CmdCommit);
        buf[2] = 0x55;
        Write(buf);
    }

    private string ReadFirmwareVersion()
    {
        var reply = Request(CmdFirmwareVersion, ReplyFirmwareVersion);
        return System.Text.Encoding.ASCII.GetString(reply, 2, 16).TrimEnd('\0', ' ');
    }

    private byte[] ReadConfigTable()
    {
        var reply = Request(CmdConfigTable, ReplyConfigTable);
        return reply[4..64];
    }

    private byte[] Request(byte command, byte expectedReply)
    {
        Write(NewPacket(command));

        var buf = new byte[ReportLength];
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var read = _stream.Read(buf, 0, buf.Length);
                if (read >= 2 && buf[0] == ReportId && buf[1] == expectedReply)
                {
                    return buf;
                }
            }
            catch (TimeoutException)
            {
                break;
            }
        }

        throw new IOException($"Aura controller did not answer request 0x{command:X2}.");
    }

    private static byte[] NewPacket(byte command)
    {
        var buf = new byte[ReportLength];
        buf[0] = ReportId;
        buf[1] = command;
        return buf;
    }

    private void Write(byte[] packet) => _stream.Write(packet);

    public void Dispose() => _stream.Dispose();
}
