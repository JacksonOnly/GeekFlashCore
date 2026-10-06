using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Transport.LibUsb;

/// <summary>Identified USB selection with finite transfer timeouts and an explicit alternate setting.</summary>
public sealed record LibUsbConnectionOptions
{
    public required UsbTransportIdentity Identity
    {
        get; init;
    }
    public int InterfaceNumber { get; init; } = -1;
    public int AlternateSetting
    {
        get; init;
    }
    public int? Configuration
    {
        get; init;
    }
    public int? ControlInterfaceNumber
    {
        get; init;
    }
    public int BufferSize { get; init; } = 65536;
    public int ReadTimeoutMilliseconds { get; init; } = 3000;
    public int WriteTimeoutMilliseconds { get; init; } = 3000;
    /// <summary>Opt-in recovery for the first single-byte bulk read after Open. Only a zero-byte Pipe
    /// clears the IN halt once and continues reading within the original timeout; no writes are retried.</summary>
    public bool RecoverInitialReadStall { get; init; }
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Identity);
        if (Identity.DevicePath is not null)
            throw new ArgumentException(Strings.LibUsbTransport_DevicePathUnsupported);
        if (Identity.SerialNumber is { } serial && string.IsNullOrWhiteSpace(serial) ||
            Identity.PortPath is { } path && (Identity.BusNumber is null || path.Split('.').Any(p => !byte.TryParse(p, out byte n) || n == 0)))
            throw new ArgumentException(Strings.LibUsbTransport_IdentityInvalid);
        if (InterfaceNumber is < -1 or > 255 || AlternateSetting is < 0 or > 255 ||
            Configuration is < 1 or > 255 || ControlInterfaceNumber is < 0 or > 255 ||
            BufferSize is < 512 or > 1048576 || ReadTimeoutMilliseconds <= 0 || WriteTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(InterfaceNumber));
    }
}
