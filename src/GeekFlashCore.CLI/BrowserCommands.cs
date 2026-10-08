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
        if (ShowHelp(args, ui)) return;
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
        if (ShowHelp(args, ui)) return;
        using var session = await CreateDeviceSessionAsync(protocol, args, progress, ct).ConfigureAwait(false);
        await RunAsync(session, ui, ct, () => protocol.IsConnected, "/" + args[0].TrimStart('/')).ConfigureAwait(false);
    }

    internal static async Task<BrowserSession> CreateDeviceSessionAsync(IProtocol protocol, string[] args,
        IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string path = BrowserPath.Normalize("/", args[0]);
        string name = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ??
            throw new ArgumentException(Strings.Cli_BrowserInvalidPath);
        if (protocol is not IBlockDeviceProvider provider) throw new NotSupportedException(Strings.Cli_BrowserBlockDeviceRequired);
        if (protocol is IQcomProtocol qcom) FirehoseCommands.Require(qcom, "read");
        IReadOnlyList<PartitionInfo> partitions = await protocol.GetPartitionsAsync(progress, ct).ConfigureAwait(false);
        uint? lun = args.Length > 1 ? CommandSyntax.Lun(args[1]) : null;
        var matches = partitions.Where(x => StorageCommands.PartitionNameMatches(protocol, x, name) && (lun is null || StorageCommands.PartitionLun(x) == lun)).ToArray();
        if (matches.Length != 1) throw new ArgumentException(Strings.FormatCli_PartitionNotUnique(name));
        var resolver = new PartitionDeviceResolver(provider, partitions, matches[0],
            protocol is IQcomProtocol device ? () => FirehoseCommands.Require(device, "program") : null);
        int slot = args.Length > 2 ? checked((int)CommandSyntax.Number(args[2])) : 0;
        var session = new BrowserSession(slot, resolver, writableResolver: resolver);
        try { session.AddMount(name, () => resolver.Open(matches[0])); return session; }
        catch { session.Dispose(); throw; }
    }

    internal static async Task ListDeviceAsync(IProtocol protocol, string[] args, ConsoleUi ui,
        IProgress<ProgressRecord> progress, CancellationToken ct)
    {
        bool previous = ui.SuppressDiagnosticLogs;
        ui.SuppressDiagnosticLogs = true;
        try
        {
            using var session = await CreateDeviceSessionAsync(protocol, args, progress, ct).ConfigureAwait(false);
            var node = session.Resolve("/" + args[0].TrimStart('/'), ct);
            if (!node.IsDirectory) throw new IOException(Strings.Cli_BrowserNotDirectory);
            List(node, 0, ui, ct);
        }
        finally { ui.SuppressDiagnosticLogs = previous; }
    }

    internal static bool ShowHelp(string[] args, ConsoleUi ui)
    {
        if (args.Length != 1 || !args[0].Equals("help", StringComparison.OrdinalIgnoreCase)) return false;
        ui.WriteLine(Strings.Cli_BrowserHelp);
        return true;
    }

    internal static async Task RunAsync(BrowserSession session, ConsoleUi ui, CancellationToken ct, Func<bool>? connected = null,
        string? initialPath = null)
    {
        bool previous = ui.SuppressDiagnosticLogs;
        ui.SuppressDiagnosticLogs = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (connected?.Invoke() == false) throw new InvalidOperationException(Strings.Cli_ReconnectRequired);
            ui.WriteLine(Strings.Cli_BrowserHint);
            // Enter the initial mount when it is a container, otherwise show the raw image at /.
            var initial = session.Resolve(initialPath ?? session.Root.Mounts.Single().Path, ct);
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
                    if (command == "cd" && tokens.Length == 2 && tokens[1] == "..")
                    {
                        command = "up";
                        tokens = ["up"];
                    }
                    if (command is "exit" or "quit") return;
                    switch (command)
                    {
                        case "help":
                            if (tokens.Length is < 1 or > 2) throw new CommandUsageException("help [command]");
                            PrintHelp(tokens.Length == 2 ? tokens[1].ToLowerInvariant() : null, ui);
                            break;
                        case "pwd": Require(tokens, 1); ui.WriteLine(session.Current.Path); break;
                        case "up": Require(tokens, 1); session.ChangeDirectory("..", ct); List(session.Current, 0, ui, ct); break;
                        case "cd": Require(tokens, 2); session.ChangeDirectory(tokens[1], ct); List(session.Current, 0, ui, ct); break;
                        case "ls":
                            if (tokens.Length is < 1 or > 3) throw new CommandUsageException("ls [path] [page]");
                            List(tokens.Length > 1 ? session.Resolve(tokens[1], ct) : session.Current,
                                tokens.Length > 2 ? checked((int)CommandSyntax.Number(tokens[2])) : 0, ui, ct);
                            break;
                        case "read":
                            Require(tokens, 3);
                            await ExportAsync(session, session.Resolve(tokens[1], ct), tokens[2], ui, ct).ConfigureAwait(false);
                            break;
                        case "write":
                            Require(tokens, 3, "write <partition-path> <image>");
                            await session.WritePartitionAsync(tokens[1], tokens[2], ui, ct).ConfigureAwait(false);
                            List(session.Current, 0, ui, ct);
                            break;
                        case "lp":
                            await LpCommands.ExecuteMountedAsync(session, tokens[1..], ui, ct).ConfigureAwait(false);
                            break;
                        case "print":
                            Require(tokens, 2);
                            ui.WriteLine(session.ReadText(session.Resolve(tokens[1], ct), ct));
                            break;
                        case "find":
                            await FindAsync(session, tokens, ui, ct, connected).ConfigureAwait(false);
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

    private static async Task FindAsync(BrowserSession session, string[] tokens, ConsoleUi ui, CancellationToken ct,
        Func<bool>? connected)
    {
        bool all = tokens.Length > 1 && tokens[1].Equals("--all", StringComparison.OrdinalIgnoreCase);
        int first = all ? 2 : 1;
        int arguments = tokens.Length - first;
        if (arguments is < 1 or > 3) throw new CommandUsageException("find [--all] <pattern> [path] [output-directory]");
        string start = arguments > 1 ? tokens[first + 1] : ".";
        int found = 0;
        using var search = ui.BeginSearch(ct);
        try
        {
            ui.WriteLine(Strings.Cli_BrowserSearching);
            BrowserNode root = session.Resolve(start, search.Token);
            var outputs = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var match in session.Find(tokens[first], start, search.Token))
            {
                search.Token.ThrowIfCancellationRequested();
                ui.WriteLine(match.Path);
                found++;
                if (arguments > 2)
                {
                    string relative = root.IsDirectory ? match.Path[(root.Path.TrimEnd('/').Length + 1)..] : match.Name;
                    string destination = BrowserPath.ExportDestination(ConsolePath.Normalize(tokens[first + 2])!, relative);
                    if (!outputs.Add(destination)) throw new IOException(Strings.Cli_BrowserOutputCollision);
                    BrowserSession.RejectLinkedAncestors(destination);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await ExportAsync(session, match, destination, ui, search.Token).ConfigureAwait(false);
                }
                if (!all) break;
            }
            search.Token.ThrowIfCancellationRequested();
            ui.WriteLine(Strings.FormatCli_BrowserFound(found));
        }
        catch (OperationCanceledException) when (search.Token.IsCancellationRequested && !ct.IsCancellationRequested &&
            connected?.Invoke() != false)
        {
            ui.WriteLine(Strings.FormatCli_BrowserSearchCancelled(found));
        }
        finally { session.SetOperationToken(ct); }
    }

    private static async Task ExportAsync(BrowserSession session, BrowserNode node, string output, ConsoleUi ui, CancellationToken ct)
    {
        await session.ExportAsync(node, output, ct, new ImmediateProgress<ProgressRecord>(ui.Report)).ConfigureAwait(false);
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

    private static void PrintHelp(string? command, ConsoleUi ui)
    {
        if (command is null) { ui.WriteLine(Strings.Cli_BrowserHelp); return; }
        if (command == "lp") { LpCommands.PrintHelp(ui); return; }
        string usage = command switch
        {
            "read" => Strings.Cli_BrowserReadHelp,
            "write" => Strings.Cli_BrowserWriteHelp,
            "find" => Strings.Cli_BrowserFindHelp,
            "ls" => "ls [path] [page]",
            "cd" => "cd <path> | cd ..", "up" => "up", "pwd" => "pwd", "print" => "print <path>\n" + Strings.Cli_BrowserPrintTooLarge,
            "exit" or "quit" => "exit", _ => throw new ArgumentException(Strings.FormatCli_UnknownCommand(command))
        };
        ui.WriteLine(usage);
    }

    private static void Require(string[] tokens, int count, string? usage = null)
    {
        if (tokens.Length != count) throw new CommandUsageException(usage ?? tokens[0] switch
        {
            "read" => "read <path> <file>", "print" => "print <path>", "cd" => "cd <path>", _ => tokens[0]
        });
    }
}
