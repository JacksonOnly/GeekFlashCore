using Serilog.Core;
using Serilog.Events;

namespace GeekFlashCore.CLI;

internal sealed class ConsoleLogSink(ConsoleUi ui, bool verbose = false) : ILogEventSink
{
    private readonly ConsoleUi _ui = ui ?? throw new ArgumentNullException(nameof(ui));

    public void Emit(LogEvent logEvent)
    {
        bool summary = logEvent.Level >= LogEventLevel.Information &&
            logEvent.Properties.TryGetValue("MtkSummary", out var marker) && marker is ScalarValue { Value: true };
        if ((!verbose && !summary) || _ui.SuppressDiagnosticLogs || logEvent.Exception is not null ||
            logEvent.Properties.ContainsKey("DeviceDiagnostic") ||
            logEvent.Properties.ContainsKey("UserPresentation")) return;
        _ui.WriteLog(logEvent);
    }
}
