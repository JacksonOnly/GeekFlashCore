namespace GeekFlashCore.IO.BlockDevice;

public static class BlockDeviceIO
{
    public static int GetReadLength(
        IReadableBlockDevice device,
        long offset,
        int requestedLength)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(requestedLength);

        if (offset >= device.Length || requestedLength == 0)
        {
            return 0;
        }

        return (int)Math.Min(requestedLength, device.Length - offset);
    }

    public static int ValidateReadResult(int read, int requestedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedLength);

        if ((uint)read > (uint)requestedLength)
        {
            throw new IOException(Strings.InvalidBlockDeviceReadLength);
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
            throw new EndOfStreamException(Strings.RequestedRangeExceedsBlockDevice);
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
                throw new EndOfStreamException(Strings.BlockDeviceMadeNoProgress);
            }

            written += read;
        }
    }
}
