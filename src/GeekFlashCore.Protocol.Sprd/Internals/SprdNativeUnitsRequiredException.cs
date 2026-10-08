namespace GeekFlashCore.Protocol.Sprd.Internals;

/// <summary>Only emitted after a complete, validated native query, before any storage write begins.</summary>
internal sealed class SprdNativeUnitsRequiredException() : InvalidOperationException(Strings.NativeUnitsDetected);
