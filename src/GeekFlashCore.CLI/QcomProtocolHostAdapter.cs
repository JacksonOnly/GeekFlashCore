using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.CLI.Localization;
using GeekFlashCore.Protocol.Qcom;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.CLI;

internal static class QcomProtocolHostAdapter
{
    internal static void ValidateOptions(CliOptions input) => CreateOptions(input).Validate();
    public static ProtocolRegistration Registration { get; } = new(
        ProtocolType.QualcommEdl,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qcom", "qualcomm", "qualcommedl" },
        "QualcommEdl",
        Strings.Cli_QcomWaitingForDevice,
        new QcomDeviceIdentify(),
        Create,
        [],
        static (protocol, ui) => ui.PrintTargetInfo(((IQcomProtocol)protocol).TargetInfo),
        new CommandSet());

    private static IProtocol Create(ProtocolHostContext context, ITransport transport)
    {
        QcomProtocolOptions options = CreateOptions(context.Options);
        options = options with { AllowOplusModeSelection = options.AllowOplusModeSelection && context.Ui.CanPrompt };
        OplusDigestMode mode = options.OplusDigest.Mode;
        return new QcomProtocol(transport, options,
            new ConsoleSaharaImageProvider(context.Ui, context.Options.Loader),
            mode == OplusDigestMode.None && !options.AllowOplusModeSelection ? null : new ConsoleOplusDigestProvider(context.Ui, context.Options.OplusDigest, context.Options.OplusSign),
            new ConsoleAuthenticationProvider(context.Ui), null, leaveTransportOpen: true, programmerInspector: null,
            firehoseDigestProvider: options.FirehoseDigest.Enabled ? new ConsoleFirehoseDigestProvider(context.Ui, context.Options.Digest) : null,
            firehoseVipProvider: options.FirehoseVip.Enabled ? new ConsoleVipProvider(context.Ui, context.Options.VipSigned, context.Options.VipChained) : null,
            vendorSelectionProvider: new ConsoleVendorSelectionProvider(context.Ui));
    }

    private static QcomProtocolOptions CreateOptions(CliOptions input)
    {
        OplusDigestMode mode = input.EffectiveOplusMode;
        return new QcomProtocolOptions
        {
            VendorOverride = input.Vendor,
            AuthenticationKind = input.AuthenticationKind,
            OnePlusProjectId = input.OnePlusProjectId,
            ReadTimeoutMilliseconds = input.ReadTimeout,
            WriteTimeoutMilliseconds = input.WriteTimeout,
            ConnectTimeoutMilliseconds = input.ConnectTimeout,
            ResourceRequestTimeoutMilliseconds = input.EffectiveResourceTimeout,
            AllowOplusModeSelection = mode == OplusDigestMode.None && !input.NonInteractive && !input.HasExplicitOplusMode &&
                string.IsNullOrWhiteSpace(input.Digest) && string.IsNullOrWhiteSpace(input.VipSigned),
            OplusDigest = new OplusDigestConfiguration
            {
                Mode = mode, FixedSectorCount = 256, ResumeAwaitingDigest = input.OplusResume
            },
            FirehoseDigest = new FirehoseDigestConfiguration { Enabled = !string.IsNullOrWhiteSpace(input.Digest) },
            FirehoseVip = new FirehoseVipConfiguration { Enabled = !string.IsNullOrWhiteSpace(input.VipSigned) }
        };
    }

    private sealed class CommandSet : IProtocolCommandSet
    {
        public bool Handles(string command) => command.Equals("qcom", StringComparison.OrdinalIgnoreCase) || FirehoseCommands.Usages.ContainsKey(command);
        public CliOptions Normalize(CliOptions options) => FirehoseCommands.Normalize(options);
        public void Validate(CliOptions options) => FirehoseCommands.Validate(options);
        public bool RequiresConnection(string command) => command is not ("configure" or "probe-sahara");
        public void PrintHelp(IProtocol protocol, ConsoleUi ui) => FirehoseCommands.PrintMapping(protocol, ui);
        public void ValidateAvailability(IProtocol protocol, string command)
        {
            if (protocol is IQcomProtocol device && !device.IsConnected &&
                command is not ("connect" or "configure" or "probe-sahara" or "help" or "devices"))
                throw new InvalidOperationException(Strings.Cli_ReconnectRequired);
            if (protocol is IQcomProtocol qcom && command == "reboot") FirehoseCommands.Require(qcom, "power");
        }
        public async Task<int> ExecuteAsync(IProtocol protocol, CliOptions options, ConsoleUi ui, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            if (protocol is not IQcomProtocol qcom) throw new NotSupportedException(Strings.Cli_QcomRequired);
            switch (options.Command)
            {
                case "program":
                    await StorageCommands.ExecuteAsync(protocol, "write", options.Arguments, ui, progress, ct);
                    return 0;
                case "probe-sahara":
                    qcom.ProbeSahara(progress);
                    ui.PrintTargetInfo(qcom.TargetInfo);
                    return 0;
                case "xml":
                    string path = ConsolePath.Normalize(options.Arguments[0])!;
                    if (new FileInfo(path).Length > FirehoseConstants.MaximumXmlPacketSize) throw new ArgumentException(Strings.Cli_XmlTooLarge);
                    var result = qcom.ExecuteFirehoseXml(File.ReadAllText(path));
                    if (!result.IsSuccess) throw new InvalidOperationException(Strings.FormatCli_CommandUnsuccessful("xml"));
                    ui.WriteLine(Strings.FormatCli_XmlResult(result.Status));
                    return 0;
                default:
                    await FirehoseCommands.ExecuteAsync(qcom, options.Command, options.Arguments, ui, ct);
                    return 0;
            }
        }
    }
}
