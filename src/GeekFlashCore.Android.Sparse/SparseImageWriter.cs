using GeekFlashCore.Android.Sparse.BlockDevice;
using GeekFlashCore.Android.Sparse.Constants;
using GeekFlashCore.Android.Sparse.Internals;
using GeekFlashCore.Android.Sparse.Models;
using GeekFlashCore.IO.BlockDevice;
using GeekFlashCore.IO.BlockDevice.Abstractions;

namespace GeekFlashCore.Android.Sparse;

public static class SparseImageWriter
{
    public static SparseImageWritePlan Analyze(
        IReadableBlockDevice source,
        SparseImageWriteOptions? options = null,
        BudgetedArrayPool? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new SparseImageWriteOptions();
        options.Validate();
        return SparseImagePlanner.Analyze(source, options, buffers);
    }

    public static SparseImageWritePlan Analyze(
        Stream source,
        SparseImageWriteOptions? options = null,
        BudgetedArrayPool? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSourceStream(source);
        long origin = source.Position;
        try
        {
            var device = new SeekableStreamBlockDevice(source, origin);
            return Analyze(device, options, buffers);
        }
        finally
        {
            source.Position = origin;
        }
    }

    public static SparseImageWritePlan Write(
        IReadableBlockDevice source,
        Stream destination,
        SparseImageWriteOptions? options = null,
        IProgress<BlockCopyProgress>? progress = null,
        BudgetedArrayPool? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateDestination(destination);
        options ??= new SparseImageWriteOptions();
        options.Validate();

        SparseImageWritePlan plan = SparseImagePlanner.Analyze(source, options, buffers);
        SparseImageEncoder.Write(source, destination, plan, options.BufferSize, progress, buffers);
        return plan;
    }

    public static SparseImageWritePlan Write(
        Stream source,
        Stream destination,
        SparseImageWritePlan plan,
        int bufferSize = SparseImageWriteOptions.DefaultBufferSize,
        IProgress<BlockCopyProgress>? progress = null,
        BudgetedArrayPool? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSourceStream(source);
        ValidateDestination(destination);
        ValidateDistinctStreams(source, destination);
        long origin = source.Position;
        try
        {
            return Write(
                new SeekableStreamBlockDevice(source, origin),
                destination,
                plan,
                bufferSize,
                progress,
                buffers);
        }
        finally
        {
            source.Position = origin;
        }
    }

    public static SparseImageWritePlan Write(
        IReadableBlockDevice source,
        Stream destination,
        SparseImageWritePlan plan,
        int bufferSize = SparseImageWriteOptions.DefaultBufferSize,
        IProgress<BlockCopyProgress>? progress = null,
        BudgetedArrayPool? buffers = null)
    {
        ValidatePlannedWrite(source, destination, plan, bufferSize);
        SparseImageEncoder.Write(source, destination, plan, bufferSize, progress, buffers);
        return plan;
    }

    public static SparseImageWritePlan Write(
        Stream source,
        Stream destination,
        SparseImageWriteOptions? options = null,
        IProgress<BlockCopyProgress>? progress = null,
        BudgetedArrayPool? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSourceStream(source);
        ValidateDestination(destination);
        ValidateDistinctStreams(source, destination);
        long origin = source.Position;
        try
        {
            return Write(new SeekableStreamBlockDevice(source, origin), destination, options, progress, buffers);
        }
        finally
        {
            source.Position = origin;
        }
    }

    public static ValueTask<SparseImageWritePlan> WriteAsync(
        IReadableBlockDevice source,
        Stream destination,
        SparseImageWriteOptions? options = null,
        IProgress<BlockCopyProgress>? progress = null,
        BudgetedArrayPool? buffers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateDestination(destination);
        options ??= new SparseImageWriteOptions();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        return WriteAsyncCore(source, destination, options, progress, buffers, cancellationToken);
    }

    public static ValueTask<SparseImageWritePlan> WriteAsync(
        IReadableBlockDevice source,
        Stream destination,
        SparseImageWritePlan plan,
        int bufferSize = SparseImageWriteOptions.DefaultBufferSize,
        IProgress<BlockCopyProgress>? progress = null,
        BudgetedArrayPool? buffers = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePlannedWrite(source, destination, plan, bufferSize);
        cancellationToken.ThrowIfCancellationRequested();
        return WritePlannedAsyncCore(
            source,
            destination,
            plan,
            bufferSize,
            progress,
            buffers,
            cancellationToken);
    }

