using System.Security.Cryptography;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Vendors.Oplus;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom;

public sealed partial class QcomProtocol
{
    private async ValueTask PrepareOplusAsync(CancellationToken ct)
    {
        if (_options.OplusDigest.Mode == OplusDigestMode.None || _oplusAuthenticated) return;
        string? builtIn = GetBuiltInOplusSign();
        bool tableRequired = true;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            OplusDigestResourceResponse resource = await _resourceResolver.ResolveAsync(token =>
                RequireOplusProvider().ResolveAsync(OplusRequest(builtIn, attempt), token), ct).ConfigureAwait(false);
            if (PrepareAndVerifyOplus(resource, attempt == 0 ? builtIn : null, tableRequired, ct,
                    out tableRequired)) return;
            if (attempt == 0) Log.Warning(Strings.Qcom_LogOplusSignRejected);
        }
        throw new QcomAuthenticationException(Strings.Qcom_OplusSignRejected);
    }

    private void PrepareOplus()
    {
        if (_options.OplusDigest.Mode == OplusDigestMode.None || _oplusAuthenticated) return;
        string? builtIn = GetBuiltInOplusSign();
        bool tableRequired = true;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            OplusDigestResourceResponse resource = _resourceResolver.Resolve(token =>
                RequireOplusProvider().ResolveAsync(OplusRequest(builtIn, attempt), token));
            if (PrepareAndVerifyOplus(resource, attempt == 0 ? builtIn : null, tableRequired, _lifetime.Token,
                    out tableRequired)) return;
            if (attempt == 0) Log.Warning(Strings.Qcom_LogOplusSignRejected);
        }
        throw new QcomAuthenticationException(Strings.Qcom_OplusSignRejected);
    }

    private IOplusDigestProvider RequireOplusProvider() =>
        _digestProvider ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);

    private string? GetBuiltInOplusSign() => _options.OplusDigest.Mode == OplusDigestMode.OplusDigestPt
        ? OplusSignatures.Find(TargetInfo!, _startup?.Logs.Select(static log => log.Message)) : null;

    private OplusDigestResourceRequest OplusRequest(string? builtIn, int attempt) =>
        new(TargetInfo!, _options.OplusDigest.Mode)
        {
            RequireSign = attempt > 0 || builtIn is null,
            PreviousSignRejected = attempt > 0
        };

    private bool PrepareAndVerifyOplus(OplusDigestResourceResponse resource, string? builtIn,
        bool sendTable, CancellationToken ct, out bool needsTable)
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
            else if (builtIn is not null)
            {
                if (!Convert.TryFromBase64String(builtIn, sign, out int length) || length == 0)
                    throw new QcomResourceException(Strings.Qcom_OplusSignInvalid);
            }
            else throw new QcomResourceException(Strings.Qcom_OplusSignRequired);

            if (_oplusDigest is null)
            {
                IDataSource digest = resource.Digest ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
                if (digest.Length is <= 0 or > OplusDigestParser.MaximumDigestLength)
                    throw new QcomResourceException(Strings.Qcom_OplusDigestLengthInvalid);
                // Validate the map before sending any command. The same resource/strategy
                // is retained across Configure/storage geometry fallbacks.
                if (_options.OplusDigest.Mode == OplusDigestMode.OplusDigestPt)
                    _oplusIndex = new OplusDigestParser().Parse(digest);
                _oplusDigest = digest;
            }
            if (sendTable)
            {
                Log.Information(Strings.Qcom_LogOplusDigestBootstrap);
                using Stream stream = _oplusDigest.OpenStream() ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
                _firehose!.SendDigest(stream, _oplusDigest.Length, 8192, ct);
                _firehose.ResetLegacyPacketCount();
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
            if (!result.IsSuccess || !result.Logs.Any(static log =>
                    log.Message.Contains("verify passed", StringComparison.OrdinalIgnoreCase)))
            {
                // Loaders can announce the next signed-table receive after the NAK.
                // Drain that bounded XML tail before prompting; never Flush it away.
                result = _firehose!.ReadOplusRejectionDetails(result,
                    _options.OplusDigest.DigestResponseTimeoutMilliseconds, ct);
                needsTable = result.Logs.Any(static log =>
                    log.Message.Contains("VIP is enabled", StringComparison.OrdinalIgnoreCase));
                return false;
            }
            needsTable = false;
            _firehose!.ExecuteXml("<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><sha256init Verbose=\"1\"/></data>", cancellationToken: ct);
            Log.Information(Strings.Qcom_LogOplusVerified);
            _oplusAuthenticated = true;
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(sign); }
    }
}
