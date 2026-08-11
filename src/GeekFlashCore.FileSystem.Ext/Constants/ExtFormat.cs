using GeekFlashCore.FileSystem.Ext.Types;

namespace GeekFlashCore.FileSystem.Ext.Constants;

internal static class ExtFormat
{
    public const string FormatId = "ext";
    public const string ResourceKey = "FileSystems.Ext";
    public const ushort Magic = 0xEF53;
    public const int SuperblockOffset = 1024;
    public const int SuperblockSize = 1024;
    public const ushort ExtentMagic = 0xF30A;
    public const uint XattrMagic = 0xEA020000;

    public const uint SupportedIncompatibleFeatures =
        (uint)(ExtIncompatibleFeatures.DirectoryFileType |
               ExtIncompatibleFeatures.Extents |
               ExtIncompatibleFeatures.Bit64 |
               ExtIncompatibleFeatures.MultiMountProtection |
               ExtIncompatibleFeatures.FlexibleBlockGroups |
               ExtIncompatibleFeatures.ExtendedAttributeInode |
               ExtIncompatibleFeatures.ChecksumSeed |
               ExtIncompatibleFeatures.LargeDirectory |
               ExtIncompatibleFeatures.Encryption |
               ExtIncompatibleFeatures.Casefold);

    public const uint SupportedReadOnlyCompatibleFeatures =
        (uint)(ExtReadOnlyCompatibleFeatures.SparseSuper |
               ExtReadOnlyCompatibleFeatures.LargeFile |
               ExtReadOnlyCompatibleFeatures.BtreeDirectory |
               ExtReadOnlyCompatibleFeatures.HugeFile |
               ExtReadOnlyCompatibleFeatures.GroupDescriptorChecksum |
               ExtReadOnlyCompatibleFeatures.DirectoryLinkCount |
               ExtReadOnlyCompatibleFeatures.ExtraInodeSize |
               ExtReadOnlyCompatibleFeatures.Quota |
               ExtReadOnlyCompatibleFeatures.Replica |
               ExtReadOnlyCompatibleFeatures.ReadOnly |
               ExtReadOnlyCompatibleFeatures.Project |
               ExtReadOnlyCompatibleFeatures.SharedBlocks |
               ExtReadOnlyCompatibleFeatures.Verity);
}
