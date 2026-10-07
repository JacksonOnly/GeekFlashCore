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

internal sealed class ConsoleVendorSelectionProvider(ConsoleUi ui) : IVendorSelectionProvider
{
    public async ValueTask<VendorSelectionResponse> ResolveAsync(VendorSelectionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ui.CanPrompt) throw new QcomResourceException(request.RequiresOplusModeSelection
            ? Strings.Cli_OplusModeSelectionRequired : Strings.Cli_VendorSelectionRequired);
        ui.PrintTargetInfo(request.TargetInfo);
        if (request.RequiresOplusModeSelection) ui.WriteLine(Strings.Cli_SignedTableWaiting);
        QcomVendorKind vendor = request.RequiresVendorSelection
            ? await SelectVendorAsync(request.RequiresOplusModeSelection, cancellationToken).ConfigureAwait(false)
            : request.TargetInfo.Vendor;
        OplusDigestMode? mode = request.RequiresOplusModeSelection
            ? await SelectOplusModeAsync(cancellationToken).ConfigureAwait(false) : null;
        return new VendorSelectionResponse(vendor) { OplusMode = mode };
    }

    private async ValueTask<QcomVendorKind> SelectVendorAsync(bool requiresOplusMode, CancellationToken cancellationToken)
    {
        QcomVendorKind[] choices = Enum.GetValues<QcomVendorKind>().Where(x => x != QcomVendorKind.Auto).ToArray();
        ui.WriteLine(Strings.Cli_VendorUnknown);
        for (int i = 0; i < choices.Length; i++) ui.WriteLine($"  {i + 1}. {choices[i]}");
        while (true)
        {
            string value = (await ui.AskAsync(Strings.Cli_VendorPrompt, cancellationToken).ConfigureAwait(false)).Trim();
            if (value.Length == 0) throw new OperationCanceledException(Strings.Cli_OperationCancelled);
            QcomVendorKind vendor;
            if (int.TryParse(value, out int number) && number > 0 && number <= choices.Length)
                vendor = choices[number - 1];
            else if (!Enum.TryParse(value, true, out vendor) || !choices.Contains(vendor))
            { ui.WriteLine(Strings.Cli_VendorInvalid); continue; }
            if (requiresOplusMode && vendor is not (QcomVendorKind.Oplus or QcomVendorKind.OnePlus))
            { ui.WriteLine(Strings.Cli_SignedTableVendorInvalid); continue; }
            return vendor;
        }
    }

    private async ValueTask<OplusDigestMode> SelectOplusModeAsync(CancellationToken cancellationToken)
    {
        ui.WriteLine(Strings.Cli_OplusModeChoices);
        while (true)
        {
            string value = (await ui.AskAsync(Strings.Cli_OplusModePrompt, cancellationToken).ConfigureAwait(false)).Trim();
            if (value.Length == 0) throw new OperationCanceledException(Strings.Cli_OperationCancelled);
            if (value.Equals("1", StringComparison.Ordinal) || value.Equals("Pt", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("DigestPt", StringComparison.OrdinalIgnoreCase) || value.Equals("OplusDigestPt", StringComparison.OrdinalIgnoreCase))
                return OplusDigestMode.OplusDigestPt;
            if (value.Equals("2", StringComparison.Ordinal) || value.Equals("Legacy", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("OplusDigestLegacy", StringComparison.OrdinalIgnoreCase))
                return OplusDigestMode.OplusDigestLegacy;
            ui.WriteLine(Strings.Cli_OplusModeInvalid);
        }
    }
}

internal sealed class ConsoleSaharaImageProvider(ConsoleUi ui, string? configuredPath) : ISaharaImageProvider
{
    public async ValueTask<SaharaImageEntryResponse> ResolveAsync(SaharaImageEntryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ui.PrintTargetInfo(new QcomTargetInfo { Sahara = request.TargetInfo });
        string path = (await ui.SelectFileAsync(Strings.Cli_SaharaProgrammerPrompt, configuredPath,
            Strings.Cli_SaharaProgrammerMissing, cancellationToken).ConfigureAwait(false))!;
        ui.WriteLine(Strings.Cli_LoadingProgrammer);
        var source = new FileDataSource(path);
        return new SaharaImageEntryResponse([new SaharaImageEntry(13, source.Length, source)]);
    }
}

internal sealed class ConsoleOplusDigestProvider(ConsoleUi ui, string? configuredPath, string? configuredSign = null) : IOplusDigestProvider
{
    private string? _digestPath = configuredPath;
    private string? _signPath = configuredSign;
    private bool _explained;
    public async ValueTask<OplusDigestResourceResponse> ResolveAsync(OplusDigestResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OplusDigestMode mode = request.Mode;
        if (!_explained)
        {
            ui.WriteLine(mode switch
            {
                OplusDigestMode.None => Strings.Cli_OplusAutoResources,
                OplusDigestMode.OplusDigestLegacy => Strings.Cli_OplusLegacyResources,
                _ => Strings.Cli_OplusPtResources
            });
            _explained = true;
        }
        string path = (await ui.SelectFileAsync(mode == OplusDigestMode.None
            ? Strings.Cli_OplusAutoDigestPrompt : Strings.FormatCli_OplusDigestPrompt(mode), _digestPath,
            Strings.Cli_OplusDigestMissing, cancellationToken).ConfigureAwait(false))!;
        _digestPath = path;
        if (request.PreviousSignRejected) ui.WriteLine(Strings.Cli_OplusSignRejected);
        string? signPath = request.PreviousSignRejected ? null : ConsolePath.Normalize(_signPath);
        signPath = await ui.SelectFileAsync(Strings.Cli_OplusSignPrompt, signPath,
            Strings.Cli_OplusSignInvalid, cancellationToken, IsValidSignFile).ConfigureAwait(false);
        _signPath = signPath;
        ui.WriteLine(Strings.Cli_OplusVerifying);
        return new OplusDigestResourceResponse(new FileDataSource(path))
        { Sign = new FileDataSource(signPath!) };
    }

    private static bool IsValidSignFile(string path) => File.Exists(path) && new FileInfo(path).Length is > 0 and <= 4096;
}

internal sealed class ConsoleFirehoseDigestProvider(ConsoleUi ui, string? configuredPath) : IFirehoseDigestProvider
{
    public async ValueTask<FirehoseDigestResourceResponse> ResolveAsync(FirehoseDigestResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = (await ui.SelectFileAsync(Strings.Cli_FirehoseDigestPrompt, configuredPath,
            Strings.Cli_FirehoseDigestMissing, cancellationToken).ConfigureAwait(false))!;
        return new FirehoseDigestResourceResponse(new FileDataSource(path));
    }
}

internal sealed class ConsoleVipProvider(ConsoleUi ui, string? signedPath, string? chainedPath) : IFirehoseVipProvider
{
    public async ValueTask<FirehoseVipResourceResponse> ResolveAsync(FirehoseVipResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string signed = (await ui.SelectFileAsync(Strings.Cli_VipSignedPrompt, signedPath,
            Strings.Cli_VipSignedMissing, cancellationToken).ConfigureAwait(false))!;
        var chained = new List<IDataSource>();
        string? path = await ui.SelectFileAsync(Strings.Cli_VipChainedPrompt, chainedPath,
            Strings.Cli_VipChainedMissing, cancellationToken, optional: true).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(path))
        {
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
