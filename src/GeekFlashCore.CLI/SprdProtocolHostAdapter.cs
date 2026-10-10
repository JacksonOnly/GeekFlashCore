using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Sprd;
using GeekFlashCore.Protocol.Sprd.Abstractions;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.CLI;

internal static class SprdProtocolHostAdapter
{
    public static ProtocolRegistration Registration { get; } = new(
        ProtocolType.Sprd,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sprd", "spreadtrum", "unisoc" },
        "Sprd", Strings.Cli_SprdWaiting, new SprdDeviceIdentify(), Create, [],
        static (protocol, ui) => ui.WriteLine(Strings.FormatCli_SprdInfo(((ISprdProtocol)protocol).SessionState,
            ((ISprdProtocol)protocol).TargetInfo?.EntryStage, ((ISprdProtocol)protocol).TargetInfo?.PartitionTableSource?.ToString() ?? "-")), new CommandSet());

    internal static SprdProtocolOptions Options(CliOptions options) => new()
    {
        EntryStage = options.SprdEntry ?? SprdBootStage.Auto,
        TransferBlockSize = options.SprdBlockSize ?? 4096,
        PartitionTableSizeUnitBytes = options.SprdPartitionUnit,
        PartitionLengthEncoding = options.SprdLengthEncoding ?? SprdPartitionLengthEncoding.UInt32,
        PadOddPayloads = options.SprdPadOdd,
        DisableTranscode = options.SprdDisableTranscode,
        EntryTranscodeDisabled = options.SprdEntryTranscodeDisabled,
        PartitionTableSource = options.SprdPartitionSource ?? SprdPartitionTableSource.Auto,
        GptSectorSize = options.SprdSectorSize == 0 ? null : options.SprdSectorSize,
        GptReadBytes = options.SprdGptBytes ?? 32 * 1024,
        RawDataMode = options.SprdRawMode ?? SprdRawDataMode.Disabled,
        RawDataFlushSizeBytes = options.SprdRawFlush,
        RawDataUsbPacketSize = options.SprdRawUsbPacket,
        CommandTimeoutMilliseconds = options.ReadTimeout,
        ConnectTimeoutMilliseconds = options.HasExplicitConnectTimeout ? options.ConnectTimeout : 120_000,
        ResourceRequestTimeoutMilliseconds = options.ResourceTimeout ?? 30_000
    };

    internal static void ValidateOptions(CliOptions options)
    {
        Options(options).Validate();
        if (options.SprdPac is not null && !options.SprdPacPrepared &&
            (options.Loader is not null || options.SprdFdl2 is not null || options.SprdFdl1Address is not null || options.SprdFdl2Address is not null))
            throw new ArgumentException(Strings.Cli_SprdPacConflict);
        if (options.Usb is not null && options.SprdRawMode is SprdRawDataMode.Version1 or SprdRawDataMode.Version2 && options.SprdRawUsbPacket is null)
            throw new ArgumentException(Strings.Cli_SprdRawPacketRequired);
        if (options.Command == "sprd-chip-uid" && options.Arguments.Length != 0)
            throw new CommandUsageException("sprd-chip-uid");
        string? first = options.Arguments.FirstOrDefault()?.ToLowerInvariant();
        if (options.Command == "lp" && first is not ("info" or "help") || options.Command == "reboot" && first == "download" ||
            options.Command is "read" or "write" or "erase" && first?.Equals("sector", StringComparison.OrdinalIgnoreCase) == true ||
            options.Command is "write" or "erase" && first?.Contains('/') == true ||
            options.Command == "partitions" && first is not (null or "all" or "0"))
            throw new NotSupportedException(Strings.Cli_SprdCommandUnsupported);
        int lunIndex = options.Command is "browse" or "ls" ? 1 : options.Command == "read" && first?.Contains('/') == true ? 2 :
            options.Command == "lp" && first == "info" ? 2 : -1;
        if (lunIndex >= 0 && options.Arguments.Length > lunIndex && CommandSyntax.Lun(options.Arguments[lunIndex]) != 0)
            throw new NotSupportedException(Strings.Cli_SprdCommandUnsupported);
        if (options.Command is "partitions" or "read" or "write" or "erase" && options.SprdPartitionUnit is null &&
            options.SprdPartitionSource == SprdPartitionTableSource.Native)
            throw new ArgumentException(Strings.Cli_SprdUnitRequired);
        if (options.Digest is not null || options.VipSigned is not null || options.VipChained is not null ||
            options.OplusDigest is not null || options.OplusSign is not null || options.OplusResume || options.HasExplicitOplusMode ||
            options.Vendor != GeekFlashCore.Protocol.Qcom.Abstractions.QcomVendorKind.Auto || options.AuthenticationKind is not null ||
            options.OnePlusProjectId is not null || options.MtkPreloader is not null || options.MtkDaMode is not null ||
            options.MtkAuthenticationFile is not null || options.MtkCertificateFile is not null || options.MtkNandWrite ||
            options.MtkNandCapacity is not null || options.MtkIoT || options.HasExplicitMtkExtensionAbi || options.MtkPmtLayout is not null ||
            options.MtkNorEraseBlockSize != 0 || options.MtkSejBase != 0 || options.MtkTzccBase != 0 || options.MtkSsrBase != 0 ||
            options.MtkUfsRpmbBlocks.Count != 0 || options.UsbInterface != -1 || options.UsbControlInterface is not null || options.UsbAlternateSetting != 0)
            throw new ArgumentException(Strings.Cli_SprdOptionConflict);
        if (options.NonInteractive && options.SprdPac is null && options.Command is not ("help" or "devices" or "firmware" or "browse-image"))
        {
            var stage = options.SprdEntry ?? SprdBootStage.Auto;
            if (stage == SprdBootStage.BootRom && (string.IsNullOrWhiteSpace(options.Loader) || options.SprdFdl1Address is null) ||
                stage is SprdBootStage.BootRom or SprdBootStage.Fdl1 && (string.IsNullOrWhiteSpace(options.SprdFdl2) || options.SprdFdl2Address is null))
                throw new ArgumentException(Strings.Cli_SprdLoadersRequired);
        }
    }
    private static IProtocol Create(ProtocolHostContext context, ITransport transport)
    {
        // Automatic discovery must validate against the selected protocol, not the initial default.
        var options = context.Options with { Protocol = "sprd" };
        options.Validate();
        return new SprdProtocol(transport, Options(options), new LoaderProvider(context.Ui, options), leaveTransportOpen: true);
    }

