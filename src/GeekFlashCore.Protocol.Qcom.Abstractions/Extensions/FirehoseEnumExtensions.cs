namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public static class FirehoseEnumExtensions
{
    public static string ToWireString(this FirehoseStorage value) => value switch
    {
        FirehoseStorage.Ufs => "UFS",
        FirehoseStorage.Emmc => "eMMC",
        FirehoseStorage.Spinor => "spinor",
        FirehoseStorage.Nand => "NAND",
        FirehoseStorage.Nvme => "NVMe",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };

    public static string ToWireString(this FirehosePowerValue value) => value switch
    {
        FirehosePowerValue.ResetToEdl => "reset_to_edl",
        FirehosePowerValue.Reset => "reset",
        FirehosePowerValue.Off => "off",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };
}
