using System.IO.Ports;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Transport.SerialPort;

internal sealed class SerialPortTransport : ITransport
{
    private readonly System.IO.Ports.SerialPort _serialPort;
    private Stream? _portBaseStream;
    private PortBuffer? _buffer;
    private readonly int _readTimeout;
    private readonly int _writeTimeout;
    private readonly int _maximumReadBufferSize;
    public bool IsOpen => _serialPort.IsOpen;

    public SerialPortTransport(string portName, int baudRate = 115200, int dataBits = 8,
        StopBits stopBits = StopBits.None, Parity parity = Parity.None, int bufferSize = 8192,
        int maximumReadBufferSize = 4 * 1024 * 1024, int readTimeout = 1000, int writeTimeout = 1000)
    {
        if (string.IsNullOrWhiteSpace(portName))
            throw new ArgumentException(Strings.SerialTransport_PortNameRequired, nameof(portName));
        if (bufferSize <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(bufferSize),
                Strings.FormatSerialTransport_InvalidBufferSize(
                    nameof(bufferSize),
                    bufferSize));
        if (maximumReadBufferSize < bufferSize)
            throw new ArgumentOutOfRangeException(
                nameof(maximumReadBufferSize),
                Strings.FormatSerialTransport_MaximumReadBufferTooSmall(
                    maximumReadBufferSize,
                    bufferSize));

        _serialPort = new System.IO.Ports.SerialPort(portName, baudRate, parity, dataBits, stopBits);
        _readTimeout = readTimeout;
        _writeTimeout = writeTimeout;
        _maximumReadBufferSize = maximumReadBufferSize;

        _serialPort.ReadBufferSize = bufferSize;
        _serialPort.WriteBufferSize = bufferSize;
    }

    public void Open()
    {
        if (_serialPort.IsOpen)
            return;

        Close();
        _serialPort.Open();
        _portBaseStream = _serialPort.BaseStream;
        _portBaseStream.WriteTimeout = _writeTimeout;
        _portBaseStream.ReadTimeout = _readTimeout;
        _buffer = new PortBuffer(_serialPort, _maximumReadBufferSize);
    }

    public void Close()
    {
        try
        {
            _buffer?.Dispose();
        }
        catch
        {
            // ignored
        }

        _buffer = null;

        try
        {
            _portBaseStream?.Close();
            _portBaseStream?.Dispose();
        }
        catch
        {
            // ignored
        }

        _portBaseStream = null;

        try
        {
            _serialPort.Close();
        }
        catch
        {
            // ignored
        }
    }

    public void Write(ReadOnlySpan<byte> data) => GetPortStream().Write(data);
    public void Write(byte[] data, int offset, int count) => GetPortStream().Write(data, offset, count);

    public int Read(Span<byte> data, int? timeoutInMilliseconds = null)
    {
        if (_buffer == null)
            throw new InvalidOperationException(Strings.SerialTransport_NotOpen);

        if (data.IsEmpty)
            return 0;

        int timeout = timeoutInMilliseconds ?? _readTimeout;
        ValidateTimeout(timeout);
        if (!_buffer.WaitForData(timeout))
            throw CreateReadTimeout(timeout, 1, 0);

        int read = _buffer.ReadUpTo(data);
        if (read == 0)
            throw CreateReadTimeout(timeout, 1, 0);
        return read;
    }

    public int Read(byte[] data, int offset, int count, int? timeoutInMilliseconds = null)
    {
        if (_buffer == null)
            throw new InvalidOperationException(Strings.SerialTransport_NotOpen);

        ArgumentNullException.ThrowIfNull(data);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), Strings.FormatSerialRead_InvalidOffset(offset));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), Strings.FormatSerialRead_InvalidCount(count));
        if (offset > data.Length - count)
            throw new ArgumentException(Strings.SerialRead_OffsetCountOutsideBuffer, nameof(count));
        return Read(data.AsSpan(offset, count), timeoutInMilliseconds);
    }

    public int ReadExact(Span<byte> destination, int? timeoutInMilliseconds = null)
    {
        if (_buffer == null)
            throw new InvalidOperationException(Strings.SerialTransport_NotOpen);

        int totalRead = 0;
        int length = destination.Length;
        int timeout = timeoutInMilliseconds ?? _readTimeout;
        ValidateTimeout(timeout);
        long deadline = timeout == System.IO.Ports.SerialPort.InfiniteTimeout
            ? 0
            : Environment.TickCount64 + timeout;

        while (totalRead < length)
        {
            int remaining = timeout == System.IO.Ports.SerialPort.InfiniteTimeout
                ? timeout
                : (int)Math.Max(0, deadline - Environment.TickCount64);
            bool dataAvailable = _buffer.WaitForData(remaining);
            if (!dataAvailable)
                throw CreateReadTimeout(timeout, length, totalRead);

            int read = _buffer.ReadUpTo(destination[totalRead..]);
            if (read == 0)
                continue;
            totalRead += read;
        }

        return totalRead;
    }

    public int ReadAvailable(Span<byte> data)
    {
        if (_buffer == null)
            throw new InvalidOperationException(Strings.SerialTransport_NotOpen);
        return _buffer.ReadAvailable(data);
    }

    public void Flush()
    {
        if (!_serialPort.IsOpen)
            throw new InvalidOperationException(Strings.SerialTransport_NotOpen);
        _serialPort.DiscardInBuffer();
        _serialPort.DiscardOutBuffer();
    }

    private Stream GetPortStream() =>
        _portBaseStream ?? throw new InvalidOperationException(Strings.SerialTransport_NotOpen);

    private static void ValidateTimeout(int timeout)
    {
        if (timeout < 0 && timeout != System.IO.Ports.SerialPort.InfiniteTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), Strings.FormatSerialTransport_InvalidTimeout(timeout));
    }

    private TimeoutException CreateReadTimeout(int timeout, int expected, int received) =>
        new(Strings.FormatSerialTransport_ReadTimedOut(
            _serialPort.PortName,
            timeout,
            expected,
            received));

    public void Dispose()
    {
        Close();
        try
        {
            _serialPort.Dispose();
        }
        catch
        {
            // ignored
        }
    }
}
