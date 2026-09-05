using System.Globalization;
using System.Text.Json;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose.Configuration;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Storage;

internal static class FirehoseStorageInfoParser
{
    private static readonly string[] DefaultSupportedFunctions =
    [
        "configure", "program", "firmwarewrite", "patch", "setbootablestoragedrive",
        "ufs", "emmc", "power", "benchmark", "read", "getstorageinfo",
        "getcrc16digest", "getsha256digest", "erase", "peek", "poke", "nop", "xml"
    ];

    public static FirehoseStorageInfo ParseStorageInfo(
        FirehoseCommandResult result,
        uint physicalPartitionNumber)
    {
        var properties = new Dictionary<string, string>(result.Attributes, StringComparer.OrdinalIgnoreCase);
        var rawLogs = new List<string>(result.Logs.Count);
        foreach (FirehoseResponseLog log in result.Logs)
        {
            string message = log.Message.Trim();
            rawLogs.Add(message);
            if (TryParseJsonStorageInfo(message, properties))
                continue;
            int separator = message.IndexOf('=');
            if (separator <= 0)
                separator = message.IndexOf(':');
            if (separator > 0)
                properties[message[..separator].Trim()] = message[(separator + 1)..].Trim();
        }

        FirehoseStorage storage = ParseStorage(Get(properties, "mem_type")) ??
                                  ParseStorage(Get(properties, "MemoryName")) ??
                                  FirehoseStorage.None;
        uint? blockSize = ParseUInt32(Get(properties, "SECTOR_SIZE_IN_BYTES")) ??
                          ParseUInt32(Get(properties, "page_size")) ??
                          ParseUInt32(Get(properties, "block_size"));
        ulong? blockCount = ParseUInt64(Get(properties, "total_blocks")) ??
                            ParseUInt64(Get(properties, "num_partition_sectors"));
        return new FirehoseStorageInfo
        {
            Storage = storage,
            PhysicalPartitionNumber = physicalPartitionNumber,
            BlockSizeInBytes = blockSize,
            BlockCount = blockCount,
            Properties = properties,
            RawLogs = rawLogs
        };
    }

    public static FirehoseBasicDevInfo ParseBasicInfo(FirehoseCommandResult result)
    {
        var supported = new List<string>();
        bool readingFunctions = false;
        uint serialNumber = 0;

        foreach (FirehoseResponseLog log in result.Logs)
        {
            string line = log.Message.Trim();
            if (line.Contains("chip serial num", StringComparison.OrdinalIgnoreCase))
                serialNumber = ParseSerialNumber(line) ?? serialNumber;

            if (line.Contains("End of supported functions", StringComparison.OrdinalIgnoreCase))
            {
                readingFunctions = false;
                continue;
            }
            int functionsIndex = line.IndexOf("Supported Functions", StringComparison.OrdinalIgnoreCase);
            if (functionsIndex >= 0)
            {
                readingFunctions = true;
                int separator = line.IndexOf(':', functionsIndex);
                if (separator >= 0)
                {
                    AddFunctions(line[(separator + 1)..], supported);
                    readingFunctions = false;
                }
                continue;
            }
            if (readingFunctions && !string.IsNullOrWhiteSpace(line))
                AddFunctions(line, supported);
        }

        if (supported.Count == 0)
            supported.AddRange(DefaultSupportedFunctions);
        return new FirehoseBasicDevInfo
        {
            BuildDate = ConfigureEvidenceParser.Parse(result).BuildDate ?? default,
            SerialNumber = serialNumber,
            SupportedFunctions = supported.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            OriginLogs = result.Logs
        };
    }

    public static FirehoseStorage? ParseStorage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Equals("UFS", StringComparison.OrdinalIgnoreCase)) return FirehoseStorage.Ufs;
        if (value.Equals("eMMC", StringComparison.OrdinalIgnoreCase)) return FirehoseStorage.Emmc;
        if (value.Equals("spinor", StringComparison.OrdinalIgnoreCase)) return FirehoseStorage.Spinor;
        if (value.Equals("NAND", StringComparison.OrdinalIgnoreCase)) return FirehoseStorage.Nand;
        if (value.Equals("NVMe", StringComparison.OrdinalIgnoreCase)) return FirehoseStorage.Nvme;
        return null;
    }

    public static bool TryFindSha256(FirehoseCommandResult result, out byte[] digest)
    {
        foreach (string value in result.Attributes.Values)
        {
            if (TryFindSha256(value, out digest))
                return true;
        }
        foreach (FirehoseResponseLog log in result.Logs)
        {
            if (TryFindSha256(log.Message, out digest))
                return true;
        }
        digest = [];
        return false;
    }

    private static bool TryFindSha256(string value, out byte[] digest)
    {
        ReadOnlySpan<char> span = value.AsSpan();
        for (int index = 0; index <= span.Length - 64; index++)
        {
            ReadOnlySpan<char> candidate = span.Slice(index, 64);
            if (!candidate.ContainsAnyExcept("0123456789abcdefABCDEF"))
            {
                digest = Convert.FromHexString(candidate);
                return true;
            }
        }
        digest = [];
        return false;
    }

    private static bool TryParseJsonStorageInfo(string message, Dictionary<string, string> properties)
    {
        int objectStart = message.IndexOf('{');
        if (objectStart < 0 || !message.AsSpan(objectStart).Contains(
                "storage_info",
                StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(message.AsMemory(objectStart));
            if (!document.RootElement.TryGetProperty("storage_info", out JsonElement storageInfo) ||
                storageInfo.ValueKind != JsonValueKind.Object)
                return false;
            foreach (JsonProperty property in storageInfo.EnumerateObject())
                properties[property.Name] = JsonValue(property.Value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => value.GetRawText()
    };

    private static uint? ParseUInt32(string? value) =>
        TryParseUnsigned(value, out ulong parsed) && parsed is > 0 and <= uint.MaxValue
            ? (uint)parsed
            : null;

    private static ulong? ParseUInt64(string? value) =>
        TryParseUnsigned(value, out ulong parsed) && parsed > 0 ? parsed : null;

    private static bool TryParseUnsigned(string? value, out ulong parsed)
    {
        parsed = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        ReadOnlySpan<char> span = value.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(
                span[2..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out parsed);
        return ulong.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

    private static uint? ParseSerialNumber(string line)
    {
        ReadOnlySpan<char> span = line.AsSpan();
        int hex = span.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (hex >= 0)
        {
            ReadOnlySpan<char> value = span[(hex + 2)..];
            int length = 0;
            while (length < value.Length && Uri.IsHexDigit(value[length]))
                length++;
            if (length > 0 && uint.TryParse(
                    value[..length],
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out uint parsed))
                return parsed;
        }
        int separator = span.LastIndexOf(':');
        return separator >= 0 && uint.TryParse(
            span[(separator + 1)..].Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out uint decimalValue)
            ? decimalValue
            : null;
    }

    private static void AddFunctions(string value, List<string> destination)
    {
        foreach (string command in value.Split(
                     [' ', '\t', ',', ';'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            destination.Add(command);
    }

    private static string? Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) ? value : null;
}
