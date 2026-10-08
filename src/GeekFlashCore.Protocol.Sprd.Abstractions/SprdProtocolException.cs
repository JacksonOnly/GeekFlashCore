using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Sprd.Abstractions.Localization;

namespace GeekFlashCore.Protocol.Sprd.Abstractions;

/// <summary>A BSL rejection or malformed response. No automatic command resend occurs.</summary>
public sealed class SprdProtocolException : ProtocolException
{
    /// <summary>Creates a wire error without including raw device data.</summary>
    public SprdProtocolException(ushort command, ushort? response = null)
        : base(Strings.FormatWireFailure(command.ToString("X4"), response?.ToString("X4") ?? "?"))
    { Command = command; Response = response; }
    /// <summary>The command being executed.</summary>
    public ushort Command { get; }
    /// <summary>The numeric response, when a valid frame was decoded.</summary>
    public ushort? Response { get; }
    /// <summary>Wire errors require a new connection.</summary>
    public bool RequiresReconnect => true;
}
