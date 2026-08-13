namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public enum SaharaStatus
{
    // Success
    StatusSuccess = 0x00,

    // Invalid command received in current state
    NakInvalidCmd = 0x01,

    // Protocol mismatch between host and target
    NakProtocolMismatch = 0x02,

    // Invalid target protocol version
    NakInvalidTargetProtocol = 0x03,

    // Invalid host protocol version
    NakInvalidHostProtocol = 0x04,

    // Invalid packet size received
    NakInvalidPacketSize = 0x05,

    // Unexpected image ID received
    NakUnexpectedImageId = 0x06,

    // Invalid image header size received
    NakInvalidHeaderSize = 0x07,

    // Invalid image data size received
    NakInvalidDataSize = 0x08,

    // Invalid image type received
    NakInvalidImageType = 0x09,

    // Invalid tranmission length
    NakInvalidTxLength = 0x0A,

    // Invalid reception length
    NakInvalidRxLength = 0x0B,

    // General transmission or reception error
    NakGeneralTxRxError = 0x0C,

    // Error while transmitting READ_DATA packet
    NakReadDataError = 0x0D,

    // Cannot receive specified number of program headers
    NakUnsupportedNumPhdrs = 0x0E,

    // Invalid data length received for program headers
    NakInvalidPdhrSize = 0x0F,

    // Multiple shared segments found in ELF image
    NakMultipleSharedSeg = 0x10,

    // Uninitialized program header location
    NakUninitPhdrLoc = 0x11,

    // Invalid destination address
    NakInvalidDestAddr = 0x12,

    // Invalid data size receieved in image header
    NakInvalidImgHdrDataSize = 0x13,

    // Invalid ELF header received
    NakInvalidElfHdr = 0x14,

    // Unknown host error received in HELLO_RESP
    NakUnknownHostError = 0x15,

    // Timeout while receiving data
    NakTimeoutRx = 0x16,

    // Timeout while transmitting data
    NakTimeoutTx = 0x17,

    // Invalid mode received from host
    NakInvalidHostMode = 0x18,

    // Invalid memory read access
    NakInvalidMemoryRead = 0x19,

    // Host cannot handle read data size requested
    NakInvalidDataSizeRequest = 0x1A,

    // Memory debug not supported
    NakMemoryDebugNotSupported = 0x1B,

    // Invalid mode switch
    NakInvalidModeSwitch = 0x1C,

    // Failed to execute command
    NakCmdExecFailure = 0x1D,

    // Invalid parameter passed to command execution
    NakExecCmdInvalidParam = 0x1E,

    // Unsupported client command received
    NakExecCmdUnsupported = 0x1F,

    // Invalid client command received for data response
    NakExecDataInvalidClientCmd = 0x20,

    // Failed to authenticate hash table
    NakHashTableAuthFailure = 0x21,

    // Failed to verify hash for a given segment of ELF image
    NakHashVerificationFailure = 0x22,

    // Failed to find hash table in ELF image
    NakHashTableNotFound = 0x23,
}