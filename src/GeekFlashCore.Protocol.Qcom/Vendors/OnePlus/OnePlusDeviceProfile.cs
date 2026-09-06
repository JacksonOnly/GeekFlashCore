namespace GeekFlashCore.Protocol.Qcom.Vendors.OnePlus;

internal sealed record OnePlusDeviceProfile(string ProjectId, int Version, string? CryptoModel, int ParamMode);

internal static class OnePlusDeviceProfiles
{
    private static readonly OnePlusDeviceProfile[] Profiles =
    [
        new("16859", 1, null, 0),
        new("17801", 1, null, 0),
        new("17819", 1, null, 0),
        new("18801", 1, null, 0),
        new("18811", 1, null, 0),
        new("18857", 1, null, 0),
        new("18821", 1, null, 0),
        new("18825", 1, null, 0),
        new("18827", 1, null, 0),
        new("18831", 1, null, 0),
        new("18865", 1, null, 0),
        new("19863", 1, null, 0),
        new("19801", 1, null, 0),
        new("19861", 1, null, 0),
        new("19821", 2, "0cffee8a", 0),
        new("19855", 2, "6d9215b4", 0),
        new("19867", 2, "4107b2d4", 0),
        new("19868", 2, "178d8213", 0),
        new("19811", 2, "40217c07", 0),
        new("19805", 2, "1a5ec176", 0),
        new("20809", 2, "d6bc8c36", 0),
        new("20801", 2, "eacf50e7", 0),
        new("20885", 3, "3a403a71", 1),
        new("20886", 3, "b8bd9e39", 1),
        new("20888", 3, "142f1bd7", 1),
        new("20889", 3, "f2056ae1", 1),
        new("20880", 3, "6ccf5913", 1),
        new("20881", 3, "fa9ff378", 1),
        new("20882", 3, "4ca1e84e", 1),
        new("20883", 3, "ad9dba4a", 1),
        new("19815", 2, "9c151c7f", 0),
        new("20859", 2, "9c151c7f", 0),
        new("20857", 2, "9c151c7f", 0),
        new("19825", 2, "0898dcd6", 0),
        new("20851", 2, "0898dcd6", 0),
        new("20852", 2, "0898dcd6", 0),
        new("20853", 2, "0898dcd6", 0),
        new("20828", 2, "f498b60f", 0),
        new("20838", 2, "f498b60f", 0),
        new("20854", 2, "16225d4e", 0),
        new("2085A", 2, "7f19519a", 0),
        new("20818", 1, null, 0),
        new("2083C", 1, null, 0),
        new("2083D", 1, null, 0),
        new("20813", 2, "48ad7b61", 0)
    ];

    public static IReadOnlyList<string> ProjectIds { get; } =
        Profiles.Select(static profile => profile.ProjectId).ToArray();

    public static bool TryGet(string projectId, out OnePlusDeviceProfile profile)
    {
        foreach (OnePlusDeviceProfile item in Profiles)
        {
            if (item.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase))
            {
                profile = item;
                return true;
            }
        }
        profile = null!;
        return false;
    }
}
