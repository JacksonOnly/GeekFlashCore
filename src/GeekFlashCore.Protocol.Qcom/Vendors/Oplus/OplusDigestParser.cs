using System.Buffers;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using OplusDigestUtils;
using OplusDigestUtils.Models;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

public sealed class OplusDigestParser
{
    public const int MaximumDigestLength = 4 * 1024 * 1024;

    private readonly IOplusDigestParser _parser = new global::OplusDigestUtils.OplusDigestParser();

    public OplusDigestIndex Parse(IDataSource source, uint physicalPartitionNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length is <= 0 or > MaximumDigestLength || source.Length > int.MaxValue)
            throw new OplusDigestException("The Oplus Digest length is invalid or exceeds the safety limit.");
        int length = checked((int)source.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            using Stream stream = source.OpenStream() ?? throw new OplusDigestException("The Oplus Digest source returned no stream.");
            stream.ReadExactly(buffer.AsSpan(0, length));
            if (!_parser.TryParse(buffer.AsSpan(0, length), out OplusDigestParseResult result) || !result.IsSuccess)
                throw new OplusDigestException(result.ErrorMessage ?? "The Oplus Digest could not be parsed.");
            OplusDigestEntry[] entries = result.Digest.Partitions.Select(partition => new OplusDigestEntry(
                physicalPartitionNumber,
                partition.Label,
                partition.FileName,
                partition.AllowRead,
                partition.AllowWrite,
                checked((long)partition.StartSector),
                checked((long)partition.Sectors),
                partition.HashHex)).ToArray();
            return new OplusDigestIndex(entries);
        }
        catch (OplusDigestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or OverflowException)
        {
            throw new OplusDigestException("The Oplus Digest is truncated or contains unsupported ranges.", exception);
        }
        finally
        {
            buffer.AsSpan(0, length).Clear();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
