using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Protocol.Abstractions;

public interface IProtocol : IAsyncDisposable
{
    ProtocolType Type { get; }
    ITransport Transport { get; }
    bool IsConnected { get; }
    
    Task ConnectAsync(IProgress<ProgressRecord>? progress = null,CancellationToken ct = default);
    Task DisconnectAsync(IProgress<ProgressRecord>? progress = null,CancellationToken ct = default);
    Task<long> WriteAsync(WriteSource source, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default);
    Task<ReadDestination> ReadAsync(ReadDestination destination, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default);
    Task<bool> EraseAsync(StorageTarget target, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default);
    Task<IReadOnlyList<PartitionInfo>> GetPartitionsAsync(IProgress<ProgressRecord>? progress = null,CancellationToken ct = default);
}