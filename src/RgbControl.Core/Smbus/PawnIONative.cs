using System.Reflection;
using System.Runtime.InteropServices;

namespace RgbControl.Core.Smbus;

/// <summary>P/Invoke bindings for PawnIOLib.dll, installed with the PawnIO driver.</summary>
internal static class PawnIONative
{
    private const string Lib = "PawnIOLib.dll";

    public const int E_ACCESSDENIED = unchecked((int)0x80070005);

    private static int _resolverSet;

    /// <summary>PawnIOLib lives in the PawnIO install folder, not next to our exe.</summary>
    public static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) == 1)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(PawnIONative).Assembly, (name, assembly, searchPath) =>
        {
            if (name != Lib)
            {
                return IntPtr.Zero;
            }

            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", Lib);
            return NativeLibrary.TryLoad(installed, out var handle) ? handle : IntPtr.Zero;
        });
    }

    [DllImport(Lib)]
    public static extern int pawnio_version(out uint version);

    [DllImport(Lib)]
    public static extern int pawnio_open(out IntPtr handle);

    [DllImport(Lib)]
    public static extern int pawnio_load(IntPtr handle, byte[] blob, nuint size);

    [DllImport(Lib)]
    public static extern int pawnio_execute(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input,
        nuint inSize,
        ulong[] output,
        nuint outSize,
        out nuint returnSize);

    [DllImport(Lib)]
    public static extern int pawnio_close(IntPtr handle);
}
