using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Nothing;

public sealed class NothingProjectVerifier(FirehoseSession session)
{
    private readonly FirehoseSession _session = session ?? throw new ArgumentNullException(nameof(session));

    public FirehoseCommandResult Verify(ulong serial, string projectId, string? token1 = null)
    {
        NothingProjectToken token = NothingTokenCodec.Create(serial, projectId, token1);
        _session.Execute(new CheckNothingFeatureCommand());
        return _session.Execute(new NothingProjectVerifyCommand
        {
            Token1 = token.Token1,
            Token2 = token.Token2,
            Token3 = token.Token3
        });
    }
}
