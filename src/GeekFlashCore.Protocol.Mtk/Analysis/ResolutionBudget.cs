namespace GeekFlashCore.Protocol.Mtk.Analysis;

/// <summary>Shared finite node budget for recursive register queries.</summary>
internal sealed class ResolutionBudget
{
    private int _remaining = 64;
    public bool Take() => _remaining-- > 0;
}
