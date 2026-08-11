namespace GeekFlashCore.IO.BlockDevice;

public static class BlockDeviceIO
{
    public static int GetReadLength(
        IReadableBlockDevice device,
        long offset,
        int requestedLength)
    {
        ArgumentNullException.ThrowIfNull(device);
        return GetReadLength(device.Length, offset, requestedLength);
    }

    internal static int GetReadLength(
        long deviceLength,
        long offset,
        int requestedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(requestedLength);

        if (offset >= deviceLength || requestedLength == 0)
        {
            return 0;
        }

        return (int)Math.Min(requestedLength, deviceLength - offset);
    }

    public static int ValidateReadResult(int read, int requestedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedLength);

        if ((uint)read > (uint)requestedLength)
        {
            throw new BlockDeviceException(Strings.InvalidBlockDeviceReadLength);
        }

        return read;
    }

    public static void ReadExactlyAt(
        IReadableBlockDevice device,
        long offset,
        Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (destination.IsEmpty)
        {
            return;
        }

        if (offset > device.Length - destination.Length)
        {
            throw new BlockDeviceException(Strings.RequestedRangeExceedsBlockDevice);
        }

        int written = 0;
        while (written < destination.Length)
        {
            int remaining = destination.Length - written;
            int read = device.ReadAt(
                checked(offset + written),
                destination.Slice(written, remaining));
            ValidateReadResult(read, remaining);

            if (read == 0)
            {
                throw new BlockDeviceException(Strings.BlockDeviceMadeNoProgress);
            }

            written += read;
        }
    }
}
