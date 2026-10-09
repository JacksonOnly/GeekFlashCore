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
        bool qcomSummary = logEvent.Level >= LogEventLevel.Information &&
            logEvent.Properties.TryGetValue("QcomSummary", out var qcomMarker) && qcomMarker is ScalarValue { Value: true };
        // Structured output suppresses chatter, not PATCH phase summaries or MTK warnings.
        // Keep raw device data, exception details and duplicate presentation filtered below.
        if ((!verbose && !summary && !qcomSummary) ||
            _ui.SuppressDiagnosticLogs && !qcomSummary && !(summary && logEvent.Level >= LogEventLevel.Warning) ||
            logEvent.Exception is not null ||
            logEvent.Properties.ContainsKey("DeviceDiagnostic") ||
            logEvent.Properties.ContainsKey("UserPresentation")) return;
        _ui.WriteLog(logEvent);
    }
}
