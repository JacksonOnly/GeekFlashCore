// Status values: penumbra core/src/error.rs, XFlashErrorKind.
namespace GeekFlashCore.Protocol.Mtk.Internals;

// This is a narrow compatibility allow-list, not a catalog of recoverable errors.
internal enum MtkXFlashStatus : uint
{
    Success = 0,
    UnsupportedCtrlCode = 0xC0010004
}
