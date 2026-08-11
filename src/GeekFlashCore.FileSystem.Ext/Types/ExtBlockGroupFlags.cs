namespace GeekFlashCore.FileSystem.Ext.Types;

[Flags]
public enum ExtBlockGroupFlags : ushort
{
    InodeBitmapUninitialized = 0x0001,
    BlockBitmapUninitialized = 0x0002,
    InodeTableZeroed = 0x0004
}
