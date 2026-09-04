namespace GeekFlashCore.Protocol.Qcom.Firehose.Programming;

internal sealed class FirehoseProgramPlan : IDisposable
{
    private readonly bool _ownsSourceView;

    public FirehoseProgramPlan(
        Stream source,
        IReadOnlyList<FirehoseProgramSegment> segments,
        bool ownsSourceView)
    {
        Source = source;
        Segments = segments;
        _ownsSourceView = ownsSourceView;
    }

    public Stream Source { get; }
    public IReadOnlyList<FirehoseProgramSegment> Segments { get; }

    public void Dispose()
    {
        if (_ownsSourceView)
            Source.Dispose();
    }
}
