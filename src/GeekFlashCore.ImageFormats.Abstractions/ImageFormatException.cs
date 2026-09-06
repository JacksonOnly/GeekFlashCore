namespace GeekFlashCore.ImageFormats.Abstractions;

public sealed class ImageFormatException : Exception
{
    public ImageFormatException(
        ImageFormatErrorCode errorCode,
        string message,
        ImageFormatDiagnostic diagnostic,
        Exception? innerException = null)
        : this(errorCode, message, diagnostic, innerException, null)
    {
    }

    public ImageFormatException(
        ImageFormatErrorCode errorCode,
        string message,
        ImageFormatDiagnostic diagnostic,
        Exception? innerException,
        IReadOnlyList<object?>? resourceArguments)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(errorCode))
        {
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        }

        ArgumentNullException.ThrowIfNull(diagnostic);

        ErrorCode = errorCode;
        Diagnostic = diagnostic;
        ResourceArguments = CloneArguments(resourceArguments);
    }

    public ImageFormatErrorCode ErrorCode { get; }
    public ImageFormatDiagnostic Diagnostic { get; }
    public ReadOnlyMemory<object?> ResourceArguments { get; }

    public string? ResourceKey => Diagnostic.ResourceKey;

    private static ReadOnlyMemory<object?> CloneArguments(
        IReadOnlyList<object?>? resourceArguments)
    {
        if (resourceArguments is null || resourceArguments.Count == 0)
        {
            return ReadOnlyMemory<object?>.Empty;
        }

        var copy = new object?[resourceArguments.Count];
        for (int index = 0; index < copy.Length; index++)
        {
            copy[index] = resourceArguments[index];
        }

        return copy;
    }
}
