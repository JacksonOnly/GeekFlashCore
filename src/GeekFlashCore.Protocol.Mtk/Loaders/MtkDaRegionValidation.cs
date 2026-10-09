namespace GeekFlashCore.Protocol.Mtk.Loaders;

/// <summary>Shared validation of the raw, non-executable DA region boundary metadata.</summary>
internal static class MtkDaRegionValidation
{
    internal static bool IsValidEntryOffset(MtkDaKind kind, long fileOffset, uint length, uint signatureLength, uint entryOffset)
    {
        if (fileOffset < 0 || length == 0 || signatureLength >= length)
            return false;
        // Older containers use a region-relative value. Some XML/v6 containers instead
        // store the absolute file offset at which the trailing signature begins.
        // Preserve the raw value; code windows still use length - signatureLength.
        return entryOffset <= length ||
            kind == MtkDaKind.Xml && (ulong)entryOffset == (ulong)fileOffset + length - signatureLength;
    }
}
