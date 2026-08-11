namespace GeekFlashCore.BlockDevice.Abstractions;

public readonly record struct BlockDeviceId
{
    public BlockDeviceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value ?? string.Empty;
}
