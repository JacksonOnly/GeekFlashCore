using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Resolves DA metadata and source; ownership of the source remains with the provider.</summary>
public interface IMtkDaProvider
{
    ValueTask<MtkDaImage> GetDownloadAgentAsync(MtkTargetInfo target, CancellationToken cancellationToken);
}
/// <summary>Supplies optional EMI. A null result does not hide a malformed preloader.</summary>
public interface IMtkEmiProvider
{
    ValueTask<MtkEmiImage?> GetEmiAsync(MtkTargetInfo target, CancellationToken cancellationToken);
}
/// <summary>Supplies legitimate authentication responses. Challenge memory stays valid until the returned operation completes,
/// including after cancellation; it is cleared on completion. No fallback signatures are generated.</summary>
public interface IMtkAuthenticationProvider
{
    ValueTask<MtkSensitiveBuffer> SignAsync(MtkAuthenticationKind kind, MtkTargetInfo target,
        ReadOnlyMemory<byte> challenge, CancellationToken cancellationToken);
}
/// <summary>Explicit resources for synchronous connection. Streams opened by the core are disposed by it.</summary>
public sealed record MtkConnectionResources(MtkDaImage DownloadAgent, MtkEmiImage? Emi = null,
    MtkSensitiveBuffer? Authentication = null, MtkSensitiveBuffer? Certificate = null,
    IMtkAuthenticationProvider? Signer = null,
    Func<MtkAuthenticationKind, ReadOnlyMemory<byte>, MtkSensitiveBuffer>? SynchronousSigner = null);
/// <summary>Reserved exploit integration contract. Core neither implements nor invokes it.</summary>
public interface IMtkExploitStrategy
{
    /// <summary>Runs a host-supplied strategy; a false result never proves device recovery.</summary>
    bool Execute(IUsbTransport transport, MtkTargetInfo target, CancellationToken cancellationToken);
}
