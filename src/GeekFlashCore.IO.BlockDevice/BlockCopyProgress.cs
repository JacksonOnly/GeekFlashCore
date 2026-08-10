namespace GeekFlashCore.IO.BlockDevice;

public readonly record struct BlockCopyProgress(long CompletedBytes, long TotalBytes);
