namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public enum SaharaMode
{
    ImageTxPending  = 0x0,
    ImageTxComplete = 0x1,
    MemoryDebug      = 0x2,
    Command           = 0x3,
}