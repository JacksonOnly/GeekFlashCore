using System.Text;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using Serilog;
using Serilog.Events;

namespace GeekFlashCore.CLI;

internal sealed class ConsoleUi
{
    private readonly object _gate = new();
    private int _progressWidth;

    public void WriteBanner() => Console.WriteLine("GeekFlashCore CLI 0.1");

    public string Ask(string prompt, string? defaultValue = null, bool secret = false)
    {
        lock (_gate)
        {
            ClearProgressUnsafe();
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
            string line = $"{record.Label,-24} [{bar}] {ratio:P0}";
            Console.Write($"\r\u001b[2K{line}");
            _progressWidth = line.Length;
            if (record.Current >= record.Total) { Console.WriteLine(); _progressWidth = 0; }
        }
    }

    public void PrintTargetInfo(QcomTargetInfo? info)
    {
        if (info is null) return;
        lock (_gate)
        {
            ClearProgressUnsafe();
            Console.WriteLine("Protocol: QualcommEdl");
            Console.WriteLine($"Vendor: {info.Vendor}");
            Console.WriteLine($"OEM: {info.OemName ?? "unknown"}, SoC: {info.SocName ?? "unknown"}, SecureBoot: {info.SecureBoot}");
            if (info.Sahara is not null)
            {
                var sahara = info.Sahara;
                var hw = sahara.MsmHwInfo;
                Console.WriteLine($"Sahara: version={sahara.Version}, min={sahara.MinimumVersionSupported}, packet={sahara.MaximumPacketSizeSupported}, mode={sahara.Mode}");
                Console.WriteLine($"  serial={sahara.Serial?.ToString() ?? "unknown"}, sbl={sahara.SblVersion?.ToString() ?? "unknown"}, caHashLength={sahara.CaHash?.Length ?? 0}");
                Console.WriteLine($"  msmId={hw?.MsmId?.ToString() ?? "unknown"}, oemId={hw?.OemId?.ToString() ?? "unknown"}, modelId={hw?.ModelId?.ToString() ?? "unknown"}, antiRollback={hw?.AntiRollbackVersion?.ToString() ?? "unknown"}, socHwVersion={hw?.SocHwVersion?.ToString() ?? "unknown"}");
            }
            if (info.Firehose is not null)
            {
                var firehose = info.Firehose;
                var config = firehose.Configuration;
                Console.WriteLine($"Firehose: target={firehose.TargetName ?? "unknown"}, ufs={firehose.UfsName ?? "unknown"}, storage={config?.Storage}, sector={config?.SectorSizeInBytes}, payload={config?.MaxPayloadSizeToTargetInBytes}");
                if (firehose.BasicDevCharacteristics is { } basic)
                    Console.WriteLine($"  chip={basic.ChipName ?? "unknown"}, serial={basic.SerialNumber}, build={basic.BuildDate:yyyy-MM-dd HH:mm:ss}, functions={basic.SupportedFunctions.Count}");
                foreach (var storage in firehose.StorageInfos)
                    Console.WriteLine($"  {storage.Storage} lun={storage.PhysicalPartitionNumber} blocks={storage.BlockCount} size={storage.BlockSizeInBytes} capacity={storage.CapacityInBytes}");
            }
        }
    }

    private void ClearProgressUnsafe()
    {
        if (_progressWidth == 0) return;
        Console.Write("\r\u001b[2K");
        _progressWidth = 0;
    }

    public static string ReadLine(string? fallback = null) => Console.ReadLine() is { } value && value.Length > 0 ? value : fallback ?? string.Empty;
    public void LogException(Exception exception) => Log.Error(exception, "Command failed: {Message}", exception.Message);

    internal void WriteLog(LogEvent logEvent)
    {
        lock (_gate)
        {
            ClearProgressUnsafe();
            string line = $"[{logEvent.Timestamp.LocalDateTime:HH:mm:ss} {logEvent.Level.ToString().ToUpperInvariant()}] {logEvent.RenderMessage()}";
            if (logEvent.Exception is not null)
                line += Environment.NewLine + logEvent.Exception;
            Console.Error.WriteLine(line);
        }
    }
}
