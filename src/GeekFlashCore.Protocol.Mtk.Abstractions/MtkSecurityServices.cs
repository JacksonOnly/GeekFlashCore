namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>RPMB data uses 256-byte blocks. Backend verification is performed on the device.</summary>
public interface IMtkRpmbService
{
    bool IsAuthenticated(uint region);
    void Authenticate(uint region, ReadOnlySpan<byte> key, CancellationToken cancellationToken = default);
    void Read(uint region, uint startBlock, uint blockCount, Stream destination, CancellationToken cancellationToken = default);
    void Write(uint region, uint startBlock, uint blockCount, Stream source, CancellationToken cancellationToken = default);
}
/// <summary>A crypto algorithm is explicitly selected and validated against the original configuration.</summary>
public interface IMtkSecurityCipher
{
    string Name
    {
        get;
    }
    byte[] Transform(ReadOnlySpan<byte> data, bool encrypt);
}
/// <summary>Optional authenticated RPMB erasure implemented as bounded zero writes, never key programming.</summary>
public interface IMtkRpmbEraseService
{
    void Erase(uint region, uint startBlock, uint blockCount, CancellationToken cancellationToken = default);
}
/// <summary>Security configuration changes are planned separately from application.</summary>
public interface IMtkSecurityConfigurationService
{
    MtkSecurityChangePlan Plan(MtkFlashRange range, bool locked, CancellationToken cancellationToken = default);
    void Apply(MtkSecurityChangePlan plan, Stream backup, CancellationToken cancellationToken = default);
}
/// <summary>Owned original/replacement windows. Disposal clears both; callers must persist their own backup.</summary>
public sealed class MtkSecurityChangePlan : IDisposable
{
    public MtkSecurityChangePlan(long generation, MtkFlashRange range, bool locked, string algorithm, byte[] original, byte[] replacement)
    {
        Generation = generation;
        Range = range;
        Locked = locked;
        Algorithm = algorithm;
        Original = new(original);
        Replacement = new(replacement);
    }
    public long Generation
    {
        get;
    }
    public MtkFlashRange Range
    {
        get;
    }
    public bool Locked
    {
        get;
    }
    public string Algorithm
    {
        get;
    }
    public MtkSensitiveBuffer Original
    {
        get;
    }
    public MtkSensitiveBuffer Replacement
    {
        get;
    }
    public void Dispose()
    {
        Original.Dispose();
        Replacement.Dispose();
    }
}
/// <summary>A failed security write may have reached the device and requires reconnect and inspection.</summary>
public sealed class MtkSecurityWriteException : GeekFlashCore.Protocol.Abstractions.ProtocolException
{
    public MtkSecurityWriteException(Exception inner) : base(Localization.Strings.SecurityWriteUnknown, inner) { }
    public bool MayHaveWritten => true;
}
/// <summary>Explicit context for an already loaded compatible DA extension. No patching or payload boot is implied.</summary>
public sealed record MtkExtensionContext(ushort HardwareCode, uint Da2Base, uint Da2Size, uint SejBase = 0, uint TzccBase = 0, uint SsrBase = 0)
{
    /// <summary>Caller-confirmed ABI of the already loaded extension; never guessed by retries.</summary>
    public MtkExtensionAbi Abi { get; init; }
    /// <summary>Explicit UFS RPMB capacities (256-byte blocks); standard UFS info does not supply them.</summary>
    public IReadOnlyList<uint> UfsRpmbDataBlocks { get; init; } = [];
    public IReadOnlyList<MtkMemoryRange> AllowedMemoryRanges { get; init; } = [];
}
public enum MtkExtensionAbi { Legacy,Penumbra2 }
public enum MtkKeySize : byte { Key128,Key192,Key256 }
public enum MtkKeyDeriveId : uint { Rpmb,Fde,Tee,AesImageEncryption,AesCustom,Motorola,RootOfTrust }
public enum MtkSejKeyId : byte { Software,Hardware,HardwareWrapped,Rid,Custom }
public sealed record MtkSejParameters(bool Encrypt,bool AntiClone=true,bool Legacy=false,bool Xor=false,
    MtkAesMode Mode=MtkAesMode.Cbc,MtkSejKeyId Key=MtkSejKeyId.Software,MtkKeySize KeySize=MtkKeySize.Key256);
/// <summary>Host-approved memory window; no addresses are inferred for unknown hardware.</summary>
public readonly record struct MtkMemoryRange(uint Address, uint Length)
{
    public bool Contains(uint address, uint length) => Length > 0 && (ulong)Address + Length <= (ulong)uint.MaxValue + 1 &&
        address >= Address && length > 0 && (ulong)address + length <= (ulong)Address + Length;
}
