using System.Buffers;
using GeekFlashCore.Android.Sparse;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Firehose.Programming;

internal static class FirehoseProgramPlanner
{
    public static FirehoseProgramPlan Create(
        FirehoseProgramRequest request,
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new InvalidDataException("The Firehose program source is not readable.");
        cancellationToken.ThrowIfCancellationRequested();

        long sourceLength = request.GetSourceLength();
        if (sourceLength == 0)
            throw new ArgumentException("The Firehose program source cannot be empty.", nameof(request));

        SourceWindowStream? sourceView = null;
        if (source.CanSeek)
        {
            sourceView = new SourceWindowStream(source, request.SourceOffset, sourceLength);
            source = sourceView;
        }
        else
        {
            PositionSource(source, request.SourceOffset, cancellationToken);
        }

        try
        {
            bool isSparse = request.Format == FirehoseProgramFormat.AndroidSparse ||
                            request.Format == FirehoseProgramFormat.Auto && SparseImageParser.IsSparse(source);
            if (isSparse)
            {
                if (!source.CanSeek)
                    throw new NotSupportedException("Android sparse programming requires a seekable source stream.");
                IReadOnlyList<FirehoseProgramSegment> segments = SparseProgramPlanner.Create(source, request);
                cancellationToken.ThrowIfCancellationRequested();
                return new FirehoseProgramPlan(source, segments, sourceView is not null);
            }

            long wireLength = request.GetWireLength();
            if (sourceLength > wireLength)
                throw new ArgumentException("The source is larger than the target range.", nameof(request));
            return new FirehoseProgramPlan(
                source,
                [FirehoseProgramSegment.Raw(request.StartSector, request.SectorCount, sourceLength)],
                sourceView is not null);
        }
        catch
        {
            sourceView?.Dispose();
            throw;
        }
    }

    private static void PositionSource(Stream source, long offset, CancellationToken cancellationToken)
    {
        if (offset == 0)
            return;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long remaining = offset;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                    throw new EndOfStreamException("The Firehose source ended before its requested offset.");
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
