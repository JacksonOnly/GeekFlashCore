using Serilog.Core;
using Serilog.Events;

namespace GeekFlashCore.CLI;

internal sealed class ConsoleLogSink(ConsoleUi ui) : ILogEventSink
{
    private readonly ConsoleUi _ui = ui ?? throw new ArgumentNullException(nameof(ui));

    public void Emit(LogEvent logEvent) => _ui.WriteLog(logEvent);
}
