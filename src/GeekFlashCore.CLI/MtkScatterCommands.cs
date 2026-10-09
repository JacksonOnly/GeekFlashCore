using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Extensions;

namespace GeekFlashCore.CLI;

internal static class MtkScatterCommands
{
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
