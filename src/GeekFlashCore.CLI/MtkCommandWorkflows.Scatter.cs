using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Extensions;

namespace GeekFlashCore.CLI;

internal static partial class MtkCommandWorkflows
{
    private static async Task ScatterAsync(IMtkProtocol protocol, MtkCommandRequest request,
        ConsoleUi ui, IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        var command = MtkScatterCommands.OnlineArguments(request);
        string path = (await ui.SelectFileAsync(Strings.Cli_MtkScatterFilePrompt, command.Path,
            Strings.Cli_MtkScatterFileInvalid, ct).ConfigureAwait(false))!;
        path = Path.GetFullPath(path);
        string text = MtkScatterCommands.ReadText(path, ct);
        var service = new MtkScatterService(protocol);
        var plan = MtkScatterCommands.SelectDownloads(service.Plan(MtkScatterParser.Parse(text), ct), MtkScatterCommands.SelectedNames(request));
        var selected = plan.Partitions.Where(p => p.Download).ToArray();
        ui.WriteLine(Strings.FormatCli_MtkScatterSummary(protocol.GetStorageInfo().Kind, selected.Length));
        foreach (var part in command.Action == "plan" ? plan.Partitions : selected)
            ui.WriteLine(Strings.FormatCli_MtkScatterRange(part.Name, part.Range.RegionId, part.Range.Offset, part.Range.Length, part.FileName ?? "-"));
        if (command.Action == "plan") return;
        if (selected.Length == 0) throw new MtkResourceException("scatter no downloadable image");
        if (!ui.CanPrompt) throw new InvalidOperationException(Strings.Cli_InputUnavailable);
        if (command.Action == "update" && protocol.GetStorageInfo().Kind is not (MtkStorageKind.Emmc or MtkStorageKind.Ufs) &&
            request.Value("--partitions") is not null)
            throw new MtkCapabilityException("selected partitions with native XML FLASH-UPDATE");
        string directory = ConsolePath.Normalize(command.Images) ?? Path.GetDirectoryName(path)!;
        string backup = BackupPath(command.Backup, "scatter", true);
        var store = new MtkScatterDirectoryStore(directory, backup);
        ui.WriteLine(Strings.Cli_MtkScatterPreflight);
        service.ValidateImages(plan, store.OpenImage, ct);
        bool rebuild = false;
        if (protocol.GetStorageInfo().Kind is MtkStorageKind.Emmc or MtkStorageKind.Ufs)
        {
            var differences = service.CompareLayout(plan, ct);
            rebuild = differences.Count != 0;
            if (rebuild)
            {
                ui.WriteLine(Strings.Cli_MtkScatterBackupGpt);
                service.BackupPartitionTable(plan, store, ct);
                ui.WriteLine(Strings.FormatCli_MtkScatterLayoutChanged(string.Join(", ", differences)));
            }
        }
        ui.WriteLine(Strings.FormatCli_MtkBackupSaved(Path.GetFullPath(backup)));
        string answer = await ui.AskAsync(rebuild ? Strings.Cli_MtkScatterConfirm : Strings.Cli_MtkScatterFlashConfirm, ct, "no").ConfigureAwait(false);
        if (!answer.Equals("yes", StringComparison.OrdinalIgnoreCase)) return;
        if (command.Action == "update" && protocol.GetStorageInfo().Kind is not (MtkStorageKind.Emmc or MtkStorageKind.Ufs) &&
            protocol.DownloadAgent?.Entry.Kind == MtkDaKind.Xml)
            (protocol as IMtkNativeScatterAccess ?? throw new MtkCapabilityException("XML FLASH-UPDATE"))
                .ApplyXmlScatter(text, store.OpenImage, store, progress, ct);
        else
            service.Apply(plan, store.OpenImage, store, rebuild, progress, ct);
        ui.WriteLine(Strings.FormatCli_CommandCompleted("scatter"));
    }
}
