using System;
using System.Runtime.CompilerServices;

namespace GeekFlashCore.Shared.Algorithms;

public static unsafe class Lz4Decoder
{
    private const int MinimumMatchLength = 4;

    public static bool TryDecode(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        bool allowPartialOutput,
        out int consumed)
    {
        fixed (byte* srcPtr = input)
        fixed (byte* dstPtr = output)
        {
            return Decode(
                srcPtr,
                input.Length,
                dstPtr,
                output.Length,
                allowPartialOutput,
                out consumed);
        }
    }


    private static bool Decode(
        byte* src,
        int srcLength,
        byte* dst,
        int dstLength,
        bool allowPartial,
        out int consumed)
    {
        int ip = 0;
        int op = 0;


        while (op < dstLength)
        {
            if ((uint)ip >= (uint)srcLength)
            {
                consumed = ip;
                return false;
            }


            byte token = src[ip++];


            int literalLength;

            if (!ReadLength(
                src,
                srcLength,
                ref ip,
                token >> 4,
                out literalLength))
            {
                consumed = ip;
                return false;
            }


            if (literalLength > srcLength - ip)
            {
                consumed = ip;
                return false;
            }


            int copyLiteral =
                Math.Min(
                    literalLength,
                    dstLength - op);


            Buffer.MemoryCopy(
                src + ip,
                dst + op,
                copyLiteral,
                copyLiteral);


            ip += literalLength;
            op += copyLiteral;


            if (copyLiteral != literalLength)
            {
                consumed = ip - (literalLength - copyLiteral);
                return allowPartial && op == dstLength;
            }


            if (op == dstLength)
            {
                consumed = ip;
                return true;
            }


            if (ip + 2 > srcLength)
            {
                consumed = ip;
                return false;
            }


            int distance =
                *(ushort*)(src + ip);

            ip += 2;


            if (distance == 0 || distance > op)
            {
                consumed = ip;
                return false;
            }


            int matchLength;

            if (!ReadLength(
                src,
                srcLength,
                ref ip,
                token & 0xF,
                out matchLength))
            {
                consumed = ip;
                return false;
            }


            matchLength += MinimumMatchLength;


            int writable =
                Math.Min(
                    matchLength,
                    dstLength - op);


            CopyMatch(
                dst,
                op,
                distance,
                writable);


            op += writable;


            if (writable != matchLength)
            {
                consumed = ip;
                return allowPartial && op == dstLength;
            }
        }


        consumed = ip;
        return true;
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ReadLength(
        byte* src,
        int srcLength,
        ref int ip,
        int length,
        out int result)
    {
        result = length;


        if (length != 15)
            return true;


        while (true)
        {
            if ((uint)ip >= (uint)srcLength)
                return false;


            byte value = src[ip++];


            if (result > int.MaxValue - value)
                return false;


            result += value;


            if (value != 255)
                break;
        }


        return true;
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyMatch(
        byte* dst,
        int offset,
        int distance,
        int length)
    {
        byte* source = dst + offset - distance;
        byte* target = dst + offset;


        if (distance >= 8)
        {
            while (length >= 8)
            {
                *(ulong*)target =
                    *(ulong*)source;

                target += 8;
                source += 8;
                length -= 8;
            }
        }


        while (length-- > 0)
        {
            *target++ = *source++;
        }
    }
}