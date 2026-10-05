using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal interface IMtkDaSession
{
    MtkDaKind Kind
    {
        get;
    }
    void Initialize(MtkDaImage image, MtkEmiImage? emi, MtkTargetInfo target,
        Func<MtkExploitStage, MtkDaImage> checkpoint);
    byte[]? GetAuthenticationChallenge();
    void Authenticate(ReadOnlySpan<byte> response);
    MtkStorageInfo GetStorage();
    void Read(MtkStorageRegion region, long offset, long length, Stream output);
    void Write(MtkStorageRegion region, long offset, long length, Stream input);
    void Erase(MtkStorageRegion region, long offset, long length);
    void Reboot(ProtocolRebootMode mode);
}
