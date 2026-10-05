namespace GeekFlashCore.Protocol.Mtk.Abstractions;

public enum MtkDeviceLockState : uint { Default=1,MpDefault=2,Unlock=3,Lock=4,Verified=5,Custom=6 }
public sealed record MtkRpmbLockInfo(uint Version,MtkDeviceLockState State);
public sealed class MtkRpmbLockWriteException(Exception inner) : GeekFlashCore.Protocol.Abstractions.ProtocolException(Localization.Strings.RpmbLockWriteUnknown,inner)
{public bool MayHaveWritten=>true;}
