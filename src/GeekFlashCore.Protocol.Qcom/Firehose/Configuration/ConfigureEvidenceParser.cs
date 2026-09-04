using System.Globalization;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Configuration;

internal static class ConfigureEvidenceParser
{
    public static ConfigureEvidence Parse(FirehoseCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        IReadOnlyDictionary<string, string> attributes = MergeAttributes(result);
        FirehoseStorage? unsupportedStorage = null;
        ulong? payloadFromLog = null;
        ulong? digestFromLog = null;
        uint? sectorFromLog = null;

        foreach (FirehoseResponseLog log in result.Logs)
        {
            ReadOnlySpan<char> message = log.Message.AsSpan();
            if (message.Contains("Not support configure MemoryName eMMC", StringComparison.OrdinalIgnoreCase))
                unsupportedStorage = FirehoseStorage.Emmc;
            else if (message.Contains("Not support configure MemoryName UFS", StringComparison.OrdinalIgnoreCase))
                unsupportedStorage = FirehoseStorage.Ufs;

            if (message.Contains("MaxPayloadSizeToTargetInBytes", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Host's payload to target size is too large", StringComparison.OrdinalIgnoreCase))
            {
                payloadFromLog ??= ReadTrailingNumber(message, "larger than supported");
            }

            if (message.Contains("MaxDigestTableSizeInBytes", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Hash table", StringComparison.OrdinalIgnoreCase))
            {
                digestFromLog ??= ReadTrailingNumber(message, "larger than supported");
            }

            if (message.Contains("disk sector size 512", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("device sector size (512)", StringComparison.OrdinalIgnoreCase))
            {
                sectorFromLog = 512;
            }
            else if (message.Contains("disk sector size 4096", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("device sector size (4096)", StringComparison.OrdinalIgnoreCase))
            {
                sectorFromLog = 4096;
            }
        }

        return new ConfigureEvidence
        {
            Storage = TryStorage(attributes, "MemoryName"),
            UnsupportedStorage = unsupportedStorage,
            SectorSizeInBytes = TryUInt32(attributes, "SECTOR_SIZE_IN_BYTES") ?? sectorFromLog,
            MaxPayloadSizeToTargetInBytes = TryPositiveUInt64(attributes, "MaxPayloadSizeToTargetInBytes") ??
                                                payloadFromLog,
            MaxPayloadSizeToTargetInBytesSupported =
                TryPositiveUInt64(attributes, "MaxPayloadSizeToTargetInBytesSupported"),
            MaxPayloadSizeFromTargetInBytes = TryPositiveUInt64(attributes, "MaxPayloadSizeFromTargetInBytes"),
            MaxXmlSizeInBytes = TryPositiveUInt64(attributes, "MaxXMLSizeInBytes"),
            MaxDigestTableSizeInBytes = TryUInt64(attributes, "MaxDigestTableSizeInBytes") ?? digestFromLog,
            TargetName = TryString(attributes, "TargetName"),
            Version = TryUInt64(attributes, "Version"),
            MinVersionSupported = TryUInt64(attributes, "MinVersionSupported")
        };
    }

    private static IReadOnlyDictionary<string, string> MergeAttributes(FirehoseCommandResult result)
    {
        if (result.PayloadElements.Count == 0)
            return result.Attributes;

        var merged = new Dictionary<string, string>(result.Attributes, StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, string> element in result.PayloadElements.Values)
        {
            foreach ((string key, string value) in element)
                merged.TryAdd(key, value);
        }
        return merged;
    }

    private static FirehoseStorage? TryStorage(IReadOnlyDictionary<string, string> values, string key)
    {
        string? value = TryString(values, key);
        if (value is null)
            return null;
        if (value.Equals("UFS", StringComparison.OrdinalIgnoreCase))
            return FirehoseStorage.Ufs;
        if (value.Equals("eMMC", StringComparison.OrdinalIgnoreCase))
            return FirehoseStorage.Emmc;
        if (value.Equals("spinor", StringComparison.OrdinalIgnoreCase))
            return FirehoseStorage.Spinor;
        if (value.Equals("NAND", StringComparison.OrdinalIgnoreCase))
            return FirehoseStorage.Nand;
        if (value.Equals("NVMe", StringComparison.OrdinalIgnoreCase))
            return FirehoseStorage.Nvme;
        return null;
    }

    private static uint? TryUInt32(IReadOnlyDictionary<string, string> values, string key) =>
        TryString(values, key) is { } value &&
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed) && parsed > 0
            ? parsed
            : null;

    private static ulong? TryUInt64(IReadOnlyDictionary<string, string> values, string key) =>
        TryString(values, key) is { } value &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed)
            ? parsed
            : null;

    private static ulong? TryPositiveUInt64(IReadOnlyDictionary<string, string> values, string key)
    {
        ulong? value = TryUInt64(values, key);
        return value is > 0 and <= int.MaxValue ? value : null;
    }

    private static string? TryString(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static ulong? ReadTrailingNumber(ReadOnlySpan<char> value, ReadOnlySpan<char> marker)
    {
        int markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;
        ReadOnlySpan<char> remaining = value[(markerIndex + marker.Length)..].TrimStart();
        int length = 0;
        while (length < remaining.Length && char.IsAsciiDigit(remaining[length]))
            length++;
        return length > 0 && ulong.TryParse(
            remaining[..length],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out ulong parsed)
            ? parsed
            : null;
    }
}
