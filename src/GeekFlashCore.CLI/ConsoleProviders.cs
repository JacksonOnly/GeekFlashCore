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
    public ValueTask<SaharaImageEntryResponse> ResolveAsync(SaharaImageEntryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = ConsolePath.Normalize(configuredPath ?? ui.AskOptional(Strings.Cli_SaharaProgrammerPrompt));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException(Strings.Cli_SaharaProgrammerMissing, path);
        var source = new FileDataSource(path);
        return ValueTask.FromResult(new SaharaImageEntryResponse([new SaharaImageEntry(13, source.Length, source)]));
    }
}

internal sealed class ConsoleOplusDigestProvider(ConsoleUi ui, string? configuredPath) : IOplusDigestProvider
{
    public ValueTask<OplusDigestResourceResponse> ResolveAsync(OplusDigestResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = ConsolePath.Normalize(configuredPath ??
            ui.AskOptional(Strings.FormatCli_OplusDigestPrompt(request.Mode)));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException(Strings.Cli_OplusDigestMissing, path);
        return ValueTask.FromResult(new OplusDigestResourceResponse(new FileDataSource(path)));
    }
}

internal sealed class ConsoleFirehoseDigestProvider(ConsoleUi ui, string? configuredPath) : IFirehoseDigestProvider
{
    public ValueTask<FirehoseDigestResourceResponse> ResolveAsync(FirehoseDigestResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = ConsolePath.Normalize(configuredPath ?? ui.AskOptional(Strings.Cli_FirehoseDigestPrompt));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException(Strings.Cli_FirehoseDigestMissing, path);
        return ValueTask.FromResult(new FirehoseDigestResourceResponse(new FileDataSource(path)));
    }
}

internal sealed class ConsoleVipProvider(ConsoleUi ui, string? signedPath, string? chainedPath) : IFirehoseVipProvider
{
    public ValueTask<FirehoseVipResourceResponse> ResolveAsync(FirehoseVipResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? signed = ConsolePath.Normalize(signedPath ?? ui.AskOptional(Strings.Cli_VipSignedPrompt));
        if (string.IsNullOrWhiteSpace(signed) || !File.Exists(signed))
            throw new FileNotFoundException(Strings.Cli_VipSignedMissing, signed);
        var chained = new List<IDataSource>();
        string? path = ConsolePath.Normalize(chainedPath ?? ui.AskOptional(Strings.Cli_VipChainedPrompt));
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path)) throw new FileNotFoundException(Strings.Cli_VipChainedMissing, path);
            chained.Add(new FileDataSource(path));
        }
        return ValueTask.FromResult(new FirehoseVipResourceResponse(new FileDataSource(signed), chained));
    }
}

internal sealed class ConsoleAuthenticationProvider(ConsoleUi ui) : IVendorAuthenticationProvider
{
    public ValueTask<VendorAuthenticationResourceResponse> ResolveAsync(VendorAuthenticationResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ui.WriteLine(Strings.FormatCli_AuthenticationRequired(request.Kind));
        string value = ui.Ask(Strings.Cli_AuthenticationPayloadPrompt, secret: true);
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
            return ValueTask.FromResult(response);
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
