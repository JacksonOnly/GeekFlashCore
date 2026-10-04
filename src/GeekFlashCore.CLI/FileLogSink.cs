using System.Globalization;
using System.Diagnostics;
using System.Text;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace GeekFlashCore.CLI;

internal sealed class FileLogSink : ILogEventSink, IDisposable
{
    private readonly object _gate = new();
    private readonly MessageTemplateTextFormatter _formatter = new(
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}", CultureInfo.InvariantCulture);
    private readonly long _maximumFileBytes;
    private readonly string _runId = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
    private StreamWriter _writer;
    private int _part;
    private int _bufferedEvents;
    private long _lastFlush = Stopwatch.GetTimestamp();
    private bool _disposed;

    public FileLogSink(string? path = null, long maximumFileBytes = 16 * 1024 * 1024)
    {
        if (maximumFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        _maximumFileBytes = maximumFileBytes;
        FilePath = Path.GetFullPath(ConsolePath.Normalize(path) ??
            Path.Combine(AppContext.BaseDirectory, "logs", $"geekflash-{_runId}.log"));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _writer = Open(FilePath);
    }

    public string FilePath { get; }

    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_writer.BaseStream.Position >= _maximumFileBytes)
            {
                _writer.Dispose();
                string next = Path.Combine(Path.GetDirectoryName(FilePath)!,
                    $"{Path.GetFileNameWithoutExtension(FilePath)}-{_runId}-part{++_part:D3}.log");
                _writer = Open(next);
            }
            _formatter.Format(logEvent, _writer);
            if (++_bufferedEvents >= 64 || logEvent.Level >= LogEventLevel.Information ||
                Stopwatch.GetElapsedTime(_lastFlush).TotalMilliseconds >= 250)
            {
                _writer.Flush();
                _bufferedEvents = 0;
                _lastFlush = Stopwatch.GetTimestamp();
            }
        }
    }

    private static StreamWriter Open(string path) => new(new FileStream(path, FileMode.Append,
        FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}
