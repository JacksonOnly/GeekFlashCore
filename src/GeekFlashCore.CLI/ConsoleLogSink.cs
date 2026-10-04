using Serilog.Core;
using Serilog.Events;

namespace GeekFlashCore.CLI;

internal sealed class ConsoleLogSink(ConsoleUi ui, bool verbose = false) : ILogEventSink
{
    private readonly ConsoleUi _ui = ui ?? throw new ArgumentNullException(nameof(ui));

    public void Emit(LogEvent logEvent)
    {
        if (!verbose || _ui.SuppressDiagnosticLogs || logEvent.Exception is not null ||
            logEvent.Properties.ContainsKey("DeviceDiagnostic") ||
            logEvent.Properties.ContainsKey("UserPresentation")) return;
        _ui.WriteLog(logEvent);
    }
}
