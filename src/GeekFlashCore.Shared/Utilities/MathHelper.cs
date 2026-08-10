using System.Runtime.CompilerServices;

// ReSharper disable once CheckNamespace
namespace GeekFlashCore;

public static class MathHelper
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DivRoundUp(uint value, uint step) => (value + step - 1) / step * step;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Align(uint x, uint y) => y * DivRoundUp(x, y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AlignDown(uint x, uint y) => y * (x / y);
}