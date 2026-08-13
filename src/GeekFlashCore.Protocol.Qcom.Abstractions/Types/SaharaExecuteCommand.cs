namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public enum SaharaExecuteCommand : byte
{
    Nop              = 0x00,
    ReadSerialNum  = 0x01,
    ReadMsmHwId   = 0x02,
    ReadOemPkHash = 0x03,
    SwitchDmss      = 0x04,
    SwitchStreaming = 0x05,
    ReadDebugData  = 0x06,
    ReadSblVersion  = 0x07,
    ReadCommandIdList  = 0x08,
    ReadTrainingData  = 0x09,
    ReadMsmHwIdV3  = 0x0A,
}