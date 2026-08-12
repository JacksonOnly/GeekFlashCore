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
    private bool _isMonitoring;

    public bool IsMonitoring => _isMonitoring;

    private const string InsertQuery =
        "SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.DeviceID LIKE '%USB%'";

    private const string RemoveQuery =
        "SELECT * FROM __InstanceDeletionEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.DeviceID LIKE '%USB%'";

    public void StartMonitoring()
    {
        if (_isMonitoring) return;


        _connection = new WmiConnection();

        _insertWatcher = _connection.CreateEventWatcher(InsertQuery);
        _removeWatcher = _connection.CreateEventWatcher(RemoveQuery);

        _insertWatcher.EventArrived += OnDeviceAdded;
        _removeWatcher.EventArrived += OnDeviceRemoved;

        _insertWatcher.Start();
        _removeWatcher.Start();

        _isMonitoring = true;
    }

    public void StopMonitoring()
    {
        if (!_isMonitoring) return;

        _insertWatcher?.Stop();
        _removeWatcher?.Stop();
        _insertWatcher?.Dispose();
        _removeWatcher?.Dispose();
        _insertWatcher = null;
        _removeWatcher = null;

        _connection?.Dispose();
        _connection = null;
        _isMonitoring = false;
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