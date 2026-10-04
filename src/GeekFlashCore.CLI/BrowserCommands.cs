using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.CLI;

internal static class BrowserCommands
{
    internal static async Task BrowseImageAsync(string[] args, ConsoleUi ui, CancellationToken ct)
    {
        string path = Path.GetFullPath(ConsolePath.Normalize(args[0])!);
        int slot = args.Length > 1 ? checked((int)CommandSyntax.Number(args[1])) : 0;
        using var session = new BrowserSession(slot, sourcePath: path);
        string name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") name = "image";
        session.AddMount(name, () => new FileBlockDevice(path));
        await RunAsync(session, ui, ct).ConfigureAwait(false);
    }

    internal static async Task BrowseDeviceAsync(IProtocol protocol, string[] args, ConsoleUi ui,
        IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        if (protocol is not IBlockDeviceProvider provider) throw new NotSupportedException(Strings.Cli_BrowserBlockDeviceRequired);
        if (protocol is IQcomProtocol qcom) FirehoseCommands.Require(qcom, "read");
        IReadOnlyList<PartitionInfo> partitions = await protocol.GetPartitionsAsync(progress, ct).ConfigureAwait(false);
        uint? lun = args.Length > 1 ? CommandSyntax.Lun(args[1]) : null;
        var matches = partitions.Where(x => x.Name == args[0] && (lun is null || StorageCommands.PartitionLun(x) == lun)).ToArray();
        if (matches.Length != 1) throw new ArgumentException(Strings.FormatCli_PartitionNotUnique(args[0]));
        var resolver = new PartitionResolver(provider, partitions, matches[0]);
        int slot = args.Length > 2 ? checked((int)CommandSyntax.Number(args[2])) : 0;
        using var session = new BrowserSession(slot, resolver);
        session.AddMount(args[0], () => resolver.Open(matches[0]));
        await RunAsync(session, ui, ct, () => protocol.IsConnected).ConfigureAwait(false);
    }

