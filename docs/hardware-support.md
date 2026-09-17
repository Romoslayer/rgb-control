# Expanded hardware support

Implementation status: experimental, 2026-09-17. Builds and hardware-free checks pass.
New hardware support requires the validation described below.

## Device paths

| Device | Path | Status |
|---|---|---|
| ASUS ROG Strix B850-E + ENE RAM | Existing USB HID / PawnIO | Established support; old config stays compatible |
| NZXT Kraken Core 360 RGB and included F360 RGB Core EV-B fans | Motherboard 5V ARGB headers | Separate header settings implemented; no NZXT USB driver needed |
| Sapphire NITRO+ RX 9070 XT | AMD ADL GPU-local I2C, address 0x28 | Implemented; exact device enumeration and lighting-register reads verified and static writes verified; visible effects/power lifecycle require testing |
| PowerColor Red Devil RX 9070 XT | AMD ADL GPU-local I2C, address 0x22 | Implemented, hardware testing pending |
| Gigabyte B650 AORUS Elite AX, target rev. 1.2 | IT5702 USB feature reports | Implemented for exact board name and 048D:5702; hardware/controller confirmation pending on target PC |

### Kraken Core / ARGB cable fallback

The Kraken Core's pump cap and included fans use 3-pin **5V** ARGB. They may be on separate
headers (independent settings) or daisy-chained (one shared effect). Do not connect these to 12V RGB.
Enable "Use separate lighting settings" for the corresponding header and give it a label.
Unconfigured headers retain the previous "Also apply" behavior. A custom header can stay on
even while onboard lighting is disabled; the master off switch and sleep/shutdown off still win.
The app does not identify passive devices or detect cables attached to headers.

GPU cable fallback is selected explicitly; it is not possible to assume a cable exists when direct
communication fails. Connect the manufacturer's ARGB sync cable to a motherboard header,
select "Use motherboard ARGB cable instead of direct control", and set the header's lighting.
The service requests the GPU's external-sync mode. If the AMD interface is unavailable, enable
external sync once in Sapphire TriXX / PowerColor Keystone and leave "Control this GPU" disabled;
the motherboard header will still operate independently of GPU-driver access.

### Direct GPUs

"Control this GPU" is off by default. Direct control is the default connection when enabled.
This implementation allows only PCI 1002:7550 with subsystem 1DA2:E489 (Sapphire) or
148C:2435 (PowerColor); it does not claim support for an entire product family.
PowerColor's controller signature is checked before writes; Sapphire's mode register is read
and validated. No broad GPU-bus address scans or motherboard-SMBus access are used for GPUs.

Initial effects are Static, Rainbow, Spectrum cycle, and Off. Sapphire static brightness is
implemented by dimming the chosen color, matching the existing app behavior. Animated effect
speed uses the existing firmware setting on Sapphire and the protocol default on PowerColor.
No explicit persistent/flash-save commands are sent to either GPU.

### Gigabyte

Choose the Gigabyte option under Motherboard. Detection requires the exact SMBIOS board
name "B650 AORUS ELITE AX", HID 048D:5702, usage FF89:00CC and a controller identity response.
V2, ICE, B650M and different controller IDs are not silently treated as the same board.
The UI exposes D_LED1 and D_LED2 as ARGB headers 1 and 2. The known onboard/12V zones share
the motherboard setting. Initial effects: Static, Breathing, Flashing, Spectrum cycle, Off.
No calibration changes or flash writes are made. ASUS "Save startup color" is unavailable here.
The service applies settings after Windows starts and sends off at shutdown/sleep, then restores
on wake. Behavior while fully powered down must be verified on the target board/BIOS.

## Verification and diagnostics

Run `dotnet build RgbControl.slnx` and
`dotnet run --project tests/RgbControl.Checks/RgbControl.Checks.csproj`.
Checks use fake GPU I2C and inspect packet data; they never change real lighting.

`rgbctl gpu-probe` enumerates AMD adapters and reads supported lighting-controller identity/state.
`rgbctl gigabyte-probe` queries board/controller identity without changing lighting effects.
The other pre-existing motherboard CLI commands still target ASUS; configure Gigabyte via the app/service.

Sapphire adapter enumeration and Static mode/color writes have passed register readback verification.
Effect changes write the mode before refreshing color, with settling delays and readback checks.
Register verification alone does not confirm the visible effect or power-cycle behavior.

Before marking the additions stable, test on each target machine: direct static color, off, cable
sync, service-start application, restart, sleep/wake, shutdown, and preservation of original
ASUS/RAM settings. The app's "Saved" message confirms config persistence, not hardware success;
device errors remain in Event Viewer > Application, source RgbControl.

## Protocol sources and attribution

The project remains GPL-3.0. New protocol adaptations draw on OpenRGB's GPL-2.0-or-later code:

- [Sapphire Nitro Glow V3, K900](https://github.com/CalcProgrammer1/OpenRGB/tree/master/Controllers/SapphireGPUController/SapphireNitroGlowV3Controller)
- [PowerColor Red Devil V2, Nexrem](https://github.com/CalcProgrammer1/OpenRGB/tree/master/Controllers/PowerColorGPUController/PowerColorRedDevilV2Controller)
- [Gigabyte RGB Fusion 2 USB, jackun and megadjc](https://github.com/CalcProgrammer1/OpenRGB/tree/master/Controllers/GigabyteRGBFusion2USBController)
- [OpenRGB AMD ADL transport, Niels Westphal](https://github.com/CalcProgrammer1/OpenRGB/blob/master/i2c_smbus/Windows/i2c_smbus_amdadl.cpp)
- [AMD ADL SDK definitions](https://github.com/GPUOpen-LibrariesAndSDKs/display-library/tree/master/include)
- [NZXT Kraken Core connections](https://support.nzxt.com/hc/en-us/articles/41139338590235-Installing-the-Kraken-Core-Connecting-the-Radiator-and-Fan)

Source files were inspected on 2026-09-17. No upstream binaries or vendor DLLs are bundled;
GPU access loads atiadlxx.dll from the Windows system directory, supplied by the AMD driver.
