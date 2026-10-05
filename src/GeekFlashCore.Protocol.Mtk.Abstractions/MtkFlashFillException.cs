namespace GeekFlashCore.Protocol.Mtk.Abstractions;

public sealed class MtkFlashFillException(Exception inner) : GeekFlashCore.Protocol.Abstractions.ProtocolException(Localization.Strings.FlashFillUnknown,inner)
{public bool MayHaveWritten=>true;}
