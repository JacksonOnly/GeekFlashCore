namespace GeekFlashCore.FileSystem.Abstractions;

public enum FileSystemProbeStatus
{
    NotRecognized,
    RecognizedSupported,
    RecognizedUnsupported,
    RecognizedCorrupt
}