namespace GeekFlashCore.FileSystem.Abstractions;

public readonly record struct FileSystemName
{
    public FileSystemName(
        FileSystemNameState state,
        ReadOnlyMemory<byte> rawBytes,
        string? text = null)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (state == FileSystemNameState.Plain && text is null)
            throw new ArgumentNullException(nameof(text));
        State = state;
        RawBytes = rawBytes;
        Text = text;
    }

    public FileSystemNameState State { get; }
    public ReadOnlyMemory<byte> RawBytes { get; }
    public string? Text { get; }
    public override string ToString() => Text ?? Convert.ToHexString(RawBytes.Span);
}