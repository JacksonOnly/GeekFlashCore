using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record FirehoseVipResourceRequest(QcomTargetInfo TargetInfo);

public sealed record FirehoseVipResourceResponse(
    IDataSource SignedTable,
    IReadOnlyList<IDataSource>? ChainedTables = null);