    internal static async Task RunAsync(BrowserSession session, ConsoleUi ui, CancellationToken ct, Func<bool>? connected = null)
    {
        bool previous = ui.SuppressDiagnosticLogs;
        ui.SuppressDiagnosticLogs = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (connected?.Invoke() == false) throw new InvalidOperationException(Strings.Cli_ReconnectRequired);
            ui.WriteLine(Strings.Cli_BrowserHelp);
            // Enter the initial mount when it is a container, otherwise show the raw image at /.
            var initial = session.Resolve(session.Root.Mounts.Single().Path, ct);
            if (initial.IsDirectory) session.ChangeDirectory(initial.Path, ct);
            List(session.Current, 0, ui, ct);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (connected?.Invoke() == false) throw new InvalidOperationException(Strings.Cli_ReconnectRequired);
                ui.Write($"{session.Current.Path}> ");
                string? line = await ui.ReadInputAsync(ct).ConfigureAwait(false);
                if (line is null) return;
                try
                {
                    string[] tokens = CliApplication.Tokenize(line);
                    if (tokens.Length == 0) continue;
                    string command = tokens[0].ToLowerInvariant();
                    if (command is "exit" or "quit") return;
                    switch (command)
                    {
                        case "help": Require(tokens, 1); ui.WriteLine(Strings.Cli_BrowserHelp); break;
                        case "pwd": Require(tokens, 1); ui.WriteLine(session.Current.Path); break;
                        case "up": Require(tokens, 1); session.ChangeDirectory("..", ct); List(session.Current, 0, ui, ct); break;
                        case "cd": Require(tokens, 2); session.ChangeDirectory(tokens[1], ct); List(session.Current, 0, ui, ct); break;
                        case "ls":
                            if (tokens.Length is < 1 or > 3) throw new CommandUsageException(Strings.Cli_BrowserHelp);
                            List(tokens.Length > 1 ? session.Resolve(tokens[1], ct) : session.Current,
                                tokens.Length > 2 ? checked((int)CommandSyntax.Number(tokens[2])) : 0, ui, ct);
                            break;
                        case "read":
                            Require(tokens, 3);
                            await ExportAsync(session, session.Resolve(tokens[1], ct), tokens[2], ui, ct).ConfigureAwait(false);
                            break;
                        case "find":
                            if (tokens.Length is < 2 or > 4) throw new CommandUsageException(Strings.Cli_BrowserHelp);
                            string start = tokens.Length > 2 ? tokens[2] : ".";
                            BrowserNode root = session.Resolve(start, ct);
                            int found = 0;
                            var outputs = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                            foreach (var match in session.Find(tokens[1], start, ct))
                            {
                                ui.WriteLine(match.Path);
                                found++;
                                if (tokens.Length > 3)
                                {
                                    string relative = root.IsDirectory ? match.Path[(root.Path.TrimEnd('/').Length + 1)..] : match.Name;
                                    string destination = BrowserPath.ExportDestination(ConsolePath.Normalize(tokens[3])!, relative);
                                    if (!outputs.Add(destination)) throw new IOException(Strings.Cli_BrowserOutputCollision);
                                    BrowserSession.RejectLinkedAncestors(destination);
                                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                                    await ExportAsync(session, match, destination, ui, ct).ConfigureAwait(false);
                                }
                            }
                            ui.WriteLine(Strings.FormatCli_BrowserFound(found));
                            break;
                        default:
                            Require(tokens, 1);
                            BrowserNode node = int.TryParse(tokens[0], out int number) && number is > 0 and <= 1_000_000
                                ? session.Current.Children(ct).ElementAtOrDefault(number - 1) ??
                                    throw new FileNotFoundException(Strings.FormatCli_BrowserNotFound(tokens[0]))
                                : session.Resolve(tokens[0], ct);
                            if (node.IsDirectory) { session.ChangeDirectory(node.Path, ct); List(session.Current, 0, ui, ct); }
                            else
                            {
                                string output = await ui.AskAsync(Strings.Cli_BrowserOutputPrompt, ct).ConfigureAwait(false);
                                if (string.IsNullOrWhiteSpace(output)) break;
                                await ExportAsync(session, node, output, ui, ct).ConfigureAwait(false);
                            }
                            break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    if (!ui.AllowPrompts || connected?.Invoke() == false) throw;
                    ui.LogException(exception);
                }
            }
        }
        finally { ui.SuppressDiagnosticLogs = previous; }
    }

    private static async Task ExportAsync(BrowserSession session, BrowserNode node, string output, ConsoleUi ui, CancellationToken ct)
    {
        await session.ExportAsync(node, output, ct,
            (copied, total) => ui.Report(new ProgressRecord(copied, total, Strings.Cli_BrowserExportProgress))).ConfigureAwait(false);
        ui.WriteLine(Strings.FormatCli_BrowserExported(node.Path, Path.GetFullPath(ConsolePath.Normalize(output)!)));
    }

    private static void List(BrowserNode node, int page, ConsoleUi ui, CancellationToken ct)
    {
        if (page > 19_999) throw new ArgumentException(Strings.Cli_BrowserLimit);
        ui.WriteLine(Strings.FormatCli_BrowserLocation(node.Path, node.Kind));
        if (node.Parent is not null) ui.WriteLine("  ..");
        int skip = checked(page * 50), index = skip;
        foreach (var child in node.Children(ct).Skip(skip).Take(51))
        {
            if (index == skip + 50) { ui.WriteLine(Strings.FormatCli_BrowserNextPage(page + 1)); break; }
            // Mount probing happens on entry, so listing LP partitions does not open every filesystem.
            ui.WriteLine($"  {++index,5}  {child.Kind,-10} {child.Size,12}  {child.Name}");
        }
    }

    private static void Require(string[] tokens, int count)
    {
        if (tokens.Length != count) throw new CommandUsageException(Strings.Cli_BrowserHelp);
    }

    private sealed class PartitionResolver(IBlockDeviceProvider provider, IReadOnlyList<PartitionInfo> partitions, PartitionInfo primary) : ILpBlockDeviceResolver
    {
        internal IReadableBlockDevice Open(PartitionInfo partition)
        {
            uint lun = StorageCommands.PartitionLun(partition);
            var descriptor = provider.GetBlockDevices().SingleOrDefault(x => x.PhysicalPartitionNumber == lun) ??
                throw new InvalidOperationException(Strings.Cli_StorageNotConfigured);
            if (partition.Offset is not { } offset || partition.Length is not { } length)
                throw new InvalidOperationException(Strings.Cli_StorageNotConfigured);
            IReadableBlockDevice source = provider.OpenBlockDevice(descriptor.Id, new BlockDeviceOpenOptions { Writable = false });
            try { return new SliceBlockDevice(source, new BlockDeviceId($"{descriptor.Id}/{partition.Name}"), offset, length, DeviceOwnership.Transfer); }
            catch { source.Dispose(); throw; }
        }

        public ValueTask<IReadableBlockDeviceLease> ResolveAsync(LpBlockDevice blockDevice, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = primary.Name == blockDevice.PartitionName ? new[] { primary } :
                partitions.Where(x => x.Name == blockDevice.PartitionName).ToArray();
            if (matches.Length != 1) throw new ArgumentException(Strings.FormatCli_PartitionNotUnique(blockDevice.PartitionName));
            return ValueTask.FromResult<IReadableBlockDeviceLease>(new BlockDeviceLease(Open(matches[0]), DeviceOwnership.Transfer));
        }
    }
}
