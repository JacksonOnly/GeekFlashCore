using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Compatibility facade for bounded scatter manifest parsing.</summary>
public static class MtkScatterParser
{
    public const int MaximumCharacters=MtkScatterManifestParser.MaximumCharacters;
    public static MtkScatterManifest Parse(string text)=>MtkScatterManifestParser.Parse(text);
}
/// <summary>Resolves scatter aliases and reserved ranges against observed storage.</summary>
public static class MtkScatterPlanner
{
    public static MtkScatterPlan Create(MtkScatterManifest manifest,MtkStorageInfo storage,long generation)=>MtkScatterPlanBuilder.Create(manifest,storage,generation);
}
