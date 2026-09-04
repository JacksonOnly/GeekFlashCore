using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using GeekFlashCore.Protocol.Qcom.Abstractions;
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

    public void SendCommand<T>(T command) where T : BaseCommand
    {
        ArgumentNullException.ThrowIfNull(command);
        string xml = command.Build();
        _logger.Debug("Send Command {CommandType} {XmlLength} bytes", typeof(T).Name, xml.Length);
        SendXml(xml);
    }

    public void SendXml(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        int byteCount = Encoding.UTF8.GetByteCount(xml);
        if (byteCount > FirehoseConstants.MaximumXmlPacketSize)
            throw new ArgumentException(
                $"The Firehose XML document exceeds {FirehoseConstants.MaximumXmlPacketSize} bytes.",
                nameof(xml));
        byte[]? rented = null;
        Span<byte> buffer = byteCount <= 1024
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));
        try
        {
            int bytesWritten = Encoding.UTF8.GetBytes(xml, buffer);
            _transport.Write(buffer[..bytesWritten]);
            _logger.Debug("Send XML {XmlLength} bytes", bytesWritten);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
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

        _logger.Debug("Send Raw {TotalLength} bytes in {PacketCount} packets", totalLength, packetCount);
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

            _logger.Debug("Send Raw {SourceLength} bytes (wire {WireLength} bytes)", sourceCompleted, wireCompleted);
            return hash?.GetHashAndReset() ?? [];
        }
        finally
        {
            hash?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
