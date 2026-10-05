namespace GeekFlashCore.Transport.Abstractions;

/// <summary>Stable USB identity. Topology survives re-enumeration; address alone does not.</summary>
public sealed record UsbTransportIdentity(ushort VendorId, ushort ProductId, string? SerialNumber = null,
    byte? BusNumber = null, string? PortPath = null, string? DevicePath = null)
{
    /// <summary>Matches one physical device without treating equal VID/PID as sufficient evidence.</summary>
    public bool IsSamePhysicalDevice(UsbTransportIdentity other) =>
        !string.IsNullOrEmpty(SerialNumber) && SerialNumber == other.SerialNumber && VendorId == other.VendorId ||
        BusNumber is not null && BusNumber == other.BusNumber && !string.IsNullOrEmpty(PortPath) && PortPath == other.PortPath ||
        !string.IsNullOrEmpty(DevicePath) && DevicePath == other.DevicePath;
}
/// <summary>Synchronous USB transport with selected interface and finite transfer timeouts.</summary>
public interface IUsbTransport : ITransport, IControlTransferTransport
{
    /// <summary>Sends one explicit USB bulk zero-length packet. Empty ordinary writes may be no-ops.</summary>
    void WriteZeroLengthPacket();
    UsbTransportIdentity Identity
    {
        get;
    }
    int InterfaceNumber
    {
        get;
    }
    int? ControlInterfaceNumber
    {
        get;
    }
}
