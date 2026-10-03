using GeekFlashCore.CLI;
using GeekFlashCore.CLI.Localization;
using Serilog;

CliOptions options;
try
{
    options = CommandLine.Parse(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine(Strings.FormatCli_ArgumentError(exception.Message));
    CommandLine.PrintHelp();
    return 2;
}
var ui = new ConsoleUi();
FileLogSink fileLog;
try { fileLog = new FileLogSink(options.LogFile); }
catch (Exception exception)
{
    Console.Error.WriteLine(Strings.FormatCli_LogFileFailed(exception.Message));
    return 1;
}
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Sink(fileLog)
    .WriteTo.Sink(new ConsoleLogSink(ui), restrictedToMinimumLevel: options.Verbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
    .CreateLogger();
Log.Information(Strings.Cli_LogFilePath, fileLog.FilePath);
Log.Debug(Strings.Cli_LogSession, options.Command, options.EffectiveOplusMode, options.ConnectTimeout,
    options.ReadTimeout, options.WriteTimeout, options.EffectiveResourceTimeout, Environment.Version);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
try
{
    return await new CliApplication(ui).RunAsync(options, cancellation.Token);
}
catch (OperationCanceledException)
{
    Log.Warning(Strings.Cli_OperationCancelled);
    return 130;
}
catch (Exception exception)
{
    Log.Error(exception, Strings.Cli_LogCommandFailed, exception.Message);
    Console.Error.WriteLine(Strings.FormatCli_CommandFailed(exception.Message));
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
