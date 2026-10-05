using System.Runtime.InteropServices;
using GeekFlashCore.CLI.Localization;
using LibUsbDotNet.LibUsb;

namespace GeekFlashCore.CLI;

/// <summary>Owns native handles for the lifetime of the CLI process.</summary>
internal static class NativeUsbRuntime
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IntPtr> Handles = new(StringComparer.OrdinalIgnoreCase);

    internal static string ProcessRuntime => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.X86 => "win-x86",
        Architecture.Arm64 => "win-arm64",
        _ => throw new PlatformNotSupportedException(Strings.Cli_UsbArchitectureUnsupported)
    };

    public static void EnsureAvailable()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            Load("libusb-1.0.dll");
        }
    }

    public static void EnsureLibUsb0Bridge()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            // Preload libusb0 by basename so the libusbK bridge can find the bundled runtime.
            Load("libusb0.dll");
            Load("libusbK.dll");
        }
    }

    private static void Load(string name)
    {
        if (Handles.ContainsKey(name)) return;
        string bundled = Path.Combine(AppContext.BaseDirectory, "runtimes", ProcessRuntime, "native", name);
        string local = Path.Combine(AppContext.BaseDirectory, name);
        string? path = File.Exists(bundled) ? bundled : File.Exists(local) ? local : null;
        try
        {
            bool loaded = path is not null
                ? NativeLibrary.TryLoad(path, out var handle)
                : NativeLibrary.TryLoad(name, typeof(UsbContext).Assembly, DllImportSearchPath.SafeDirectories, out handle);
            if (loaded) { Handles.Add(name, handle); return; }
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException(Strings.FormatCli_UsbNativeMissing(name, ProcessRuntime), exception);
        }
        throw new InvalidOperationException(Strings.FormatCli_UsbNativeMissing(name, ProcessRuntime));
    }
}
