using GeekFlashCore.CLI;
using GeekFlashCore.CLI.Localization;
using Serilog;

if (args.FirstOrDefault() == WindowsMtkDriverBackend.HelperSwitch)
{
    try
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(Strings.Cli_UsbArchitectureUnsupported);
        return await WindowsMtkDriverBackend.InstallElevatedAsync(args[1..], CancellationToken.None);
    }
    catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
}

CliOptions options;
try
{
    options = CommandLine.Parse(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine(Strings.FormatCli_ArgumentError(exception.Message));
    Console.Error.WriteLine(Strings.Cli_HelpHint);
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
    .WriteTo.Sink(new ConsoleLogSink(ui, options.Verbose))
    .CreateLogger();
ui.LogFilePath = fileLog.FilePath;
Log.ForContext("UserPresentation", true).Information(Strings.Cli_LogFilePath, fileLog.FilePath);
Log.Debug(Strings.Cli_LogSession, options.Command, options.EffectiveOplusMode, options.ConnectTimeout,
    options.ReadTimeout, options.WriteTimeout, options.EffectiveResourceTimeout, Environment.Version);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    if (!ui.TryCancelSearch()) cancellation.Cancel();
};
try
{
    return await new CliApplication(ui).RunAsync(options, cancellation.Token);
}
catch (OperationCanceledException)
{
    ui.ShowCancelled();
    return 130;
}
catch (Exception exception)
{
    ui.LogException(exception);
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
