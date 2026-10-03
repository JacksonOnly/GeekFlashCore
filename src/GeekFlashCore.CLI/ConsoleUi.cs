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
    private readonly ConsoleInputReader _input;
    public bool AllowPrompts { get; set; } = true;

    public ConsoleUi(TextReader? input = null) => _input = new ConsoleInputReader(input);

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

    public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken,
        string? defaultValue = null, bool secret = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AllowPrompts || !_input.CanPrompt) throw new InvalidOperationException(Strings.Cli_InputUnavailable);
        lock (_gate)
        {
            ClearProgressUnsafe();
            Console.Write($"{prompt}{(defaultValue is null ? "" : $" [{defaultValue}]")}: ");
        }
        string? value = await _input.ReadAsync(secret, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(value) ? defaultValue ?? string.Empty : value;
    }

    public async Task<string?> AskOptionalAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AllowPrompts || !_input.CanPrompt) return null;
        string value = await AskAsync(prompt, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public Task<string?> ReadInputAsync(CancellationToken cancellationToken) => _input.ReadAsync(false, cancellationToken);

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
            var names = SaharaIdentityDisplay.Names(info);
            Console.WriteLine(Strings.FormatCli_InfoProtocol("QualcommEdl"));
            Console.WriteLine(Strings.FormatCli_InfoVendor(info.Vendor));
            Console.WriteLine(Strings.FormatCli_InfoHardware(
                names.Oem,
                names.Soc,
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
                    SaharaIdentityDisplay.Hex(sahara.Serial),
                    SaharaIdentityDisplay.Hex(sahara.SblVersion)));
                Console.WriteLine(Strings.FormatCli_InfoSaharaPkHash(
                    sahara.CaHash is { Length: > 0 } hash ? Convert.ToHexString(hash.Span) : unknown));
                Console.WriteLine(Strings.FormatCli_InfoHardwareIds(
                    SaharaIdentityDisplay.Hex(hw?.MsmId),
                    SaharaIdentityDisplay.Hex(hw?.OemId, 4),
                    SaharaIdentityDisplay.Hex(hw?.ModelId, 4),
                    SaharaIdentityDisplay.Hex(hw?.AntiRollbackVersion),
                    SaharaIdentityDisplay.Hex(hw?.SocHwVersion)));
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
                        SaharaIdentityDisplay.Hex(basic.SerialNumber),
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
