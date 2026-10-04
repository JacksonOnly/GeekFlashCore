using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;
using GeekFlashCore.Transport.Abstractions;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal readonly struct FirehoseCmdSender
{
    private readonly ILogger _logger;
    private readonly ITransport _transport;

    public FirehoseCmdSender(ILogger logger, ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _logger = logger;
        _transport = transport;
    }

    public void Flush() => _transport.Flush();

    public void SendXml(string xml, Action? packetSent = null, int? maximumWireLength = null)
    {
        ArgumentNullException.ThrowIfNull(xml);

        int byteCount = Encoding.UTF8.GetByteCount(xml);
        if (byteCount > FirehoseConstants.MaximumXmlPacketSize)
            throw new ArgumentException(
                Strings.FormatFirehose_XmlPacketTooLarge(FirehoseConstants.MaximumXmlPacketSize), nameof(xml));
        byte[]? rented = null;
        Span<byte> buffer = byteCount <= 1024
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(xml, buffer);
            if (_logger.IsEnabled(Serilog.Events.LogEventLevel.Debug))
                _logger.Debug(Strings.Qcom_LogWireCommand, GetCommandName(xml), Math.Min(bytesWritten, maximumWireLength ?? bytesWritten));
            _transport.Write(buffer[..Math.Min(bytesWritten, maximumWireLength ?? bytesWritten)]);
            packetSent?.Invoke();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string GetCommandName(string xml)
    {
        // Log just the validated element name, never attributes, values or the XML.
        using var reader = XmlReader.Create(new StringReader(FirehoseLegacyXml.ForValidation(xml)),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = FirehoseConstants.MaximumXmlPacketSize });
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1)
                return QcomDeviceText.ForDisplay(reader.Name);
        return "?";
    }

    public void SendRaw(
        int bufferSize,
        ReadOnlySpan<byte> source,
        CancellationToken cancellationToken = default,
        Action? packetSent = null)
    {
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));

        int totalLength = source.Length;
        int packetCount = 0;
        while (!source.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(bufferSize, source.Length);
            _transport.Write(source[..length]);
            packetSent?.Invoke();
            packetCount++;
            source = source[length..];
        }

        _logger.Debug(Strings.Qcom_LogSendRaw, totalLength, packetCount);
    }

    public byte[] SendRaw(
        int bufferSize,
        Stream source,
        long sourceLength,
        long wireLength,
        byte paddingByte,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        bool computeDigest = false,
        Action? packetSent = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException(Strings.Firehose_SourceStreamNotReadable, nameof(source));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        if (sourceLength < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceLength));
        if (wireLength < sourceLength)
            throw new ArgumentOutOfRangeException(nameof(wireLength));

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        IncrementalHash? hash = computeDigest
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            : null;
        try
        {
            long wireCompleted = 0;
            long sourceCompleted = 0;
            while (wireCompleted < wireLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int chunkLength = (int)Math.Min(bufferSize, wireLength - wireCompleted);
                int sourceChunkLength = (int)Math.Min(chunkLength, sourceLength - sourceCompleted);

                int offset = 0;
                while (offset < sourceChunkLength)
                {
                    int read = source.Read(buffer, offset, sourceChunkLength - offset);
                    if (read == 0)
                        throw new EndOfStreamException(Strings.Firehose_SourceEndedBeforeDeclaredLength);
                    offset += read;
                }

                if (sourceChunkLength < chunkLength)
                    buffer.AsSpan(sourceChunkLength, chunkLength - sourceChunkLength).Fill(paddingByte);

                ReadOnlySpan<byte> chunk = buffer.AsSpan(0, chunkLength);
                hash?.AppendData(chunk);
                _transport.Write(chunk);
                packetSent?.Invoke();
                sourceCompleted += sourceChunkLength;
                wireCompleted += chunkLength;
                progress?.Report(sourceCompleted);
            }

            _logger.Debug(Strings.Qcom_LogSendRawCompleted, sourceCompleted, wireCompleted);
            return hash?.GetHashAndReset() ?? [];
        }
        finally
        {
            hash?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
