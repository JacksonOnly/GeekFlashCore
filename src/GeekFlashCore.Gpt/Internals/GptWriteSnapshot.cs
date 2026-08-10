namespace GeekFlashCore.Gpt.Internals;

internal sealed record GptWriteSnapshot(
    GptLayout Layout,
    int AvailableEntryCount,
    GptHeader Header,
    byte[] HeaderTemplate,
    byte[] EntryStorage);