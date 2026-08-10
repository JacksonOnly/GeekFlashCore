namespace GeekFlashCore.Gpt.Abstractions;

public interface IGptParser
{
    IGpt Parse(
        ReadOnlySpan<byte> image,
        GptParseOptions? options = null);

    IGpt Parse(
        Stream stream,
        GptParseOptions? options = null);

    IGpt ParseFile(
        string path,
        GptParseOptions? options = null);
}
