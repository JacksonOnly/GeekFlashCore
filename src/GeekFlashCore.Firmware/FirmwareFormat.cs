namespace GeekFlashCore.Firmware;

/// <summary>Supported firmware containers. Auto performs bounded signature detection.</summary>
public enum FirmwareFormat
{
    Auto, Zip, Ozip, OfpQualcomm, OfpMediaTek, Ops, Pac, Kdz, Dz, UpdateApp, AndroidPayload, Directory
}
