using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose;

internal static class FirehoseProgramRejection
{
    private static readonly object UnsupportedMarker = new();

    internal static bool IsUnsupported(FirehoseNakException exception) =>
        exception.Data.Contains(UnsupportedMarker);

    internal static void MarkUnsupported(FirehoseNakException exception)
    {
        FirehoseCommandResult result = exception.Result;
        if (result.RawMode || !result.Attributes.TryGetValue("value", out string? value) ||
            !string.Equals(value, "NAK", StringComparison.OrdinalIgnoreCase)) return;
        if (result.Logs.Any(log => IsEvidence(log.Message)) ||
            result.Attributes.TryGetValue("reason", out string? reason) && IsEvidence(reason))
            exception.Data[UnsupportedMarker] = true;
    }

    private static bool IsEvidence(string text) => text.Trim().TrimEnd('.').ToLowerInvariant() is
        "unsupported command: program" or "unknown command: program" or
        "program is not supported" or "program command is not supported" or "no handler for program";
}
