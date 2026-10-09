namespace GeekFlashCore.Protocol.Qcom.Abstractions;

/// <summary>Selects the wire backend for framework PROGRAM operations.</summary>
public enum FirehoseProgramWriteMode
{
    /// <summary>Use PROGRAM only, without PATCH fallback.</summary>
    Program,
    /// <summary>Use PROGRAM, falling back before payload transfer only on an explicit unsupported-command NAK.</summary>
    Auto,
    /// <summary>Write disk bytes through acknowledged PATCH commands of at most eight bytes.</summary>
    Patch
}
