using System.Security.Cryptography;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Vendors.Oplus;
using Serilog;
using QcomImageUtils.Constants;
using QcomImageUtils.Types;

namespace GeekFlashCore.Protocol.Qcom;

public sealed partial class QcomProtocol
{
    private async ValueTask PrepareOplusAsync(CancellationToken ct)
    {
        if (!ShouldPrepareOplus()) return;
        bool tableRequired = true;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            OplusDigestResourceResponse resource = await _resourceResolver.ResolveAsync(token =>
                RequireOplusProvider().ResolveAsync(OplusRequest(attempt), token), ct).ConfigureAwait(false);
            ActivateOplusMode(resource);
            if (PrepareAndVerifyOplus(resource, tableRequired,
                    attempt == 0 && _oplusConfiguration.ResumeAwaitingDigest, ct,
                    out tableRequired)) return;
            if (attempt == 0) Log.Warning(Strings.Qcom_LogOplusSignRejected);
        }
        throw new QcomAuthenticationException(Strings.Qcom_OplusSignRejected);
    }

    private void PrepareOplus()
    {
        if (!ShouldPrepareOplus()) return;
        bool tableRequired = true;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            OplusDigestResourceResponse resource = _resourceResolver.Resolve(token =>
                RequireOplusProvider().ResolveAsync(OplusRequest(attempt), token));
            ActivateOplusMode(resource);
            if (PrepareAndVerifyOplus(resource, tableRequired,
                    attempt == 0 && _oplusConfiguration.ResumeAwaitingDigest, _lifetime.Token,
                    out tableRequired)) return;
            if (attempt == 0) Log.Warning(Strings.Qcom_LogOplusSignRejected);
        }
        throw new QcomAuthenticationException(Strings.Qcom_OplusSignRejected);
    }

    private IOplusDigestProvider RequireOplusProvider() =>
        _digestProvider ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);

    private bool ShouldPrepareOplus()
    {
        if (_oplusAuthenticated) return false;
        if (_oplusConfiguration.Mode != OplusDigestMode.None) return true;
        return _options.AllowOplusModeSelection && _digestProvider is not null &&
            !_options.FirehoseDigest.Enabled && !_options.FirehoseVip.Enabled &&
            _programmer is { IsParsed: true, IsProgrammer: true, Vendor: QcomVendorKind.Oplus or QcomVendorKind.OnePlus } &&
            QualcommMapping.GetOemType(_targetInfo?.Sahara?.MsmHwInfo?.OemId) is
                QualcommOemType.OppoOneplusRealme or QualcommOemType.Oxygen &&
            _startup?.Logs.Any(static log => log.Message.Contains("VIP is enabled", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private void ActivateOplusMode(OplusDigestResourceResponse resource)
    {
        if (_oplusConfiguration.Mode != OplusDigestMode.None)
        {
            if (resource.SelectedMode is { } selected && selected != _oplusConfiguration.Mode)
                throw new QcomResourceException(Strings.Qcom_InvalidResource);
            return;
        }
        OplusDigestMode mode;
        if (resource.SelectedMode is { } selectedMode)
            mode = selectedMode;
        else
        {
            // Startup has already established both Oplus identities and VIP. Only
            // an unusable partition map falls back; resource I/O failures propagate.
            IDataSource digest = resource.Digest ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
            mode = new OplusDigestParser().TryParse(digest, out _oplusIndex)
                ? OplusDigestMode.OplusDigestPt : OplusDigestMode.OplusDigestLegacy;
        }
        if (mode is not (OplusDigestMode.OplusDigestPt or OplusDigestMode.OplusDigestLegacy))
            throw new QcomResourceException(Strings.Qcom_InvalidResource);
        _oplusConfiguration = _oplusConfiguration with { Mode = mode };
        if (resource.SelectedMode is null)
            Log.ForContext("UserPresentation", true).Information(Strings.Qcom_LogOplusModeDetected, mode);
        if (_oplusConfiguration.Mode == OplusDigestMode.OplusDigestLegacy)
        {
            _firehose!.ConfigureLegacyWire(_oplusConfiguration);
            _firehose.SetXmlDeclarationAttribute("chimerais=\"power\"");
        }
    }

    private OplusDigestResourceRequest OplusRequest(int attempt) =>
        new(TargetInfo!, _oplusConfiguration.Mode)
        {
            // OplusSignatures.Find(TargetInfo!, _startup?.Logs.Select(static log => log.Message));
            RequireSign = true,
            PreviousSignRejected = attempt > 0
        };

    private bool PrepareAndVerifyOplus(OplusDigestResourceResponse resource,
        bool sendTable, bool allowResumeRecovery, CancellationToken ct, out bool needsTable)
    {
        ct.ThrowIfCancellationRequested();
        byte[] sign = new byte[4096];
        try
        {
            if (resource.Sign is { } source)
            {
                long signLength = source.Length;
                if (signLength is <= 0 or > 4096)
                    throw new QcomResourceException(Strings.Qcom_OplusSignInvalid);
                using Stream stream = source.OpenStream() ?? throw new QcomResourceException(Strings.Qcom_OplusSignInvalid);
                if (!stream.CanRead) throw new QcomResourceException(Strings.Qcom_OplusSignInvalid);
                stream.ReadExactly(sign.AsSpan(0, checked((int)signLength)));
            }
            else throw new QcomResourceException(Strings.Qcom_OplusSignRequired);

            if (_oplusDigest is null)
            {
                IDataSource digest = resource.Digest ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
                if (digest.Length is <= 0 or > OplusDigestParser.MaximumDigestLength)
                    throw new QcomResourceException(Strings.Qcom_OplusDigestLengthInvalid);
                // Validate the map before sending any command. The same resource/strategy
                // is retained across Configure/storage geometry fallbacks.
                if (_oplusConfiguration.Mode == OplusDigestMode.OplusDigestPt && _oplusIndex is null)
                    _oplusIndex = new OplusDigestParser().Parse(digest);
                _oplusDigest = digest;
            }
            if (sendTable)
            {
                Log.Information(Strings.Qcom_LogOplusDigestBootstrap);
                long length = _oplusDigest.Length;
                for (int tableAttempt = 0; tableAttempt < 2; tableAttempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_oplusDigest.Length != length)
                        throw new QcomResourceException(Strings.Qcom_OplusLegacySourceChanged);
                    using Stream stream = _oplusDigest.OpenStream() ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
                    if (_firehose!.TrySendOplusInitialDigest(stream, length, 8192,
                            allowResumeRecovery: allowResumeRecovery && tableAttempt == 0,
                            _oplusConfiguration.DigestResponseTimeoutMilliseconds, ct))
                        break;
                }
                _firehose!.ResetLegacyPacketCount();
            }

            Log.Information(Strings.Qcom_LogOplusVerify);
            FirehoseCommandResult result;
            try
            {
                _firehose!.BeginOplusVerify(ct);
                result = _firehose.SendOplusSign(sign, ct);
            }
            catch (FirehoseNakException exception) when (!exception.Result.RawMode)
            {
                result = exception.Result;
            }
            if (!IsOplusSignVerified(result))
            {
                // Loaders can announce the next signed-table receive after the NAK.
                // Drain that bounded XML tail before prompting; never Flush it away.
                result = _firehose!.ReadOplusRejectionDetails(result,
                    _oplusConfiguration.DigestResponseTimeoutMilliseconds, ct);
                if (!IsOplusSignVerified(result))
                {
                    needsTable = result.Logs.Any(static log =>
                        log.Message.Contains("VIP is enabled", StringComparison.OrdinalIgnoreCase));
                    return false;
                }
            }
            needsTable = false;
            _firehose!.InitializeOplusSha256(ct);
            Log.Information(Strings.Qcom_LogOplusVerified);
            _oplusAuthenticated = true;
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(sign); }
    }

    private static bool IsOplusSignVerified(FirehoseCommandResult result) => result.IsSuccess &&
        result.Logs.Any(static log => log.Message.Contains("verify passed", StringComparison.OrdinalIgnoreCase));
}
