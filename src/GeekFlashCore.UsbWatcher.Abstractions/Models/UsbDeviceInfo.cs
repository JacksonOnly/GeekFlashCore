using System.Text;

namespace GeekFlashCore.UsbWatcher.Abstractions;

public record UsbDeviceInfo
{
    public string? FriendlyName { get; set; }
    public string? ClassGuid { get; set; }
    public string? HardwareId { get; set; }
    public int? VendorId { get; set; }
    public int? ProductId { get; set; }
    public string? Description { get; set; }
    public string? Manufacturer { get; set; }
}