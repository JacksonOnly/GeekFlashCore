namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

/// <summary>Counts XML and complete outgoing payloads at Rector packet boundaries.</summary>
internal sealed class OplusDigestCommandCounter
{
    private readonly int _maximum;
    private long _completed;

    public OplusDigestCommandCounter(int maximum, uint initial = 0)
    {
        if (maximum < 4) throw new ArgumentOutOfRangeException(nameof(maximum));
        _maximum = maximum;
        _completed = initial;
    }

    public bool RequiresDigest => _maximum - _completed <= 2;
    public long Current => _completed;
    public long Trigger => (long)_maximum + 1;

    public void Complete()
    {
        _completed = checked(_completed + 1);
        Serilog.Log.Debug(Strings.Qcom_LogLegacyPacketCount, _completed, _maximum);
    }

    public void CommandSent() => Complete();

    public void Reset(long value = 0) => _completed = value;
}
