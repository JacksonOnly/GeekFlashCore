using System.Security.Cryptography;
using GeekFlashCore.Protocol.Mtk.Da;

namespace GeekFlashCore.Protocol.Mtk;

public sealed partial class MtkProtocol
{
    private MtkDaAuthenticationState _da1Authentication;
    private MtkDaAuthenticationState _da2Authentication;

    private byte[]? GetDaAuthenticationChallenge(MtkAuthenticationKind kind)
    {
        byte[]? challenge = _da!.GetAuthenticationChallenge();
        if (challenge is null)
        {
            var evidence = _da is XmlSession xml ? xml.AuthenticationState :
                _da is LegacySession ? MtkDaAuthenticationState.Unsupported : MtkDaAuthenticationState.NotRequired;
            if (kind == MtkAuthenticationKind.Da1Sla) _da1Authentication = evidence;
            else _da2Authentication = evidence;
        }
        return challenge;
    }
    private void AuthenticateDaSynchronously(MtkAuthenticationKind kind, MtkConnectionResources resources)
    {
        byte[]? challenge = GetDaAuthenticationChallenge(kind);
        if (challenge is null) return;
        try
        {
            State(MtkSessionState.Authenticating);
            if (resources.SynchronousSigner is null) throw new MtkResourceException("synchronous DA SLA signer");
            _wire.Check();
            using (var response = resources.SynchronousSigner(kind, challenge))
            {
                _wire.Check();
                _da!.Authenticate(response.Memory.Span);
            }
            if (kind == MtkAuthenticationKind.Da1Sla) _da1Authentication = MtkDaAuthenticationState.Authenticated;
            else _da2Authentication = MtkDaAuthenticationState.Authenticated;
        }
        finally { CryptographicOperations.ZeroMemory(challenge); }
    }
    private void CompleteDaAuthentication()
    {
        // Packet limits can change after DA2 SLA. This must precede the host extension checkpoint.
        if (_da is XFlashSession xflash) xflash.CompleteAuthentication();
        _wire.Check();
    }
}
