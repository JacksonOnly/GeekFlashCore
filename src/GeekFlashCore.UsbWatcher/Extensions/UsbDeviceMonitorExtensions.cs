using GeekFlashCore.UsbWatcher.Abstractions;

namespace GeekFlashCore.UsbWatcher.Extensions;

public static class UsbDeviceMonitorExtensions
{
    public static string? ExtractPortName(this UsbDeviceInfo device)
    {
        var name = device.FriendlyName;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        int start = name.IndexOf("(COM", StringComparison.Ordinal);
        if (start < 0)
            return null;

        int end = name.IndexOf(')', start + 4);
        return end < 0 ? null : name[(start + 1)..end];
    }
    public static async Task<UsbDeviceInfo?> WaitForDeviceAsync(
        this IUsbDeviceMonitor monitor,
        Func<UsbDeviceInfo, bool> predicate,
        CancellationToken cancellationToken = default,
        bool autoStart = true)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(predicate);

        var tcs = new TaskCompletionSource<UsbDeviceInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool startedByUs = false;
        if (autoStart && !monitor.IsMonitoring)
        {
            monitor.StartMonitoring();
            startedByUs = true;
        }
        else if (!monitor.IsMonitoring)
        {
            throw new InvalidOperationException(nameof(monitor.IsMonitoring));
        }

        EventHandler<UsbDeviceEventArgs> handler = (sender, e) =>
        {
            if (predicate(e.Device))
            {
                tcs.TrySetResult(e.Device);
            }
        };

        try
        {
            monitor.DeviceAdded += handler;

            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            monitor.DeviceAdded -= handler;
            if (startedByUs)
            {
                monitor.StopMonitoring();
            }
        }
    }
}