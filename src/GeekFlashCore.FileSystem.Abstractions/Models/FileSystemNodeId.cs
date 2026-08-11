namespace GeekFlashCore.FileSystem.Abstractions;

public readonly record struct FileSystemNodeId(ulong Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}