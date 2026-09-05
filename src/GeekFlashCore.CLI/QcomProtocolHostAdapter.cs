using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.CLI;

internal static class QcomProtocolHostAdapter
{
    public static ProtocolRegistration Registration { get; } = new(
        ProtocolType.QualcommEdl,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qcom", "qualcomm", "qualcommedl" },
        "QualcommEdl",
        "等待 Qualcomm EDL USB 设备热插拔...",
        new QcomDeviceIdentify(),
        Create,
        [new QcomCommandHandler()],
        static (protocol, ui) => ui.PrintTargetInfo(((IQcomProtocol)protocol).TargetInfo));

    private static IProtocol Create(ProtocolHostContext context, ITransport transport)
    {
        QcomProtocolOptions options = CreateOptions(context.Options);
        OplusDigestMode mode = options.OplusDigest.Mode;
        return new QcomProtocol(transport, options,
            new ConsoleSaharaImageProvider(context.Ui, context.Options.Loader),
            mode == OplusDigestMode.None ? null : new ConsoleOplusDigestProvider(context.Ui, context.Options.OplusDigest),
            new ConsoleAuthenticationProvider(context.Ui), null, leaveTransportOpen: true,
            firehoseDigestProvider: options.FirehoseDigest.Enabled ? new ConsoleFirehoseDigestProvider(context.Ui, context.Options.Digest) : null,
            firehoseVipProvider: options.FirehoseVip.Enabled ? new ConsoleVipProvider(context.Ui, context.Options.VipSigned, context.Options.VipChained) : null);
    }

    private static QcomProtocolOptions CreateOptions(CliOptions input)
    {
        OplusDigestMode mode = input.OplusMode;
        if (!string.IsNullOrWhiteSpace(input.OplusDigest) && mode == OplusDigestMode.None) mode = OplusDigestMode.OplusDigestPt;
        return new QcomProtocolOptions
        {
            VendorOverride = input.Vendor,
            AuthenticationKind = input.AuthenticationKind,
            ReadTimeoutMilliseconds = input.ReadTimeout,
            WriteTimeoutMilliseconds = input.WriteTimeout,
            OplusDigest = new OplusDigestConfiguration { Mode = mode },
            FirehoseDigest = new FirehoseDigestConfiguration { Enabled = !string.IsNullOrWhiteSpace(input.Digest) },
            FirehoseVip = new FirehoseVipConfiguration { Enabled = !string.IsNullOrWhiteSpace(input.VipSigned) }
        };
    }

    private sealed class QcomCommandHandler : IProtocolCommandHandler
    {
        public string Name => "qcom";
        public string HelpText => "qcom probe-sahara | configure | xml <file>";
        public bool Handles(IReadOnlyList<string> arguments) => arguments.Count > 0;
        public bool RequiresConnection(IReadOnlyList<string> arguments) => !arguments[0].Equals("probe-sahara", StringComparison.OrdinalIgnoreCase) && !arguments[0].Equals("configure", StringComparison.OrdinalIgnoreCase);

        public Task<int> ExecuteAsync(IProtocol protocol, IReadOnlyList<string> arguments, ConsoleUi ui, IProgress<ProgressRecord> progress, CancellationToken cancellationToken)
        {
            if (protocol is not IQcomProtocol qcom) throw new ArgumentException("当前协议不支持 Qualcomm 命令");
            string command = arguments[0].ToLowerInvariant();
            switch (command)
            {
                case "probe-sahara":
                    var target = qcom.ProbeSahara(progress);
                    Console.WriteLine($"Sahara v{target.Version}, mode={target.Mode}, packet={target.MaximumPacketSizeSupported}");
                    return Task.FromResult(0);
                case "configure":
                    var configured = qcom.ConfigureFirehose(progress);
                    Console.WriteLine($"Firehose configured: {configured.Status}, bytes={configured.BytesTransferred}");
                    return Task.FromResult(configured.IsSuccess ? 0 : 1);
                case "xml":
                    string path = arguments.Count > 1 ? arguments[1] : ui.Ask("XML 文件路径");
                    string xml = File.ReadAllText(path);
                    var response = qcom.ExecuteFirehoseXml(xml);
                    Console.WriteLine($"XML result: {response.Status}");
                    return Task.FromResult(response.IsSuccess ? 0 : 1);
                default: throw new ArgumentException("qcom 子命令必须是 probe-sahara、configure 或 xml");
            }
        }
    }
}
