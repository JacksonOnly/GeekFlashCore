// SPDX-License-Identifier: AGPL-3.0-or-later
// Based on Penumbra utils/analysis/aarch64.rs, Copyright (c) 2025-2026 Shomy.
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Analysis;

/// <summary>Bounded A64 analysis using ADRP/ADD and register-copy heuristics, not full control-flow simulation.</summary>
public sealed class Aarch64Analyzer : ArchAnalyzer
{
    /// <summary>Borrows a stable source at an aligned host-selected virtual address without opening a stream.</summary>
    public Aarch64Analyzer(IDataSource data, ulong baseAddress) : base(Arch.Aarch64, data, baseAddress) { }
    /// <summary>Decodes ADRP to a page address and destination register; unrepresentable address arithmetic returns null.</summary>
    public static (ulong PageAddress, byte Register)? DecodeAdrp(uint instruction, ulong pc)
    {
        if ((instruction & 0x9F000000) != 0x90000000) return null;
        long immediate = ((instruction >> 5) & 0x7FFFF) << 2 | ((instruction >> 29) & 3);
        if ((immediate & 0x100000) != 0) immediate -= 0x200000;
        ulong page = pc & ~0xFFFUL; long displacement = immediate << 12;
        if (displacement < 0 && (ulong)-displacement > page || displacement >= 0 && (ulong)displacement > ulong.MaxValue - page) return null;
        ulong address = displacement < 0 ? page - (ulong)-displacement : page + (ulong)displacement;
        return (address, (byte)(instruction & 31));
    }
    /// <summary>Decodes supported 64-bit ADD immediate to (source, destination, shifted immediate).</summary>
    public static (byte Source, byte Destination, uint Immediate)? DecodeAddImmediate(uint instruction)
    {
        if ((instruction & 0xFF800000) != 0x91000000) return null;
        uint immediate = (instruction >> 10) & 0xFFF;
        if ((instruction & 0x00400000) != 0) immediate <<= 12;
        return ((byte)((instruction >> 5) & 31), (byte)(instruction & 31), immediate);
    }
    /// <summary>Recognizes the PACIASP/PACIBSP prologue words supported here, not an authentication result.</summary>
    public static bool IsPointerAuthentication(uint instruction) => instruction is 0xD503233F or 0xD503237F;
    private static (byte Source, byte Destination)? DecodeMove(uint instruction) =>
        (instruction & 0xFFE0FFE0) == 0xAA0003E0 ? ((byte)((instruction >> 16) & 31), (byte)(instruction & 31)) : null;
    internal override ulong? BranchTargetCore(BinaryAnalysisReader reader, long offset, bool link)
    {
        if (reader.ReadUInt32(offset) is not { } word || (word & 0xFC000000) != (link ? 0x94000000U : 0x14000000U)) return null;
        long displacement = ((int)(word << 6) >> 6) * 4L;
        return AddSigned(BaseAddress + (ulong)offset, displacement);
    }
    internal override long? NextBranchCore(BinaryAnalysisReader reader, long offset, bool link)
    {
        for (long current = offset; current <= Length - 4; current += 4)
            if (BranchTargetCore(reader, current, link).HasValue) return current;
        return null;
    }
    internal override long? StringReferenceCore(BinaryAnalysisReader reader, byte[] pattern)
    {
        long? text = FindString(reader, pattern); if (!text.HasValue) return null;
        ulong address = BaseAddress + (ulong)text.Value;
        for (long offset = 0; offset <= Length - 4; offset += 4)
        {
            uint word = reader.ReadUInt32(offset)!.Value;
            if (DecodeAdrp(word, BaseAddress + (ulong)offset) is not { } page || page.PageAddress != (address & ~0xFFFUL) || page.Register == 31) continue;
            long end = Math.Min(Length - 4, offset + 60);
            for (long next = offset + 4; next <= end; next += 4)
            {
                uint addWord = reader.ReadUInt32(next)!.Value;
                if (DecodeAddImmediate(addWord) is { } add && add.Source == page.Register && add.Destination != 31 &&
                    AddSigned(page.PageAddress, add.Immediate) == address &&
                    Resolve(reader, next, offset, add.Destination, 0, new ResolutionBudget()) == address) return next;
            }
        }
        return null;
    }
    internal override long? FunctionStartCore(BinaryAnalysisReader reader, long offset)
    {
        long end = Math.Max(0, offset - 0x5000);
        for (long current = offset; current >= end; current -= 4)
        {
            if (reader.ReadUInt32(current) is not { } word) continue;
            if (IsPointerAuthentication(word)) return current;
            if ((word & 0xFFC07FFF) == 0xA9807BFD)
                return current >= 4 && reader.ReadUInt32(current - 4) is { } previous && IsPointerAuthentication(previous) ? current - 4 : current;
            if (current >= 4 && (word & 0xFFC003FF) == (0xA9007BFD & 0xFFC003FF) &&
                reader.ReadUInt32(current - 4) is { } sub && (sub & 0xFFC003FF) == 0xD10003FF) return current - 4;
        }
        return null;
    }
    internal override ulong? RegisterValueCore(BinaryAnalysisReader reader, long offset, byte register, int lookback) =>
        Resolve(reader, offset - 4, Math.Max(0, offset - lookback * 4L), register, 0, new ResolutionBudget());
    private ulong? Resolve(BinaryAnalysisReader reader, long start, long end, byte register, int depth, ResolutionBudget budget)
    {
        if (register == 31 || start < end || depth > 10 || !budget.Take()) return null;
        for (long offset = start; offset >= end; offset -= 4)
        {
            if (reader.ReadUInt32(offset) is not { } word) return null;
            if ((word & 0x7C000000) == 0x14000000 || (word & 0xFE000000) == 0x54000000 ||
                (word & 0xFE000000) == 0xD6000000 || (word & 0x7E000000) is 0x34000000 or 0x36000000) return null;
            if (DecodeAddImmediate(word) is { } add && add.Destination == register)
            {
                ulong? value = Resolve(reader, offset - 4, end, add.Source, depth + 1, budget);
                return value.HasValue ? AddSigned(value.Value, add.Immediate) : null;
            }
            if (DecodeMove(word) is { } move && move.Destination == register)
            {
                if (move.Source == 31) return 0;
                register = move.Source; continue;
            }
            if (DecodeAdrp(word, BaseAddress + (ulong)offset) is { } page && page.Register == register) return page.PageAddress;
            if ((word & 31) == register && word != 0xD503201F) return null;
        }
        return null;
    }
}
