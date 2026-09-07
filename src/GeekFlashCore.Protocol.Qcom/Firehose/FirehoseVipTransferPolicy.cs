using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose;

/// <summary>
/// Transfers qdl VIP tables before Firehose XML commands and rotates chained
/// tables after their frame capacity is consumed.
/// </summary>
internal sealed class FirehoseVipTransferPolicy
{
    private readonly IDataSource _signedTable;
    private readonly IReadOnlyList<IDataSource> _chainedTables;
    private readonly int _signedCapacity;
    private readonly int _chainedCapacity;
    private int _tableIndex = -1;
    private int _framesSent;

    public FirehoseVipTransferPolicy(
        FirehoseVipResourceResponse resource,
        int signedCapacity = FirehoseVipConfiguration.SignedTableFrameCapacity,
        int chainedCapacity = FirehoseVipConfiguration.ChainedTableFrameCapacity)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _signedTable = resource.SignedTable ?? throw new ArgumentException(Strings.Qcom_VipSignedTableRequired, nameof(resource));
        _chainedTables = resource.ChainedTables ?? [];
        _signedCapacity = signedCapacity > 0 ? signedCapacity : throw new ArgumentOutOfRangeException(nameof(signedCapacity));
        _chainedCapacity = chainedCapacity > 0 ? chainedCapacity : throw new ArgumentOutOfRangeException(nameof(chainedCapacity));
        ValidateSource(_signedTable);
        foreach (IDataSource table in _chainedTables) ValidateSource(table);
    }

    public void BeforeCommand(FirehoseSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        if (_tableIndex < 0 || _framesSent >= (_tableIndex == 0 ? _signedCapacity : _chainedCapacity))
        {
            int next = _tableIndex + 1;
            if (next == 0)
                SendTable(session, _signedTable, cancellationToken);
            else if (next - 1 < _chainedTables.Count)
                SendTable(session, _chainedTables[next - 1], cancellationToken);
            else
                throw new QcomResourceException(Strings.Qcom_VipChainedTablesExhausted);
            _tableIndex = next;
            _framesSent = 0;
        }
    }

    public void CommandSent() => _framesSent = checked(_framesSent + 1);

    private static void SendTable(FirehoseSession session, IDataSource table, CancellationToken cancellationToken)
    {
        using Stream source = table.OpenStream() ?? throw new QcomResourceException(Strings.Qcom_VipTableStreamMissing);
        if (!source.CanRead)
            throw new QcomResourceException(Strings.Qcom_VipTableNotReadable);
        FirehoseCommandResult result = session.SendAuxiliaryRaw(source, table.Length, GetBufferSize(table.Length), cancellationToken);
        if (result.Status != FirehoseResponseStatus.Ack || result.RawMode)
            throw new FirehoseProtocolException(Strings.Qcom_VipTableNotAcknowledged);
    }

    private static int GetBufferSize(long length) => checked((int)Math.Min(Math.Max(1, length), 1024 * 1024));

    private static void ValidateSource(IDataSource source)
    {
        if (source.Length <= 0 || source.Length > 16 * 1024 * 1024)
            throw new QcomResourceException(Strings.Qcom_VipTableLengthInvalid);
    }
}
