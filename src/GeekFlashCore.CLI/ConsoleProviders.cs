using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

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
        string? path = ConsolePath.Normalize(configuredPath ?? ui.AskOptional("Sahara programmer 路径"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("找不到 Sahara programmer 文件", path);
        var source = new FileDataSource(path);
        return ValueTask.FromResult(new SaharaImageEntryResponse([new SaharaImageEntry(13, source.Length, source)]));
    }
}

internal sealed class ConsoleOplusDigestProvider(ConsoleUi ui, string? configuredPath) : IOplusDigestProvider
{
    public ValueTask<OplusDigestResourceResponse> ResolveAsync(OplusDigestResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = ConsolePath.Normalize(configuredPath ?? ui.AskOptional($"Oplus Digest ({request.Mode}) 路径"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("找不到 Oplus Digest 文件", path);
        return ValueTask.FromResult(new OplusDigestResourceResponse(new FileDataSource(path)));
    }
}

internal sealed class ConsoleFirehoseDigestProvider(ConsoleUi ui, string? configuredPath) : IFirehoseDigestProvider
{
    public ValueTask<FirehoseDigestResourceResponse> ResolveAsync(FirehoseDigestResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? path = ConsolePath.Normalize(configuredPath ?? ui.AskOptional("Firehose Digest 路径"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("找不到 Firehose Digest 文件", path);
        return ValueTask.FromResult(new FirehoseDigestResourceResponse(new FileDataSource(path)));
    }
}

internal sealed class ConsoleVipProvider(ConsoleUi ui, string? signedPath, string? chainedPath) : IFirehoseVipProvider
{
    public ValueTask<FirehoseVipResourceResponse> ResolveAsync(FirehoseVipResourceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? signed = ConsolePath.Normalize(signedPath ?? ui.AskOptional("VIP signed table 路径"));
        if (string.IsNullOrWhiteSpace(signed) || !File.Exists(signed)) throw new FileNotFoundException("找不到 VIP signed table 文件", signed);
        var chained = new List<IDataSource>();
        string? path = ConsolePath.Normalize(chainedPath ?? ui.AskOptional("VIP chained table 路径（可留空）"));
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到 VIP chained table 文件", path);
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
        ui.WriteLine($"需要 {request.Kind} 认证材料。输入十六进制 payload，留空取消。");
        string value = ui.Ask("认证 payload", secret: true);
        if (string.IsNullOrWhiteSpace(value)) throw new OperationCanceledException("用户取消认证");
        try
        {
            byte[] bytes = Convert.FromHexString(value.Replace(" ", string.Empty, StringComparison.Ordinal));
            return ValueTask.FromResult(new VendorAuthenticationResourceResponse(SensitiveDataOwner.CopyFrom(bytes)));
        }
        catch (FormatException exception) { throw new FormatException("认证 payload 必须是十六进制", exception); }
    }
}
