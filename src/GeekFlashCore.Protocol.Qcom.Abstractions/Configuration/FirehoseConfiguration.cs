using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public sealed record FirehoseConfiguration
{
    public FirehoseStorage MemoryName { get; init; } = FirehoseStorage.None;
    public uint SectorSizeInBytes { get; init; } = 512;
    public ulong MaxPayloadSizeToTargetInBytes { get; init; } = FirehoseConstants.DefaultPayloadSize;
    public ulong MaxDigestTableSizeInBytes { get; init; } = FirehoseConstants.DefaultMaxDigestTableSize;
    public int MaxConfigureAttempts { get; init; } = FirehoseConstants.MaxConfigureAttempts;
    public bool Verbose { get; init; }
    public bool AlwaysValidate { get; init; }
    public bool ZlpAwareHost { get; init; } = true;
    public bool SkipWrite { get; init; }
    public bool SkipStorageInit { get; init; }
    public bool SkipResponse { get; init; }

    public void Validate()
    {
        if (SectorSizeInBytes == 0)
            throw new ArgumentOutOfRangeException(nameof(SectorSizeInBytes));
        if (MaxPayloadSizeToTargetInBytes is 0 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadSizeToTargetInBytes));
        if (MaxDigestTableSizeInBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaxDigestTableSizeInBytes));
        if (MaxConfigureAttempts is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(MaxConfigureAttempts));
    }
}

public sealed record OplusDigestConfiguration
{
    /// <summary>Selects partition mapping or the Rector packet-counted Legacy flow.</summary>
    public OplusDigestMode Mode { get; init; }
    /// <summary>Explicitly resume a running Oplus loader waiting for its initial Digest once per protocol instance; skip Sahara detection and startup replay. A rejected single-packet initial Digest may be resent once only after complete XML confirms a transition to signed-table receive. Never use after RAW interruption.</summary>
    public bool ResumeAwaitingDigest { get; init; }
    /// <summary>Maximum sectors per transfer; zero keeps the request unsegmented.</summary>
    public int FixedSectorCount { get; init; } = 256;
    /// <summary>Legacy table packet capacity. Counts XML and complete outgoing payloads, not transport chunks.</summary>
    public int MaxCommandsBeforeDigest { get; init; } = 53;
    /// <summary>Device table count before the first packet of a new Firehose session. Core counts its own probes and Configure retries.</summary>
    public uint InitialPacketCount { get; init; }
    /// <summary>Exact confirmation NOP; null selects the compatible built-in NOP.</summary>
    public string? NopXml { get; init; }
    /// <summary>Legacy wire XML truncation limit retained from Rector.</summary>
    public int MaximumXmlSendSize { get; init; } = 4096;
    /// <summary>Independent Digest reply window, including partial XML.</summary>
    public int DigestResponseTimeoutMilliseconds { get; init; } = 1000;

    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (ResumeAwaitingDigest && Mode == OplusDigestMode.None)
            throw new ArgumentException(Strings.OplusResumeRequiresMode);
        if (Mode != OplusDigestMode.OplusDigestLegacy)
            return;
        if (FixedSectorCount < 0)
            throw new ArgumentOutOfRangeException(nameof(FixedSectorCount));
        if (MaxCommandsBeforeDigest < 4)
            throw new ArgumentOutOfRangeException(nameof(MaxCommandsBeforeDigest));
        if (InitialPacketCount > (long)MaxCommandsBeforeDigest + 1)
            throw new ArgumentOutOfRangeException(nameof(InitialPacketCount));
        if (MaximumXmlSendSize is < 1 or > FirehoseConstants.MaximumXmlPacketSize)
            throw new ArgumentOutOfRangeException(nameof(MaximumXmlSendSize));
        if (DigestResponseTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(DigestResponseTimeoutMilliseconds));
        if (NopXml?.Length > FirehoseConstants.MaximumXmlPacketSize)
            throw new ArgumentOutOfRangeException(nameof(NopXml));
        if (NopXml is not null) ValidateNop(NopXml);
    }

    private static void ValidateNop(string xml)
    {
        if (Encoding.UTF8.GetByteCount(xml) > FirehoseConstants.MaximumXmlPacketSize)
            throw new ArgumentException(Strings.LegacyNopInvalid, nameof(NopXml));
        // These two declaration attributes are intentional Rector compatibility;
        // validation normalizes them without changing the bytes sent to the device.
        int end = xml.IndexOf("?>", StringComparison.Ordinal);
        if (xml.StartsWith("<?xml", StringComparison.Ordinal) && end >= 0)
            xml = Regex.Replace(xml[..end], "\\s+(?:chimerais|Bylaowang)\\s*=\\s*(?:\"power\"|'power')", "",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) + xml[end..];
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = FirehoseConstants.MaximumXmlPacketSize,
                IgnoreComments = true, IgnoreProcessingInstructions = true
            });
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "data" || reader.IsEmptyElement)
                throw new XmlException();
            int commands = 0;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.Depth != 1 || reader.Name != "nop") throw new XmlException();
                commands++;
            }
            if (commands != 1) throw new XmlException();
        }
        catch (XmlException exception)
        {
            throw new ArgumentException(Strings.LegacyNopInvalid, nameof(NopXml), exception);
        }
    }
}
