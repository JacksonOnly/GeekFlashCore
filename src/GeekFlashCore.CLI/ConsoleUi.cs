using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using Serilog;
using Serilog.Events;
using System.Diagnostics;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;

namespace GeekFlashCore.CLI;

internal sealed class ConsoleUi
{
    private readonly object _gate = new();
    private readonly object _searchGate = new();
    private SearchCancellation? _searchCancellation;
    private int _progressRows;
    private volatile bool _suppressDiagnosticLogs;
    private readonly ProgressDisplay _progress = new(TimeProvider.System);
    private readonly ConsoleInputReader _input;
    private static readonly Serilog.Formatting.Display.MessageTemplateTextFormatter MessageFormatter =
        new("{Message:lj}", System.Globalization.CultureInfo.InvariantCulture);
    public bool AllowPrompts { get; set; } = true;
    internal bool CanPrompt => AllowPrompts && _input.CanPrompt;
    public string? LogFilePath { get; set; }
    public bool SuppressDiagnosticLogs
    {
        get => _suppressDiagnosticLogs;
        set => _suppressDiagnosticLogs = value;
    }

    public ConsoleUi(TextReader? input = null) => _input = new ConsoleInputReader(input);

    internal SearchCancellation BeginSearch(CancellationToken applicationToken)
    {
        lock (_searchGate)
        {
            if (_searchCancellation is not null) throw new InvalidOperationException(Strings.Cli_BrowserSearchAlreadyActive);
            return _searchCancellation = new SearchCancellation(this, applicationToken);
        }
    }

    internal bool TryCancelSearch()
    {
        lock (_searchGate)
        {
            if (_searchCancellation is null) return false;
            _searchCancellation.Cancel();
            return true;
        }
    }

    internal sealed class SearchCancellation(ConsoleUi owner, CancellationToken applicationToken) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        internal CancellationToken Token => _cancellation.Token;
        internal void Cancel() => _cancellation.Cancel();
        public void Dispose()
        {
            lock (owner._searchGate)
            {
                if (!ReferenceEquals(owner._searchCancellation, this)) return;
                owner._searchCancellation = null;
                _cancellation.Dispose();
            }
        }
    }

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
        long started = Stopwatch.GetTimestamp();
        Log.Debug(Strings.Cli_LogInputWait, prompt);
        try
        {
            string? value = await _input.ReadAsync(secret, cancellationToken).ConfigureAwait(false);
            Log.Debug(Strings.Cli_LogInputReceived, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return string.IsNullOrEmpty(value) ? defaultValue ?? string.Empty : value;
        }
        catch (OperationCanceledException)
        {
            Log.Debug(Strings.Cli_LogInputCancelled, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    public async Task<string?> AskOptionalAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AllowPrompts || !_input.CanPrompt) return null;
        string value = await AskAsync(prompt, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public Task<string?> ReadInputAsync(CancellationToken cancellationToken) => _input.ReadAsync(false, cancellationToken);

    internal Task<string?> ReadCommandAsync(string prompt, Func<string, IReadOnlyList<string>> complete, CancellationToken ct) =>
        _input.ReadCommandAsync(prompt, complete, ct);

    public async Task<string?> SelectFileAsync(string prompt, string? configuredPath, string invalidMessage,
        CancellationToken cancellationToken, Func<string, bool>? validate = null, bool optional = false)
    {
        string? path = ConsolePath.Normalize(configuredPath);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && (validate?.Invoke(path) ?? true))
                return path;
            if (!AllowPrompts || !_input.CanPrompt)
            {
                if (optional && string.IsNullOrWhiteSpace(path)) return null;
                throw new FileNotFoundException(invalidMessage, path);
            }
            if (!string.IsNullOrWhiteSpace(path)) WriteLine(invalidMessage);
            path = ConsolePath.Normalize(await AskOptionalAsync(prompt, cancellationToken).ConfigureAwait(false));
            if (string.IsNullOrWhiteSpace(path))
            {
                if (optional) return null;
                throw new OperationCanceledException(Strings.Cli_OperationCancelled);
            }
        }
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
            var names = SaharaIdentityDisplay.Names(info);
            Console.WriteLine(Strings.FormatCli_InfoProtocol("QualcommEdl"));
            Console.WriteLine(Strings.FormatCli_InfoVendor(info.Vendor == QcomVendorKind.Generic
                ? Strings.Cli_GenericStrategy : info.Vendor.ToString()));
            Console.WriteLine(Strings.FormatCli_InfoHardware(
                names.Oem,
                names.Soc,
                SaharaIdentityDisplay.SecureBoot(info.SecureBoot)));
            if (info.Sahara is not null)
            {
                var sahara = info.Sahara;
                var hw = sahara.MsmHwInfo;
                Console.WriteLine(Strings.FormatCli_InfoSahara(
                    sahara.Version,
                    sahara.MinimumVersionSupported,
                    sahara.MaximumPacketSizeSupported,
                    SaharaIdentityDisplay.Mode(sahara.Mode)));
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
        => $"{bytes.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} Bytes ({ProgressDisplay.Size(bytes)})";

    public void LogException(Exception exception)
    {
        Log.Error(exception, Strings.Cli_LogCommandFailed, exception.Message);
        string message = exception switch
        {
            MtkLoaderSelectionTimeoutException => Strings.Cli_MtkDaSelectionTimedOut,
            TimeoutException => Strings.Cli_ResponseTimedOut,
            FirehoseNakException => Strings.Cli_DeviceRejected,
            UsbException { ErrorCode: Error.NoDevice } => Strings.Cli_UsbDisconnected,
            MtkResourceException or MtkProtocolException or MtkCapabilityException or MtkExploitException or
            ArgumentException or FileNotFoundException or InvalidOperationException or QcomResourceException =>
                exception.Message.Replace('\r', ' ').Replace('\n', ' '),
            _ => Strings.Cli_OperationFailed
        };
        if (message.Length > 240) message = message[..240];
        lock (_gate)
        {
            ClearProgressUnsafe();
            Console.Error.WriteLine(Strings.FormatCli_CommandFailed(message));
            if (LogFilePath is not null) Console.Error.WriteLine(Strings.FormatCli_LogLocation(LogFilePath));
        }
    }

    public void ShowCancelled()
    {
        Log.ForContext("UserPresentation", true).Information(Strings.Cli_OperationCancelled);
        WriteLine(Strings.Cli_OperationCancelled);
    }

    internal void WriteLog(LogEvent logEvent)
    {
        lock (_gate)
        {
            ClearProgressUnsafe();
            string line = $"[{logEvent.Timestamp.LocalDateTime:HH:mm:ss} {FormatLevel(logEvent.Level)}] {RenderMessage(logEvent)}";
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
        MessageFormatter.Format(logEvent, writer);
        return writer.ToString();
    }
}
