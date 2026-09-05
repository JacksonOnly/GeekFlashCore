namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

/// <summary>Counts Legacy read/program XML commands after they are sent.</summary>
internal sealed class OplusDigestCommandCounter
{
    private readonly int _maximum;
    private int _completed;

    public OplusDigestCommandCounter(int maximum)
    {
        if (maximum < 2) throw new ArgumentOutOfRangeException(nameof(maximum));
        _maximum = maximum;
    }

    public bool RequiresDigest => _maximum - _completed < 2;

    public void Complete()
    {
        // Saturation also keeps the subtraction safe when callers report extra completions.
        if (_completed < _maximum) _completed++;
    }

    public void CommandSent() => Complete();

    public void Reset() => _completed = 0;
}
