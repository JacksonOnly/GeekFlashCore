namespace GeekFlashCore.Protocol.Abstractions;

public enum ProgressUnit { Steps, Bytes }
public enum ProgressPhase { Running, Started, Completed }

public record ProgressRecord(long Total, long Current, string Label)
{
    public ProgressUnit Unit { get; init; }
    public ProgressPhase Phase { get; init; }
}
