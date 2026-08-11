using System.Buffers;

namespace GeekFlashCore.BlockDevice;

public static class BoundedStreamCopier
{
    public const int DefaultBufferSize = 256 * 1024;
    public const int MaximumBufferSize = 1024 * 1024;

    public static ValueTask CopyExactlyAsync(
        Stream source,
        Stream destination,
        long length,
        int bufferSize = DefaultBufferSize,
        IProgress<BlockCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(source, destination, length, bufferSize);
        cancellationToken.ThrowIfCancellationRequested();
        if (length == 0)
        {
            return ValueTask.CompletedTask;
        }

        var buffers = new BudgetedArrayPool(
            new ByteBudget(MaximumBufferSize),
            ArrayPool<byte>.Shared);
        return CopyCoreAsync(
            source,
            destination,
            length,
            buffers,
            bufferSize,
            progress,
            cancellationToken);
    }

    public static ValueTask CopyExactlyAsync(
        Stream source,
        Stream destination,
        long length,
        BudgetedArrayPool buffers,
        int bufferSize = DefaultBufferSize,
        IProgress<BlockCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(source, destination, length, bufferSize);
        ArgumentNullException.ThrowIfNull(buffers);
        cancellationToken.ThrowIfCancellationRequested();
        return length == 0
            ? ValueTask.CompletedTask
            : CopyCoreAsync(
                source,
                destination,
                length,
                buffers,
                bufferSize,
                progress,
                cancellationToken);
    }

    private static async ValueTask CopyCoreAsync(
        Stream source,
        Stream destination,
        long length,
        BudgetedArrayPool buffers,
        int bufferSize,
        IProgress<BlockCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using PooledBufferLease buffer = await buffers
            .RentAsync(bufferSize, cancellationToken)
            .ConfigureAwait(false);
        Memory<byte> memory = buffer.Memory;
        long completed = 0;

        while (completed < length)
        {
            int requested = (int)Math.Min(memory.Length, length - completed);
            int read = await source
                .ReadAsync(memory[..requested], cancellationToken)
                .ConfigureAwait(false);
            if ((uint)read > (uint)requested)
            {
                throw new BlockDeviceException(Strings.SourceInvalidReadLength);
            }

            if (read == 0)
            {
                throw new BlockDeviceException(Strings.SourceEndedEarly);
            }

            await destination
                .WriteAsync(memory[..read], cancellationToken)
                .ConfigureAwait(false);
            completed += read;
            progress?.Report(new BlockCopyProgress(completed, length));
        }
    }

    private static void ValidateArguments(
        Stream source,
        Stream destination,
        long length,
        int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!source.CanRead) throw new ArgumentException(Strings.SourceMustBeReadable, nameof(source));
        if (!destination.CanWrite)
            throw new ArgumentException(Strings.DestinationMustBeWritable, nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (bufferSize is < 1 or > MaximumBufferSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferSize),
                bufferSize,
                Strings.FormatExpectedRange(1, MaximumBufferSize));
        }
    }
}
