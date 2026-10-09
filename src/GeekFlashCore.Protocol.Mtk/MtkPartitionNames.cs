namespace GeekFlashCore.Protocol.Mtk;

/// <summary>Names shared by partition snapshots, range lookup and native DA commands.</summary>
internal static class MtkPartitionNames
{
    public const string PrimaryGpt = "PrimaryGPT";
    public const string BackupGpt = "BackupGPT";
    public const string Preloader = "Preloader";
    public const string PreloaderBackup = "Preloader Backup";

    public static string Display(string name)
    {
        if (name.Equals("PGPT", StringComparison.OrdinalIgnoreCase) || name.Equals(PrimaryGpt, StringComparison.OrdinalIgnoreCase))
            return PrimaryGpt;
        if (name.Equals("SGPT", StringComparison.OrdinalIgnoreCase) || name.Equals(BackupGpt, StringComparison.OrdinalIgnoreCase))
            return BackupGpt;
        if (name.Equals(Preloader, StringComparison.OrdinalIgnoreCase))
            return Preloader;
        if (name.Equals("preloader_backup", StringComparison.OrdinalIgnoreCase) || name.Equals("PreloaderBackup", StringComparison.OrdinalIgnoreCase) || name.Equals(PreloaderBackup, StringComparison.OrdinalIgnoreCase))
            return PreloaderBackup;
        return name;
    }

    public static string Wire(string name) => Display(name) switch
    {
        PrimaryGpt => "PGPT",
        BackupGpt => "SGPT",
        Preloader => "preloader",
        PreloaderBackup => "preloader_backup",
        _ => name
    };

    public static bool Matches(string left, string? right) =>
        right is not null && Display(left).Equals(Display(right), StringComparison.OrdinalIgnoreCase);

    public static bool IsPreloader(string name) => Display(name) is Preloader or PreloaderBackup;
}
