using GeekFlashCore.UsbWatcher.Abstractions;
using WmiLight;

namespace GeekFlashCore.UsbWatcher.Internals;

internal sealed class WmiUsbDeviceMonitor : IUsbDeviceMonitor, IDisposable
{
    public event EventHandler<UsbDeviceEventArgs>? DeviceAdded;
    public event EventHandler<UsbDeviceEventArgs>? DeviceRemoved;

    private WmiConnection? _connection;
    private WmiEventWatcher? _insertWatcher;
    private WmiEventWatcher? _removeWatcher;
    private WmiEventWatcher? _reconnectWatcher;
    private WmiEventWatcher? _disconnectWatcher;
    private bool _isMonitoring;

    public bool IsMonitoring => _isMonitoring;

    private const string InsertQuery =
        "SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.DeviceID LIKE '%USB%' AND TargetInstance.Present = TRUE";

    private const string RemoveQuery =
        "SELECT * FROM __InstanceDeletionEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.DeviceID LIKE '%USB%'";

    // Reconnecting an installed device may update an existing instance instead of creating one.
    private const string ReconnectQuery =
        "SELECT * FROM __InstanceModificationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.DeviceID LIKE '%USB%' AND TargetInstance.Present = TRUE AND PreviousInstance.Present = FALSE";

    private const string DisconnectQuery =
        "SELECT * FROM __InstanceModificationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.DeviceID LIKE '%USB%' AND TargetInstance.Present = FALSE AND PreviousInstance.Present = TRUE";

    public void StartMonitoring()
    {
        if (_isMonitoring) return;


        _isMonitoring = true;
        try
        {
            _connection = new WmiConnection();
            _insertWatcher = _connection.CreateEventWatcher(InsertQuery);
            _removeWatcher = _connection.CreateEventWatcher(RemoveQuery);
            _reconnectWatcher = _connection.CreateEventWatcher(ReconnectQuery);
            _disconnectWatcher = _connection.CreateEventWatcher(DisconnectQuery);

            _insertWatcher.EventArrived += OnDeviceAdded;
            _removeWatcher.EventArrived += OnDeviceRemoved;
            _reconnectWatcher.EventArrived += OnDeviceAdded;
            _disconnectWatcher.EventArrived += OnDeviceRemoved;

            _insertWatcher.Start();
            _removeWatcher.Start();
            _reconnectWatcher.Start();
            _disconnectWatcher.Start();
        }
        catch
        {
            try { StopMonitoring(); }
            catch { /* Preserve the monitoring startup failure after attempting all cleanup. */ }
            throw;
        }
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring) return;

        var watchers = new[] { _insertWatcher, _removeWatcher, _reconnectWatcher, _disconnectWatcher };
        var connection = _connection;
        _insertWatcher = null;
        _removeWatcher = null;
        _reconnectWatcher = null;
        _disconnectWatcher = null;
        _connection = null;
        _isMonitoring = false;

        Exception? failure = null;
        foreach (var watcher in watchers)
        {
            try { watcher?.Stop(); }
            catch (Exception exception) { failure ??= exception; }
            try { watcher?.Dispose(); }
            catch (Exception exception) { failure ??= exception; }
        }
        try { connection?.Dispose(); }
        catch (Exception exception) { failure ??= exception; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void OnDeviceAdded(object? sender, WmiEventArrivedEventArgs wmiEventArrivedEventArgs)
    {
        var instance = wmiEventArrivedEventArgs.NewEvent["TargetInstance"] as WmiObject;
        if (instance is not null && Utils.CreateDeviceInfo(instance) is { } deviceInfo)
        {
            DeviceAdded?.Invoke(this, new UsbDeviceEventArgs(deviceInfo));
        }
    }

    private void OnDeviceRemoved(object? sender, WmiEventArrivedEventArgs wmiEventArrivedEventArgs)
    {
        var instance = wmiEventArrivedEventArgs.NewEvent["TargetInstance"] as WmiObject;
        if (instance is not null && Utils.CreateDeviceInfo(instance) is { } deviceInfo)
        {
            DeviceRemoved?.Invoke(this, new UsbDeviceEventArgs(deviceInfo));
        }
    }

    public void Dispose()
    {
        StopMonitoring();
        GC.SuppressFinalize(this);
    }
}
