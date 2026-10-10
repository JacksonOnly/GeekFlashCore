using GeekFlashCore.Transport.Abstractions;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace GeekFlashCore.Transport.LibUsb.Internals;

internal class LibUsbTransport : IUsbTransport
{
    private readonly LibUsbConnectionOptions? _options;
    public UsbTransportIdentity Identity { get; private set; } = new(0, 0);
    public int InterfaceNumber => _claimedInterface;
    private int? _controlInterfaceNumber;
    public int? ControlInterfaceNumber => _controlInterfaceNumber;

    internal LibUsbTransport(LibUsbConnectionOptions options):this(new UsbContext(),options) { }
    internal LibUsbTransport(IUsbContext context,LibUsbConnectionOptions options)
    {
        _options = options;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        List<IUsbDevice>? candidates=null;
        try
        {
            ArgumentNullException.ThrowIfNull(options);options.Validate();
            var finder = new UsbDeviceFinder { Vid = options.Identity.VendorId, Pid = options.Identity.ProductId,
                SerialNumber = options.Identity.SerialNumber! };
            candidates = _context.FindAll(finder).ToList();
            var matches = candidates.Where(d => MatchesIdentity(d, options.Identity)).ToList();
            if (matches.Count != 1)
            {
                throw new InvalidOperationException(matches.Count == 0 ? Strings.LibUsbTransport_DeviceMissing : Strings.LibUsbTransport_AmbiguousDevice);
            }
            _device = matches[0];
            foreach (var device in candidates) if (!ReferenceEquals(device, _device)) device.Dispose();
            Identity = GetIdentity(_device, options.Identity.SerialNumber);
        }
        catch
        {
            if(candidates is not null)foreach(var device in candidates)
                try { device.Dispose(); } catch { /* Preserve the selection failure and release every candidate. */ }
            try { _context.Dispose(); } catch { /* Preserve the primary construction failure. */ }
            throw;
        }
        _requestedInterface = options.InterfaceNumber;
        _bufferSize = options.BufferSize;
        _readTimeout = options.ReadTimeoutMilliseconds;
        _writeTimeout = options.WriteTimeoutMilliseconds;
    }

    internal static UsbTransportIdentity GetIdentity(IUsbDevice device, string? serial = null) =>
        device is UsbDevice usb
            ? new((ushort)device.VendorId, (ushort)device.ProductId, serial, usb.BusNumber, string.Join(".", usb.PortNumbers))
            : new((ushort)device.VendorId, (ushort)device.ProductId, serial);

    private static bool MatchesIdentity(IUsbDevice device, UsbTransportIdentity wanted)
    {
        var actual = GetIdentity(device, wanted.SerialNumber);
        return (wanted.BusNumber is null || actual.BusNumber == wanted.BusNumber) &&
            (wanted.PortPath is null || actual.PortPath == wanted.PortPath) && wanted.DevicePath is null;
    }
    public bool IsOpen
    {
        get
        {
            lock (_sync)
                return !_disposed && _opened && _device.IsOpen;
        }
    }

    private readonly object _sync = new();
    private readonly IUsbDevice _device;
    private UsbEndpointReader? _reader;
    private UsbEndpointWriter? _writer;
    private bool _disposed;
    private bool _opened;
    private readonly InitialUsbReadRecovery _initialReadRecovery = new();

    private readonly IUsbContext _context;
    private readonly int _bufferSize;
    private readonly int _requestedInterface;
    private int _claimedInterface = -1;
    private readonly ReadEndpointID? _requestedReadEndpoint;
    private readonly WriteEndpointID? _requestedWriteEndpoint;
    private ReadEndpointID? _readEndpointId;
    private WriteEndpointID? _writeEndpointId;
    private readonly int _readTimeout;
    private readonly int _writeTimeout;
    public LibUsbTransport(UsbDeviceFinder finder, int claimedInterface = -1, int bufferSize = 8192, ReadEndpointID? readEndpointId = null,
        WriteEndpointID? writeEndpointId = null, int readTimeout = 1000, int writeTimeout = 1000)
        : this(new UsbContext(), finder, claimedInterface, bufferSize, readEndpointId, writeEndpointId, readTimeout, writeTimeout)
    {
    }

