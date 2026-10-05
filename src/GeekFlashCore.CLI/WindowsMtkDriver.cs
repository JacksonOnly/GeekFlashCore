using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Mtk;
using Microsoft.Win32;
using Serilog;

namespace GeekFlashCore.CLI;

internal sealed record MtkDriverDevice(string HardwareId, bool Compatible, bool RequiresBridge = false);

internal interface IMtkDriverBackend
{
    IReadOnlyList<MtkDriverDevice> ReadDevices();
    void EnsureDependencies();
    Task<int> InstallAsync(IReadOnlyList<string> hardwareIds, bool nonInteractive, CancellationToken ct);
}

/// <summary>Prepares flash hardware IDs without modifying shared Windows setup classes.</summary>
internal sealed class WindowsMtkDriver(IMtkDriverBackend backend)
{
    private static readonly Regex HardwareIdPattern = new(
        @"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})(?:&(?:REV_[0-9A-F]{4}|MI_[0-9A-F]{2}))*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static string? GetFlashHardwareId(string value)
    {
        if (value.Length > 128) return null;
        var match = HardwareIdPattern.Match(value);
        if (!match.Success) return null;
        ushort vid = ushort.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        ushort pid = ushort.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return MtkDeviceIdentify.IsSupported(vid, pid) ? $@"USB\VID_{vid:X4}&PID_{pid:X4}" : null;
    }

    internal static string[] BuildArguments(IReadOnlyList<string> hardwareIds)
    {
        if (hardwareIds.Count is < 1 or > 4 || hardwareIds.Any(id => GetFlashHardwareId(id) != id))
            throw new ArgumentException(Strings.Cli_MtkDriverScopeInvalid, nameof(hardwareIds));
        return ["install", .. hardwareIds.Distinct().Select(id => "--device=" + id)];
    }

    public async Task EnsureAsync(bool nonInteractive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var devices = backend.ReadDevices().Where(d => GetFlashHardwareId(d.HardwareId) is not null).ToArray();
        var missing = devices.Where(d => !d.Compatible).Select(d => GetFlashHardwareId(d.HardwareId)!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0)
        {
            if (devices.Any(d => d.RequiresBridge)) backend.EnsureDependencies();
            return;
        }
        backend.EnsureDependencies();
        Log.Information(Strings.FormatCli_MtkDriverInstalling(missing.Length));
        int exit = await backend.InstallAsync(missing, nonInteractive, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (exit != 0) throw new InvalidOperationException(Strings.FormatCli_MtkDriverInstallFailed(exit));
        // A successful process exit is insufficient: re-read effective bindings before native enumeration.
        var verified = backend.ReadDevices();
        if (missing.Any(id => !verified.Any(d => GetFlashHardwareId(d.HardwareId) == id)) ||
            verified.Any(d => !d.Compatible && missing.Contains(GetFlashHardwareId(d.HardwareId))))
            throw new InvalidOperationException(Strings.Cli_MtkDriverVerificationFailed);
        Log.Information(Strings.Cli_MtkDriverInstalled);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsMtkDriverBackend : IMtkDriverBackend
{
    internal const string HelperSwitch = "--internal-install-mtk-usb-filter";
    private const string EnumPath = @"SYSTEM\CurrentControlSet\Enum\USB";
    private const string ClassPath = @"SYSTEM\CurrentControlSet\Control\Class\";
    private static string NativeSystemDirectory => Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative") : Environment.SystemDirectory;
    private static string InstallerDirectory => Path.Combine(AppContext.BaseDirectory, "drivers", "libusb-win32",
        RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64", Architecture.X86 => "x86",
            _ => throw new PlatformNotSupportedException(Strings.Cli_UsbArchitectureUnsupported)
        });

    public IReadOnlyList<MtkDriverDevice> ReadDevices()
    {
        var result = new List<MtkDriverDevice>();
        using var usb = Registry.LocalMachine.OpenSubKey(EnumPath);
        if (usb is null) return result;
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\libusb0");
        bool filterService = service is not null && File.Exists(Path.Combine(NativeSystemDirectory, "drivers", "libusb0.sys"));
        foreach (string deviceName in usb.GetSubKeyNames())
        {
            if (WindowsMtkDriver.GetFlashHardwareId(@"USB\" + deviceName) is null) continue;
            using var device = usb.OpenSubKey(deviceName);
            if (device is null) continue;
            foreach (string instanceName in device.GetSubKeyNames())
            {
                using var instance = device.OpenSubKey(instanceName);
                if (instance is null) continue;
                string? classGuid = instance.GetValue("ClassGUID") as string;
                using var setupClass = Guid.TryParse(classGuid, out var guid)
                    ? Registry.LocalMachine.OpenSubKey(ClassPath + guid.ToString("B")) : null;
                string? driver = instance.GetValue("Service") as string;
                bool libusb0 = string.Equals(driver, "libusb0", StringComparison.OrdinalIgnoreCase);
                bool filtered = HasFilter(instance) || HasFilter(setupClass);
                bool compatible = (filterService && (libusb0 || filtered)) ||
                    string.Equals(driver, "WinUSB", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(driver, "libusbK", StringComparison.OrdinalIgnoreCase);
                result.Add(new(@"USB\" + deviceName, compatible, libusb0 || filtered));
            }
        }
        return result;
    }

    private static bool HasFilter(RegistryKey? key) => new[] { "UpperFilters", "LowerFilters" }.Any(name =>
        key?.GetValue(name) is string[] values && values.Contains("libusb0", StringComparer.OrdinalIgnoreCase));

    public void EnsureDependencies() => NativeUsbRuntime.EnsureLibUsb0Bridge();

    public async Task<int> InstallAsync(IReadOnlyList<string> hardwareIds, bool nonInteractive, CancellationToken ct)
    {
        WindowsMtkDriver.BuildArguments(hardwareIds);
        ValidatePackage();
        using var identity = WindowsIdentity.GetCurrent();
        bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (admin) return await InstallElevatedAsync(hardwareIds, ct).ConfigureAwait(false);
        if (!admin && nonInteractive) throw new InvalidOperationException(Strings.Cli_MtkDriverAdministratorRequired);
        string host = Environment.ProcessPath ?? throw new InvalidOperationException(Strings.Cli_MtkDriverLaunchFailed);
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = true, Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(WindowsMtkDriverBackend).Assembly.Location);
        start.ArgumentList.Add(HelperSwitch);
        foreach (string id in hardwareIds) start.ArgumentList.Add(id);
        return await RunInstallerAsync(start, ct).ConfigureAwait(false);
    }

    private static void ValidatePackage()
    {
        foreach (string file in new[] { "install-filter.exe", "libusb0.sys", "libusb0.dll" })
        {
            string path = Path.Combine(InstallerDirectory, file);
            if (!File.Exists(path)) throw new InvalidOperationException(Strings.FormatCli_MtkDriverInstallerMissing(path));
        }
    }

    internal static async Task<int> InstallElevatedAsync(IReadOnlyList<string> hardwareIds, CancellationToken ct)
    {
        string[] arguments = WindowsMtkDriver.BuildArguments(hardwareIds);
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException(Strings.Cli_MtkDriverAdministratorRequired);
        ValidatePackage();
        ct.ThrowIfCancellationRequested();
        // install-filter creates the service but does not deploy its SYS file (official filter setup does).
        // Preserve existing shared system files; this operation does not upgrade other devices' drivers.
        foreach (string file in new[] { "libusb0.sys", "libusb0.dll" })
        {
            string target = Path.Combine(NativeSystemDirectory, file == "libusb0.sys" ? "drivers" : "", file);
            if (!File.Exists(target)) File.Copy(Path.Combine(InstallerDirectory, file), target, overwrite: false);
        }
        var start = new ProcessStartInfo(Path.Combine(InstallerDirectory, "install-filter.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = InstallerDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return await RunInstallerAsync(start, ct).ConfigureAwait(false);
    }

    internal static async Task<int> RunInstallerAsync(ProcessStartInfo start, CancellationToken ct, int timeoutMilliseconds = 60_000)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMilliseconds);
        // ShellExecute may wait inside Windows UAC. Bound the host wait and release late process handles.
        Task<Process?> launch = Task.Run(() =>
        {
            timeout.Token.ThrowIfCancellationRequested();
            return Process.Start(start);
        });
        Process? process;
        try { process = await launch.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _ = ReleaseLateProcessAsync(launch);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException(Strings.Cli_MtkDriverInstallTimedOut);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(Strings.Cli_MtkDriverLaunchFailed, exception);
        }
        if (process is null) throw new InvalidOperationException(Strings.Cli_MtkDriverLaunchFailed);
        using (process)
        {
            Task drainOutput = start.RedirectStandardOutput ? process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token) : Task.CompletedTask;
            Task drainError = start.RedirectStandardError ? process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token) : Task.CompletedTask;
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(drainOutput, drainError).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Elevated processes may reject termination. Driver changes cannot be rolled back safely.
                TryTerminate(process);
                try { await Task.WhenAll(drainOutput, drainError).ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException(Strings.Cli_MtkDriverInstallTimedOut);
            }
            return process.ExitCode;
        }
    }

    private static async Task ReleaseLateProcessAsync(Task<Process?> launch)
    {
        try
        {
            using var process = await launch.ConfigureAwait(false);
            if (process is not null) TryTerminate(process);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException)
        { Log.Debug(exception, Strings.Cli_MtkDriverLateLaunchFailed); }
    }

    private static void TryTerminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        { Log.Debug(exception, Strings.Cli_MtkDriverTerminationFailed); }
    }
}
