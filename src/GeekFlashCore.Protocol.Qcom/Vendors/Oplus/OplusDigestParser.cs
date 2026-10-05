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
        => ParseCore(source, physicalPartitionNumber, allowFormatFailure: false)!;

    internal bool TryParse(IDataSource source, out OplusDigestIndex? index)
    {
        index = ParseCore(source, 0, allowFormatFailure: true);
        if (index?.Entries.Count > 0) return true;
        index = null;
        return false;
    }

    private OplusDigestIndex? ParseCore(IDataSource source, uint physicalPartitionNumber, bool allowFormatFailure)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length is <= 0 or > MaximumDigestLength || source.Length > int.MaxValue)
            throw new OplusDigestException(Strings.Qcom_OplusDigestLengthInvalid);
        int length = checked((int)source.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            using Stream stream = source.OpenStream() ?? throw new OplusDigestException(Strings.Qcom_OplusDigestStreamMissing);
            stream.ReadExactly(buffer.AsSpan(0, length));
            if (!_parser.TryParse(buffer.AsSpan(0, length), out OplusDigestParseResult result) || !result.IsSuccess)
            {
                if (allowFormatFailure) return null;
                throw new OplusDigestException(Strings.FormatQcom_OplusDigestParseFailed(
                    result.ErrorMessage ?? Strings.Qcom_OplusDigestInvalid));
            }
            try
            {
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
            catch (Exception exception) when (allowFormatFailure && exception is OplusDigestException or OverflowException)
            {
                return null;
            }
        }
        catch (OplusDigestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or OverflowException)
        {
            throw new OplusDigestException(Strings.Qcom_OplusDigestInvalid, exception);
        }
        finally
        {
            buffer.AsSpan(0, length).Clear();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
