// SPDX-License-Identifier: AGPL-3.0-or-later
// Based on Penumbra utils/analysis/thumb.rs, Copyright (c) 2026 Shomy.
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Analysis;

/// <summary>Bounded mixed Thumb/T32 analysis. Offset zero must be an instruction boundary supplied by the host.</summary>
public sealed class Thumb2Analyzer : ArchAnalyzer
{
    /// <summary>Borrows a stable source at an even 32-bit address without opening a stream.</summary>
    public Thumb2Analyzer(IDataSource data, ulong baseAddress) : base(Arch.Thumb2, data, baseAddress) { }
    /// <summary>Decodes MOVW from a T32 word (first LE halfword in bits 31-16) to register and immediate.</summary>
    public static (byte Register, uint Immediate)? DecodeMovw(uint instruction) => DecodeWideMove(instruction, 0xF2400000);
    /// <summary>Decodes MOVT from a T32 word (first LE halfword in bits 31-16) to register and immediate.</summary>
    public static (byte Register, uint Immediate)? DecodeMovt(uint instruction) => DecodeWideMove(instruction, 0xF2C00000);
    private static (byte Register, uint Immediate)? DecodeWideMove(uint word, uint opcode) =>
        (word & 0xFBF08000) == opcode ? ((byte)((word >> 8) & 15),
            ((word >> 16) & 15) << 12 | ((word >> 26) & 1) << 11 | ((word >> 12) & 7) << 8 | (word & 255)) : null;
    /// <summary>Decodes an unshifted T32 SUB register to (left source, right source, destination).</summary>
    public static (byte Left, byte Right, byte Destination)? DecodeSubRegister(uint instruction) =>
        (instruction & 0xFFE0F0F0) == 0xEBA00000
        ? ((byte)((instruction >> 16) & 15), (byte)(instruction & 15), (byte)((instruction >> 8) & 15)) : null;
    /// <summary>Recognizes a 16-bit BX LR return word.</summary>
    public static bool IsReturn(ushort halfword) => halfword == 0x4770;
    private static (byte Source, byte Destination)? DecodeMove(ushort halfword) => (halfword & 0xFF00) == 0x4600
        ? ((byte)((halfword >> 3) & 15), (byte)((halfword & 7) | ((halfword >> 4) & 8))) : null;
    private static int InstructionSize(BinaryAnalysisReader reader, long offset)
    {
        if (reader.ReadUInt16(offset) is not { } halfword) return 0;
        int size = halfword >> 11 is 29 or 30 or 31 ? 4 : 2;
        return offset <= reader.Length - size ? size : 0;
    }
    private static uint? ReadWide(BinaryAnalysisReader reader, long offset)
    {
        ushort? first = reader.ReadUInt16(offset), second = reader.ReadUInt16(offset + 2);
        return first.HasValue && second.HasValue ? ((uint)first.Value << 16) | second.Value : null;
    }
    private static bool IsBoundary(BinaryAnalysisReader reader, long target)
    {
        long offset = 0;
        while (offset < target) { int size = InstructionSize(reader, offset); if (size == 0) return false; offset += size; }
        return offset == target;
    }
    private ulong? DecodeBranch(BinaryAnalysisReader reader, long offset, bool link)
    {
        int size = InstructionSize(reader, offset);
        if (size == 2 && !link && reader.ReadUInt16(offset) is { } halfword && (halfword & 0xF800) == 0xE000)
        {
            long immediate = halfword & 0x7FF; if ((immediate & 0x400) != 0) immediate -= 0x800;
            return AddSigned(BaseAddress + (ulong)offset, immediate * 2 + 4);
        }
        if (size != 4 || ReadWide(reader, offset) is not { } word ||
            (word & 0xF800D000) != (link ? 0xF000D000U : 0xF0009000U)) return null;
        uint first = word >> 16, second = word & 0xFFFF;
        uint sign = (first >> 10) & 1;
        uint i1 = ~((second >> 13) ^ sign) & 1, i2 = ~((second >> 11) ^ sign) & 1;
        long displacement = sign << 24 | i1 << 23 | i2 << 22 | (first & 0x3FF) << 12 | (second & 0x7FF) << 1;
        if (sign != 0) displacement -= 1L << 25;
        return AddSigned(BaseAddress + (ulong)offset, displacement + 4);
    }
    internal override ulong? BranchTargetCore(BinaryAnalysisReader reader, long offset, bool link) =>
        IsBoundary(reader, offset) ? DecodeBranch(reader, offset, link) : null;
    internal override long? NextBranchCore(BinaryAnalysisReader reader, long offset, bool link)
    {
        if (!IsBoundary(reader, offset)) return null;
        for (long current = offset; current < Length;)
        {
            int size = InstructionSize(reader, current); if (size == 0) break;
            if (DecodeBranch(reader, current, link).HasValue) return current;
            current += size;
        }
        return null;
    }
    private (byte Register, ulong Address)? DecodeLiteral(BinaryAnalysisReader reader, long offset)
    {
        int size = InstructionSize(reader, offset);
        ulong? pc = AddSigned(BaseAddress + (ulong)offset, 4); if (!pc.HasValue) return null;
        ulong aligned = pc.Value & ~3UL;
        if (size == 2 && reader.ReadUInt16(offset) is { } halfword && (halfword & 0xF800) == 0x4800)
        {
            ulong? address = AddSigned(aligned, (halfword & 255) * 4L);
            return address.HasValue ? ((byte)((halfword >> 8) & 7), address.Value) : null;
        }
        if (size != 4 || ReadWide(reader, offset) is not { } word || (word & 0xFF7F0000) != 0xF85F0000) return null;
        ulong? target = AddSigned(aligned, (word & 0x00800000) != 0 ? word & 0xFFF : -(long)(word & 0xFFF));
        return target.HasValue ? ((byte)((word >> 12) & 15), target.Value) : null;
    }
    internal override long? StringReferenceCore(BinaryAnalysisReader reader, byte[] pattern)
    {
        long? text = FindString(reader, pattern); if (!text.HasValue) return null;
        uint address = (uint)(BaseAddress + (ulong)text.Value);
        for (long offset = 0; offset < Length;)
        {
            int size = InstructionSize(reader, offset); if (size == 0) break;
            if (size == 4 && ReadWide(reader, offset) is { } word && DecodeMovw(word) is { } low && low.Immediate == (address & 0xFFFF))
            {
                long end = Math.Min(Length, offset + 80);
                for (long next = offset + 4; next < end;)
                {
                    int nextSize = InstructionSize(reader, next); if (nextSize == 0) break;
                    if (nextSize == 4 && ReadWide(reader, next) is { } highWord && DecodeMovt(highWord) is { } high &&
                        high.Register == low.Register && high.Immediate == address >> 16)
                    {
                        ThumbHistory? history = History(reader, offset, next + 4, 40);
                        if (history is not null && Resolve(reader, history, history.Count - 1, low.Register, 0, new ResolutionBudget()) == address) return offset;
                    }
                    next += nextSize;
                }
            }
            if (DecodeLiteral(reader, offset) is { } literal)
            {
                long? pool = VirtualAddressToOffset(literal.Address);
                uint? pointer = pool.HasValue ? reader.ReadUInt32(pool.Value) : null;
                if (literal.Address == address || pointer == address || (pointer.HasValue && (pointer.Value & ~1U) == address)) return offset;
            }
            offset += size;
        }
        return null;
    }
    internal override long? FunctionStartCore(BinaryAnalysisReader reader, long offset)
    {
        long? result = null; long end = Math.Max(0, offset - 0x2000);
        for (long current = 0; current <= offset;)
        {
            int size = InstructionSize(reader, current); if (size == 0) break;
            if (current >= end && ((size == 2 && reader.ReadUInt16(current) is { } halfword && (halfword & 0xFF00) == 0xB500) ||
                (size == 4 && ReadWide(reader, current) is { } word && (word & 0xFFFF4000) == 0xE92D4000))) result = current;
            current += size;
        }
        return result;
    }
    internal override ulong? RegisterValueCore(BinaryAnalysisReader reader, long offset, byte register, int lookback)
    {
        ThumbHistory? history = History(reader, 0, offset, lookback);
        return history is null ? null : Resolve(reader, history, history.Count - 1, register, 0, new ResolutionBudget());
    }
    private static ThumbHistory? History(BinaryAnalysisReader reader, long start, long at, int lookback)
    {
        var history = new ThumbHistory(lookback); long offset = start;
        while (offset < at)
        {
            int size = InstructionSize(reader, offset); if (size == 0 || offset + size > at) return null;
            history.Add(offset); offset += size;
        }
        return offset == at ? history : null;
    }
    private ulong? Resolve(BinaryAnalysisReader reader, ThumbHistory history, int index, byte register, int depth, ResolutionBudget budget)
    {
        if (register > 14 || depth > 10 || !budget.Take()) return null;
        uint? high = null;
        for (; index >= 0; index--)
        {
            long offset = history[index]; int size = InstructionSize(reader, offset);
            ushort halfword = reader.ReadUInt16(offset)!.Value;
            if ((size == 4 && ReadWide(reader, offset) is { } branch && (branch & 0xF8008000) == 0xF0008000) ||
                (size == 2 && ((halfword & 0xF800) == 0xE000 || (halfword & 0xFF00) is 0x4700 or 0xBD00 ||
                    (halfword & 0xF000) == 0xD000 || (halfword & 0xF500) == 0xB100))) return null;
            if (size == 4 && ReadWide(reader, offset) is { } word)
            {
                if (DecodeMovt(word) is { } upper && upper.Register == register) { high ??= upper.Immediate << 16; continue; }
                if (DecodeMovw(word) is { } lower && lower.Register == register) return lower.Immediate | (high ?? 0);
                if (DecodeSubRegister(word) is { } sub && sub.Destination == register)
                {
                    ulong? left = Resolve(reader, history, index - 1, sub.Left, depth + 1, budget);
                    ulong? right = Resolve(reader, history, index - 1, sub.Right, depth + 1, budget);
                    if (!left.HasValue || !right.HasValue) return null;
                    uint value = unchecked((uint)left.Value - (uint)right.Value);
                    return high.HasValue ? (value & 0xFFFF) | high.Value : value;
                }
            }
            if (DecodeLiteral(reader, offset) is { } literal && literal.Register == register)
            {
                long? pool = VirtualAddressToOffset(literal.Address);
                uint? value = pool.HasValue ? reader.ReadUInt32(pool.Value) : null;
                return value.HasValue ? (high.HasValue ? (value.Value & 0xFFFF) | high.Value : value.Value) : null;
            }
            if (size == 2 && DecodeMove(halfword) is { } move && move.Destination == register)
            { register = move.Source; if (register > 14) return null; continue; }
            if (size == 2 ? WritesRegister(halfword, register) : ReadWide(reader, offset) is { } unsupported && ((unsupported >> 8) & 15) == register) return null;
        }
        return null;
    }
    private static bool WritesRegister(ushort word, byte register)
    {
        if ((word & 0xE000) == 0 || (word & 0xF800) is 0x6800 or 0x7800 or 0x8800) return (word & 7) == register;
        if ((word & 0xE000) == 0x2000 && (word & 0xF800) != 0x2800) return ((word >> 8) & 7) == register;
        if ((word & 0xFF00) == 0x4400) return ((word & 7) | ((word >> 4) & 8)) == register;
        if ((word & 0xFE00) == 0xBC00) return (word & (1 << register)) != 0 || register == 13;
        return (word & 0xFF00) == 0xB000 && register == 13;
    }
    private sealed class ThumbHistory(int capacity)
    {
        private readonly long[] _offsets = new long[capacity];
        private int _next;
        public int Count { get; private set; }
        public void Add(long offset) { _offsets[_next] = offset; _next = (_next + 1) % capacity; Count = Math.Min(Count + 1, capacity); }
        public long this[int index] => _offsets[((Count == capacity ? _next : 0) + index) % capacity];
    }
}
