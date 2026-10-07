using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Sprd.Abstractions;

/// <summary>Serialized synchronous BSL operations with an asynchronous host-resource facade.</summary>
public interface ISprdProtocol : IProtocol
{
    /// <summary>Current connection state.</summary>
    SprdSessionState SessionState { get; }
    /// <summary>Monotonically increasing identity used to expire old partition views.</summary>
    long Generation { get; }
    /// <summary>Last validated connection metadata, cleared when disconnected or faulted.</summary>
    SprdTargetInfo? TargetInfo { get; }
    /// <summary>Connects using borrowed resources; all wire I/O stays synchronous.</summary>
    void Connect(SprdConnectionResources resources, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Disconnects and expires all views.</summary>
    void Disconnect(CancellationToken cancellationToken = default);
    /// <summary>Gets immutable capacities from the supplied list or explicitly scaled native table.</summary>
    IReadOnlyList<SprdPartition> GetSprdPartitions(CancellationToken cancellationToken = default);
    /// <summary>Reads a checked byte range, borrowing the output stream.</summary>
    void ReadPartition(string name, long offset, long length, Stream output, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Writes Raw or expands Sparse sequentially from offset zero. Sparse holes are zero-filled.</summary>
    long WritePartition(string name, IDataSource source, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default);
    /// <summary>Erases a confirmed partition. NV-specific transformations are not implemented.</summary>
    void ErasePartition(string name, CancellationToken cancellationToken = default);
    /// <summary>Opens a borrowed-session, generation-checked read-only named-partition view.</summary>
    IReadableBlockDevice OpenPartition(string name, CancellationToken cancellationToken = default);
    /// <summary>Requests normal reset or power off and expires the connection.</summary>
    void Reboot(ProtocolRebootMode mode, CancellationToken cancellationToken = default);
}
