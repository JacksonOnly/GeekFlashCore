namespace GeekFlashCore.Protocol.Mtk.Internals;

/// <summary>Shared XFlash/XML framing; independent of BROM byte commands.</summary>
internal static class MtkDaFrame
{
    public const uint Magic = 0xfeeeeeef;
    public const int HeaderSize = 12;
}

internal enum MtkDaFrameType : uint
{
    Flow = 1,
    Message = 2
}
