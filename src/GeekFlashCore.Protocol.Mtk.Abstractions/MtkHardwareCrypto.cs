namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Already authorized synchronous register access, bound to one serialized session.
/// Memory bytes are little-endian; aligned reads and writes never round outside the requested range.</summary>
public interface IMtkHardwareAccess
{
    uint Read32(uint address);
    void Write32(uint address, uint value);
    void ReadMemory(uint address, Span<byte> destination);
    void WriteMemory(uint address, ReadOnlySpan<byte> data);
    /// <summary>Checks session lifetime and its cancellation/deadline.</summary>
    void Check();
    /// <summary>Marks an interrupted hardware operation unusable until reconnect.</summary>
    void Invalidate();
}
/// <summary>Optional lifetime control on a BROM callback. Invalidate requires reconnect.</summary>
public interface IMtkBromSessionControl
{
    void Check();
    void Invalidate();
}
/// <summary>Standard Legacy/XML register access scoped to one DA gate and explicit approved windows.</summary>
public interface IMtkDaHardwareSessionAccess
{
    /// <summary>Runs one synchronous action with 1..256 approved aligned windows; retained access expires on return.</summary>
    T UseDaHardware<T>(IReadOnlyList<MtkMemoryRange> allowedRanges,Func<IMtkHardwareAccess,T> action,CancellationToken cancellationToken=default);
}
/// <summary>No padding is added by hardware AES operations.</summary>
public enum MtkAesMode { Ecb, Cbc }
/// <summary>Explicit peripheral and scratch layout, never inferred from an unknown chip.</summary>
public sealed record MtkHardwareCryptoProfile(uint BaseAddress)
{
    public MtkMemoryRange Scratch { get; init; }
    public int MaximumPolls { get; init; } = 1000;
    public int TimeoutMilliseconds { get; init; } = 10000;
    public int MaximumInputSize { get; init; } = 65536;
    public ushort HardwareCode { get; init; }
    /// <summary>Optional clock gate registers; zero means the caller has enabled the clock.</summary>
    public uint ClockEnableAddress { get; init; }
    public uint ClockEnableValue { get; init; }
    public uint ClockDisableAddress { get; init; }
    public uint ClockDisableValue { get; init; }
    public void Validate()
    {
        if (BaseAddress == 0 || (BaseAddress & 3) != 0 || (ulong)BaseAddress + 0x1000 > (ulong)uint.MaxValue + 1 ||
            MaximumPolls is < 1 or > 1000000 || TimeoutMilliseconds <= 0 || MaximumInputSize is < 16 or > 1048576 ||
            (ClockEnableAddress & 3) != 0 || (ClockDisableAddress & 3) != 0 ||
            (ClockEnableAddress == 0) != (ClockDisableAddress == 0) ||
            Scratch.Length != 0 && ((Scratch.Address & 3) != 0 || (Scratch.Length & 3) != 0 ||
                (ulong)Scratch.Address + Scratch.Length > (ulong)uint.MaxValue + 1 ||
                (ulong)Scratch.Address < (ulong)BaseAddress + 0x1000 && (ulong)BaseAddress < (ulong)Scratch.Address + Scratch.Length))
            throw new ArgumentOutOfRangeException(nameof(BaseAddress));
    }
}
