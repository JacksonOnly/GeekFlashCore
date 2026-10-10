namespace GeekFlashCore.Protocol.Mtk.Abstractions;

/// <summary>Canonical names of DA-managed auxiliary partitions, distinct from ordinary GPT entries.</summary>
public static class MtkPartitionNames
{
    /// <summary>Primary GPT auxiliary mapping.</summary>
    public const string PrimaryGpt = "pgpt";
    /// <summary>Backup GPT auxiliary mapping.</summary>
    public const string BackupGpt = "sgpt";
    /// <summary>Primary DA-managed Preloader.</summary>
    public const string Preloader = "preloader";
    /// <summary>Backup DA-managed Preloader.</summary>
    public const string PreloaderBackup = "preloader_backup";

    /// <summary>Normalizes auxiliary names and accepts historical display names as input aliases.</summary>
    public static string Display(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Equals(PrimaryGpt, StringComparison.OrdinalIgnoreCase) || name.Equals("PrimaryGPT", StringComparison.OrdinalIgnoreCase))
            return PrimaryGpt;
        if (name.Equals(BackupGpt, StringComparison.OrdinalIgnoreCase) || name.Equals("BackupGPT", StringComparison.OrdinalIgnoreCase))
            return BackupGpt;
        if (name.Equals(Preloader, StringComparison.OrdinalIgnoreCase))
            return Preloader;
        if (name.Equals(PreloaderBackup, StringComparison.OrdinalIgnoreCase) || name.Equals("PreloaderBackup", StringComparison.OrdinalIgnoreCase) || name.Equals("Preloader Backup", StringComparison.OrdinalIgnoreCase))
            return PreloaderBackup;
        return name;
    }

    /// <summary>Preserves the DA's uppercase GPT wire names and lowercase Preloader wire names.</summary>
    public static string Wire(string name) => Display(name) switch
    {
        PrimaryGpt => "PGPT",
        BackupGpt => "SGPT",
        var normalized => normalized
    };

    /// <summary>Compares partition names with case-insensitive auxiliary alias normalization.</summary>
    public static bool Matches(string left, string? right) =>
        right is not null && Display(left).Equals(Display(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a name denotes a DA-managed Preloader.</summary>
    public static bool IsPreloader(string name) => Display(name) is Preloader or PreloaderBackup;
    /// <summary>Whether a name denotes an auxiliary GPT mapping.</summary>
    public static bool IsGpt(string name) => Display(name) is PrimaryGpt or BackupGpt;
    /// <summary>Whether a name denotes a synthetic auxiliary mapping rather than an ordinary GPT entry.</summary>
    public static bool IsMapped(string name) => IsPreloader(name) || IsGpt(name);
}
