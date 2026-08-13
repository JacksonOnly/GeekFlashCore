namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public enum SaharaCommand : byte
{
    Hello = 1,
    HelloResponse = 2,
    ReadData32Bit = 3,
    EndImageTransmit = 4,
    Done = 5,
    DoneResponse = 6,
    Reset = 7,
    ResetResponse = 8,
    MemoryDebug32Bit = 9,
    MemoryRead32Bit = 0xA,
    ReadyResponse = 0xB,
    SwitchMode = 0xC,
    Execute = 0xD,
    ExecuteResponse = 0xE,
    ExecuteDataResponse = 0xF,
    MemoryDebug64Bit = 0x10,
    MemoryRead64Bit = 0x11,
    ReadData64Bit = 0x12,
    ResetStateMachine = 0x13
}