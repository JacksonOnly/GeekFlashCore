namespace GeekFlashCore.Protocol.Qcom.Abstractions;

/// <summary>
/// Renders Sahara protocol enums as readable member names for logging, so callers
/// do not need per-enum switch expressions. The mapping is compiled to constant
/// branches (no reflection), and values that are not defined (e.g. an unknown
/// status code received from the target) fall back to a hex form instead of
/// throwing.
/// </summary>
public static class SaharaEnumExtensions
{
    public static string ToName(this SaharaCommand command) => command switch
    {
        SaharaCommand.Hello => "Hello",
        SaharaCommand.HelloResponse => "HelloResponse",
        SaharaCommand.ReadData32Bit => "ReadData32Bit",
        SaharaCommand.EndImageTransmit => "EndImageTransmit",
        SaharaCommand.Done => "Done",
        SaharaCommand.DoneResponse => "DoneResponse",
        SaharaCommand.Reset => "Reset",
        SaharaCommand.ResetResponse => "ResetResponse",
        SaharaCommand.MemoryDebug32Bit => "MemoryDebug32Bit",
        SaharaCommand.MemoryRead32Bit => "MemoryRead32Bit",
        SaharaCommand.ReadyResponse => "ReadyResponse",
        SaharaCommand.SwitchMode => "SwitchMode",
        SaharaCommand.Execute => "Execute",
        SaharaCommand.ExecuteResponse => "ExecuteResponse",
        SaharaCommand.ExecuteDataResponse => "ExecuteDataResponse",
        SaharaCommand.MemoryDebug64Bit => "MemoryDebug64Bit",
        SaharaCommand.MemoryRead64Bit => "MemoryRead64Bit",
        SaharaCommand.ReadData64Bit => "ReadData64Bit",
        SaharaCommand.ResetStateMachine => "ResetStateMachine",
        _ => $"Unknown(0x{(int)command:X})"
    };

    public static string ToName(this SaharaMode mode) => mode switch
    {
        SaharaMode.ImageTxPending => "ImageTxPending",
        SaharaMode.ImageTxComplete => "ImageTxComplete",
        SaharaMode.MemoryDebug => "MemoryDebug",
        SaharaMode.Command => "Command",
        _ => $"Unknown(0x{(int)mode:X})"
    };

    public static string ToName(this SaharaExecuteCommand command) => command switch
    {
        SaharaExecuteCommand.Nop => "Nop",
        SaharaExecuteCommand.ReadSerialNum => "ReadSerialNum",
        SaharaExecuteCommand.ReadMsmHwId => "ReadMsmHwId",
        SaharaExecuteCommand.ReadOemPkHash => "ReadOemPkHash",
        SaharaExecuteCommand.SwitchDmss => "SwitchDmss",
        SaharaExecuteCommand.SwitchStreaming => "SwitchStreaming",
        SaharaExecuteCommand.ReadDebugData => "ReadDebugData",
        SaharaExecuteCommand.ReadSblVersion => "ReadSblVersion",
        SaharaExecuteCommand.ReadCommandIdList => "ReadCommandIdList",
        SaharaExecuteCommand.ReadTrainingData => "ReadTrainingData",
        SaharaExecuteCommand.ReadMsmHwIdV3 => "ReadMsmHwIdV3",
        _ => $"Unknown(0x{(int)command:X})"
    };

    public static string ToName(this SaharaStatus status) => status switch
    {
        SaharaStatus.StatusSuccess => "StatusSuccess",
        SaharaStatus.NakInvalidCmd => "NakInvalidCmd",
        SaharaStatus.NakProtocolMismatch => "NakProtocolMismatch",
        SaharaStatus.NakInvalidTargetProtocol => "NakInvalidTargetProtocol",
        SaharaStatus.NakInvalidHostProtocol => "NakInvalidHostProtocol",
        SaharaStatus.NakInvalidPacketSize => "NakInvalidPacketSize",
        SaharaStatus.NakUnexpectedImageId => "NakUnexpectedImageId",
        SaharaStatus.NakInvalidHeaderSize => "NakInvalidHeaderSize",
        SaharaStatus.NakInvalidDataSize => "NakInvalidDataSize",
        SaharaStatus.NakInvalidImageType => "NakInvalidImageType",
        SaharaStatus.NakInvalidTxLength => "NakInvalidTxLength",
        SaharaStatus.NakInvalidRxLength => "NakInvalidRxLength",
        SaharaStatus.NakGeneralTxRxError => "NakGeneralTxRxError",
        SaharaStatus.NakReadDataError => "NakReadDataError",
        SaharaStatus.NakUnsupportedNumPhdrs => "NakUnsupportedNumPhdrs",
        SaharaStatus.NakInvalidPdhrSize => "NakInvalidPdhrSize",
        SaharaStatus.NakMultipleSharedSeg => "NakMultipleSharedSeg",
        SaharaStatus.NakUninitPhdrLoc => "NakUninitPhdrLoc",
        SaharaStatus.NakInvalidDestAddr => "NakInvalidDestAddr",
        SaharaStatus.NakInvalidImgHdrDataSize => "NakInvalidImgHdrDataSize",
        SaharaStatus.NakInvalidElfHdr => "NakInvalidElfHdr",
        SaharaStatus.NakUnknownHostError => "NakUnknownHostError",
        SaharaStatus.NakTimeoutRx => "NakTimeoutRx",
        SaharaStatus.NakTimeoutTx => "NakTimeoutTx",
        SaharaStatus.NakInvalidHostMode => "NakInvalidHostMode",
        SaharaStatus.NakInvalidMemoryRead => "NakInvalidMemoryRead",
        SaharaStatus.NakInvalidDataSizeRequest => "NakInvalidDataSizeRequest",
        SaharaStatus.NakMemoryDebugNotSupported => "NakMemoryDebugNotSupported",
        SaharaStatus.NakInvalidModeSwitch => "NakInvalidModeSwitch",
        SaharaStatus.NakCmdExecFailure => "NakCmdExecFailure",
        SaharaStatus.NakExecCmdInvalidParam => "NakExecCmdInvalidParam",
        SaharaStatus.NakExecCmdUnsupported => "NakExecCmdUnsupported",
        SaharaStatus.NakExecDataInvalidClientCmd => "NakExecDataInvalidClientCmd",
        SaharaStatus.NakHashTableAuthFailure => "NakHashTableAuthFailure",
        SaharaStatus.NakHashVerificationFailure => "NakHashVerificationFailure",
        SaharaStatus.NakHashTableNotFound => "NakHashTableNotFound",
        _ => $"Unknown(0x{(int)status:X})"
    };
}
