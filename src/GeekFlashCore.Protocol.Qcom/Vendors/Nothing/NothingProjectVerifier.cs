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

    internal string VerifyBuiltIn(ulong serial, IEnumerable<string>? projectIds = null,
        CancellationToken cancellationToken = default)
    {
        if (serial == 0)
            throw new QcomAuthenticationException(Strings.Qcom_InvalidAuthentication);
        _session.Execute(new CheckNothingFeatureCommand(), cancellationToken: cancellationToken);
        QcomAuthenticationException? lastFailure = null;
        foreach (string projectId in projectIds ?? ["22111", "20111"])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(projectId))
                continue;
            try
            {
                NothingProjectToken token = NothingTokenCodec.Create(serial, projectId);
                FirehoseCommandResult result = _session.Execute(new NothingProjectVerifyCommand
                {
                    Token1 = token.Token1,
                    Token2 = token.Token2,
                    Token3 = token.Token3
                }, cancellationToken: cancellationToken);
                if (result.IsSuccess)
                    return projectId;
            }
            catch (FirehoseNakException exception)
            {
                lastFailure = new QcomAuthenticationException(Strings.Qcom_NothingProjectVerificationFailed, exception);
            }
        }
        throw lastFailure ?? new QcomAuthenticationException(Strings.Qcom_NothingProjectVerificationFailed);
    }
}
