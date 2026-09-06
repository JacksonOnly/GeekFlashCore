namespace GeekFlashCore.ImageFormats.Abstractions;

public enum ImageFormatErrorCode
{
    InvalidFormat,
    CorruptMetadata,
    ChecksumMismatch,
    UnsupportedFeature,
    UnsupportedSource,
    ResourceLimitExceeded,
    EncryptionKeyRequired,
    IoFailure
}
