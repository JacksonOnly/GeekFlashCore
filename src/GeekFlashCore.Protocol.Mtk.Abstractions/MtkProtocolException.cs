using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Abstractions.Localization;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>A wire failure with numeric status and explicit recovery requirements.</summary>
public class MtkProtocolException : ProtocolException
{
    public MtkProtocolException(MtkBootStage stage, uint command, uint status = 0, bool requiresReconnect = true)
        : base(Strings.FormatWireFailure(stage, command.ToString("X"), status.ToString("X")))
    {
        Stage = stage;
        Command = command;
        Status = status;
        RequiresReconnect = requiresReconnect;
    }
    public MtkBootStage Stage
    {
        get;
    }
    public uint Command
    {
        get;
    }
    public uint Status
    {
        get;
    }
    public bool RequiresReconnect
    {
        get;
    }
}
/// <summary>Required resources are missing or invalid.</summary>
public sealed class MtkResourceException(string resource) : ProtocolException(Strings.FormatResourceMissing(resource));
/// <summary>The selected capability has no verified implementation or prerequisite.</summary>
public sealed class MtkCapabilityException(string capability) : ProtocolException(Strings.FormatCapabilityUnavailable(capability));

/// <summary>A terminal host checkpoint result. No automatic retry or recovery is performed.</summary>
public sealed class MtkExploitException(MtkExploitStage stage, MtkExploitOutcome outcome)
    : ProtocolException(Strings.FormatExploitStopped(stage, outcome))
{
    /// <summary>The checkpoint at which connection was stopped.</summary>
    public MtkExploitStage Stage { get; } = stage;
    /// <summary>The terminal outcome reported by the host.</summary>
    public MtkExploitOutcome Outcome { get; } = outcome;
    /// <summary>The current session is invalid; the host must reconnect.</summary>
    public bool RequiresReconnect => true;
}
