namespace GeekFlashCore.Protocol.Sprd.Internals;

internal static class SprdCommand
{
    internal const ushort ReadChipUid = 0x1a, ChipUid = 0xab;
    internal const ushort UnsupportedCommand = 0xfe;
    internal const ushort EnableRawData = 0x28, MidstRawStart = 0x31, MidstRawStart2 = 0x33;
    internal const ushort Connect = 0x00, Start = 0x01, Midst = 0x02, End = 0x03, Execute = 0x04,
        Reset = 0x05, Erase = 0x0a, ReadStart = 0x10, ReadMidst = 0x11, ReadEnd = 0x12,
        KeepCharge = 0x13, PowerOff = 0x17, DisableTranscode = 0x21, ReadPartition = 0x2d,
        CheckBaud = 0x7e, Ack = 0x80, Version = 0x81, ReadFlash = 0x93, LoaderInfo = 0x96,
        PartitionTable = 0xba, Log = 0xff;
}
