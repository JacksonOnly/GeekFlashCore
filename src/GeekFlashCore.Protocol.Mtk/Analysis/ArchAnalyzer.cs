// SPDX-License-Identifier: AGPL-3.0-or-later
// Based on Penumbra utils/analysis, Copyright (c) 2025-2026 Shomy.
using System.Text;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Analysis;

/// <summary>Shared bounded offline operations for the built-in analyzers. Each query owns its stream
/// and fixed-size cache; neither the borrowed source nor device state is changed.</summary>
public abstract class ArchAnalyzer : IArchAnalyzer
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private protected ArchAnalyzer(Arch architecture, IDataSource data, ulong baseAddress)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!Enum.IsDefined(architecture)) throw new ArgumentOutOfRangeException(nameof(architecture));
        long length = data.Length;
        if (length is < 0 or > 256L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(data));
        ulong maximum = architecture == Arch.Aarch64 ? ulong.MaxValue : uint.MaxValue;
        int alignment = architecture == Arch.Thumb2 ? 2 : 4;
        if (baseAddress > maximum || (baseAddress & (ulong)(alignment - 1)) != 0 ||
            (length > 0 && (ulong)(length - 1) > maximum - baseAddress))
            throw new ArgumentOutOfRangeException(nameof(baseAddress));
        Architecture = architecture; Data = data; BaseAddress = baseAddress; Length = length;
    }
    /// <inheritdoc/>
    public IDataSource Data { get; }
    /// <inheritdoc/>
    public Arch Architecture { get; }
    /// <inheritdoc/>
    public ulong BaseAddress { get; }
    /// <inheritdoc/>
    public long Length { get; }
    /// <inheritdoc/>
    public bool IsEmpty => Length == 0;
    /// <inheritdoc/>
    public long? VirtualAddressToOffset(ulong address)
    {
        if (Architecture == Arch.Thumb2) address &= ~1UL;
        return address >= BaseAddress && address - BaseAddress < (ulong)Length ? (long)(address - BaseAddress) : null;
    }
    /// <inheritdoc/>
    public ulong? OffsetToVirtualAddress(long offset) => offset >= 0 && offset < Length ? BaseAddress + (ulong)offset : null;
    /// <inheritdoc/>
    public uint? ReadUInt32(long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0 || offset > Length - 4) return null;
        using var reader = Open(cancellationToken); return reader.ReadUInt32(offset);
    }
    /// <inheritdoc/>
    public long? FindStringReference(string text, CancellationToken cancellationToken = default)
    {
        byte[] pattern = StringPattern(text, cancellationToken);
        if (IsEmpty) return null;
        using var reader = Open(cancellationToken); return StringReferenceCore(reader, pattern);
    }
    /// <inheritdoc/>
    public long? FindFunctionFromString(string text, CancellationToken cancellationToken = default)
    {
        byte[] pattern = StringPattern(text, cancellationToken);
        if (IsEmpty) return null;
        using var reader = Open(cancellationToken);
        long? reference = StringReferenceCore(reader, pattern);
        return reference.HasValue ? FunctionStartCore(reader, reference.Value) : null;
    }
    /// <inheritdoc/>
    public long? FindFunctionFromOffset(long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsOffsetAligned(offset)) return null;
        using var reader = Open(cancellationToken); return FunctionStartCore(reader, offset);
    }
    /// <inheritdoc/>
    public ulong? FindCallArgumentFromString(string text, byte argumentIndex, CancellationToken cancellationToken = default)
    {
        byte[] pattern = StringPattern(text, cancellationToken);
        if (argumentIndex > (Architecture == Arch.Aarch64 ? 7 : 3)) throw new ArgumentOutOfRangeException(nameof(argumentIndex));
        if (IsEmpty) return null;
        using var reader = Open(cancellationToken);
        long? reference = StringReferenceCore(reader, pattern);
        long? call = reference.HasValue ? NextBranchCore(reader, reference.Value, true) : null;
        return call.HasValue ? RegisterValueCore(reader, call.Value, argumentIndex, 50) : null;
    }
    /// <inheritdoc/>
    public ulong? BranchLinkTarget(long offset, CancellationToken cancellationToken = default) => Branch(offset, true, cancellationToken);
    /// <inheritdoc/>
    public ulong? BranchTarget(long offset, CancellationToken cancellationToken = default) => Branch(offset, false, cancellationToken);
    /// <inheritdoc/>
    public long? BranchLinkTargetOffset(long offset, CancellationToken cancellationToken = default)
    {
        ulong? address = BranchLinkTarget(offset, cancellationToken);
        return address.HasValue ? VirtualAddressToOffset(address.Value) : null;
    }
    /// <inheritdoc/>
    public long? NextBranchLinkFromOffset(long offset, CancellationToken cancellationToken = default) => NextBranch(offset, true, cancellationToken);
    /// <inheritdoc/>
    public long? NextBranchFromOffset(long offset, CancellationToken cancellationToken = default) => NextBranch(offset, false, cancellationToken);
    /// <inheritdoc/>
    public ulong? RegisterValue(long offset, byte register, int lookback, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (register > (Architecture == Arch.Aarch64 ? 30 : 14)) throw new ArgumentOutOfRangeException(nameof(register));
        if (lookback is < 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(lookback));
        if (lookback == 0 || offset <= 0 || offset > Length || offset % (Architecture == Arch.Thumb2 ? 2 : 4) != 0) return null;
        using var reader = Open(cancellationToken); return RegisterValueCore(reader, offset, register, lookback);
    }
    private ulong? Branch(long offset, bool link, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsOffsetAligned(offset)) return null;
        using var reader = Open(ct); return BranchTargetCore(reader, offset, link);
    }
    private long? NextBranch(long offset, bool link, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsOffsetAligned(offset)) return null;
        using var reader = Open(ct); return NextBranchCore(reader, offset, link);
    }
    private bool IsOffsetAligned(long offset) => offset >= 0 && offset < Length && offset % (Architecture == Arch.Thumb2 ? 2 : 4) == 0;
    private BinaryAnalysisReader Open(CancellationToken ct) => new(Data, Length, ct);
    private static byte[] StringPattern(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) throw new ArgumentException(Strings.FormatInvalidData("analysis string"), nameof(text));
        if (text.Length > 4095 || Utf8.GetByteCount(text) > 4095) throw new ArgumentOutOfRangeException(nameof(text));
        return Utf8.GetBytes(text);
    }
    internal long? FindString(BinaryAnalysisReader reader, byte[] pattern)
    {
        if (Architecture != Arch.Aarch64)
        {
            var terminated = new byte[pattern.Length + 1]; pattern.CopyTo(terminated, 0);
            long? found = reader.FindPattern(terminated); if (found.HasValue) return found;
        }
        return reader.FindPattern(pattern);
    }
    internal ulong? AddSigned(ulong address, long displacement)
    {
        ulong maximum = Architecture == Arch.Aarch64 ? ulong.MaxValue : uint.MaxValue;
        if (address > maximum) return null;
        if (displacement < 0)
        {
            ulong magnitude = (ulong)(-(displacement + 1)) + 1;
            return magnitude <= address ? address - magnitude : null;
        }
        return (ulong)displacement <= maximum - address ? address + (ulong)displacement : null;
    }
    internal abstract ulong? BranchTargetCore(BinaryAnalysisReader reader, long offset, bool link);
    internal abstract long? NextBranchCore(BinaryAnalysisReader reader, long offset, bool link);
    internal abstract long? StringReferenceCore(BinaryAnalysisReader reader, byte[] pattern);
    internal abstract long? FunctionStartCore(BinaryAnalysisReader reader, long offset);
    internal abstract ulong? RegisterValueCore(BinaryAnalysisReader reader, long offset, byte register, int lookback);
}