    private sealed class LoaderProvider(ConsoleUi ui, CliOptions options) : ISprdLoaderProvider
    {
        public async ValueTask<SprdConnectionResources> GetLoadersAsync(SprdBootStage stage, CancellationToken cancellationToken)
        {
            SprdLoader? first = null, second = null;
            try
            {
                first = stage == SprdBootStage.BootRom ?
                    await Select("FDL1", options.Loader, options.SprdFdl1Address, cancellationToken).ConfigureAwait(false) : null;
                second = stage != SprdBootStage.Fdl2 ?
                    await Select("FDL2", options.SprdFdl2, options.SprdFdl2Address, cancellationToken).ConfigureAwait(false) : null;
                return new(first, second, ownsSources: true);
            }
            catch
            {
                try { (first?.Source as IDisposable)?.Dispose(); }
                finally { (second?.Source as IDisposable)?.Dispose(); }
                throw;
            }
        }
        private async Task<SprdLoader> Select(string stage, string? path, uint? address, CancellationToken ct)
        {
            if (path?.Contains("::", StringComparison.Ordinal) != true)
                path = await ui.SelectFileAsync(Strings.FormatCli_SprdLoaderPrompt(stage), path,
                    Strings.Cli_SprdLoaderMissing, ct).ConfigureAwait(false);
            if (address is null)
            {
                if (!ui.CanPrompt) throw new ArgumentException(Strings.Cli_SprdLoadersRequired);
                address = checked((uint)CommandSyntax.Number(await ui.AskAsync(Strings.FormatCli_SprdAddressPrompt(stage), ct).ConfigureAwait(false)));
            }
            return new(FirmwareLoaderSource.Open(path!, ct), address.Value);
        }
    }
    private sealed class CommandSet : IProtocolCommandSet
    {
        public bool Handles(string command) => command.Equals("sprd-chip-uid", StringComparison.OrdinalIgnoreCase);
        public CliOptions Normalize(CliOptions options) => options;
        public void Validate(CliOptions options) => ValidateOptions(options);
        public bool RequiresConnection(string command) => true;
        public void ValidateAvailability(IProtocol protocol, string command)
        {
            if (!protocol.IsConnected && command is not ("connect" or "help" or "devices")) throw new InvalidOperationException(Strings.Cli_ReconnectRequired);
        }
        public void PrintHelp(IProtocol protocol, ConsoleUi ui)
        { ui.WriteLine(Strings.Cli_HelpSprd); ui.WriteLine(Strings.Cli_HelpSprdPac); ui.WriteLine(Strings.Cli_HelpSprdBrowser); }
        public Task<int> ExecuteAsync(IProtocol protocol, CliOptions options, ConsoleUi ui, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            if (!Handles(options.Command) || options.Arguments.Length != 0) throw new CommandUsageException("sprd-chip-uid");
            ui.WriteLine(Strings.FormatCli_SprdChipUid(Convert.ToHexString(((ISprdProtocol)protocol).ReadChipUid(ct))));
            return Task.FromResult(0);
        }
    }
}
