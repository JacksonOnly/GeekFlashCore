// SPDX-License-Identifier: AGPL-3.0-or-later
// Based on Penumbra utils/analysis, Copyright (c) 2025-2026 Shomy.
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Analysis;

/// <summary>Explicit architecture dispatch equivalent to Penumbra's Analyzer enum; it performs no architecture detection.</summary>
public sealed class Analyzer : ArchAnalyzer
{
    /// <summary>Wraps an explicitly chosen built-in analyzer without opening its source.</summary>
    public Analyzer(ArchAnalyzer implementation) : base(
        (implementation ?? throw new ArgumentNullException(nameof(implementation))).Architecture,
        implementation.Data, implementation.BaseAddress)
    {
        if (Length != implementation.Length) throw new MtkResourceException("analysis source length");
        Implementation = implementation is Analyzer wrapper ? wrapper.Implementation : implementation;
    }
    /// <summary>Creates the selected analyzer over a stable borrowed source and an aligned host-provided base address.</summary>
    public Analyzer(Arch architecture, IDataSource data, ulong baseAddress) : this(Create(architecture, data, baseAddress)) { }
    /// <summary>The explicitly selected implementation.</summary>
    public ArchAnalyzer Implementation { get; }
    private static ArchAnalyzer Create(Arch architecture, IDataSource data, ulong baseAddress) => architecture switch
    {
        Arch.Arm => new ArmAnalyzer(data, baseAddress),
        Arch.Aarch64 => new Aarch64Analyzer(data, baseAddress),
        Arch.Thumb2 => new Thumb2Analyzer(data, baseAddress),
        _ => throw new ArgumentOutOfRangeException(nameof(architecture))
    };
    internal override ulong? BranchTargetCore(BinaryAnalysisReader reader, long offset, bool link) => Implementation.BranchTargetCore(reader, offset, link);
    internal override long? NextBranchCore(BinaryAnalysisReader reader, long offset, bool link) => Implementation.NextBranchCore(reader, offset, link);
    internal override long? StringReferenceCore(BinaryAnalysisReader reader, byte[] pattern) => Implementation.StringReferenceCore(reader, pattern);
    internal override long? FunctionStartCore(BinaryAnalysisReader reader, long offset) => Implementation.FunctionStartCore(reader, offset);
    internal override ulong? RegisterValueCore(BinaryAnalysisReader reader, long offset, byte register, int lookback) => Implementation.RegisterValueCore(reader, offset, register, lookback);
}
