namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public interface IFirehoseConfigurationProvider
{
    ValueTask<FirehoseConfigurationResponse> ResolveAsync(
        FirehoseConfigurationRequest request,
        CancellationToken cancellationToken = default);
}
