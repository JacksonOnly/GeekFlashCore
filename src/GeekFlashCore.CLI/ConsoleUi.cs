using System.Text;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using Serilog;

namespace GeekFlashCore.CLI;

internal sealed class ConsoleUi
{
    private readonly object _gate = new();
    private int _progressWidth;

    public void WriteBanner() => Console.WriteLine("GeekFlashCore CLI 0.1");

    public string Ask(string prompt, string? defaultValue = null, bool secret = false)
    {
        Console.Write($"{prompt}{(defaultValue is null ? "" : $" [{defaultValue}]")}: ");
        if (!secret) return ReadLine(defaultValue);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return buffer.ToString(); }
            if (key.Key == ConsoleKey.Backspace && buffer.Length > 0) { buffer.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
    }

    public string? AskOptional(string prompt) => Ask(prompt, null) switch { "" => null, var value => value };

    public bool Confirm(string prompt, bool defaultValue = false)
    {
        string suffix = defaultValue ? "Y/n" : "y/N";
        string value = Ask($"{prompt} ({suffix})");
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    public void Report(ProgressRecord record)
    {
        lock (_gate)
        {
            long total = Math.Max(1, record.Total);
            double ratio = Math.Clamp((double)record.Current / total, 0, 1);
            int terminalWidth;
            try { terminalWidth = Console.WindowWidth; }
            catch (IOException) { terminalWidth = 100; }
            int width = Math.Max(10, Math.Min(36, terminalWidth - 34));
            int filled = (int)(ratio * width);
            string bar = new string('#', filled) + new string('-', width - filled);
            string line = $"\r{record.Label,-24} [{bar}] {ratio:P0}";
            if (line.Length < _progressWidth) line += new string(' ', _progressWidth - line.Length);
            _progressWidth = line.Length;
            Console.Write(line);
            if (record.Current >= record.Total) { Console.WriteLine(); _progressWidth = 0; }
        }
    }

    public void PrintTargetInfo(QcomTargetInfo? info)
    {
        if (info is null) return;
        Console.WriteLine($"Protocol: {info.Vendor}");
        Console.WriteLine($"OEM: {info.OemName ?? "unknown"}, SoC: {info.SocName ?? "unknown"}, SecureBoot: {info.SecureBoot}");
        if (info.Sahara is not null) Console.WriteLine($"Sahara: v{info.Sahara.Version}, mode={info.Sahara.Mode}, packet={info.Sahara.MaximumPacketSizeSupported}");
        if (info.Firehose is not null)
        {
            var config = info.Firehose.Configuration;
            Console.WriteLine($"Firehose: storage={config?.Storage}, sector={config?.SectorSizeInBytes}, payload={config?.MaxPayloadSizeToTargetInBytes}");
            foreach (var storage in info.Firehose.StorageInfos)
                Console.WriteLine($"  {storage.Storage} lun={storage.PhysicalPartitionNumber} blocks={storage.BlockCount} size={storage.BlockSizeInBytes} capacity={storage.CapacityInBytes}");
        }
    }

    public static string ReadLine(string? fallback = null) => Console.ReadLine() is { } value && value.Length > 0 ? value : fallback ?? string.Empty;
    public void LogException(Exception exception) => Log.Error(exception, "Command failed: {Message}", exception.Message);
}
