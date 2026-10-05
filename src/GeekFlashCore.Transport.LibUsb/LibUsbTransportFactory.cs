using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.Transport.LibUsb.Internals;
using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace GeekFlashCore.Transport.LibUsb;

public static class LibUsbTransportFactory
{
    /// <summary>Creates a unique identified USB connection; ambiguous VID/PID matches are rejected.</summary>
    public static IUsbTransport Create(LibUsbConnectionOptions options) => new LibUsbTransport(options);

    /// <summary>Enumerates backend topology identities without choosing a device.</summary>
    public static IReadOnlyList<UsbTransportIdentity> Enumerate(ushort? vendorId = null, ushort? productId = null,string? serialNumber=null)
    {
        using var context = new UsbContext();
        var devices = context.FindAll(new UsbDeviceFinder { Vid = vendorId ?? int.MaxValue, Pid = productId ?? int.MaxValue,SerialNumber=serialNumber! }).ToList();
        try { return devices.Select(d => LibUsbTransport.GetIdentity(d,serialNumber)).ToArray(); }
        finally { foreach (var device in devices) device.Dispose(); }
    }

    /// <summary>Waits for one device on the same physical topology and an allowed new PID.</summary>
    public static async ValueTask<IUsbTransport> WaitForReenumerationAsync(LibUsbConnectionOptions previous,
        IReadOnlySet<ushort> allowedProductIds, int timeoutMilliseconds, CancellationToken cancellationToken = default)
    {
        previous.Validate();
        ArgumentNullException.ThrowIfNull(allowedProductIds);
        if(allowedProductIds.Count==0||previous.Identity.SerialNumber is null&&
            (previous.Identity.BusNumber is null||string.IsNullOrEmpty(previous.Identity.PortPath)))
            throw new ArgumentException(Strings.LibUsbTransport_AmbiguousDevice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeoutMilliseconds);
        try
        {
            while (true)
            {
                budget.Token.ThrowIfCancellationRequested();
                var matches = Enumerate(previous.Identity.VendorId,serialNumber:previous.Identity.SerialNumber).Where(id => allowedProductIds.Contains(id.ProductId) &&
                    previous.Identity.IsSamePhysicalDevice(id)).ToArray();
                if (matches.Length > 1) throw new InvalidOperationException(Strings.LibUsbTransport_AmbiguousDevice);
                if (matches.Length == 1) return Create(previous with { Identity = matches[0] });
                await Task.Delay(100, budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException(Strings.FormatLibUsbTransport_TransferTimedOut("USB re-enumeration", timeoutMilliseconds)); }
    }
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
