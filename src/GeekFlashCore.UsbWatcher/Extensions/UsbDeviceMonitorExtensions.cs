using GeekFlashCore.UsbWatcher.Abstractions;

namespace GeekFlashCore.UsbWatcher.Extensions;

public static class UsbDeviceMonitorExtensions
{
    public static string? ExtractPortName(this UsbDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

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
        => await WaitForDeviceAsync(monitor, predicate, cancellationToken, autoStart, null).ConfigureAwait(false);

    /// <summary>Subscribes before monitoring starts and rechecks inventory to close the initial scan/hot-plug gap.</summary>
    public static async Task<UsbDeviceInfo?> WaitForDeviceAsync(
        this IUsbDeviceMonitor monitor,
        Func<UsbDeviceInfo, bool> predicate,
        CancellationToken cancellationToken,
        bool autoStart,
        Func<IEnumerable<UsbDeviceInfo>>? enumerateDevices)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(predicate);
        cancellationToken.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<UsbDeviceInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool startedByUs = false;
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
                cancellationToken.ThrowIfCancellationRequested();
                if (autoStart && !monitor.IsMonitoring)
                {
                    startedByUs = true;
                    monitor.StartMonitoring();
                }
                else if (!monitor.IsMonitoring)
                    throw new InvalidOperationException(nameof(monitor.IsMonitoring));

                if (!tcs.Task.IsCompleted && enumerateDevices is not null)
                {
                    foreach (var device in enumerateDevices())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (predicate(device)) { tcs.TrySetResult(device); break; }
                    }
                }
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
