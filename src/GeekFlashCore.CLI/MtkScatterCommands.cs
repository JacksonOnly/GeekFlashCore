using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Extensions;

namespace GeekFlashCore.CLI;

internal static class MtkScatterCommands
{
    internal const string OnlineUsage = "mtk scatter [file] | plan file | flash|update file [image-directory] [--backup directory] [--partitions name,...]";
    internal static (string Action, string? Path, string? Images, string? Backup) OnlineArguments(MtkCommandRequest request)
    {
        string[] a = request.Arguments;
        if (a.Length == 0) return ("flash", null, null, request.Value("--backup"));
        if (a[0] is not ("plan" or "flash" or "update"))
        {
            if (a.Length != 1) throw new CommandUsageException(OnlineUsage);
            return ("flash", a[0], null, request.Value("--backup"));
        }
        if (a[0] == "plan" && a.Length != 2 || a[0] != "plan" && a.Length is < 1 or > 4 ||
            a.Length == 4 && request.Value("--backup") is not null)
            throw new CommandUsageException(OnlineUsage);
        return (a[0], a.ElementAtOrDefault(1), a.ElementAtOrDefault(2), a.ElementAtOrDefault(3) ?? request.Value("--backup"));
    }
    internal static string[]? SelectedNames(MtkCommandRequest request)
    {
        if (request.Value("--partitions") is not { } value) return null;
        var names = value.Split(',');
        if (names.Length is < 1 or > 4096 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length ||
            names.Any(n => n.Length is < 1 or > 128 || n.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))))
            throw new CommandUsageException(OnlineUsage);
        return names;
    }
    internal static void ValidateOnline(MtkCommandRequest request)
    {
        request.Allow("--backup", "--partitions");
        _ = OnlineArguments(request);
        _ = SelectedNames(request);
        if (request.Arguments.Any(string.IsNullOrWhiteSpace)) throw new CommandUsageException(OnlineUsage);
    }
    internal static MtkScatterPlan SelectDownloads(MtkScatterPlan plan, string[]? names)
    {
        if (names is null) return plan;
        foreach (string name in names)
            if (!plan.Partitions.Any(p => p.Download && p.FileName is not null && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new MtkResourceException("scatter downloadable partition: " + name);
        return plan with { Partitions = plan.Partitions.Select(p => p with
            { Download = p.Download && names.Contains(p.Name, StringComparer.OrdinalIgnoreCase) }).ToArray() };
    }
    internal static string ReadText(string path, CancellationToken ct)
    {
        using var reader = new StreamReader(File.OpenRead(path), detectEncodingFromByteOrderMarks: true);
        var text = new System.Text.StringBuilder();
        char[] buffer = System.Buffers.ArrayPool<char>.Shared.Rent(16384);
        try
        {
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
            {
                ct.ThrowIfCancellationRequested();
                if (text.Length > MtkScatterParser.MaximumCharacters - count) throw new MtkResourceException("scatter text length");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
        finally { System.Buffers.ArrayPool<char>.Shared.Return(buffer, true); }
    }
    internal static bool IsOffline(CliOptions o) => o.Command == "mtk-scatter" && o.Arguments.FirstOrDefault() is "to-gpt" or "from-gpt";
    internal const string ConversionUsage = "mtk-scatter to-gpt <scatter> <output-prefix> <emmc|ufs> <block-size> <user-capacity> | mtk-scatter from-gpt <gpt> <scatter-output> <emmc|ufs> <block-size> <user-capacity> <platform>";
    internal static void Validate(string[] a)
    {
        try
        {
            if (a.Length == 0 || a[0] is not ("to-gpt" or "from-gpt") ||
                a.Length != (a[0] == "to-gpt" ? 6 : 7) || a[3] is not ("emmc" or "ufs") ||
                CommandSyntax.Number(a[4]) is not (512 or 4096) || CommandSyntax.Number(a[5]) == 0)
                throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        { throw new CommandUsageException(ConversionUsage); }
    }
    internal static async Task ExecuteOfflineAsync(CliOptions o, ConsoleUi ui, CancellationToken ct)
    {
        string[] a = o.Arguments; Validate(a); ct.ThrowIfCancellationRequested();
        var kind = a[3] == "ufs" ? MtkStorageKind.Ufs : MtkStorageKind.Emmc; uint id = kind == MtkStorageKind.Ufs ? 3u : 8u;
        var storage = new MtkStorageInfo(kind, [new(kind, id, "USER", CommandSyntax.Number(a[5]), checked((int)CommandSyntax.Number(a[4])))], id, 0);
        string input = ConsolePath.Normalize(a[1])!, output = ConsolePath.Normalize(a[2])!;
        if (a[0] == "to-gpt")
        {
            var manifest = MtkScatterParser.Parse(ReadText(input, ct));
            var images = MtkScatterGptConverter.ToGpt(manifest, storage);
            await AtomicReadOutput.WriteAsync(output + ".pgpt.bin", s => s.WriteAsync(images.Primary, ct).AsTask(), ct);
            await AtomicReadOutput.WriteAsync(output + ".sgpt.bin", s => s.WriteAsync(images.Backup, ct).AsTask(), ct);
        }
        else
        {
            using var source = File.OpenRead(input);
            if (source.Length is <= 0 or > 4194304) throw new MtkResourceException("GPT input length");
            byte[] image = new byte[(int)source.Length];
            await source.ReadExactlyAsync(image, ct);
            string scatter = MtkScatterGptConverter.FromGpt(image, storage, a[6]);
            await AtomicReadOutput.WriteAsync(output, async s =>
            { using var writer = new StreamWriter(s, new System.Text.UTF8Encoding(false), leaveOpen: true); await writer.WriteAsync(scatter.AsMemory(), ct); await writer.FlushAsync(ct); }, ct);
        }
        ui.WriteLine(Strings.Cli_MtkScatterConverted);
    }
}
