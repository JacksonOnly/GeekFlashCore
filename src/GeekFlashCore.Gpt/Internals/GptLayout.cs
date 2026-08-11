namespace GeekFlashCore.Gpt.Internals;


internal sealed record GptHeaderLayout(
    GptHeaderCopy Copy,
    long HeaderOffset,
    long EntriesOffset,
    int CapacityBytes,
    int AvailableEntryCount,
    GptHeader Header,
    byte[] HeaderBytes,
    byte[] EntryStorage,
    GptCopyStatus Status);

internal sealed record GptLayout(
    GptImageType ImageType,
    GptContainerType ContainerType,
    int SectorSize,
    long SourceLength,
    byte[]? SourceImage,
    string? SourcePath,
    DateTime? SourceLastWriteTimeUtc,
    byte[]? ProtectiveMbr,
    GptHeaderLayout? MainHeader,
    GptHeaderLayout? BackupHeader)
{
    private static readonly GptCopyStatus MissingCopy =
        new(false, false, null);

    public GptHeaderLayout ActiveHeader =>
        SelectUsable(MainHeader, BackupHeader) ??
        MainHeader ?? BackupHeader ??
        throw new GptException(Strings.LayoutHasNoHeader);

    public GptRedundancyStatus RedundancyStatus
    {
        get
        {
            bool? headersConsistent = MainHeader is not null && BackupHeader is not null
                ? GptFormatValidator.HeadersDescribeSameTable(
                    MainHeader.Header,
                    BackupHeader.Header)
                : null;
            bool? entriesConsistent = null;
            if (MainHeader is not null && BackupHeader is not null &&
                MainHeader.Status.PartitionEntryArrayValid is not null &&
                BackupHeader.Status.PartitionEntryArrayValid is not null)
            {
                int primaryLength =
                    GptFormatValidator.GetEntryArrayLength(MainHeader.Header);
                int backupLength =
                    GptFormatValidator.GetEntryArrayLength(BackupHeader.Header);
                entriesConsistent = primaryLength == backupLength &&
                    MainHeader.EntryStorage.AsSpan(0, primaryLength)
                        .SequenceEqual(BackupHeader.EntryStorage.AsSpan(0, backupLength));
            }
            return new GptRedundancyStatus(
                MainHeader?.Status ?? MissingCopy,
                BackupHeader?.Status ?? MissingCopy,
                ActiveHeader.Copy,
                headersConsistent,
                entriesConsistent);
        }
    }

    private static GptHeaderLayout? SelectUsable(
        GptHeaderLayout? primary,
        GptHeaderLayout? backup)
    {
        if (primary?.Status.IsUsable == true) return primary;
        if (backup?.Status.IsUsable == true) return backup;
        if (primary?.Status.HeaderCrcValid == true) return primary;
        if (backup?.Status.HeaderCrcValid == true) return backup;
        return null;
    }
}
