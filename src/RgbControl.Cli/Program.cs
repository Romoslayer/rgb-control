using RgbControl.Core;
using RgbControl.Core.Aura;
using RgbControl.Core.Ene;
using RgbControl.Core.Smbus;
using RgbControl.Core.Gpu;
using RgbControl.Core.Gigabyte;

return Run(args);

static int Run(string[] args)
{
    var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

    try
    {
        switch (command)
        {
            case "gpu-on":
                var gpuConfig = LightingConfig.Load();
                foreach (var settings in gpuConfig.Gpus.Where(g => g.Managed))
                {
                    if (GpuLightingController.Apply(settings, gpuConfig.AllLightingOff, Console.WriteLine) == 0)
                        throw new IOException($"Configured GPU not found: {settings.Kind}");
                }
                return 0;
            case "gpu-probe":
                using (var adl = new AmdAdl())
                    foreach (var gpu in adl.GetAdapters())
                    {
                        Console.WriteLine($"{gpu.Name}: {gpu.Vendor:X4}:{gpu.Device:X4} {gpu.SubVendor:X4}:{gpu.SubDevice:X4}; supported={GpuLightingController.IsSupported(gpu)}");
                        if (GpuLightingController.IsSupported(gpu))
                        {
                            if (gpu.SubVendor == 0x1DA2)
                                Console.WriteLine($"  Mode: {adl.Read(gpu, 0x28, 0x10, 1)[0]}; external sync: {adl.Read(gpu, 0x28, 0x0F, 1)[0]}");
                            else
                                Console.WriteLine($"  Controller signature: {Convert.ToHexString(adl.Read(gpu, 0x22, 0x82, 3))}");
                        }
                    }
                return 0;
            case "gigabyte-probe":
                Console.WriteLine($"Motherboard: {GigabyteController.BoardProduct}");
                using (var board = GigabyteController.TryOpen())
                {
                    Console.WriteLine(board is null ? "Supported Gigabyte controller not found." : $"IT5702 firmware: {board.Firmware}; D_LED1 and D_LED2");
                    return board is null ? 1 : 0;
                }
            case "probe":
                return Probe();
            case "on":
                return WithAura(aura => LightingActions.ApplyOn(aura, LightingConfig.Load()), "Applied config.");
            case "off":
                return WithAura(LightingActions.ApplyOff, "Lighting off.");
            case "set":
                return Set(args);
            case "direct":
                return Direct(args);
            case "ram-probe":
                return RamProbe();
            case "ram-set":
                return RamSet(args);
            case "ram-on":
                return WithRam(bus => LightingActions.ApplyRamOn(bus, LightingConfig.Load(), Console.WriteLine), "Applied RAM config");
            case "ram-off":
                return WithRam(bus => LightingActions.ApplyRamOff(bus, Console.WriteLine), "RAM lighting off");
            case "save-hardware":
                return WithAura(aura => LightingActions.SaveToHardware(aura, LightingConfig.Load()),
                    "Saved to controller: config effect while on, off while the PC is off.");
            case "init-config":
                return InitConfig();
            default:
                PrintHelp();
                return command == "help" ? 0 : 1;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

static int Probe()
{
    using var aura = AuraUsbController.TryOpen();
    if (aura is null)
    {
        Console.Error.WriteLine("ASUS Aura USB controller not found.");
        return 1;
    }

    Console.WriteLine($"Device:   {aura.Device.GetProductName()} ({aura.Device.VendorID:X4}:{aura.Device.ProductID:X4})");
    Console.WriteLine($"Firmware: {aura.FirmwareVersion}");
    Console.WriteLine("Config table:");
    for (var i = 0; i < aura.ConfigTable.Length; i += 6)
    {
        Console.WriteLine($"  {i:X2}: {Convert.ToHexString(aura.ConfigTable, i, 6)}");
    }
    Console.WriteLine("Channels:");
    foreach (var channel in aura.Channels)
    {
        Console.WriteLine($"  {channel}");
    }
    return 0;
}

static int Set(string[] args)
{
    if (args.Length < 2 || !Enum.TryParse<AuraMode>(args[1], ignoreCase: true, out var mode))
    {
        Console.Error.WriteLine($"Usage: set <mode> [#RRGGBB]. Modes: {string.Join(", ", Enum.GetNames<AuraMode>())}");
        return 1;
    }

    var color = args.Length > 2 ? Rgb.Parse(args[2]) : new Rgb(255, 255, 255);
    return WithAura(aura => LightingActions.Apply(aura, mode, color, includeAddressable: true), $"Set {mode} {color}.");
}

static int Direct(string[] args)
{
    var color = args.Length > 1 ? Rgb.Parse(args[1]) : new Rgb(255, 255, 255);
    return WithAura(aura =>
    {
        aura.Initialize();
        foreach (var channel in aura.Channels)
        {
            aura.SetEffect(channel, AuraMode.Direct, color);
            aura.SetDirect(channel, Enumerable.Repeat(color, channel.LedCount).ToArray());
        }
    }, $"Direct {color}.");
}

static int RamProbe()
{
    using var bus = SmbusPiix4.Open();
    Console.WriteLine($"PawnIO:   version 0x{bus.PawnIOVersion:X}");
    Console.WriteLine($"SMBus:    PCI {bus.PciVendorId:X4}:{bus.PciDeviceId:X4}, I/O base 0x{bus.IoBase:X}");

    if (!bus.IsAmdFch)
    {
        Console.Error.WriteLine("Not the expected AMD FCH SMBus controller; stopping before touching the bus.");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine("Stage 1: receive-byte scan of 0x70-0x77 (no commands sent)");
    var responding = EneDram.FindResponding(bus);
    foreach (var address in EneDram.AllowedAddresses)
    {
        Console.WriteLine($"  0x{address:X2}: {(responding.Contains(address) ? "responds" : "-")}");
    }

    if (responding.Count == 0)
    {
        Console.WriteLine("No devices in range.");
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine("Stage 2: ENE signature (registers 0xA0-0xAF) and identification");
    foreach (var address in responding)
    {
        var isEne = EneDram.HasEneSignature(bus, address);
        Console.WriteLine($"  0x{address:X2}: ENE signature {(isEne ? "OK" : "not matched")}");
        if (!isEne)
        {
            continue;
        }

        if (address == EneDram.DefaultAddress)
        {
            Console.WriteLine("        0x77 is the power-on address; controllers here have not been remapped. Skipping register reads.");
            continue;
        }

        var info = EneDram.ReadInfo(bus, address);
        Console.WriteLine($"        Version: {info.Version}{(info.IsMicron ? " (Micron - would be skipped)" : "")}");
        Console.WriteLine($"        LEDs:    {info.LedCount}");
        for (var i = 0; i < info.ConfigTable.Length; i += 16)
        {
            Console.WriteLine($"        cfg {i:X2}: {Convert.ToHexString(info.ConfigTable, i, 16)}");
        }
    }

    return 0;
}

static int RamSet(string[] args)
{
    if (args.Length < 2 || !Enum.TryParse<AuraMode>(args[1], ignoreCase: true, out var mode) || mode == AuraMode.Direct)
    {
        Console.Error.WriteLine($"Usage: ram-set <mode> [#RRGGBB]. Modes: {string.Join(", ", Enum.GetNames<AuraMode>().Where(m => m != nameof(AuraMode.Direct)))}");
        return 1;
    }

    var color = args.Length > 2 ? Rgb.Parse(args[2]) : new Rgb(255, 255, 255);
    return WithRam(bus => LightingActions.ApplyRam(bus, mode, color, log: Console.WriteLine), $"Set RAM {mode} {color}");
}

static int WithRam(Func<SmbusPiix4, int> action, string done)
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    using var bus = SmbusPiix4.Open();
    var count = action(bus);
    Console.WriteLine($"Took {stopwatch.ElapsedMilliseconds} ms.");
    if (count == 0)
    {
        Console.Error.WriteLine("No supported RAM lighting controllers found.");
        return 1;
    }

    Console.WriteLine($"{done} on {count} stick(s).");
    return 0;
}

static int InitConfig()
{
    var path = LightingConfig.DefaultPath;
    if (File.Exists(path))
    {
        Console.WriteLine($"Config already exists: {path}");
        return 0;
    }

    new LightingConfig().Save(path);
    Console.WriteLine($"Created {path}");
    return 0;
}

static int WithAura(Action<AuraUsbController> action, string done)
{
    using var aura = AuraUsbController.TryOpen();
    if (aura is null)
    {
        Console.Error.WriteLine("ASUS Aura USB controller not found.");
        return 1;
    }

    action(aura);
    Console.WriteLine(done);
    return 0;
}

static void PrintHelp()
{
    Console.WriteLine($"""
        rgbctl <command>

          probe           Read-only: show controller firmware, config table and channels
          gpu-probe       Enumerate AMD GPU identities without lighting writes
          gpu-on          Apply saved settings to managed GPUs only
          gigabyte-probe  Query Gigabyte board/controller identity without changing lighting
          on              Apply the config file ({LightingConfig.DefaultPath})
          off             Turn all motherboard lighting off
          direct [#RRGGBB] Set a color via direct (software) mode (not saved)
          set <mode> [#RRGGBB]
                          Try an effect without editing the config (not saved)
          ram-probe       Read-only RAM lighting scan (admin required)
          ram-set <mode> [#RRGGBB]
                          Try an effect on the RAM (not saved, admin required)
          ram-on / ram-off
                          Apply the config's RAM lighting / turn RAM lighting off (admin required)
          save-hardware   Save the config's effect to the controller, with "off" while the PC is off
          init-config     Create a default config file
        """);
}
