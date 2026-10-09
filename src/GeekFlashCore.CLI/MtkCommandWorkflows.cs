using System.Globalization;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Extensions;

namespace GeekFlashCore.CLI;

internal static partial class MtkCommandWorkflows
{
    internal static string BackupPath(string? requested, string operation, bool directory = false)
    {
        string path = ConsolePath.Normalize(requested) ?? Path.Combine(Environment.CurrentDirectory, "backups",
            $"mtk-{operation}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}" + (directory ? "" : ".bin"));
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(directory ? path : Path.GetDirectoryName(path)!);
        return path;
    }
    internal static async Task<MtkFlashRange> PartitionAsync(IMtkProtocol protocol, string[] names, CancellationToken ct, bool userOnly = false)
    {
        var partitions = await protocol.GetPartitionsAsync(ct: ct).ConfigureAwait(false);
        uint userRegion = userOnly ? protocol.GetStorageInfo().UserRegionId : 0;
        var candidates = partitions.Where(p => names.Any(n => StorageCommands.PartitionNameMatches(protocol, p, n)) &&
            (!userOnly || p.Metadata?.TryGetValue("PhysicalPartitionNumber", out string? physical) == true &&
                uint.TryParse(physical, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) && id == userRegion)).ToArray();
        if (candidates.Length != 1 || candidates[0].Offset is not { } offset || candidates[0].Length is not { } length ||
            candidates[0].Metadata?.TryGetValue("PhysicalPartitionNumber", out string? id) != true || !uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out uint region))
            throw new MtkResourceException("unique partition: " + string.Join('/', names));
        return new(region, offset, length);
    }
    private static async Task<bool> ConfirmAsync(ConsoleUi ui, string operation, string backup, CancellationToken ct)
    {
        ui.WriteLine(Strings.FormatCli_MtkBackupSaved(backup));
        return (await ui.AskAsync(Strings.FormatCli_MtkWriteConfirm(operation), ct, "no").ConfigureAwait(false)).Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
    internal static async Task<bool> TryExecuteAsync(IMtkProtocol protocol, CliOptions options, ConsoleUi ui, IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        if (!MtkCommandRequest.TryValidate(options.Command, options.Arguments)) return false;
        var request = MtkCommandRequest.Parse(options.Arguments); string[] a = request.Arguments;
        switch (options.Command)
        {
            case "mtk-seccfg": await SeccfgAsync(protocol, options, request, ui, ct); return true;
            case "mtk-slot": await SlotAsync(protocol, request, ui, ct); return true;
            case "mtk-rpmb": await RpmbAsync(protocol, options, request, ui, ct); return true;
            case "mtk-rpmb-lock": await RpmbLockAsync(protocol, options, request, ui, ct); return true;
            case "mtk-key":
                string bits = a[0].Equals("Fde", StringComparison.OrdinalIgnoreCase) || a[0].Equals("AesImageEncryption", StringComparison.OrdinalIgnoreCase) ? "128" : "256";
                a = ["id", a[0], bits, a[1]]; break;
            case "mtk-partition":
                long maximum = request.Value("--maximum") is { } value ? checked((long)CommandSyntax.Number(value)) :
                    (await PartitionAsync(protocol, [a[1]], ct)).Length;
                a = [a[0], a[1], maximum.ToString(CultureInfo.InvariantCulture), a[2]]; break;
            case "mtk-efuse":
                await EfuseAsync(protocol, request, ui, ct); return true;
            case "mtk-scatter":
                await ScatterAsync(protocol, request, ui, progress, ct); return true;
            default: throw new CommandUsageException(options.Command);
        }
        await MtkProtocolHostAdapter.Registration.CommandSet!.ExecuteAsync(protocol, options with { Arguments = a }, ui, progress, ct).ConfigureAwait(false);
        return true;
    }
    private static async Task EfuseAsync(IMtkProtocol protocol, MtkCommandRequest request, ConsoleUi ui, CancellationToken ct)
    {
        var operations = protocol as IMtkDaStandardOperations ?? throw new MtkCapabilityException("standard eFuse operations");
        byte[] bytes = MtkProtocolHostAdapter.ReadBounded(request.Arguments[1], 0x5000);
        try
        {
            if (bytes.Length == 0 || protocol.DownloadAgent?.Entry.Kind == MtkDaKind.XFlash && bytes.Length != 0x42d4)
                throw new MtkResourceException("eFuse image length");
            using var original = operations.ReadEfuses(ct);
            string path = BackupPath(request.Value("--backup"), "efuse");
            using (var backup = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { backup.Write(original.Memory.Span); backup.Flush(true); }
            if (!await ConfirmAsync(ui, Strings.Cli_MtkEfuseWriteTitle, path, ct)) return;
            operations.WriteEfuses(new EfuseSource(bytes), ct);
            ui.WriteLine(Strings.FormatCli_CommandCompleted("efuse write"));
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    private sealed class EfuseSource(byte[] bytes) : IDataSource
    {
        public long Length => bytes.Length;
        public Stream OpenStream() => new MemoryStream(bytes, false);
        public ValueTask<Stream> OpenStreamAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(OpenStream()); }
    }
    private static async Task SeccfgAsync(IMtkProtocol protocol, CliOptions options, MtkCommandRequest request, ConsoleUi ui, CancellationToken ct)
    {
        var range = await PartitionAsync(protocol, [request.Value("--partition") ?? "seccfg"], ct, userOnly: true);
        IReadOnlyList<IMtkSecurityCipher>? ciphers = null;
        if (MtkProtocolHostAdapter.GetCapabilities(protocol).Crypto == MtkCapabilitySupport.Supported || options.MtkSejBase != 0)
        {
            var extension = MtkProtocolHostAdapter.CreateExtension(protocol, options, [], ct);
            ciphers = protocol.DownloadAgent!.Entry.Kind == MtkDaKind.XFlash
                ? [new MtkPlainSecurityCipher(), new MtkSoftwareSecurityCipher(), new MtkSejSecurityCipher(extension), new MtkSejSecurityCipher(extension, xor: true), new MtkSejSecurityCipher(extension, legacy: true)]
                : [new MtkPlainSecurityCipher(), new MtkSoftwareSecurityCipher(), new MtkSejSecurityCipher(extension)];
        }
        var service = new MtkSecurityConfigurationService(protocol, ciphers);
        using var plan = service.Plan(range, request.Arguments[0] == "lock", ct);
        string path = BackupPath(request.Value("--backup"), "seccfg");
        using var backup = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        backup.Write(plan.Original.Memory.Span); backup.Flush(true);
        if (!await ConfirmAsync(ui, "seccfg " + request.Arguments[0], path, ct)) return;
        backup.Position = 0;
        service.Apply(plan, backup, ct);
        ui.WriteLine(Strings.FormatCli_CommandCompleted("seccfg " + request.Arguments[0]));
    }
    private static async Task SlotAsync(IMtkProtocol protocol, MtkCommandRequest request, ConsoleUi ui, CancellationToken ct)
    {
        var range = await PartitionAsync(protocol, request.Value("--partition") is { } name ? [name] : ["misc", "para"], ct, userOnly: true);
        var service = new MtkBootControlService(protocol); var info = service.Read(range, ct);
        if (request.Arguments[0] is "read" or "get")
        { ui.WriteLine(Strings.FormatCli_MtkSlotInfo(info.ActiveSlot, info.Version, info.CurrentSlot)); return; }
        int slot = request.Arguments[1] is "a" or "0" ? 0 : 1;
        if (info.ActiveSlot == slot) { ui.WriteLine(Strings.FormatCli_MtkSlotWritten(slot)); return; }
        string path = BackupPath(request.Value("--backup"), "slot");
        if (!(await ui.AskAsync(Strings.FormatCli_MtkWriteConfirm("slot " + request.Arguments[1]), ct, "no")).Equals("yes", StringComparison.OrdinalIgnoreCase)) return;
        using var backup = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        service.SetActiveSlot(range, slot, backup, ct);
        ui.WriteLine(Strings.FormatCli_MtkBackupSaved(path)); ui.WriteLine(Strings.FormatCli_MtkSlotWritten(slot));
    }
}
