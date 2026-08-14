using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Models;

public static class SaharaStatusMapping
{
    public static readonly IReadOnlyDictionary<SaharaStatus, string> MessageMapping =
        new Dictionary<SaharaStatus, string>()
        {
            { SaharaStatus.StatusSuccess, Strings.SaharaStatus_Success },
            { SaharaStatus.NakInvalidCmd, Strings.SaharaNak_InvalidCmd },
            { SaharaStatus.NakProtocolMismatch, Strings.SaharaNak_ProtocolMismatch },
            { SaharaStatus.NakInvalidTargetProtocol, Strings.SaharaNak_InvalidTargetProtocol },
            { SaharaStatus.NakInvalidHostProtocol, Strings.SaharaNak_InvalidHostProtocol },
            { SaharaStatus.NakInvalidPacketSize, Strings.SaharaNak_InvalidPacketSize },
            { SaharaStatus.NakUnexpectedImageId, Strings.SaharaNak_UnexpectedImageId },
            { SaharaStatus.NakInvalidHeaderSize, Strings.SaharaNak_InvalidHeaderSize },
            { SaharaStatus.NakInvalidDataSize, Strings.SaharaNak_InvalidDataSize },
            { SaharaStatus.NakInvalidImageType, Strings.SaharaNak_InvalidImageType },
            { SaharaStatus.NakInvalidTxLength, Strings.SaharaNak_InvalidTxLength },
            { SaharaStatus.NakInvalidRxLength, Strings.SaharaNak_InvalidRxLength },
            { SaharaStatus.NakGeneralTxRxError, Strings.SaharaNak_GeneralTxRxError },
            { SaharaStatus.NakReadDataError, Strings.SaharaNak_ReadDataError },
            { SaharaStatus.NakUnsupportedNumPhdrs, Strings.SaharaNak_UnsupportedNumPhdrs },
            { SaharaStatus.NakInvalidPdhrSize, Strings.SaharaNak_InvalidPdhrSize },
            { SaharaStatus.NakMultipleSharedSeg, Strings.SaharaNak_MultipleSharedSeg },
            { SaharaStatus.NakUninitPhdrLoc, Strings.SaharaNak_UninitPhdrLoc },
            { SaharaStatus.NakInvalidDestAddr, Strings.SaharaNak_InvalidDestAddr },
            { SaharaStatus.NakInvalidImgHdrDataSize, Strings.SaharaNak_InvalidImgHdrDataSize },
            { SaharaStatus.NakInvalidElfHdr, Strings.SaharaNak_InvalidElfHdr },
            { SaharaStatus.NakUnknownHostError, Strings.SaharaNak_UnknownHostError },
            { SaharaStatus.NakTimeoutRx, Strings.SaharaNak_TimeoutRx },
            { SaharaStatus.NakTimeoutTx, Strings.SaharaNak_TimeoutTx },
            { SaharaStatus.NakInvalidHostMode, Strings.SaharaNak_InvalidHostMode },
            { SaharaStatus.NakInvalidMemoryRead, Strings.SaharaNak_InvalidMemoryRead },
            { SaharaStatus.NakInvalidDataSizeRequest, Strings.SaharaNak_InvalidDataSizeRequest },
            { SaharaStatus.NakMemoryDebugNotSupported, Strings.SaharaNak_MemoryDebugNotSupported },
            { SaharaStatus.NakInvalidModeSwitch, Strings.SaharaNak_InvalidModeSwitch },
            { SaharaStatus.NakCmdExecFailure, Strings.SaharaNak_CmdExecFailure },
            { SaharaStatus.NakExecCmdInvalidParam, Strings.SaharaNak_ExecCmdInvalidParam },
            { SaharaStatus.NakExecCmdUnsupported, Strings.SaharaNak_ExecCmdUnsupported },
            { SaharaStatus.NakExecDataInvalidClientCmd, Strings.SaharaNak_ExecDataInvalidClientCmd },
            { SaharaStatus.NakHashTableAuthFailure, Strings.SaharaNak_HashTableAuthFailure },
            { SaharaStatus.NakHashVerificationFailure, Strings.SaharaNak_HashVerificationFailure },
            { SaharaStatus.NakHashTableNotFound, Strings.SaharaNak_HashTableNotFound },
        };
}