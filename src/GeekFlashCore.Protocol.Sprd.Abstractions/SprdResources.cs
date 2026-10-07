using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Sprd.Abstractions.Localization;

namespace GeekFlashCore.Protocol.Sprd.Abstractions;

/// <summary>Borrowed, stable, reopenable loader bytes and an explicitly confirmed 32-bit RAM address.</summary>
public sealed record SprdLoader(IDataSource Source, uint Address);

/// <summary>Loader container. A synchronous connection borrows it; provider results transfer its ownership.</summary>
public sealed class SprdConnectionResources(SprdLoader? fdl1 = null, SprdLoader? fdl2 = null, bool ownsSources = false) : IDisposable
{
    private int _disposed;
    /// <summary>First loader; required only when entering through Boot ROM.</summary>
    public SprdLoader? Fdl1 { get; } = fdl1;
    /// <summary>Second loader; required unless FDL2 is already running.</summary>
    public SprdLoader? Fdl2 { get; } = fdl2;

    /// <summary>Checks required resources and RAM ranges before opening the transport.</summary>
    public void Validate(SprdProtocolOptions options)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(options);
        if (options.EntryStage == SprdBootStage.BootRom) ValidateLoader(Fdl1, options);
        if (options.EntryStage != SprdBootStage.Fdl2) ValidateLoader(Fdl2, options);
    }

    private static void ValidateLoader(SprdLoader? loader, SprdProtocolOptions options)
    {
        if (loader?.Source is null) throw new ArgumentException(Strings.LoaderRequired);
        long length = loader.Source.Length;
        if (length < 1 || length > options.MaximumLoaderBytes || options.PadOddPayloads && length % 2 != 0 ||
            (ulong)loader.Address + (ulong)length > 0x1_0000_0000UL)
            throw new ArgumentException(Strings.InvalidLoader);
    }

    /// <summary>Releases explicitly owned sources at most once; borrowed sources remain alive.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || !ownsSources) return;
        try { (Fdl1?.Source as IDisposable)?.Dispose(); }
        finally { if (!ReferenceEquals(Fdl1?.Source, Fdl2?.Source)) (Fdl2?.Source as IDisposable)?.Dispose(); }
    }
}

/// <summary>Asynchronous host resource boundary; return promptly and honor cancellation.</summary>
public interface ISprdLoaderProvider
{
    /// <summary>Returns an owned container; a late result after cancellation or timeout is also disposed.</summary>
    ValueTask<SprdConnectionResources> GetLoadersAsync(SprdBootStage entryStage, CancellationToken cancellationToken);
}
