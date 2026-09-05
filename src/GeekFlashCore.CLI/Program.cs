using GeekFlashCore.CLI;
using Serilog;

CliOptions options;
try
{
    options = CommandLine.Parse(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"参数错误: {exception.Message}");
    CommandLine.PrintHelp();
    return 2;
}
var ui = new ConsoleUi();
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(options.Verbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
    .WriteTo.Sink(new ConsoleLogSink(ui))
    .CreateLogger();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
try
{
    return await new CliApplication(ui).RunAsync(options, cancellation.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("操作已取消。");
    return 130;
}
catch (Exception exception)
{
    Log.Error(exception, "Command failed: {Message}", exception.Message);
    Console.Error.WriteLine($"失败: {exception.Message}");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
