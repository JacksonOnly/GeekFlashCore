// SPDX-License-Identifier: AGPL-3.0-or-later
// Based on Penumbra utils/analysis/mod.rs, Copyright (c) 2025-2026 Shomy.
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Analysis;

/// <summary>Offline heuristic analysis of a stable borrowed source. Offsets are source-relative;
/// null denotes no supported match, not a validated execution target.</summary>
public interface IArchAnalyzer
{
    /// <summary>Borrowed source; it must remain stable for the lifetime of the analyzer.</summary>
    IDataSource Data { get; }
    /// <summary>Host-selected instruction architecture.</summary>
    Arch Architecture { get; }
    /// <summary>Host-selected virtual address of source offset zero.</summary>
    ulong BaseAddress { get; }
    /// <summary>Captured source length in bytes.</summary>
    long Length { get; }
    /// <summary>Whether the captured source has no bytes.</summary>
    bool IsEmpty { get; }
    /// <summary>Reads an unaligned LE word, or null outside a complete four-byte range.</summary>
    uint? ReadUInt32(long offset, CancellationToken cancellationToken = default);
    /// <summary>Converts a mapped virtual address to a source offset; Thumb clears the state bit.</summary>
    long? VirtualAddressToOffset(ulong address);
    /// <summary>Converts an in-range source offset to a virtual address without a Thumb state bit.</summary>
    ulong? OffsetToVirtualAddress(long offset);
    /// <summary>Finds a supported reference to the first occurrence of caller-supplied UTF-8 text.</summary>
    long? FindStringReference(string text, CancellationToken cancellationToken = default);
    /// <summary>Finds a common function prologue preceding a supported string reference.</summary>
    long? FindFunctionFromString(string text, CancellationToken cancellationToken = default);
    /// <summary>Finds a common prologue within the architecture's bounded backward window.</summary>
    long? FindFunctionFromOffset(long offset, CancellationToken cancellationToken = default);
    /// <summary>Resolves a register argument before the next direct call after a string reference;
    /// only register arguments (ARM/Thumb 0-3, AArch64 0-7) are supported.</summary>
    ulong? FindCallArgumentFromString(string text, byte argumentIndex, CancellationToken cancellationToken = default);
    /// <summary>Decodes a direct BL target; B and indirect calls do not match.</summary>
    ulong? BranchLinkTarget(long offset, CancellationToken cancellationToken = default);
    /// <summary>Decodes a direct B target; BL and indirect jumps do not match.</summary>
    ulong? BranchTarget(long offset, CancellationToken cancellationToken = default);
    /// <summary>Converts a direct call target to an offset in this source.</summary>
    long? BranchLinkTargetOffset(long offset, CancellationToken cancellationToken = default);
    /// <summary>Finds the next complete direct BL instruction at or after an instruction boundary.</summary>
    long? NextBranchLinkFromOffset(long offset, CancellationToken cancellationToken = default);
    /// <summary>Finds the next complete direct B instruction at or after an instruction boundary.</summary>
    long? NextBranchFromOffset(long offset, CancellationToken cancellationToken = default);
    /// <summary>Resolves a supported local register value immediately before the query offset.
    /// Lookback is bounded to 4096 instructions; unsupported writes/control flow return null.</summary>
    ulong? RegisterValue(long offset, byte register, int lookback, CancellationToken cancellationToken = default);
}
