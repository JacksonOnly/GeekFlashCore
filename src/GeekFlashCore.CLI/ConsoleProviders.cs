using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.CLI.Localization;
using System.Security.Cryptography;

namespace GeekFlashCore.CLI;

internal static class ConsolePath
{
    public static string? Normalize(string? value)
    {
        if (value is null) return null;
        string path = value.Trim();
        if (path.Length >= 2 && ((path[0] == '\"' && path[^1] == '\"') || (path[0] == '\'' && path[^1] == '\'')))
            path = path[1..^1].Trim();
        return path;
    }
}

internal sealed class ConsoleSaharaImageProvider(ConsoleUi ui, string? configuredPath) : ISaharaImageProvider
{
    public async ValueTask<SaharaImageEntryResponse> ResolveAsync(SaharaImageEntryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = ConsolePath.Normalize(configuredPath ?? await ui.AskOptionalAsync(Strings.Cli_SaharaProgrammerPrompt, cancellationToken).ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException(Strings.Cli_SaharaProgrammerMissing, path);
        var source = new FileDataSource(path);
        return new SaharaImageEntryResponse([new SaharaImageEntry(13, source.Length, source)]);
    }
}

internal sealed class ConsoleOplusDigestProvider(ConsoleUi ui, string? configuredPath, string? configuredSign = null) : IOplusDigestProvider
{
    private string? _digestPath = configuredPath;
    public async ValueTask<OplusDigestResourceResponse> ResolveAsync(OplusDigestResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = ConsolePath.Normalize(_digestPath ??
            await ui.AskOptionalAsync(Strings.FormatCli_OplusDigestPrompt(request.Mode), cancellationToken).ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException(Strings.Cli_OplusDigestMissing, path);
        _digestPath = path;
        if (request.PreviousSignRejected) ui.WriteLine(Strings.Cli_OplusSignRejected);
        string? signPath = request.PreviousSignRejected ? null : ConsolePath.Normalize(configuredSign);
        bool requireSign = request.RequireSign || request.Mode == OplusDigestMode.OplusDigestLegacy;
        if (signPath is not null && !IsValidSignFile(signPath))
        {
            ui.WriteLine(Strings.Cli_OplusSignInvalid);
            signPath = null;
            requireSign = true;
        }
        if (signPath is null && requireSign)
            signPath = ConsolePath.Normalize(await ui.AskOptionalAsync(Strings.Cli_OplusSignPrompt, cancellationToken).ConfigureAwait(false));
        if (signPath is not null && !IsValidSignFile(signPath))
            throw new FileNotFoundException(Strings.Cli_OplusSignInvalid, signPath);
        if (string.IsNullOrWhiteSpace(signPath) && requireSign)
            throw new FileNotFoundException(Strings.Cli_OplusSignMissing);
        return new OplusDigestResourceResponse(new FileDataSource(path))
        { Sign = signPath is null ? null : new FileDataSource(signPath) };
    }

    private static bool IsValidSignFile(string path) => File.Exists(path) && new FileInfo(path).Length is > 0 and <= 4096;
}

internal sealed class ConsoleFirehoseDigestProvider(ConsoleUi ui, string? configuredPath) : IFirehoseDigestProvider
{
    public async ValueTask<FirehoseDigestResourceResponse> ResolveAsync(FirehoseDigestResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = ConsolePath.Normalize(configuredPath ?? await ui.AskOptionalAsync(Strings.Cli_FirehoseDigestPrompt, cancellationToken).ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException(Strings.Cli_FirehoseDigestMissing, path);
        return new FirehoseDigestResourceResponse(new FileDataSource(path));
    }
}

internal sealed class ConsoleVipProvider(ConsoleUi ui, string? signedPath, string? chainedPath) : IFirehoseVipProvider
{
    public async ValueTask<FirehoseVipResourceResponse> ResolveAsync(FirehoseVipResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? signed = ConsolePath.Normalize(signedPath ?? await ui.AskOptionalAsync(Strings.Cli_VipSignedPrompt, cancellationToken).ConfigureAwait(false));
        if (string.IsNullOrWhiteSpace(signed) || !File.Exists(signed))
            throw new FileNotFoundException(Strings.Cli_VipSignedMissing, signed);
        var chained = new List<IDataSource>();
        string? path = ConsolePath.Normalize(chainedPath ?? await ui.AskOptionalAsync(Strings.Cli_VipChainedPrompt, cancellationToken).ConfigureAwait(false));
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path)) throw new FileNotFoundException(Strings.Cli_VipChainedMissing, path);
            chained.Add(new FileDataSource(path));
        }
        return new FirehoseVipResourceResponse(new FileDataSource(signed), chained);
    }
}

internal sealed class ConsoleAuthenticationProvider(ConsoleUi ui) : IVendorAuthenticationProvider
{
    public async ValueTask<VendorAuthenticationResourceResponse> ResolveAsync(VendorAuthenticationResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ui.WriteLine(Strings.FormatCli_AuthenticationRequired(request.Kind));
        string value = await ui.AskAsync(Strings.Cli_AuthenticationPayloadPrompt, cancellationToken, secret: true).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value))
            throw new OperationCanceledException(Strings.Cli_AuthenticationCancelled);
        byte[]? bytes = null;
        SensitiveDataOwner? owner = null;
        try
        {
            bytes = Convert.FromHexString(value.Replace(" ", string.Empty, StringComparison.Ordinal));
            owner = SensitiveDataOwner.TakeOwnership(bytes);
            bytes = null;
            var response = new VendorAuthenticationResourceResponse(owner);
            owner = null;
            return response;
        }
        catch (FormatException exception)
        {
            throw new FormatException(Strings.Cli_AuthenticationPayloadInvalid, exception);
        }
        finally
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
            owner?.Dispose();
        }
    }
}
