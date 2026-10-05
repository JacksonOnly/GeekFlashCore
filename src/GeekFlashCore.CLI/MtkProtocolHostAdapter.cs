using System.Security.Cryptography;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Extensions;
using GeekFlashCore.Protocol.Mtk.Loaders;
using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.Transport.LibUsb;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal static class MtkProtocolHostAdapter
{
    public static ProtocolRegistration Registration
    {
        get;
    } = new(ProtocolType.Mtk,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mtk", "mediatek" }, "MediaTek", Strings.Cli_MtkWaiting,
        new MtkDeviceIdentify(), Create, [], Present, new CommandSet(), CreateUsb);
    internal static void ValidateOptions(CliOptions o)
    {
        if (o.Port is not null)
            throw new ArgumentException(Strings.Cli_MtkRequiresUsb);
        _ = DaKind(o.MtkDaMode);
        if (o.ResourceTimeout is <= 0)
            throw new ArgumentException(Strings.Cli_TimeoutMustBePositive);
        if (o.Digest is not null || o.VipSigned is not null || o.VipChained is not null || o.OplusDigest is not null ||
            o.OplusSign is not null || o.OplusResume || o.HasExplicitOplusMode || o.OnePlusProjectId is not null || o.AuthenticationKind is not null ||
            o.Vendor != GeekFlashCore.Protocol.Qcom.Abstractions.QcomVendorKind.Auto)
            throw new ArgumentException(Strings.Cli_MtkOptionConflict);
        if (o.UsbPortPath is not null && (o.UsbBus is null || o.UsbPortPath.Split('.').Any(p => !byte.TryParse(p, out byte n) || n == 0)))
            throw new ArgumentException(Strings.Cli_UsbIdentityInvalid);
    }
    private static MtkDaKind? DaKind(string? mode) => mode?.ToLowerInvariant() switch
    {
        null or "auto" => null,
        "legacy" => MtkDaKind.Legacy,
        "xflash" => MtkDaKind.XFlash,
        "xml" => MtkDaKind.Xml,
        _ => throw new ArgumentException(Strings.Cli_MtkDaModeInvalid)
    };
    private static ITransport CreateUsb(UsbTransportIdentity id, CliOptions o) => LibUsbTransportFactory.Create(new LibUsbConnectionOptions
    {
        Identity = id with { SerialNumber = o.UsbSerial ?? id.SerialNumber, BusNumber = o.UsbBus ?? id.BusNumber, PortPath = o.UsbPortPath ?? id.PortPath },
        InterfaceNumber = o.UsbInterface,
        ControlInterfaceNumber = o.UsbControlInterface,
        AlternateSetting = o.UsbAlternateSetting,
        ReadTimeoutMilliseconds = o.ReadTimeout,
        WriteTimeoutMilliseconds = o.WriteTimeout
    });
    private static IProtocol Create(ProtocolHostContext context, ITransport transport)
    {
        if (transport is not IUsbTransport usb)
            throw new ArgumentException(Strings.Cli_MtkRequiresUsb);
        var o = context.Options;
        ValidateOptions(o);
        var kind = DaKind(o.MtkDaMode);
        return new MtkProtocol(usb, new()
        {
            DaKind = kind,
            ReadTimeoutMilliseconds = o.ReadTimeout,
            ConnectTimeoutMilliseconds = o.HasExplicitConnectTimeout ? o.ConnectTimeout : MtkProtocolOptions.DefaultConnectTimeoutMilliseconds,
            ResourceTimeoutMilliseconds = o.ResourceTimeout is > 0 ? o.ResourceTimeout.Value : 30000
        }, resources: target =>
        {
            if (o.Loader is null)
                throw new MtkResourceException("DA --loader");
            var image = MtkDaParser.Select(new FileDataSource(ConsolePath.Normalize(o.Loader)!), target, kind);
            var emi = o.MtkPreloader is null ? null : MtkEmiParser.Parse(new FileDataSource(ConsolePath.Normalize(o.MtkPreloader)!));
            MtkSensitiveBuffer? auth = null, cert = null;
            try
            {
                if (o.MtkAuthenticationFile is not null)
                    auth = new(ReadBounded(o.MtkAuthenticationFile, 1048576));
                if (o.MtkCertificateFile is not null)
                    cert = new(ReadBounded(o.MtkCertificateFile, 1048576));
                return new(image, emi, auth, cert);
            }
            catch { auth?.Dispose(); cert?.Dispose(); throw; }
        }, leaveTransportOpen: true);
    }
    private static byte[] ReadBounded(string path, int maximum)
    {
        using var s = File.OpenRead(ConsolePath.Normalize(path)!);
        if (s.Length <= 0 || s.Length > maximum)
            throw new MtkResourceException("bounded file");
        byte[] data = new byte[(int)s.Length];
        try
        {
            s.ReadExactly(data);
            return data;
        }
        catch { CryptographicOperations.ZeroMemory(data); throw; }
    }
    private static void Present(IProtocol protocol, ConsoleUi ui)
    {
        var p = (IMtkProtocol)protocol;
        ui.WriteLine(Strings.FormatCli_MtkInfo(p.TargetInfo?.HardwareCode.ToString("X4") ?? "?", p.SessionState, p.Capabilities));
        if (p.IsConnected)
            foreach (var r in p.GetStorageInfo().Regions)
                ui.WriteLine($"{r.WireId} {r.Name} {r.Length} / {r.BlockSize}");
    }
    private sealed class CommandSet : IProtocolCommandSet
    {
        private static readonly string[] Names = ["mtk-probe", "mtk-capabilities", "mtk-rpmb", "mtk-memory", "mtk-seccfg"];
        public bool Handles(string command) => Names.Contains(command, StringComparer.OrdinalIgnoreCase);
        public CliOptions Normalize(CliOptions options) => options with { Command = options.Command.ToLowerInvariant() };
        public bool RequiresConnection(string command) => command is not ("mtk-probe" or "mtk-capabilities");
        public void ValidateAvailability(IProtocol protocol, string command)
        {
            if (RequiresConnection(command) && !protocol.IsConnected)
                throw new InvalidOperationException(Strings.Cli_ReconnectRequired);
        }
        public void PrintHelp(IProtocol protocol, ConsoleUi ui) => ui.WriteLine(Strings.Cli_HelpMtk);
        public void Validate(CliOptions options)
        {
            string[] a = options.Arguments;
            switch (options.Command)
            {
                case "mtk-probe":
                case "mtk-capabilities":
                    if (a.Length != 0)
                        throw new CommandUsageException(options.Command);
                    break;
                case "mtk-rpmb":
                    if (a.Length != 6 || a[0] is not ("read" or "write") || CommandSyntax.Number(a[1]) > 3 ||
                        CommandSyntax.Number(a[2]) > uint.MaxValue || CommandSyntax.Number(a[3]) is 0 or > uint.MaxValue)
                        throw new CommandUsageException("mtk-rpmb <read|write> <region> <start> <count> <key-file> <data-file>");
                    break;
                case "mtk-memory":
                    if (a.Length != 4 || a[0] is not ("read" or "write") || CommandSyntax.Number(a[1]) > uint.MaxValue ||
                        CommandSyntax.Number(a[2]) is 0 or > uint.MaxValue || CommandSyntax.Number(a[1]) + CommandSyntax.Number(a[2]) > (ulong)uint.MaxValue + 1)
                        throw new CommandUsageException("mtk-memory <read|write> <address> <length> <file>");
                    break;
                case "mtk-seccfg":
                    if (a.Length != 5 || a[0] is not ("lock" or "unlock") || CommandSyntax.Number(a[1]) > uint.MaxValue ||
                        CommandSyntax.Number(a[2]) > long.MaxValue || CommandSyntax.Number(a[3]) is 0 or > long.MaxValue)
                        throw new CommandUsageException("mtk-seccfg <lock|unlock> <region> <offset> <length> <backup-file>");
                    break;
                default:
                    throw new CommandUsageException(options.Command);
            }
        }
        public async Task<int> ExecuteAsync(IProtocol protocol, CliOptions options, ConsoleUi ui, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            var p = (IMtkProtocol)protocol;
            string[] a = options.Arguments;
            if (options.Command == "mtk-probe")
            {
                p.Probe(ct);
                Present(p, ui);
                return 0;
            }
            if (options.Command == "mtk-capabilities")
            {
                Present(p, ui);
                return 0;
            }
            if (options.Command == "mtk-seccfg")
            {
                IReadOnlyList<IMtkSecurityCipher>? ciphers = null;
                if (options.MtkSejBase != 0)
                {
                    var extension = CreateExtension(p, options, [], ct);
                    ciphers = p.DownloadAgent!.Entry.Kind == MtkDaKind.XFlash ?
                        [new MtkSoftwareSecurityCipher(), new MtkSejSecurityCipher(extension), new MtkSejSecurityCipher(extension, xor: true), new MtkSejSecurityCipher(extension, legacy: true)] :
                        [new MtkSoftwareSecurityCipher(), new MtkSejSecurityCipher(extension)];
                }
                var service = new MtkSecurityConfigurationService(p, ciphers);
                using var plan = service.Plan(new((uint)CommandSyntax.Number(a[1]), (long)CommandSyntax.Number(a[2]), (long)CommandSyntax.Number(a[3])), a[0] == "lock", ct);
                using var backup = new FileStream(ConsolePath.Normalize(a[4])!, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                backup.Write(plan.Original.Memory.Span);
                backup.Flush(true);
                backup.Position = 0;
                service.Apply(plan, backup, ct);
                return 0;
            }
            uint address = options.Command == "mtk-memory" ? (uint)CommandSyntax.Number(a[1]) : 0, length = options.Command == "mtk-memory" ? (uint)CommandSyntax.Number(a[2]) : 0;
            var ext = CreateExtension(p, options, length > 0 ? [new(address, length)] : [], ct);
            if (options.Command == "mtk-rpmb")
            {
                uint region = (uint)CommandSyntax.Number(a[1]), start = (uint)CommandSyntax.Number(a[2]), count = (uint)CommandSyntax.Number(a[3]);
                byte[] key = ReadBounded(a[4], 32);
                try
                {
                    ext.Authenticate(region, key, ct);
                }
                finally { CryptographicOperations.ZeroMemory(key); }
                if (a[0] == "read")
                    await AtomicReadOutput.WriteAsync(ConsolePath.Normalize(a[5])!, s => { ext.Read(region, start, count, s, ct); return Task.CompletedTask; }, ct);
                else
                {
                    using var s = File.OpenRead(ConsolePath.Normalize(a[5])!);
                    ext.Write(region, start, count, s, ct);
                }
            }
            else if (a[0] == "read")
                await AtomicReadOutput.WriteAsync(ConsolePath.Normalize(a[3])!, s => { ext.ReadMemory(address, length, s, ct); return Task.CompletedTask; }, ct);
            else
            {
                using var s = File.OpenRead(ConsolePath.Normalize(a[3])!);
                ext.WriteMemory(address, length, s, ct);
            }
            return 0;
        }
        private static MtkDaExtension CreateExtension(IMtkProtocol p, CliOptions options, IReadOnlyList<MtkMemoryRange> ranges, CancellationToken ct)
        {
            var image = p.DownloadAgent ?? throw new MtkResourceException("DA metadata");
            var da2 = image.Entry.Regions[image.Entry.EntryRegionIndex + 1];
            var ext = new MtkDaExtension(p);
            ext.Initialize(new(p.TargetInfo!.HardwareCode, da2.Address, da2.Length - da2.SignatureLength,
                options.MtkSejBase, options.MtkTzccBase, options.MtkSsrBase)
            {
                AllowedMemoryRanges = ranges,
                UfsRpmbDataBlocks = options.MtkUfsRpmbBlocks
            }, ct);
            return ext;
        }
    }
}
