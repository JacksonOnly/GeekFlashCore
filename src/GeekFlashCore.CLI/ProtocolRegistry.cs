using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.CLI;

internal sealed record ProtocolRegistration(
    ProtocolType Type,
    IReadOnlySet<string> Names,
    string DisplayName);

internal static class ProtocolRegistry
{
    private static readonly IReadOnlyList<ProtocolRegistration> Registrations =
    [
        new(ProtocolType.QualcommEdl,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "qcom", "qualcomm", "qualcommedl" },
            "QualcommEdl")
    ];

    public static bool TryResolve(string? name, out ProtocolRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            registration = Registrations[0];
            return true;
        }
        registration = Registrations.FirstOrDefault(item => item.Names.Contains(name))!;
        return registration is not null;
    }

    public static string SupportedNames => string.Join(", ", Registrations.Select(item => item.DisplayName));
}
