using System.Buffers.Binary;
using System.Globalization;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom;

public sealed partial class QcomProtocol
{
    public long Peek(ulong address, long length, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException(Strings.Firehose_DestinationStreamNotWritable, nameof(destination));
        ValidateMemoryRange(address, length);
        using var operation = EnterConnected();
        Span<byte> buffer = stackalloc byte[256];
        long completed = 0;
        while (completed < length)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, length - completed);
            var result = _firehose!.Execute(new PeekCommand
            {
                Address64 = checked(address + (ulong)completed).ToString(CultureInfo.InvariantCulture),
                SizeInBytes = (ulong)count
            }, cancellationToken: ct);
            int decoded = 0;
            foreach (var log in result.Logs)
            {
                string[] tokens = log.Message.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0 || !tokens.All(IsHexBytes)) continue;
                foreach (string token in tokens)
                {
                    ReadOnlySpan<char> hex = token.AsSpan();
                    if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
                    if (hex.Length / 2 > count - decoded) throw new QcomProtocolException(Strings.Qcom_PeekLengthMismatch);
                    for (int i = 0; i < hex.Length; i += 2)
                        buffer[decoded++] = byte.Parse(hex.Slice(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
            }
            if (decoded != count) throw new QcomProtocolException(Strings.Qcom_PeekLengthMismatch);
            destination.Write(buffer[..count]);
            buffer.Clear();
            completed += count;
        }
        return completed;
    }

    private static bool IsHexBytes(string token)
    {
        ReadOnlySpan<char> hex = token.AsSpan();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return !hex.IsEmpty && hex.Length % 2 == 0 && !hex.ContainsAnyExcept("0123456789abcdefABCDEF");
    }

    public long Poke(ulong address, IDataSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        long length = source.Length;
        ValidateMemoryRange(address, length);
        using var operation = EnterConnected();
        ct.ThrowIfCancellationRequested();
        using Stream stream = source.OpenStream() ?? throw new InvalidDataException(Strings.Qcom_FirehoseProgramStreamMissing);
        if (!stream.CanRead) throw new ArgumentException(Strings.Firehose_SourceStreamNotReadable, nameof(source));
        Span<byte> buffer = stackalloc byte[8];
        for (long completed = 0; completed < length;)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(8, length - completed);
            buffer.Clear();
            stream.ReadExactly(buffer[..count]);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
            _firehose!.Execute(new PokeCommand
            {
                Address64 = checked(address + (ulong)completed), SizeInBytes = (uint)count,
                Value64 = "0x" + value.ToString("X16", CultureInfo.InvariantCulture)
            }, cancellationToken: ct);
            completed += count;
        }
        buffer.Clear();
        return length;
    }

    public FirehoseCommandResult FirmwareWrite(uint physicalPartitionNumber, IDataSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        long length = source.Length;
        if (length is <= 0 or > FirehoseConstants.MaximumRawTransferLength)
            throw new ArgumentOutOfRangeException(nameof(source));
        using var operation = EnterConnected();
        ValidateLun(physicalPartitionNumber);
        ct.ThrowIfCancellationRequested();
        using Stream stream = source.OpenStream() ?? throw new InvalidDataException(Strings.Qcom_FirehoseProgramStreamMissing);
        if (!stream.CanRead) throw new ArgumentException(Strings.Firehose_SourceStreamNotReadable, nameof(source));
        _firehose!.Execute(new FirmwareWriteCommand
        {
            PhysicalPartitionNumber = physicalPartitionNumber, SectorSizeInBytes = 1,
            NumPartitionSectors = (ulong)length
        }, expectedRawMode: true, cancellationToken: ct);
        return _firehose.SendRaw(stream, length, length,
            checked((int)Math.Min(_storage!.Configuration.MaxPayloadSizeToTargetInBytes, 1024 * 1024)), cancellationToken: ct);
    }

    private static void ValidateMemoryRange(ulong address, long length)
    {
        if (length <= 0 || (ulong)length > ulong.MaxValue - address)
            throw new ArgumentOutOfRangeException(nameof(length));
    }
}
