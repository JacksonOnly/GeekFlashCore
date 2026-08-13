namespace GeekFlashCore.Protocol.Qcom.Abstractions;

internal static class SaharaPacketConstants
{
    public const int HelloSize = 48;
    public const int HelloResponseSize = 48;
    public const int ExecuteSize = 12;
    public const int ExecuteResponseSize = 16;
    public const int ExecuteDataResponseSize = 12;
    public const int ReadData32BitSize = 20;
    public const int ReadData64BitSize = 32;
    public const int EndImageTransmitSize = 16;
    public const int DoneSize = 8;
    public const int DoneResponseSize = 12;
    public const int ResetSize = 8;
    public const int ResetStateMachineSize = 8;
    public const int ResetResponseSize = 8;
    public const int MemoryDebug32BitSize = 16;
    public const int MemoryDebug64BitSize = 24;
    public const int MemoryRead32BitSize = 16;
    public const int MemoryRead64BitSize = 24;
    public const int ReadyResponseSize = 8;
    public const int SwitchModeSize = 12;
}