namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record FirehoseConfigurationRequest(
    QcomTargetInfo TargetInfo,
    FirehoseConfiguration SuggestedConfiguration);

public sealed record FirehoseConfigurationResponse(FirehoseConfiguration Configuration);
