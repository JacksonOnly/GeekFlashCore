using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Abstractions.Localization;

namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>A wire failure with numeric status and explicit recovery requirements.</summary>
public class MtkProtocolException : ProtocolException
{
    public MtkProtocolException(MtkBootStage stage, uint command, uint status = 0, bool requiresReconnect = true,
        string? xmlResultCode = null, string? xmlMessage = null)
        : base(xmlResultCode is null
            ? Strings.FormatWireFailure(stage, command.ToString("X"), status.ToString("X"))
            : Strings.FormatXmlWireFailure(stage, command.ToString("X"), status.ToString("X"), SanitizeXmlCode(xmlResultCode),
                ExtractXmlMessageCode(xmlMessage) ?? SanitizeXmlMessage(xmlMessage)))
    {
        Stage = stage;
        Command = command;
        Status = status;
        RequiresReconnect = requiresReconnect;
        XmlResultCode = xmlResultCode is null ? null : SanitizeXmlCode(xmlResultCode);
        XmlMessageCode = ExtractXmlMessageCode(xmlMessage);
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
    public string? XmlResultCode { get; }
    public string? XmlMessageCode { get; }

    private static string SanitizeXmlCode(string value)
    {
        return value.Length is 0 or > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.' and not '!')
            ? "[redacted]"
            : value;
    }

    private static string? ExtractXmlMessageCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string text = value.Trim();
        if (!text.StartsWith("ERR!", StringComparison.OrdinalIgnoreCase))
        {
            string lower = text.ToLowerInvariant();
            if (lower.Contains("hash") && lower.Contains("mismatch")) return "HASH_MISMATCH";
            if (lower.Contains("dram") && (lower.Contains("unstable") || lower.Contains("flaw"))) return "DRAM_UNSTABLE";
            if (lower.Contains("emi") && lower.Contains("setting")) return "EMI_SETTING";
            return null;
        }
        int length = 0;
        while (length < text.Length && (char.IsAsciiLetterOrDigit(text[length]) || text[length] is '_' or '-' or '!' or '.'))
            length++;
        return length is 0 or > 64 ? "[redacted]" : text[..length];
    }

    private static string SanitizeXmlMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "[none]";
        string text = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > 160)
            text = text[..160];
        string lower = text.ToLowerInvariant();
        string[] sensitive = ["hash", "signature", "challenge", "nonce", "meid", "socid", "identity", "token", "key"];
        if (sensitive.Any(lower.Contains))
            return "[redacted]";
        return text.Length == 0 ? "[none]" : text;
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
