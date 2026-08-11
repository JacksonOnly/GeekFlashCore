namespace GeekFlashCore.BlockDevice.Abstractions;

public sealed record BlockDeviceOpenOptions
{
    public bool Writable { get; init; }
    public bool EnableReadAhead { get; init; } = true;
    public int? ReadAheadSize { get; init; }
    public BlockDeviceLimits Limits { get; init; } = BlockDeviceLimits.Default;
}
