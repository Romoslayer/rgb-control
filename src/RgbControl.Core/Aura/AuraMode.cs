namespace RgbControl.Core.Aura;

/// <summary>Hardware effects built into the ASUS Aura USB controller.</summary>
public enum AuraMode : byte
{
    Off = 0,
    Static = 1,
    Breathing = 2,
    Flashing = 3,
    SpectrumCycle = 4,
    Rainbow = 5,
    SpectrumCycleBreathing = 6,
    ChaseFade = 7,
    SpectrumCycleChaseFade = 8,
    Chase = 9,
    SpectrumCycleChase = 10,
    SpectrumCycleWave = 11,
    ChaseRainbowPulse = 12,
    RandomFlicker = 13,

    /// <summary>Colors are driven by software via SetDirect.</summary>
    Direct = 0xFF,
}

public enum AuraChannelType
{
    /// <summary>The board's own LEDs plus any 12V RGB headers.</summary>
    Onboard,

    /// <summary>A 5V addressable (ARGB) header.</summary>
    Addressable,
}

/// <param name="EffectChannel">Channel index used by effect (0x35) commands.</param>
/// <param name="DirectChannel">Channel index used by direct (0x40) commands.</param>
/// <param name="LedCount">LEDs addressed by this channel's effect color mask.</param>
public sealed record AuraChannel(AuraChannelType Type, byte EffectChannel, byte DirectChannel, int LedCount, int RgbHeaderCount)
{
    public override string ToString() => Type == AuraChannelType.Onboard
        ? $"Onboard (effect channel {EffectChannel}, {LedCount} LEDs incl. {RgbHeaderCount} RGB header(s))"
        : $"Addressable header (effect channel {EffectChannel})";
}
