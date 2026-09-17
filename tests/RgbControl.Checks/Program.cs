using RgbControl.Core;
using RgbControl.Core.Aura;
using RgbControl.Core.Gigabyte;
using RgbControl.Core.Gpu;

var passed = 0;
void Check(string name, Action test) { test(); Console.WriteLine($"PASS {name}"); passed++; }
void Require(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } catch (IOException) { return; } throw new Exception("Expected rejection"); }
var sapphire = new GpuAdapter(0, "Sapphire", 0x1002, 0x7550, 0x1DA2, 0xE489);
var devil = new GpuAdapter(1, "PowerColor", 0x1002, 0x7550, 0x148C, 0x2435);

Check("Old config keeps ASUS and no added device control", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"rgb-check-{Guid.NewGuid()}.json");
    try
    {
        File.WriteAllText(path, "{\"motherboard\":{\"color\":\"#123456\"},\"ram\":{\"color\":\"#ABCDEF\"}}");
        var config = LightingConfig.Load(path);
        Require(config.Motherboard.Controller == MotherboardKind.AsusAura && config.ArgbHeaders.Count == 0 && config.Gpus.Count == 0);
        Require(LightingActions.ResolveChannel(config, 1)!.Value.Color == Rgb.Parse("#123456"));
    }
    finally { File.Delete(path); }
});
Check("Header override remains independent of onboard off", () =>
{
    var config = new LightingConfig { Motherboard = new() { Enabled = false }, ArgbHeaders = [new() { Header = 2, UseCustomSettings = true, Color = "#FF0000", Brightness = 50 }] };
    Require(LightingActions.ResolveChannel(config, 0)!.Value.Mode == AuraMode.Off);
    Require(LightingActions.ResolveChannel(config, 2)!.Value.Color == new Rgb(128, 0, 0));
    config.AllLightingOff = true;
    Require(LightingActions.ResolveChannel(config, 2)!.Value.Mode == AuraMode.Off);
});
Check("Unmanaged header stays untouched, explicit off overrides", () =>
{
    var config = new LightingConfig { Motherboard = new() { IncludeAddressableHeaders = false } };
    Require(LightingActions.ResolveChannel(config, 1) is null);
    config.ArgbHeaders.Add(new() { Header = 1, UseCustomSettings = true, Enabled = false, Color = "invalid" });
    Require(LightingActions.ResolveChannel(config, 1)!.Value.Mode == AuraMode.Off);
});
Check("New device settings round-trip", () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"rgb-check-{Guid.NewGuid()}.json");
    try
    {
        new LightingConfig { Motherboard = new() { Controller = MotherboardKind.GigabyteB650AorusEliteAx },
            ArgbHeaders = [new() { Header = 2, Name = "Kraken Core", UseCustomSettings = true }],
            Gpus = [new() { Kind = GpuKind.PowerColorRedDevil9070Xt, Managed = true, Connection = GpuConnection.ArgbCable }] }.Save(path);
        var config = LightingConfig.Load(path);
        Require(config.Motherboard.Controller == MotherboardKind.GigabyteB650AorusEliteAx && config.ArgbHeaders[0].Name == "Kraken Core");
        Require(config.Gpus[0].Managed && config.Gpus[0].Connection == GpuConnection.ArgbCable);
        Require(!File.ReadAllText(path).Contains("effectiveColor"));
    }
    finally { File.Delete(path); }
});
Check("Gigabyte packet uses correct D_LED1 zone, color and offsets", () =>
{
    var packet = GigabyteController.BuildEffectPacket(5, AuraMode.Static, new(0x12, 0x34, 0x56));
    Require(packet.Length == 64 && packet[0] == 0xCC && packet[1] == 0x25 && packet[2] == 0x20);
    Require(packet[11] == 1 && packet[12] == 255 && packet[14] == 0x56 && packet[15] == 0x34 && packet[16] == 0x12);
    Reject(() => GigabyteController.BuildEffectPacket(5, AuraMode.Rainbow, Rgb.Black));
    var off = GigabyteController.BuildEffectPacket(5, AuraMode.Off, new(255, 255, 255));
    Require(off[11] == 1 && off[12] == 0 && off[14] == 0 && off[15] == 0 && off[16] == 0);
});
Check("GPU allowlist rejects near matches before I/O", () =>
{
    var bus = new FakeI2c();
    Require(GpuLightingController.IsSupported(sapphire) && GpuLightingController.IsSupported(devil));
    Reject(() => GpuLightingController.ApplyToAdapter(bus, sapphire with { SubDevice = 0xE490 }, new() { Managed = true }, false));
    Require(bus.Reads == 0 && bus.Writes.Count == 0);
});
Check("Unmanaged GPU performs no I/O", () =>
{
    var bus = new FakeI2c();
    GpuLightingController.ApplyToAdapter(bus, sapphire, new(), false);
    Require(bus.Reads == 0 && bus.Writes.Count == 0);
});
Check("Sapphire static writes GPU-local color and custom mode", () =>
{
    var bus = new FakeI2c();
    GpuLightingController.ApplyToAdapter(bus, sapphire, new() { Managed = true, Color = "#123456" }, false);
    Require(bus.Writes.All(w => w.Address == 0x28));
    Require(bus.Has(0x0F, 0) && bus.Has(0x1A, 0x12) && bus.Has(0x1B, 0x34) && bus.Has(0x1C, 0x56) && bus.Has(0x10, 6));
});
Check("PowerColor validates signature before writes", () =>
{
    var bus = new FakeI2c { Signature = [0, 0, 0] };
    Reject(() => GpuLightingController.ApplyToAdapter(bus, devil, new() { Managed = true, Kind = GpuKind.PowerColorRedDevil9070Xt }, false));
    Require(bus.Writes.Count == 0);
});
Check("Sapphire effect-only transitions select mode before refreshing unchanged color", () =>
{
    var bus = new FakeI2c();
    foreach (var (mode, expected) in new[] { (AuraMode.Rainbow, 0), (AuraMode.Static, 6), (AuraMode.SpectrumCycle, 2), (AuraMode.Static, 6) })
    {
        bus.Writes.Clear();
        GpuLightingController.ApplyToAdapter(bus, sapphire, new() { Managed = true, Mode = mode, Color = "#FF0000" }, false);
        Require(bus.Writes.Select(w => w.Register).SequenceEqual(new byte[] { 0x0F, 0x10, 0x1A, 0x1B, 0x1C }));
        Require(bus.Writes[1].Values[0] == expected && bus.Has(0x1A, 255) && bus.Has(0x1B, 0) && bus.Has(0x1C, 0));
    }
});
Check("Sapphire rejected mode write is reported rather than treated as applied", () =>
{
    var bus = new FakeI2c { IgnoreRegister = 0x10 };
    Reject(() => GpuLightingController.ApplyToAdapter(bus, sapphire, new() { Managed = true, Mode = AuraMode.Rainbow }, false));
});
Check("Invalid GPU color causes no writes", () =>
{
    foreach (var gpu in new[] { sapphire, devil })
    {
        var bus = new FakeI2c();
        try
        {
            GpuLightingController.ApplyToAdapter(bus, gpu, new() { Managed = true, Color = "invalid",
                Kind = gpu == sapphire ? GpuKind.SapphireNitro9070Xt : GpuKind.PowerColorRedDevil9070Xt }, false);
            throw new Exception("Invalid color was accepted");
        }
        catch (FormatException) { Require(bus.Writes.Count == 0); }
    }
});
Check("PowerColor static sends both color banks and mode", () =>
{
    var bus = new FakeI2c();
    GpuLightingController.ApplyToAdapter(bus, devil, new() { Managed = true, Kind = GpuKind.PowerColorRedDevil9070Xt, Color = "#123456" }, false);
    Require(bus.Writes.All(w => w.Address == 0x22));
    Require(bus.Has(0x30, 0x12, 0x34, 0x56) && bus.Has(0x31, 0x12, 0x34, 0x56) && bus.Has(0x01, 1, 255, 0x32));
});
Check("Cable sync, shutdown off and resume restore work for both GPU types", () =>
{
    foreach (var gpu in new[] { sapphire, devil })
    {
        var config = new GpuLighting { Managed = true, Enabled = false, Connection = GpuConnection.ArgbCable,
            Kind = gpu == sapphire ? GpuKind.SapphireNitro9070Xt : GpuKind.PowerColorRedDevil9070Xt };
        var bus = new FakeI2c();
        GpuLightingController.ApplyToAdapter(bus, gpu, config, false);
        Require(gpu == sapphire ? bus.Has(0x0F, 1) : bus.Has(0x04, 1, 1, 1));
        bus.Writes.Clear();
        GpuLightingController.ApplyToAdapter(bus, gpu, config, true);
        Require(gpu == sapphire ? bus.Has(0x10, 7) : bus.Has(0x01, 0, 255, 0x32));
        bus.Writes.Clear();
        GpuLightingController.ApplyToAdapter(bus, gpu, config, false);
        Require(gpu == sapphire ? bus.Has(0x0F, 1) : bus.Has(0x04, 1, 1, 1));
    }
});
Console.WriteLine($"{passed} checks passed; no hardware accessed.");

sealed class FakeI2c : IGpuI2c
{
    public int Reads;
    public byte[] Signature = [1, 0x32, 0];
    public List<(byte Address, byte Register, byte[] Values)> Writes = [];
    public Dictionary<byte, byte> Registers = new() { [0x10] = 6 };
    public byte? IgnoreRegister;
    public byte[] Read(GpuAdapter adapter, byte address, byte register, int count)
    {
        Reads++;
        return register == 0x82 ? Signature : [Registers.GetValueOrDefault(register)];
    }
    public void Write(GpuAdapter adapter, byte address, byte register, params byte[] values)
    {
        Writes.Add((address, register, values));
        if (register != IgnoreRegister) Registers[register] = values[0];
    }
    public bool Has(byte register, params byte[] values) => Writes.Any(w => w.Register == register && w.Values.SequenceEqual(values));
}
