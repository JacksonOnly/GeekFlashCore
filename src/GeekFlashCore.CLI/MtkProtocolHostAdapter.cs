using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Extensions;
using GeekFlashCore.Protocol.Mtk.Exploits;
using GeekFlashCore.Protocol.Mtk.Loaders;
using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.Transport.LibUsb;
using GeekFlashCore.CLI.Localization;
using LibUsbDotNet.LibUsb;

namespace GeekFlashCore.CLI;

internal sealed class MtkLoaderSelectionTimeoutException() : TimeoutException(Strings.Cli_MtkDaSelectionTimedOut);

internal static partial class MtkProtocolHostAdapter
{
    internal static async Task<CliOptions> SelectLoaderAsync(CliOptions options, ConsoleUi ui, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.ResourceTimeout ?? 30000);
        try
        {
            string path = (await ui.SelectFileAsync(Strings.Cli_MtkDaPrompt, options.Loader,
                Strings.Cli_MtkDaMissing, budget.Token, path => new FileInfo(path).Length > 0).ConfigureAwait(false))!;
            return options with { Loader = path };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        { throw new MtkLoaderSelectionTimeoutException(); }
    }

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
        var kind = DaKind(o.MtkDaMode);
        _ = PmtLayout(o.MtkPmtLayout);
        if(!Enum.IsDefined(o.MtkExtensionAbi))throw new ArgumentException(Strings.Cli_MtkExtensionAbiInvalid);
        if (o.MtkBromChunkSize is < 64 or > 1048576)
            throw new ArgumentException(Strings.Cli_MtkBromChunkInvalid);
        if (o.MtkNorEraseBlockSize < 0 || o.MtkNorEraseBlockSize > 16 * 1024 * 1024 ||
            (o.MtkNorEraseBlockSize & (o.MtkNorEraseBlockSize - 1)) != 0 || o.MtkNandCapacity is <= 0 ||
            o.MtkIoT && kind is not (null or MtkDaKind.Legacy))
            throw new ArgumentException(Strings.Cli_MtkMediaOptionsInvalid);
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
    private static MtkPmtLayout? PmtLayout(string? layout) => layout switch
    {
        null => null, "32" => MtkPmtLayout.Word32, "64" => MtkPmtLayout.Word64, "96" => MtkPmtLayout.Legacy96, "disk" => MtkPmtLayout.DiskV1,
        _ => throw new ArgumentException(Strings.Cli_MtkMediaOptionsInvalid)
    };
    private static ITransport CreateUsb(UsbTransportIdentity id, CliOptions o) =>
        LibUsbTransportFactory.Create(CreateUsbOptions(id, o));
    internal static LibUsbConnectionOptions CreateUsbOptions(UsbTransportIdentity id, CliOptions o) => new()
    {
        Identity = id with { SerialNumber = o.UsbSerial ?? id.SerialNumber, BusNumber = o.UsbBus ?? id.BusNumber, PortPath = o.UsbPortPath ?? id.PortPath },
        InterfaceNumber = o.UsbInterface,
        ControlInterfaceNumber = o.UsbControlInterface,
        AlternateSetting = o.UsbAlternateSetting,
        ReadTimeoutMilliseconds = o.ReadTimeout,
        WriteTimeoutMilliseconds = o.WriteTimeout,
        RecoverInitialReadStall = true
    };
    internal static async Task<IProtocol> CreatePreparedAsync(ProtocolHostContext context, ITransport transport,
        CancellationToken ct, Action? admitted = null)
    {
        CliOptions options = context.Options;
        IProtocol protocol = Create(context, transport, target => ResolveResources(options, target));
        try
        {
            var mtk = (MtkProtocol)protocol;
            if (options.Command == "reconnect" && options.Arguments.FirstOrDefault() is null or "auto" &&
                mtk.InspectEntrySignal(ct) is MtkEntrySignal.Da1Sync or MtkEntrySignal.FramedDa)
            {
                await ResumeAsync(mtk, options, context.Ui, null, false, [], ct).ConfigureAwait(false);
                admitted?.Invoke();
                return protocol;
            }
            var target = ProbeForAdmission((MtkProtocol)protocol, ct);
            admitted?.Invoke();
            PresentTarget(target, context.Ui);
            options = await SelectLoaderAsync(options, context.Ui, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return protocol;
        }
        catch
        {
            try { await protocol.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { Serilog.Log.Debug(cleanup, Strings.Cli_MtkOperationStopped); }
            throw;
        }
    }
    internal static MtkTargetInfo ProbeForAdmission(MtkProtocol protocol, CancellationToken ct)
    {
        try { return protocol.Probe(ct); }
        catch (Exception exception) when (!protocol.HasIdentifiedTarget &&
            exception is UsbException or IOException or TimeoutException or MtkProtocolException)
        {
            ct.ThrowIfCancellationRequested();
            throw new MtkInitialProbeException(exception);
        }
    }
    private static IProtocol Create(ProtocolHostContext context, ITransport transport) =>
        Create(context, transport, target =>
        {
            PresentTarget(target, context.Ui);
            return ResolveResources(context.Options, target);
        });

    private static readonly MtkExploitDependencies ExploitDependencies = new(
        MtkExploitResourceStore.FromEmbeddedResources());
    private static readonly ConditionalWeakTable<IMtkProtocol, MtkDaExtension> Extensions = new();

    internal static MtkCapabilities GetCapabilities(IMtkProtocol protocol) =>
        Extensions.TryGetValue(protocol, out var extension) ? extension.Capabilities : protocol.Capabilities;
    internal static bool IsCurrentExtension(IMtkProtocol protocol, MtkDaExtension extension) =>
        Extensions.TryGetValue(protocol, out var current) && ReferenceEquals(current, extension);

    private static void RememberExtension(IMtkProtocol protocol, MtkDaExtension extension)
    {
        Extensions.Remove(protocol);
        Extensions.Add(protocol, extension);
    }

    internal static void InitializeExtension(IProtocol protocol, CliOptions options, CancellationToken ct)
    {
        if (protocol is not IMtkProtocol p || p.DownloadAgent is not { } image || image.Entry.Kind == MtkDaKind.Legacy) return;
        var chip = MtkChipCatalog.Find(p.TargetInfo!.HardwareCode);
        byte[]? prepared = chip?.Uart0 is { } uart
            ? MtkPenumbraExtensionPreparer.Prepare(image, uart, p.GetStorageInfo().Kind, ExploitDependencies.Resources, ct) : null;
        if (prepared is null)
        {
            Serilog.Log.ForContext("MtkSummary", true).Warning(Strings.Cli_MtkExtensionNotPrepared);
            return;
        }
        try
        {
            var extension = new MtkDaExtension(p);
            extension.Load(ExtensionContext(p, options, []), 0x68000000, new PreparedExtensionSource(prepared), ct);
            RememberExtension(p, extension);
            Serilog.Log.ForContext("MtkSummary", true).Information(Strings.Cli_MtkExtensionLoaded);
        }
        finally { CryptographicOperations.ZeroMemory(prepared); }
    }
    private sealed class PreparedExtensionSource(byte[] bytes) : IDataSource
    {
        public long Length => bytes.Length;
        public Stream OpenStream() => new MemoryStream(bytes, false);
        public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(OpenStream()); }
    }
    private static MtkExtensionContext ExtensionContext(IMtkProtocol p, CliOptions options, IReadOnlyList<MtkMemoryRange> ranges)
    {
        var da2 = p.DownloadAgent!.Entry.Regions[p.DownloadAgent.Entry.EntryRegionIndex + 1];
        uint sej = options.MtkSejBase != 0 ? options.MtkSejBase : p.TargetInfo!.HardwareCode == 0x950 ? 0x1000a000u : 0;
        return new(p.TargetInfo!.HardwareCode, da2.Address, da2.Length - da2.SignatureLength, sej, options.MtkTzccBase, options.MtkSsrBase)
        { AllowedMemoryRanges = ranges, Abi = MtkExtensionAbi.Penumbra2, UfsRpmbDataBlocks = options.MtkUfsRpmbBlocks };
    }
    internal static MtkDaExtension CreateExtension(IMtkProtocol p, CliOptions options, IReadOnlyList<MtkMemoryRange> ranges, CancellationToken ct)
    {
        if (ranges.Count == 0 && !options.HasExplicitMtkExtensionAbi && options.MtkSejBase == 0 && options.MtkTzccBase == 0 &&
            options.MtkSsrBase == 0 && options.MtkUfsRpmbBlocks.Count == 0 && Extensions.TryGetValue(p, out var current) && current.IsReady)
            return current;
        var ext = new MtkDaExtension(p);
        ext.Initialize(ExtensionContext(p, options, ranges) with { Abi = options.MtkExtensionAbi }, ct);
        RememberExtension(p, ext);
        return ext;
    }
    private static IProtocol Create(ProtocolHostContext context, ITransport transport,
        Func<MtkTargetInfo, MtkConnectionResources> resources)
    {
        if (transport is not IUsbTransport usb)
            throw new ArgumentException(Strings.Cli_MtkRequiresUsb);
        var o = context.Options;
        ValidateOptions(o);
        var kind = DaKind(o.MtkDaMode) ?? (o.MtkIoT ? MtkDaKind.Legacy : (MtkDaKind?)null);
        return new MtkProtocol(usb, new()
        {
            DaKind = kind,
            InitializeWatchdogOnProbe = true,
            EnableNandLogicalWrites = o.MtkNandWrite,
            NandLogicalCapacity = o.MtkNandCapacity,
            LegacyIoT = o.MtkIoT,
            BromUploadChunkSize = o.MtkBromChunkSize ?? 0,
            BromUploadZeroLengthPacket = o.MtkBromZeroLengthPacket,
            NorEraseBlockSize = o.MtkNorEraseBlockSize,
            LegacyPmtLayout = PmtLayout(o.MtkPmtLayout),
            ReadTimeoutMilliseconds = o.ReadTimeout,
            ConnectTimeoutMilliseconds = o.HasExplicitConnectTimeout ? o.ConnectTimeout : MtkProtocolOptions.DefaultConnectTimeoutMilliseconds,
            ResourceTimeoutMilliseconds = o.ResourceTimeout is > 0 ? o.ResourceTimeout.Value : 30000
        }, resources: resources, leaveTransportOpen: true, exploitStrategies:
        [
            new UnfusedExploitStrategy(ExploitDependencies),
            new LineCodeExploitStrategy(ExploitDependencies),
            new CarbonaraExploitStrategy(ExploitDependencies),
            new HeapBaitExploitStrategy(ExploitDependencies)
        ]);
    }
    private static MtkConnectionResources ResolveResources(CliOptions o, MtkTargetInfo target)
    {
        if (o.Loader is null)
            throw new MtkResourceException("DA --loader");
        var kind = DaKind(o.MtkDaMode) ?? (o.MtkIoT ? MtkDaKind.Legacy : (MtkDaKind?)null);
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
    }
    internal static byte[] ReadBounded(string path, int maximum)
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
        if (p.SessionState == MtkSessionState.Probed && p.TargetInfo is { } target)
            PresentTarget(target, ui);
        var capabilities = GetCapabilities(p);
        ui.WriteLine(Strings.FormatCli_MtkInfo(p.TargetInfo?.HardwareCode.ToString("X4") ?? "?", p.SessionState,
            Strings.FormatCli_MtkCapabilities(capabilities.Flash, capabilities.Memory, capabilities.Crypto,
                capabilities.Rpmb, capabilities.SecurityConfiguration)));
        if (p.IsConnected)
            foreach (var r in p.GetStorageInfo().Regions)
                ui.WriteLine($"{r.WireId} {r.Name} {r.Length} / {r.BlockSize}");
    }
    internal static void PresentTarget(MtkTargetInfo target, ConsoleUi ui)
    {
        ui.WriteLine(Strings.FormatCli_MtkChip(target.ChipName ?? Strings.Cli_MtkUnknownChip,
            target.ChipDescription ?? "", target.HardwareCode.ToString("X4"), MtkChipCatalog.GetDaHardwareCode(target).ToString("X4")));
        ui.WriteLine(Strings.FormatCli_MtkVersions(target.HardwareSubCode.ToString("X4"),
            target.InitialHardwareVersion.ToString("X4"), target.HardwareVersion.ToString("X4"),
            target.SoftwareVersion.ToString("X4"), target.BromVersion.ToString("X2"),
            target.PreloaderVersion.ToString("X2"), target.Stage));
        static string Flag(bool enabled) => enabled ? Strings.Cli_MtkEnabled : Strings.Cli_MtkDisabled;
        var s = target.Security;
        ui.WriteLine(Strings.FormatCli_MtkSecurity(s.Raw.ToString("X8"), Flag(s.SecureBoot), Flag(s.Sla),
            Flag(s.Daa), Flag(s.CertificateRequired), Flag(s.MemoryReadAuthenticationRequired),
            Flag(s.MemoryWriteAuthenticationRequired), Flag(s.EmmcBootParameterPresent), Flag(s.CacheCommandBlocked)));
        ui.WriteLine(Strings.FormatCli_MtkWatchdog(target.WatchdogState switch
        {
            MtkWatchdogState.Disabled => Strings.Cli_MtkWatchdogDisabled,
            MtkWatchdogState.ProfileUnavailable => Strings.Cli_MtkWatchdogUnavailable,
            _ => Strings.Cli_MtkWatchdogNotRequested
        }));
    }
    private sealed class CommandSet : IProtocolCommandSet
    {
        private static readonly string[] Names = ["mtk-probe", "mtk-capabilities", "mtk-rpmb", "mtk-memory", "mtk-seccfg", "mtk-query", "mtk-property", "mtk-register", "mtk-pmt", "mtk-slot", "mtk-scatter", "mtk-efuse", "mtk-partition", "mtk-key", "mtk-fill", "mtk-rsc", "mtk-rpmb-lock"];
        public bool Handles(string command) => Names.Contains(command, StringComparer.OrdinalIgnoreCase);
        public CliOptions Normalize(CliOptions options) => options with { Command = options.Command.ToLowerInvariant() };
        public bool RequiresConnection(string command) => command is not ("mtk-probe" or "mtk-capabilities");
        public void ValidateAvailability(IProtocol protocol, string command)
        {
            if (command is "connect" or "reconnect" or "info" or "help" or "firmware" or "browse-image") return;
            if (RequiresConnection(command) && !protocol.IsConnected)
                throw new InvalidOperationException(Strings.Cli_ReconnectRequired);
        }
        public void PrintHelp(IProtocol protocol, ConsoleUi ui) { ui.WriteLine(Strings.Cli_HelpMtk); ui.WriteLine(Strings.Cli_HelpMtkCommands); ui.WriteLine(Strings.Cli_HelpMtkRepair); }
        public void Validate(CliOptions options)
        {
            if (MtkCommandRequest.TryValidate(options.Command, options.Arguments)) return;
            string[] a = options.Arguments;
            switch (options.Command)
            {
                case "mtk-probe":
                case "mtk-capabilities":
                    if (a.Length != 0)
                        throw new CommandUsageException(options.Command);
                    break;
                case "mtk-rpmb":
                    if (a.Length is not (5 or 6) || a[0] is not ("read" or "write" or "erase") || a.Length != (a[0] == "erase" ? 5 : 6) || CommandSyntax.Number(a[1]) > 3 ||
                        CommandSyntax.Number(a[2]) > uint.MaxValue || CommandSyntax.Number(a[3]) is 0 or > uint.MaxValue)
                        throw new CommandUsageException("mtk-rpmb <read|write|erase> <region> <start> <count> <key-file> [data-file]");
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
                case "mtk-query":
                    if (a.Length != 2 || !Enum.GetNames<MtkDaQuery>().Contains(a[0], StringComparer.OrdinalIgnoreCase))
                        throw new CommandUsageException("mtk-query <query-name> <output-file>");
                    break;
                case "mtk-property":
                    if (a.Length != 2 || a[0].Length is 0 or > 128 || a[0].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
                        throw new CommandUsageException("mtk-property <key> <output-file>");
                    break;
                case "mtk-register":
                    if (a.Length is not (2 or 3) || a[0] is not ("read" or "write") || a.Length != (a[0] == "read" ? 2 : 3) ||
                        CommandSyntax.Number(a[1]) > uint.MaxValue || CommandSyntax.Number(a[1]) % 4 != 0 ||
                        a.Length == 3 && CommandSyntax.Number(a[2]) > uint.MaxValue)
                        throw new CommandUsageException("mtk-register <read|write> <address> [value]");
                    break;
                case "mtk-pmt":
                    if (a.Length != 1 || a[0] is not ("32" or "64" or "96" or "disk" or "xml"))
                        throw new CommandUsageException("mtk-pmt <32|64|96|disk|xml>");
                    break;
                case "mtk-slot":
                    if (a.Length is not (4 or 6) || a[0] is not ("read" or "set") || a.Length != (a[0] == "read" ? 4 : 6))
                        throw new CommandUsageException("mtk-slot read <region> <offset> <length> | set <slot-index> <region> <offset> <length> <backup-file>");
                    int index = a[0] == "read" ? 1 : 2;
                    if (index == 2 && CommandSyntax.Number(a[1]) > 3 || CommandSyntax.Number(a[index]) > uint.MaxValue ||
                        CommandSyntax.Number(a[index + 1]) > long.MaxValue || CommandSyntax.Number(a[index + 2]) is < 2080 or > long.MaxValue)
                        throw new CommandUsageException("mtk-slot");
                    break;
                case "mtk-scatter":
                    if (a.FirstOrDefault() is "to-gpt" or "from-gpt") { MtkScatterCommands.Validate(a); break; }
                    if(a.Length is not (2 or 4) || a[0] is not ("plan" or "flash" or "update") || a.Length!=(a[0]=="plan"?2:4))
                        throw new CommandUsageException("mtk-scatter plan scatter-file | flash|update scatter-file image-directory backup-directory");
                    break;
                case "mtk-key":
                    if(a.Length is not (4 or 5) || a[0] is not ("id" or "input") || a.Length!=(a[0]=="id"?4:5) ||
                        a[0]=="id" && !Enum.GetNames<MtkKeyDeriveId>().Contains(a[1],StringComparer.OrdinalIgnoreCase) ||
                        a[a[0]=="id"?2:3] is not ("128" or "192" or "256"))throw new CommandUsageException("mtk-key id key-name 128|192|256 output-file | input label-file salt-file 128|192|256 output-file");
                    break;
                case "mtk-fill":
                    if(a.Length!=4 || CommandSyntax.Number(a[0])>uint.MaxValue || CommandSyntax.Number(a[1])>long.MaxValue ||
                        CommandSyntax.Number(a[2]) is 0 or >long.MaxValue || CommandSyntax.Number(a[3])>255)throw new CommandUsageException("mtk-fill region offset length byte-value");
                    break;
                case "mtk-rsc":
                    if(a.Length!=2 || a[0].Length is 0 or >63 || a[0].Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))throw new CommandUsageException("mtk-rsc partition source-file");
                    break;
                case "mtk-rpmb-lock":
                    if(a.Length is not (2 or 3) || a[0] is not ("read" or "lock" or "unlock") || a.Length!=(a[0]=="read"?2:3))throw new CommandUsageException("mtk-rpmb-lock read key-file | lock|unlock key-file backup-file");
                    break;
                case "mtk-efuse":
                    if(a.Length is not (2 or 3) || a[0] is not ("read" or "write") || a.Length!=(a[0]=="read"?2:3))
                        throw new CommandUsageException("mtk-efuse read output-file | write image-file backup-file");
                    break;
                case "mtk-partition":
                    if(a.Length is not (2 or 4) || a[0] is not ("read" or "write" or "erase") || a.Length!=(a[0]=="erase"?2:4) ||
                        a[1].Length is 0 or >64 || a[1].Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')) ||
                        a.Length==4 && CommandSyntax.Number(a[2]) is 0 or >long.MaxValue)
                        throw new CommandUsageException("mtk-partition read|write name maximum-length file | erase name");
                    break;
                default:
                    throw new CommandUsageException(options.Command);
            }
        }
        public async Task<int> ExecuteAsync(IProtocol protocol, CliOptions options, ConsoleUi ui, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            var p = (IMtkProtocol)protocol;
            if (await MtkCommandWorkflows.TryExecuteAsync(p, options, ui, progress, ct).ConfigureAwait(false)) return 0;
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
            if(options.Command=="mtk-key")
            {
                if(options.MtkExtensionAbi!=MtkExtensionAbi.Penumbra2)throw new MtkCapabilityException("--mtk-extension-abi penumbra2");
                byte[] label=[],salt=[];
                try
                {
                    if(a[0]=="input"){label=a[1]=="-"?[]:ReadBounded(a[1],32);salt=a[2]=="-"?[]:ReadBounded(a[2],32);}
                    var extension=CreateExtension(p,options,[],ct);string bits=a[a[0]=="id"?2:3];
                    MtkKeySize size=bits switch {"128"=>MtkKeySize.Key128,"192"=>MtkKeySize.Key192,_=>MtkKeySize.Key256};
                    using var key=a[0]=="id"?extension.DeriveKey(Enum.Parse<MtkKeyDeriveId>(a[1],true),size,ct):extension.DeriveKey(label,salt,size,ct);
                    await AtomicReadOutput.WriteAsync(ConsolePath.Normalize(a[^1])!,s=>s.WriteAsync(key.Memory,ct).AsTask(),ct);
                }
                finally{CryptographicOperations.ZeroMemory(label);CryptographicOperations.ZeroMemory(salt);}
                return 0;
            }
            if(options.Command=="mtk-fill")
            {
                new MtkFlashFillService(p).Fill(new((uint)CommandSyntax.Number(a[0]),(long)CommandSyntax.Number(a[1]),(long)CommandSyntax.Number(a[2])),(byte)CommandSyntax.Number(a[3]),progress:progress,cancellationToken:ct);return 0;
            }
            if(options.Command=="mtk-rsc")
            {
                (p as IMtkDaStandardOperations??throw new MtkCapabilityException("standard RSC info")).SetRscInfo(a[0],new FileDataSource(ConsolePath.Normalize(a[1])!),progress,ct);return 0;
            }
            if(options.Command=="mtk-rpmb-lock")
            {
                byte[] key=ReadBounded(a[1],32);
                try
                {
                    if(key.Length!=32)throw new MtkResourceException("RPMB key length");
                    if(p.DownloadAgent?.Entry.Kind!=MtkDaKind.Xml || p.GetStorageInfo().Kind!=MtkStorageKind.Ufs)throw new MtkCapabilityException("XML/UFS RPMB lock metadata");
                    var extension=CreateExtension(p,options,[],ct);extension.Authenticate(1,key,ct);
                    if(a[0]=="read"){var info=extension.ReadRpmbLockState(ct);ui.WriteLine(Strings.FormatCli_MtkRpmbLockState(info.Version,info.State));}
                    else{using var backup=new FileStream(ConsolePath.Normalize(a[2])!,FileMode.CreateNew,FileAccess.Write,FileShare.None);extension.SetRpmbLockState(a[0]=="lock",backup,ct);}
                }
                finally{CryptographicOperations.ZeroMemory(key);}
                return 0;
            }
            if(options.Command=="mtk-efuse")
            {
                var operations=p as IMtkDaStandardOperations??throw new MtkCapabilityException("standard eFuse operations");
                if(a[0]=="read")
                {
                    using var result=operations.ReadEfuses(ct);
                    await AtomicReadOutput.WriteAsync(ConsolePath.Normalize(a[1])!,s=>s.WriteAsync(result.Memory,ct).AsTask(),ct);
                }
                else
                {
                    var source=new FileDataSource(ConsolePath.Normalize(a[1])!);
                    if(source.Length is <=0 or >0x5000 || p.DownloadAgent?.Entry.Kind==MtkDaKind.XFlash && source.Length!=0x42d4)
                        throw new MtkResourceException("eFuse image length");
                    using var original=operations.ReadEfuses(ct);
                    using var backup=new FileStream(ConsolePath.Normalize(a[2])!,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                    backup.Write(original.Memory.Span);backup.Flush(true);
                    operations.WriteEfuses(source,ct);
                }
                return 0;
            }
            if(options.Command=="mtk-partition")
            {
                var partitions=p as IMtkNamedPartitionAccess??throw new MtkCapabilityException("native partitions");
                if(a[0]=="erase")partitions.EraseNamedPartition(a[1],ct);
                else if(a[0]=="read")await AtomicReadOutput.WriteAsync(ConsolePath.Normalize(a[3])!,s=>
                {partitions.ReadNamedPartition(a[1],s,(long)CommandSyntax.Number(a[2]),ct);return Task.CompletedTask;},ct);
                else partitions.WriteNamedPartition(a[1],new FileDataSource(ConsolePath.Normalize(a[3])!),(long)CommandSyntax.Number(a[2]),ct);
                return 0;
            }
            if(options.Command=="mtk-scatter")
            {
                string path=ConsolePath.Normalize(a[1])!;
                string scatterText=MtkScatterCommands.ReadText(path, ct);var manifest=MtkScatterParser.Parse(scatterText);
                var service=new MtkScatterService(p);var plan=service.Plan(manifest,ct);
                foreach(var part in plan.Partitions)ui.WriteLine(Strings.FormatCli_MtkScatterRange(part.Name,part.Range.RegionId,part.Range.Offset,part.Range.Length,part.FileName??"-"));
                if(a[0]!="plan")
                {
                    var store=new MtkScatterDirectoryStore(ConsolePath.Normalize(a[2])!,ConsolePath.Normalize(a[3])!);
                    if (p.GetStorageInfo().Kind is not (MtkStorageKind.Emmc or MtkStorageKind.Ufs))
                    {
                        if (a[0] == "update" && p.DownloadAgent?.Entry.Kind == MtkDaKind.Xml)
                            (p as IMtkNativeScatterAccess ?? throw new MtkCapabilityException("XML FLASH-UPDATE")).ApplyXmlScatter(scatterText,store.OpenImage,store,progress,ct);
                        else service.Apply(plan,store.OpenImage,store,false,progress,ct);
                        return 0;
                    }
                    var differences = service.CompareLayout(plan, ct);
                    bool rebuild = differences.Count != 0;
                    if (rebuild)
                    {
                        service.BackupPartitionTable(plan, store, ct);
                        ui.WriteLine(Strings.FormatCli_MtkScatterLayoutChanged(string.Join(", ", differences)));
                        string answer = await ui.AskAsync(Strings.Cli_MtkScatterConfirm, ct, "no");
                        if (!answer.Equals("yes", StringComparison.OrdinalIgnoreCase)) return 0;
                    }
                    service.Apply(plan,store.OpenImage,store,rebuild,progress,ct);
                }
                return 0;
            }
            if (options.Command is "mtk-query" or "mtk-property" or "mtk-register" or "mtk-pmt")
            {
                var diagnostics = p as IMtkDaDiagnostics ?? throw new MtkCapabilityException("standard DA diagnostics");
                if (options.Command is "mtk-query" or "mtk-property")
                {
                    using var result = options.Command == "mtk-query" ? diagnostics.QueryDa(Enum.Parse<MtkDaQuery>(a[0], true), ct) : diagnostics.GetDaSystemProperty(a[0], ct);
                    await AtomicReadOutput.WriteAsync(ConsolePath.Normalize(a[1])!, s => s.WriteAsync(result.Memory, ct).AsTask(), ct);
                }
                else if (options.Command == "mtk-register")
                {
                    uint register = (uint)CommandSyntax.Number(a[1]);
                    if (a[0] == "read") ui.WriteLine(Strings.FormatCli_MtkRegisterValue(register.ToString("X8"), diagnostics.ReadDaRegister(register, ct).ToString("X8")));
                    else diagnostics.WriteDaRegister(register, (uint)CommandSyntax.Number(a[2]), ct);
                }
                else foreach (var partition in a[0] == "xml" ? diagnostics.GetXmlPartitionTable(ct) : diagnostics.GetLegacyPartitionTable(PmtLayout(a[0])!.Value, ct))
                    ui.WriteLine(Strings.FormatCli_MtkPartitionRange(partition.Name, partition.Offset, partition.Length));
                return 0;
            }
            if (options.Command == "mtk-slot")
            {
                int index = a[0] == "read" ? 1 : 2;
                var range = new MtkFlashRange((uint)CommandSyntax.Number(a[index]), (long)CommandSyntax.Number(a[index + 1]), (long)CommandSyntax.Number(a[index + 2]));
                var service = new MtkBootControlService(p);
                if (a[0] == "set")
                {
                    using var backup = new FileStream(ConsolePath.Normalize(a[5])!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    service.SetActiveSlot(range, (int)CommandSyntax.Number(a[1]), backup, ct);
                    ui.WriteLine(Strings.FormatCli_MtkSlotWritten(a[1]));
                    return 0;
                }
                var info = service.Read(range, ct);
                ui.WriteLine(Strings.FormatCli_MtkSlotInfo(info.ActiveSlot, info.Version, info.CurrentSlot));
                foreach (var slot in info.Slots)
                    ui.WriteLine(Strings.FormatCli_MtkSlotState(slot.Index, slot.Priority, slot.TriesRemaining, slot.SuccessfulBoot, slot.VerityCorrupted));
                return 0;
            }
            if (options.Command == "mtk-seccfg")
            {
                IReadOnlyList<IMtkSecurityCipher>? ciphers = null;
                if (options.MtkSejBase != 0 || GetCapabilities(p).Crypto == MtkCapabilitySupport.Supported)
                {
                    var extension = CreateExtension(p, options, [], ct);
                    ciphers = p.DownloadAgent!.Entry.Kind == MtkDaKind.XFlash ?
                        [new MtkPlainSecurityCipher(),new MtkSoftwareSecurityCipher(), new MtkSejSecurityCipher(extension), new MtkSejSecurityCipher(extension, xor: true), new MtkSejSecurityCipher(extension, legacy: true)] :
                        [new MtkPlainSecurityCipher(),new MtkSoftwareSecurityCipher(), new MtkSejSecurityCipher(extension)];
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
                else if (a[0] == "erase") ext.Erase(region, start, count, ct);
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
    }
}
