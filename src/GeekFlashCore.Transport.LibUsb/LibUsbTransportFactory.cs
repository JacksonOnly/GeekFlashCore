using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.Transport.LibUsb.Internals;
using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace GeekFlashCore.Transport.LibUsb;

public static class LibUsbTransportFactory
{
    private static ITransport CreateCore(int? vid = null, int? pid = null, Guid? classGuid = null,
        int claimedInterface = -1, int bufferSize = 8192, ReadEndpointID? readEndpointId = null,
        WriteEndpointID? writeEndpointId = null, int readTimeout = 1000, int writeTimeout = 1000)
    {
        var usbFinder = new UsbDeviceFinder()
        {
            Vid = vid ?? int.MaxValue,
            Pid = pid ?? int.MaxValue,
            DeviceInterfaceGuid = classGuid ?? Guid.Empty
        };
        return new LibUsbTransport(usbFinder, claimedInterface, bufferSize, readEndpointId, writeEndpointId,
            readTimeout, writeTimeout);
    }

    public static ITransport Create(int vid, int pid)
    {
        return Create(vid, pid, Guid.Empty);
    }

    public static ITransport Create(int vid, int pid, Guid classGuid)
    {
        return CreateCore(vid, pid, classGuid);
    }

    public static ITransport Create(int vid, int pid, Guid classGuid, int claimedInterface = -1, int bufferSize = 8192,
        int readTimeout = 1000, int writeTimeout = 1000)
    {
        return CreateCore(vid, pid, classGuid, claimedInterface, bufferSize, readTimeout: readTimeout,
            writeTimeout: writeTimeout);
    }
}