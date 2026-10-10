// SPDX-License-Identifier: AGPL-3.0-or-later
// Based on Penumbra utils/analysis/arm.rs, Copyright (c) 2025-2026 Shomy.
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Analysis;

/// <summary>Bounded ARM/A32 analysis of caller-selected data. This is a supported-instruction heuristic, not a full disassembler.</summary>
public sealed class ArmAnalyzer : ArchAnalyzer
{
    /// <summary>Borrows a stable source mapped at an aligned 32-bit virtual address; no stream is opened.</summary>
    public ArmAnalyzer(IDataSource data, ulong baseAddress) : base(Arch.Arm, data, baseAddress) { }
    /// <summary>Decodes a MOVW word to its destination register and unsigned 16-bit immediate.</summary>
    public static (byte Register, uint Immediate)? DecodeMovw(uint instruction) =>
        (instruction & 0x0FF00000) == 0x03000000 && instruction >> 28 != 15
        ? ((byte)((instruction >> 12) & 15), ((instruction >> 16) & 15) << 12 | (instruction & 0xFFF)) : null;
    /// <summary>Decodes a MOVT word to its destination register and unsigned high-half immediate.</summary>
    public static (byte Register, uint Immediate)? DecodeMovt(uint instruction) =>
        (instruction & 0x0FF00000) == 0x03400000 && instruction >> 28 != 15
        ? ((byte)((instruction >> 12) & 15), ((instruction >> 16) & 15) << 12 | (instruction & 0xFFF)) : null;
    /// <summary>Decodes unshifted SUB register to (left source, right source, destination).</summary>
    public static (byte Left, byte Right, byte Destination)? DecodeSubRegister(uint instruction) =>
        (instruction & 0x0FE00FF0) == 0x00400000 && instruction >> 28 != 15
        ? ((byte)((instruction >> 16) & 15), (byte)(instruction & 15), (byte)((instruction >> 12) & 15)) : null;
    /// <summary>Recognizes BX LR encodings; it does not execute a return.</summary>
    public static bool IsReturn(uint instruction) => (instruction & 0x0FFFFFFF) == 0x012FFF1E && instruction >> 28 != 15;
    private static (byte Source, byte Destination)? DecodeMove(uint instruction) =>
        (instruction & 0x0FE00FF0) == 0x01A00000
        ? ((byte)(instruction & 15), (byte)((instruction >> 12) & 15)) : null;
    private (byte Register, ulong Address)? DecodeLiteral(uint instruction, ulong address)
    {
        if ((instruction & 0x0F7F0000) != 0x051F0000 || instruction >> 28 == 15) return null;
        ulong? pc = AddSigned(address, 8);
        ulong? target = pc.HasValue ? AddSigned(pc.Value, (instruction & 0x00800000) != 0 ? instruction & 0xFFF : -(long)(instruction & 0xFFF)) : null;
        return target.HasValue ? ((byte)((instruction >> 12) & 15), target.Value) : null;
    }
    internal override ulong? BranchTargetCore(BinaryAnalysisReader reader, long offset, bool link)
    {
        uint? instruction = reader.ReadUInt32(offset);
        if (!instruction.HasValue || instruction.Value >> 28 == 15 ||
            (instruction.Value & 0x0F000000) != (link ? 0x0B000000U : 0x0A000000U)) return null;
        long displacement = ((int)(instruction.Value << 8) >> 8) * 4L;
        return AddSigned(BaseAddress + (ulong)offset, displacement + 8);
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
        uint address = (uint)(BaseAddress + (ulong)text.Value);
        for (long offset = 0; offset <= Length - 4; offset += 4)
        {
            uint word = reader.ReadUInt32(offset)!.Value;
            if (DecodeMovw(word) is { } low && low.Immediate == (address & 0xFFFF))
            {
                long end = Math.Min(Length - 4, offset + 80);
                for (long next = offset + 4; next <= end; next += 4)
                {
                    uint highWord = reader.ReadUInt32(next)!.Value;
                    if (DecodeMovt(highWord) is { } high && high.Register == low.Register && high.Immediate == address >> 16 &&
                        Resolve(reader, next, offset, low.Register, 0, new ResolutionBudget()) == address) return offset;
                }
            }
            if (DecodeLiteral(word, BaseAddress + (ulong)offset) is { } literal)
            {
                long? pool = VirtualAddressToOffset(literal.Address);
                if (literal.Address == address || (pool.HasValue && reader.ReadUInt32(pool.Value) == address)) return offset;
            }
        }
        return null;
    }
    internal override long? FunctionStartCore(BinaryAnalysisReader reader, long offset)
    {
        long end = Math.Max(0, offset - 0x2000);
        for (long current = offset; current >= end; current -= 4)
            if (reader.ReadUInt32(current) is { } word && (word & 0xFFFF4000) == 0xE92D4000) return current;
        return null;
    }
    internal override ulong? RegisterValueCore(BinaryAnalysisReader reader, long offset, byte register, int lookback) =>
        Resolve(reader, offset - 4, Math.Max(0, offset - lookback * 4L), register, 0, new ResolutionBudget());
    private ulong? Resolve(BinaryAnalysisReader reader, long start, long end, byte register, int depth, ResolutionBudget budget)
    {
        if (depth > 10 || !budget.Take() || start < end || register > 14) return null;
        uint? high = null;
        for (long offset = start; offset >= end; offset -= 4)
        {
            if (reader.ReadUInt32(offset) is not { } word) return null;
            if ((word & 0x0E000000) == 0x0A000000 || (word & 0x0FFFFFF0) == 0x012FFF10) return null;
            if (word >> 28 != 14)
            {
                // A decoded conditional MOVW/MOVT to another register cannot change
                // this value. Do not assume other conditional/aliased encodings are
                // harmless, or evaluate a conditional write to the tracked register.
                bool otherMove = DecodeMovw(word) is { } conditionalLow && conditionalLow.Register != register ||
                    DecodeMovt(word) is { } conditionalHigh && conditionalHigh.Register != register;
                if (!otherMove) return null;
                continue;
            }
            if (DecodeMovt(word) is { } upper && upper.Register == register) { high ??= upper.Immediate << 16; continue; }
            if (DecodeMovw(word) is { } lower && lower.Register == register) return lower.Immediate | (high ?? 0);
            if (DecodeSubRegister(word) is { } sub && sub.Destination == register)
            {
                ulong? left = Resolve(reader, offset - 4, end, sub.Left, depth + 1, budget);
                ulong? right = Resolve(reader, offset - 4, end, sub.Right, depth + 1, budget);
                if (!left.HasValue || !right.HasValue) return null;
                uint value = unchecked((uint)left.Value - (uint)right.Value);
                return high.HasValue ? (value & 0xFFFF) | high.Value : value;
            }
            if (DecodeLiteral(word, BaseAddress + (ulong)offset) is { } literal && literal.Register == register)
            {
                long? pool = VirtualAddressToOffset(literal.Address);
                uint? value = pool.HasValue ? reader.ReadUInt32(pool.Value) : null;
                return value.HasValue ? (high.HasValue ? (value.Value & 0xFFFF) | high.Value : value.Value) : null;
            }
            if (DecodeMove(word) is { } move && move.Destination == register) { register = move.Source; if (register == 15) return null; continue; }
            if (WritesRegister(word, register)) return null;
        }
        return null;
    }
    private static bool WritesRegister(uint word, byte register)
    {
        // Data processing (excluding compares), loads, and LDM destinations / writeback bases.
        if ((word & 0x0C000000) == 0)
        {
            uint opcode = (word >> 21) & 15;
            return opcode is not (>= 8 and <= 11) && ((word >> 12) & 15) == register;
        }
        if ((word & 0x0C100000) == 0x04100000 && ((word >> 12) & 15) == register) return true;
        if ((word & 0x0E000000) == 0x08000000)
            return ((word & 0x00100000) != 0 && (word & (1U << register)) != 0) ||
                ((word & 0x00200000) != 0 && ((word >> 16) & 15) == register);
        return (word & 0x04200000) == 0x04200000 && ((word >> 16) & 15) == register;
    }
}