    internal LibUsbTransport(IUsbContext context, UsbDeviceFinder finder, int claimedInterface = -1,
        int bufferSize = 8192, ReadEndpointID? readEndpointId = null, WriteEndpointID? writeEndpointId = null,
        int readTimeout = 1000, int writeTimeout = 1000)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
            ValidateTimeout(readTimeout);
            ValidateTimeout(writeTimeout);
            _device = _context.Find(finder) ?? throw new InvalidOperationException(
                Strings.LibUsbTransport_DeviceMissing);
            Identity = GetIdentity(_device);
        }
        catch
        {
            _context.Dispose();
            throw;
        }
        _requestedInterface = claimedInterface;
        _bufferSize = bufferSize;
        _readTimeout = readTimeout;
        _writeTimeout = writeTimeout;
        _requestedReadEndpoint = readEndpointId;
        _requestedWriteEndpoint = writeEndpointId;
    }

    public void Open()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_opened && _device.IsOpen)
                return;

            CloseCore();

            try
            {
                if (!_device.IsOpen)
                    _device.Open();

                ConfigureDevice();
                GetEndpointId(out _readEndpointId, out _writeEndpointId);
                ArgumentNullException.ThrowIfNull(_readEndpointId);
                ArgumentNullException.ThrowIfNull(_writeEndpointId);
                _reader = _device.OpenEndpointReader(_readEndpointId.Value, _bufferSize);
                _writer = _device.OpenEndpointWriter(_writeEndpointId.Value);
                _opened = true;
                _initialReadRecovery.Reset(_options?.RecoverInitialReadStall == true);
            }
            catch
            {
                CloseCore();
                throw;
            }
        }
    }

    private void GetEndpointId(out ReadEndpointID? readEndpointId, out WriteEndpointID? writeEndpointId)
    {
        if (!_device.IsOpen)
            throw new InvalidOperationException(Strings.LibUsbTransport_NotOpen);
        readEndpointId = _requestedReadEndpoint;
        writeEndpointId = _requestedWriteEndpoint;
        lock (_sync)
        {
            foreach (var usbConfigInfo in _device.Configs)
            {
                if (usbConfigInfo.ConfigurationValue != _device.Configuration)
                    continue;
                foreach (var usbInterfaceInfo in usbConfigInfo.Interfaces)
                {
                    if (usbInterfaceInfo.Number != _claimedInterface)
                        continue;
                    if (usbInterfaceInfo.AlternateSetting != (_options?.AlternateSetting ?? 0))
                        continue;
                    if (usbInterfaceInfo.Endpoints.Count > 1)
                    {
                        foreach (var usbEndpointInfo in usbInterfaceInfo.Endpoints)
                        {
                            const byte USB_ENDPOINT_DIR_MASK = 0x80;
                            if ((usbEndpointInfo.Attributes & 3) != (byte)EndpointType.Bulk)
                                continue;
                            var address = usbEndpointInfo.EndpointAddress;
                            switch ((EndpointDirection)(address & USB_ENDPOINT_DIR_MASK))
                            {
                                case EndpointDirection.In:
                                    readEndpointId ??= (ReadEndpointID)address;
                                    break;
                                case EndpointDirection.Out:
                                    writeEndpointId ??= (WriteEndpointID)address;
                                    break;
                            }
                        }
                    }
                }
            }
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            CloseCore();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            try
            {
                CloseCore();
            }
            finally
            {
                _disposed = true;
                try { _device.Dispose(); }
                finally { _context.Dispose(); }
            }
        }
    }

    public void WriteZeroLengthPacket()
    {
        lock (_sync)
        {
            Error error = GetWriter().Write(ReadOnlySpan<byte>.Empty, _writeTimeout, out int transferred);
            ThrowTransferError(error, "USB write", _writeTimeout);
            if (transferred != 0)
                throw new IOException(Strings.FormatLibUsbTransport_WriteIncomplete(0, transferred));
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            UsbEndpointWriter writer = GetWriter();
            if (data.IsEmpty)
                return;

            int totalWritten = 0;
            while (totalWritten < data.Length)
            {
                ReadOnlySpan<byte> remaining = data[totalWritten..];
                Error error = writer.Write(remaining, _writeTimeout, out int transferLength);
                ThrowTransferError(error, "USB write", _writeTimeout);
                if (transferLength <= 0 || transferLength > remaining.Length)
                    throw new IOException(
                        Strings.FormatLibUsbTransport_WriteIncomplete(remaining.Length, transferLength));
                totalWritten += transferLength;
            }
        }
    }

    public void Write(byte[] data, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateBufferArguments(data.Length, offset, count);
        Write(data.AsSpan(offset, count));
    }

    public int Read(Span<byte> data, int? timeoutInMilliseconds = null)
    {
        lock (_sync)
        {
            UsbEndpointReader reader = GetReader();
            if (data.IsEmpty)
                return 0;

            int timeout = ValidateTimeout(timeoutInMilliseconds ?? _readTimeout);
            int transferLength;
            Error error = _initialReadRecovery.IsPending
                ? _initialReadRecovery.Read(data, timeout, reader.Read, ClearInitialReadHalt, out transferLength)
                : reader.Read(data, timeout, out transferLength);
            ThrowTransferError(error, "USB read", timeout);
            return transferLength;
        }
    }

    public int Read(byte[] data, int offset, int count, int? timeoutInMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateBufferArguments(data.Length, offset, count);
        return Read(data.AsSpan(offset, count), timeoutInMilliseconds);
    }

    public int ReadExact(Span<byte> destination, int? timeoutInMilliseconds = null)
    {
        lock (_sync)
        {
            UsbEndpointReader reader = GetReader();
            if (destination.IsEmpty)
                return 0;

            int timeout = ValidateTimeout(timeoutInMilliseconds ?? _readTimeout);
            _initialReadRecovery.Suppress();
            long deadline = Environment.TickCount64 + timeout;
            int totalRead = 0;
            while (totalRead < destination.Length)
            {
                int remaining = timeout == 0 ? 0 : checked((int)Math.Max(0, deadline - Environment.TickCount64));
                if (timeout > 0 && remaining == 0)
                    throw new TimeoutException(Strings.FormatLibUsbTransport_TransferTimedOut("USB read", timeout));
                Error error = reader.Read(destination[totalRead..], remaining, out int transferLength);
                ThrowTransferError(error, "USB read", timeout);
                if (transferLength <= 0)
                    throw new EndOfStreamException(Strings.LibUsbTransport_ZeroByteRead);
                totalRead += transferLength;
            }

            return totalRead;
        }
    }

    public int ReadAvailable(Span<byte> data)
    {
        lock (_sync)
        {
            UsbEndpointReader reader = GetReader();
            if (data.IsEmpty)
                return 0;
            // libusb timeout 0 means an unlimited wait, including available-data probes.
            _initialReadRecovery.Suppress();
            Error error = reader.Read(data, 1, out int transferLength);
            if (error == Error.Timeout)
                return transferLength;
            ThrowTransferError(error, "USB available-data read", 1);
            return transferLength;
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            _initialReadRecovery.Suppress();
            Error error = GetReader().ReadFlush();
            ThrowTransferError(error, "USB input flush", 0);
        }
    }

    public bool GetDescriptor(
        byte descriptorType,
        byte index,
        short langId,
        IntPtr buffer,
        int bufferLength,
        out int transferLength)
    {
        lock (_sync)
        {
            EnsureOpen();
            ValidateLength(bufferLength);
            return _device.GetDescriptor(
                descriptorType,
                index,
                langId,
                buffer,
                bufferLength,
                out transferLength);
        }
    }

    public bool GetDescriptor(
        byte descriptorType,
        byte index,
        short langId,
        object buffer,
        int bufferLength,
        out int transferLength)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_sync)
        {
            EnsureOpen();
            ValidateLength(bufferLength);
            return _device.GetDescriptor(
                descriptorType,
                index,
                langId,
                buffer,
                bufferLength,
                out transferLength);
        }
    }

    public int ControlTransfer(UsbSetupPacket setupPacket, byte[] buffer, int offset, int length)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ValidateBufferArguments(buffer.Length, offset, length);
        ValidateControlLength(length);
        lock (_sync)
        {
            EnsureOpen();
            EnsureFiniteControlTimeout();
            return _device.ControlTransfer(setupPacket, buffer, offset, length);
        }
    }

    public int ControlTransfer(UsbSetupPacket setupPacket)
    {
        lock (_sync)
        {
            EnsureOpen();
            EnsureFiniteControlTimeout();
            return _device.ControlTransfer(setupPacket);
        }
    }

    public void ControlOut(
        byte requestType,
        byte request,
        ushort value,
        ushort index,
        ReadOnlySpan<byte> data)
    {
        int length = ValidateControlLength(data.Length);
        UsbSetupPacket setupPacket = new(requestType, request, value, index, length);
        if (data.IsEmpty)
        {
            ControlTransfer(setupPacket);
            return;
        }

        byte[] buffer = data.ToArray();
        int transferLength = ControlTransfer(setupPacket, buffer, 0, buffer.Length);
        if (transferLength != buffer.Length)
            throw new IOException(Strings.FormatLibUsbTransport_WriteIncomplete(buffer.Length, transferLength));
    }

    public int ControlIn(
        byte requestType,
        byte request,
        ushort value,
        ushort index,
        Span<byte> destination)
    {
        int length = ValidateControlLength(destination.Length);
        UsbSetupPacket setupPacket = new(requestType, request, value, index, length);
        if (destination.IsEmpty)
            return ControlTransfer(setupPacket);

        byte[] buffer = new byte[destination.Length];
        int transferLength = ControlTransfer(setupPacket, buffer, 0, buffer.Length);
        if ((uint)transferLength > (uint)buffer.Length)
            throw new IOException(Strings.FormatLibUsbTransport_WriteIncomplete(buffer.Length, transferLength));
        buffer.AsSpan(0, transferLength).CopyTo(destination);
        return transferLength;
    }

    private void ConfigureDevice()
    {
        if (_options?.Configuration is { } requested && _device.Configuration != requested)
            _device.SetConfiguration(requested);
        int activeConfiguration;
        try
        {
            activeConfiguration = _device.Configuration;
        }
        catch
        {
            activeConfiguration = 0;
        }
        if (activeConfiguration == 0)
        {
            var configuration = _device.Configs.FirstOrDefault(config => config.Interfaces.Any(face =>
                (_requestedInterface < 0 || face.Number == _requestedInterface) &&
                face.AlternateSetting==(_options?.AlternateSetting??0) && HasBulkPair(face)));
            if (configuration is null)
                throw new InvalidOperationException(Strings.LibUsbTransport_BulkInterfaceMissing);
            _device.SetConfiguration(configuration.ConfigurationValue);
            if (_device.Configuration != configuration.ConfigurationValue)
                throw new InvalidOperationException(Strings.LibUsbTransport_ConfigurationFailed);
        }

        int selectedInterface = _requestedInterface >= 0 ? _requestedInterface : FindFirstInterfaceNumber();
        if (selectedInterface < 0)
            throw new InvalidOperationException(Strings.LibUsbTransport_BulkInterfaceMissing);
        var activeFaces=_device.Configs.Where(c=>c.ConfigurationValue==_device.Configuration).SelectMany(c=>c.Interfaces).ToArray();
        var selected=activeFaces.SingleOrDefault(f=>f.Number==selectedInterface&&f.AlternateSetting==(_options?.AlternateSetting??0));
        if(selected is null||!HasBulkPair(selected))
            throw new InvalidOperationException(Strings.LibUsbTransport_BulkInterfaceMissing);
        _controlInterfaceNumber=_options?.ControlInterfaceNumber;
        if(_options is not null&&_controlInterfaceNumber is null&&selected.Class==ClassCode.Data)
        {
            var control=activeFaces.Where(f=>f.Class==ClassCode.Comm).Select(f=>f.Number).Distinct().ToArray();
            if(control.Length>1)throw new InvalidOperationException(Strings.LibUsbTransport_AmbiguousDevice);
            if(control.Length==1)_controlInterfaceNumber=control[0];
        }
        if(_controlInterfaceNumber is { } controlNumber&&!activeFaces.Any(f=>f.Number==controlNumber))
            throw new InvalidOperationException(Strings.LibUsbTransport_BulkInterfaceMissing);
        if (!_device.ClaimInterface(selectedInterface))
            throw new InvalidOperationException(
                Strings.FormatLibUsbTransport_InterfaceClaimFailed(selectedInterface));
        _claimedInterface = selectedInterface;
        if (_options is { } options && !_device.SetAltInterface(options.AlternateSetting))
            throw new InvalidOperationException(Strings.LibUsbTransport_ConfigurationFailed);
    }

    private int FindFirstInterfaceNumber()
    {
        int activeConfiguration = _device.Configuration;
        foreach (var config in _device.Configs)
        {
            if (activeConfiguration != 0 && config.ConfigurationValue != activeConfiguration)
                continue;
            foreach (var usbInterface in config.Interfaces)
            {
                if (usbInterface.AlternateSetting == (_options?.AlternateSetting ?? 0) &&
                    usbInterface.Class is ClassCode.Data or ClassCode.VendorSpec && HasBulkPair(usbInterface))
                {
                    return usbInterface.Number;
                }
            }
        }

        return -1;
    }

    private static bool HasBulkPair(LibUsbDotNet.Info.UsbInterfaceInfo face) =>
        face.Endpoints.Any(static e => (e.Attributes & 3) == (byte)EndpointType.Bulk && (e.EndpointAddress & 0x80) != 0) &&
        face.Endpoints.Any(static e => (e.Attributes & 3) == (byte)EndpointType.Bulk && (e.EndpointAddress & 0x80) == 0);

    private void CloseCore()
    {
        _initialReadRecovery.Suppress();
        _reader = null;
        _writer = null;

        try
        {
            if (_claimedInterface >= 0)
            {
                try { _device.ReleaseInterface(_claimedInterface); }
                finally { _claimedInterface=-1; }
            }
        }
        finally
        {
            try { if (_device.IsOpen) _device.Close(); }
            finally { _opened=false;_controlInterfaceNumber=null; }
        }
    }

    private UsbEndpointReader GetReader()
    {
        EnsureOpen();
        return _reader!;
    }

    private Error ClearInitialReadHalt()
    {
        EnsureFiniteControlTimeout();
        return GetReader().ClearHalt();
    }

    private UsbEndpointWriter GetWriter()
    {
        EnsureOpen();
        return _writer!;
    }

    private void EnsureOpen()
    {
        ThrowIfDisposed();
        if (!_opened || !_device.IsOpen || _reader is null || _writer is null)
            throw new InvalidOperationException(Strings.LibUsbTransport_NotOpen);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LibUsbTransport));
    }
    private void EnsureFiniteControlTimeout()
    {
        if(_options is not null&&UsbDevice.ControlTransferTimeout<=0)
            throw new InvalidOperationException(Strings.FormatLibUsbTransport_TransferTimedOut("USB control",UsbDevice.ControlTransferTimeout));
    }
    private static int ValidateTimeout(int timeout)
    {
        if (timeout < 0)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return timeout;
    }

    private static void ValidateLength(int length)
    {
        if (length < 0 || length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length));
    }

    private static int ValidateControlLength(int length)
    {
        if (length < 0 || length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(length));
        return length;
    }

    private static void ValidateBufferArguments(int bufferLength, int offset, int count)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (offset > bufferLength - count)
            throw new ArgumentException(Strings.LibUsbTransport_BufferRangeInvalid);
    }

    private static void ThrowTransferError(Error error, string operation, int timeout)
    {
        if (error == Error.Success)
            return;
        // Keep the TimeoutException contract while retaining the native cause so
        // hosts can distinguish USB reconnect waits from resource-provider timeouts.
        if (error == Error.Timeout)
            throw new TimeoutException(Strings.FormatLibUsbTransport_TransferTimedOut(operation, timeout), new UsbException(error));
        error.ThrowOnError();
    }
}
