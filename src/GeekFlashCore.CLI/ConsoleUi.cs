using System.Text;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using Serilog;
using Serilog.Events;

namespace GeekFlashCore.CLI;

internal sealed class ConsoleUi
{
    private readonly object _gate = new();
    private int _progressRows;
    private readonly ProgressDisplay _progress = new(TimeProvider.System);

    public void WriteBanner() => WriteLine(Strings.Cli_Banner);

    public void WriteLine(string value)
    {
        lock (_gate)
        {
            ClearProgressUnsafe();
            Console.WriteLine(value);
        }
    }

    public void Write(string value)
    {
        lock (_gate)
        {
            ClearProgressUnsafe();
            Console.Write(value);
        }
    }

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
            bool redirected = Console.IsOutputRedirected;
            int terminalWidth;
            try { terminalWidth = Console.WindowWidth; if (terminalWidth <= 0) terminalWidth = 100; }
            catch (IOException) { terminalWidth = 100; }
            ProgressFrame? frame = _progress.TryRender(record, redirected, redirected ? 120 : terminalWidth);
            if (frame is null) return;
            if (redirected) { Console.WriteLine(frame.Line); return; }
            ClearProgressUnsafe();
            var (text, rows) = ProgressDisplay.Wrap(frame.Line, terminalWidth);
            Console.Write(text);
            _progressRows = rows;
            if (frame.Completed) { Console.WriteLine(); _progressRows = 0; }
        }
    }

    public void PrintTargetInfo(QcomTargetInfo? info)
    {
        if (info is null) return;
        lock (_gate)
        {
            ClearProgressUnsafe();
            string unknown = Strings.Cli_UnknownValue;
            Console.WriteLine(Strings.FormatCli_InfoProtocol("QualcommEdl"));
            Console.WriteLine(Strings.FormatCli_InfoVendor(info.Vendor));
            Console.WriteLine(Strings.FormatCli_InfoHardware(
                info.OemName ?? unknown,
                info.SocName ?? unknown,
                info.SecureBoot));
            if (info.Sahara is not null)
            {
                var sahara = info.Sahara;
                var hw = sahara.MsmHwInfo;
                Console.WriteLine(Strings.FormatCli_InfoSahara(
                    sahara.Version,
                    sahara.MinimumVersionSupported,
                    sahara.MaximumPacketSizeSupported,
                    sahara.Mode));
                Console.WriteLine(Strings.FormatCli_InfoSaharaIdentity(
                    sahara.Serial?.ToString() ?? unknown,
                    sahara.SblVersion?.ToString() ?? unknown,
                    sahara.CaHash?.Length ?? 0));
                Console.WriteLine(Strings.FormatCli_InfoHardwareIds(
                    hw?.MsmId?.ToString() ?? unknown,
                    hw?.OemId?.ToString() ?? unknown,
                    hw?.ModelId?.ToString() ?? unknown,
                    hw?.AntiRollbackVersion?.ToString() ?? unknown,
                    hw?.SocHwVersion?.ToString() ?? unknown));
            }
            if (info.Firehose is not null)
            {
                var firehose = info.Firehose;
                var config = firehose.Configuration;
                Console.WriteLine(Strings.FormatCli_InfoFirehose(
                    firehose.TargetName ?? unknown,
                    firehose.UfsName ?? unknown,
                    config?.Storage,
                    config?.SectorSizeInBytes,
                    config?.MaxPayloadSizeToTargetInBytes));
                if (firehose.BasicDevCharacteristics is { } basic)
                    Console.WriteLine(Strings.FormatCli_InfoFirehoseDevice(
                        basic.ChipName ?? unknown,
                        basic.SerialNumber,
                        basic.BuildDate == default
                            ? unknown
                            : basic.BuildDate.ToString(
                                "yyyy-MM-dd HH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture),
                        basic.SupportedFunctions.Count));
                foreach (var storage in firehose.StorageInfos)
                    Console.WriteLine(Strings.FormatCli_InfoStorage(
                        storage.Storage,
                        storage.PhysicalPartitionNumber,
                        storage.BlockCount,
                        storage.BlockSizeInBytes,
                        storage.CapacityInBytes is { } capacity ? FormatBytes(capacity) : unknown));
            }
        }
    }

    private void ClearProgressUnsafe()
    {
        if (_progressRows == 0) return;
        Console.Write("\r\u001b[2K");
        for (int row = 1; row < _progressRows; row++) Console.Write("\u001b[1A\r\u001b[2K");
        _progressRows = 0;
    }

    internal static string FormatBytes(decimal bytes)
    {
        string[] units = ["Bytes", "KB", "MB", "GB", "TB", "PB", "EB"];
        decimal value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{bytes.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} Bytes ({value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]})";
    }

    public static string ReadLine(string? fallback = null) => Console.ReadLine() is { } value && value.Length > 0 ? value : fallback ?? string.Empty;
    public void LogException(Exception exception) => Log.Error(exception, Strings.Cli_LogCommandFailed, exception.Message);

    internal void WriteLog(LogEvent logEvent)
    {
        lock (_gate)
        {
            ClearProgressUnsafe();
            string line = $"[{logEvent.Timestamp.LocalDateTime:HH:mm:ss} {FormatLevel(logEvent.Level)}] {RenderMessage(logEvent)}";
            if (logEvent.Exception is not null)
                line += Environment.NewLine + logEvent.Exception;
            Console.Error.WriteLine(line);
        }
    }

    private static string FormatLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => level.ToString().ToUpperInvariant()
    };

    private static string RenderMessage(LogEvent logEvent)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        new Serilog.Formatting.Display.MessageTemplateTextFormatter("{Message:lj}", System.Globalization.CultureInfo.InvariantCulture).Format(logEvent, writer);
        return writer.ToString();
    }
}