    public static ValueTask<SparseImageWritePlan> WriteAsync(
        Stream source,
        Stream destination,
        SparseImageWritePlan plan,
        int bufferSize = SparseImageWriteOptions.DefaultBufferSize,
        IProgress<BlockCopyProgress>? progress = null,
        BudgetedArrayPool? buffers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSourceStream(source);
        ValidateDestination(destination);
        ValidateDistinctStreams(source, destination);
        ValidateBufferSize(bufferSize);
        cancellationToken.ThrowIfCancellationRequested();
        return WritePlannedStreamAsyncCore(
            source,
            destination,
            plan,
            bufferSize,
            progress,
            buffers,
            cancellationToken);
    }

    /// <summary>
    /// Splits an encoding into independently writable sparse images no larger than
    /// <paramref name="maximumEncodedLength"/>.
    /// </summary>
    public static IReadOnlyList<SparseImageWritePlan> Split(
        SparseImageWritePlan plan,
        long maximumEncodedLength)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumEncodedLength,
            SparseConstant.HeaderLength + SparseConstant.ChunkLength);
        return SparseImagePlanSplitter.Split(plan, maximumEncodedLength);
    }

    public static ValueTask<SparseImageWritePlan> WriteAsync(
        Stream source,
        Stream destination,
        SparseImageWriteOptions? options = null,
        IProgress<BlockCopyProgress>? progress = null,
        BudgetedArrayPool? buffers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSourceStream(source);
        ValidateDestination(destination);
        ValidateDistinctStreams(source, destination);
        cancellationToken.ThrowIfCancellationRequested();
        return WriteStreamAsyncCore(source, destination, options, progress, buffers, cancellationToken);
    }

    private static async ValueTask<SparseImageWritePlan> WriteStreamAsyncCore(
        Stream source,
        Stream destination,
        SparseImageWriteOptions? options,
        IProgress<BlockCopyProgress>? progress,
        BudgetedArrayPool? buffers,
        CancellationToken cancellationToken)
    {
        long origin = source.Position;
        try
        {
            var device = new SeekableStreamBlockDevice(source, origin);
            options ??= new SparseImageWriteOptions();
            options.Validate();
            return await WriteAsyncCore(device, destination, options, progress, buffers, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            source.Position = origin;
        }
    }

    private static async ValueTask<SparseImageWritePlan> WriteAsyncCore(
        IReadableBlockDevice source,
        Stream destination,
        SparseImageWriteOptions options,
        IProgress<BlockCopyProgress>? progress,
        BudgetedArrayPool? buffers,
        CancellationToken cancellationToken)
    {
        SparseImageWritePlan plan = SparseImagePlanner.Analyze(source, options, buffers, cancellationToken);
        await SparseImageEncoder
            .WriteAsync(source, destination, plan, options.BufferSize, progress, buffers, cancellationToken)
            .ConfigureAwait(false);
        return plan;
    }

    private static async ValueTask<SparseImageWritePlan> WritePlannedAsyncCore(
        IReadableBlockDevice source,
        Stream destination,
        SparseImageWritePlan plan,
        int bufferSize,
        IProgress<BlockCopyProgress>? progress,
        BudgetedArrayPool? buffers,
        CancellationToken cancellationToken)
    {
        await SparseImageEncoder
            .WriteAsync(source, destination, plan, bufferSize, progress, buffers, cancellationToken)
            .ConfigureAwait(false);
        return plan;
    }

    private static async ValueTask<SparseImageWritePlan> WritePlannedStreamAsyncCore(
        Stream source,
        Stream destination,
        SparseImageWritePlan plan,
        int bufferSize,
        IProgress<BlockCopyProgress>? progress,
        BudgetedArrayPool? buffers,
        CancellationToken cancellationToken)
    {
        long origin = source.Position;
        try
        {
            var device = new SeekableStreamBlockDevice(source, origin);
            ValidatePlannedWrite(device, destination, plan, bufferSize);
            return await WritePlannedAsyncCore(
                    device,
                    destination,
                    plan,
                    bufferSize,
                    progress,
                    buffers,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            source.Position = origin;
        }
    }

    private static void ValidateSourceStream(Stream source)
    {
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException(Strings.SourceMustBeReadableAndSeekable, nameof(source));
    }

    private static void ValidateDestination(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException(Strings.DestinationMustBeWritable, nameof(destination));
    }

    private static void ValidateDistinctStreams(Stream source, Stream destination)
    {
        if (ReferenceEquals(source, destination))
            throw new ArgumentException(null, nameof(destination));
    }

    private static void ValidatePlannedWrite(
        IReadableBlockDevice source,
        Stream destination,
        SparseImageWritePlan plan,
        int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        ValidateDestination(destination);
        ValidateBufferSize(bufferSize);
        if (source.Length != plan.SourceLength)
            throw new ArgumentException(null, nameof(source));
    }

    private static void ValidateBufferSize(int bufferSize)
    {
        if (bufferSize < sizeof(uint) || bufferSize > BoundedStreamCopier.MaximumBufferSize ||
            (bufferSize & (sizeof(uint) - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
    }
}
