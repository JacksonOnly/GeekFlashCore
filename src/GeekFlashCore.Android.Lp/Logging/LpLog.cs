using GeekFlashCore.Android.Lp.Localization;
using Microsoft.Extensions.Logging;

namespace GeekFlashCore.Android.Lp;

internal static class LpLog
{
    internal static void CommitStarted(ILogger logger, int slotNumber) =>
        Write(logger, LogLevel.Information, slotNumber, static (current, slotNumber) =>
            current.LogInformation(
                LpEventIds.CommitStarted,
                LogMessages.CommitStarted,
                slotNumber));

    internal static void DataPhaseCompleted(ILogger logger, long byteCount) =>
        Write(logger, LogLevel.Debug, byteCount, static (current, byteCount) =>
            current.LogDebug(
                LpEventIds.DataPhaseCompleted,
                LogMessages.DataPhaseCompleted,
                byteCount));

    internal static void OldCopyRepaired(ILogger logger, object copyKind) =>
        Write(logger, LogLevel.Warning, copyKind, static (current, copyKind) =>
            current.LogWarning(
                LpEventIds.OldCopyRepaired,
                LogMessages.OldCopyRepaired,
                copyKind));

    internal static void MetadataCommitted(ILogger logger, int slotNumber) =>
        Write(logger, LogLevel.Information, slotNumber, static (current, slotNumber) =>
            current.LogInformation(
                LpEventIds.MetadataCommitted,
                LogMessages.MetadataCommitted,
                slotNumber));

    internal static void VerificationIncomplete(ILogger logger, object status) =>
        Write(logger, LogLevel.Warning, status, static (current, verificationStatus) =>
            current.LogWarning(
                LpEventIds.VerificationIncomplete,
                LogMessages.VerificationIncomplete,
                verificationStatus));

    internal static void CleanupIncomplete(ILogger logger, object status) =>
        Write(logger, LogLevel.Warning, status, static (current, cleanupStatus) =>
            current.LogWarning(
                LpEventIds.CleanupIncomplete,
                LogMessages.CleanupIncomplete,
                cleanupStatus));

    internal static void FileOpened(ILogger logger, int slotNumber) =>
        Write(logger, LogLevel.Debug, slotNumber, static (current, slotNumber) =>
            current.LogDebug(
                LpEventIds.FileOpened,
                LogMessages.FileOpened,
                slotNumber));

    internal static void RawExportCompleted(ILogger logger, string partitionName) =>
        Write(logger, LogLevel.Information, partitionName, static (current, partitionName) =>
            current.LogInformation(
                LpEventIds.RawExportCompleted,
                LogMessages.RawExportCompleted,
                partitionName));

    internal static void LeaseDisposeFailed(
        ILogger logger,
        uint blockDeviceIndex,
        Exception exception) =>
        Write(
            logger,
            LogLevel.Warning,
            blockDeviceIndex,
            (current, blockDeviceIndex) => current.LogWarning(
                LpEventIds.LeaseDisposeFailed,
                exception,
                LogMessages.LeaseDisposeFailed,
                blockDeviceIndex));

    private static void Write<T>(
        ILogger logger,
        LogLevel level,
        T state,
        Action<ILogger, T> write)
    {
        try
        {
            if (!logger.IsEnabled(level))
            {
                return;
            }

            write(logger, state);
        }
        catch (Exception)
        {
            // A logging provider must not change the outcome of an LP transaction.
        }
    }
}
