using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Internals;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom;

public sealed partial class QcomProtocol
{
    private const int PblReopenDelayMilliseconds = 1000;

    private void PreparePblPatch(CancellationToken cancellationToken)
    {
        if (!RunPblPatch(cancellationToken)) return;
        if (cancellationToken.WaitHandle.WaitOne(PblReopenDelayMilliseconds))
            cancellationToken.ThrowIfCancellationRequested();
        ReopenAfterPblPatch(cancellationToken);
    }

    private async ValueTask PreparePblPatchAsync(CancellationToken cancellationToken)
    {
        if (!RunPblPatch(cancellationToken)) return;
        await Task.Delay(PblReopenDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        ReopenAfterPblPatch(cancellationToken);
    }

    /// <returns>True only when the fixed 710/845 sequence requires a transport reopen.</returns>
    private bool RunPblPatch(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.EnablePblPatch) return false;
        PblPatchKind kind = PblPatch.ResolveKind(_targetInfo?.Sahara);
        if (kind == PblPatchKind.None) return false;
        Log.ForContext("UserPresentation", true).Information(Strings.Qcom_LogPblStart, (int)kind);
        // Once patching starts, ordinary Sahara reset/retry cannot restore a known
        // state. Detach it so failure cleanup closes without issuing another packet.
        using SaharaProtocol sahara = _sahara ?? throw new InvalidOperationException(Strings.Qcom_InvalidSessionState);
        _sahara = null;
        sahara.BeginPblImageTransfer();
        byte[]? firstRequest = new PblPatch(_wire!, _options.ReadTimeoutMilliseconds)
            .Run(kind, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (firstRequest is not null)
        {
            ((QcomSessionTransport)_wire!).Prepend(firstRequest);
            ResumeSaharaAfterPblPatch();
            return false;
        }
        _wire = null;
        Transport.Close();
        return true;
    }

    private void ReopenAfterPblPatch(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Transport.Open();
        cancellationToken.ThrowIfCancellationRequested();
        Transport.Flush();
        _wire = new QcomSessionTransport(Transport, [], _options.ReadTimeoutMilliseconds);
        new PblPatch(_wire, _options.ReadTimeoutMilliseconds).SendLoaderHello(cancellationToken);
        ResumeSaharaAfterPblPatch();
    }

    private void ResumeSaharaAfterPblPatch()
    {
        _sahara = new SaharaProtocol(_wire!);
        _sahara.ResumePblImageTransfer(_targetInfo!.Sahara!);
        _targetInfo = _targetInfo with { Sahara = _sahara.TargetInfo with { } };
        Log.Information(Strings.Qcom_LogPblLoaderTransfer);
    }
}
