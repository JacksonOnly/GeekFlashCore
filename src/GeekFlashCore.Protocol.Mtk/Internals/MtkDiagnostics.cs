using Serilog;

namespace GeekFlashCore.Protocol.Mtk.Internals;

// Fact events only: no exception messages, arbitrary parameters, raw payloads or device text.
internal static class MtkDiagnostics
{
    public static void Summary(ILogger logger, string template, params object?[] values) =>
        logger.ForContext("MtkSummary", true).Information(template, values);

    public static string Phase(MtkSessionState state) => state switch
    {
        MtkSessionState.Opening => Strings.PhaseOpening,
        MtkSessionState.Handshaking => Strings.PhaseHandshaking,
        MtkSessionState.Probed => Strings.PhaseProbed,
        MtkSessionState.Authenticating => Strings.PhaseAuthenticating,
        MtkSessionState.UploadingDa1 => Strings.PhaseUploadingDa1,
        MtkSessionState.Da1Ready => Strings.PhaseDa1Ready,
        MtkSessionState.UploadingDa2 => Strings.PhaseUploadingDa2,
        MtkSessionState.Da2Ready => Strings.PhaseDa2Ready,
        MtkSessionState.StorageReady => Strings.PhaseStorageReady,
        MtkSessionState.InitializingEmi => Strings.PhaseInitializingEmi,
        MtkSessionState.Extending => Strings.PhaseExtending,
        _ => Strings.PhaseDisconnected
    };
}

internal enum MtkTransferKind { Read, Write, Erase, NamedRead, NamedWrite, NamedErase }

// Completion is explicit and only emitted after the final ACK/lifetime and cancellation check.
internal sealed class MtkTransferLog(ILogger logger, MtkDaKind dialect, MtkTransferKind kind, uint? region, long offset, long length, string? partition = null)
{
    private readonly long _started = Environment.TickCount64;
    private readonly string _operation = kind switch
    {
        MtkTransferKind.Read => Strings.TransferRead,
        MtkTransferKind.Write => Strings.TransferWrite,
        MtkTransferKind.Erase => Strings.TransferErase,
        MtkTransferKind.NamedRead => Strings.TransferNamedRead,
        MtkTransferKind.NamedWrite => Strings.TransferNamedWrite,
        _ => Strings.TransferNamedErase
    };
    public void Start()
    {
        if (kind == MtkTransferKind.NamedErase)
            MtkDiagnostics.Summary(logger, Strings.NamedEraseStarted, dialect, partition);
        else if (partition is not null)
            MtkDiagnostics.Summary(logger, Strings.NamedTransferStarted, _operation, dialect, partition, length);
        else
            MtkDiagnostics.Summary(logger, Strings.TransferStarted, _operation, dialect, region, offset, length);
    }
    public void Complete(long? transferred = null)
    {
        long elapsed = Environment.TickCount64 - _started;
        if (kind == MtkTransferKind.NamedErase)
            MtkDiagnostics.Summary(logger, Strings.NamedEraseCompleted, dialect, partition, elapsed);
        else if (partition is not null)
            MtkDiagnostics.Summary(logger, Strings.NamedTransferCompleted, _operation, dialect, partition, transferred ?? length, elapsed);
        else
            MtkDiagnostics.Summary(logger, Strings.TransferCompleted, _operation, dialect, transferred ?? length, elapsed);
    }
}
