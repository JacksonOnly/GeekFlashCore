namespace GeekFlashCore.Transport.SerialPort;

internal sealed class PortBuffer : IDisposable
{
    private readonly System.IO.Ports.SerialPort _port;
    private readonly object _lock = new();
    private readonly int _maximumCapacity;
    private bool _disposed;

    public PortBuffer(System.IO.Ports.SerialPort port, int maximumCapacity)
    {
        _port = port;
        _maximumCapacity = maximumCapacity;
        _port.DataReceived += OnDataReceived;
        _port.ErrorReceived += OnError;
    }

    private void OnDataReceived(object? sender, System.IO.Ports.SerialDataReceivedEventArgs e)
    {
        lock (_lock)
        {
            if (!_disposed)
                Monitor.PulseAll(_lock);
        }
    }

    private void OnError(object? sender, System.IO.Ports.SerialErrorReceivedEventArgs e)
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                Monitor.PulseAll(_lock);
            }
        }
    }

    public bool WaitForData(int timeoutMs)
    {
        lock (_lock)
        {
            if (_disposed)
                return false;

            if (HasData())
                return true;

            if (timeoutMs == System.IO.Ports.SerialPort.InfiniteTimeout)
            {
                while (!_disposed && !HasData())
                {
                    Monitor.Wait(_lock);
                }

                return !_disposed && HasData();
            }

            if (timeoutMs < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(timeoutMs),
                    Strings.FormatSerialTransport_InvalidTimeout(timeoutMs));

            long deadline = Environment.TickCount64 + timeoutMs;
            while (!_disposed && !HasData())
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    return false;

                Monitor.Wait(_lock, (int)Math.Min(remaining, 100));
            }

            return !_disposed && HasData();
        }
    }

    public int ReadUpTo(Span<byte> destination)
    {
        if (destination.IsEmpty)
            return 0;

        lock (_lock)
        {
            if (_disposed)
                return 0;

            int available = GetAvailable();
            if (available <= 0)
                return 0;
            EnsureWithinLimit(available);

            int count = Math.Min(available, destination.Length);
            try
            {
                return _port.BaseStream.Read(destination[..count]);
            }
            catch (TimeoutException)
            {
                return 0;
            }
        }
    }

    public int ReadAvailable(Span<byte> destination) => ReadUpTo(destination);

    private bool HasData() => GetAvailable() > 0;

    private int GetAvailable()
    {
        if (!_port.IsOpen)
            return 0;
        return _port.BytesToRead;
    }

    private void EnsureWithinLimit(int available)
    {
        if (available > _maximumCapacity)
        {
            throw new IOException(
                Strings.FormatSerialTransport_ReceiveBufferExceeded(
                    _port.PortName,
                    _maximumCapacity,
                    available));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Monitor.PulseAll(_lock);
        }

        _port.DataReceived -= OnDataReceived;
        _port.ErrorReceived -= OnError;
    }
}
