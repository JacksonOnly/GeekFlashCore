using System.Security.Cryptography;
using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Generation-bound seccfg plans with backup, pre-write comparison, minimal aligned writes and readback.</summary>
public sealed class MtkSecurityConfigurationService : IMtkSecurityConfigurationService
{
    private readonly IMtkSessionAccess _access;
    private readonly IReadOnlyList<IMtkSecurityCipher> _ciphers;
    public MtkSecurityConfigurationService(IMtkProtocol protocol, IReadOnlyList<IMtkSecurityCipher>? ciphers = null)
    {
        _access = protocol as IMtkSessionAccess ?? throw new MtkCapabilityException("scoped session");
        _ciphers = ciphers?.ToArray() ?? [new MtkPlainSecurityCipher(), new MtkSoftwareSecurityCipher()];
        if (_ciphers.Count is 0 or > 8 || _ciphers.Any(c => c is null))
            throw new ArgumentOutOfRangeException(nameof(ciphers));
    }

    public MtkSecurityChangePlan Plan(MtkFlashRange range, bool locked, CancellationToken cancellationToken = default) => _access.UseSession(c =>
    {
        var region = c.Storage.Regions.SingleOrDefault(r => r.WireId == range.RegionId) ?? throw new MtkCapabilityException("seccfg region");
        if (range.Length < region.BlockSize || range.Offset < 0 || range.Offset % region.BlockSize != 0 || range.Offset > region.Length - range.Length)
            throw new ArgumentOutOfRangeException(nameof(range));
        byte[] first = new byte[region.BlockSize];
        byte[]? original = null;
        try
        {
            using (var output = new MemoryStream(first, true))
                c.ReadFlash(new(range.RegionId, range.Offset, first.Length), output);
            int declared = MtkSecurityCodec.DeclaredSize(first), size = checked((declared + region.BlockSize - 1) / region.BlockSize * region.BlockSize);
            if (size > range.Length || size > 65536)
                throw new MtkResourceException("seccfg range");
            original = new byte[size];
            first.CopyTo(original, 0);
            if (size > first.Length)
                using (var output = new MemoryStream(original, first.Length, size - first.Length, true))
                    c.ReadFlash(new(range.RegionId, range.Offset + first.Length, size - first.Length), output);
            byte[] replacement = MtkSecurityCodec.Change(original, locked, _ciphers, c, out string algorithm);
            return new MtkSecurityChangePlan(c.Generation, new(range.RegionId, range.Offset, size), locked, algorithm, original, replacement);
        }
        catch
        {
            if (original is not null)
                CryptographicOperations.ZeroMemory(original);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(first);
        }
    }, cancellationToken);
    public void Apply(MtkSecurityChangePlan plan, Stream backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(backup);
        if (!backup.CanWrite)
            throw new ArgumentException(nameof(backup));
        _access.UseSession(c =>
        {
            if (plan.Generation != c.Generation)
                throw new InvalidOperationException(Localization.Strings.ExtensionUnavailable);
            var original = plan.Original.Memory;
            var replacement = plan.Replacement.Memory;
            if (original.Length is <= 0 or > 65536 || original.Length != plan.Range.Length || replacement.Length != original.Length)
                throw new MtkResourceException("seccfg plan");
            byte[] current = new byte[original.Length];
            bool writing = false;
            try
            {
                using (var output = new MemoryStream(current, true))
                    c.ReadFlash(plan.Range, output);
                if (!CryptographicOperations.FixedTimeEquals(current, original.Span))
                    throw new MtkResourceException("seccfg changed since planning");
                // Publicly constructed plans are independently regenerated and checked.
                byte[] checkedPlan = MtkSecurityCodec.Change(current, plan.Locked, _ciphers, c, out string algorithm);
                try
                {
                    if (algorithm != plan.Algorithm || !CryptographicOperations.FixedTimeEquals(checkedPlan, replacement.Span))
                        throw new MtkResourceException("seccfg plan integrity");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(checkedPlan);
                }

                backup.Write(original.Span);
                if (backup is FileStream file)
                    file.Flush(true);
                else
                    backup.Flush();
                int block = c.Storage.Regions.Single(r => r.WireId == plan.Range.RegionId).BlockSize;
                int first = -1, last = 0;
                for (int i = 0; i < current.Length; i++)
                {
                    if (current[i] != replacement.Span[i])
                    {
                        if (first < 0)
                            first = i;
                        last = i + 1;
                    }
                }

                if (first < 0)
                    return 0;
                int start = first / block * block, end = checked((last + block - 1) / block * block);
                byte[] data = replacement.Slice(start, end - start).ToArray();
                try
                {
                    using var input = new MemoryStream(data, false);
                    writing = true;
                    c.WriteFlash(new(plan.Range.RegionId, plan.Range.Offset + start, end - start), input);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(data);
                }

                using (var output = new MemoryStream(current, true))
                    c.ReadFlash(plan.Range, output);
                if (!CryptographicOperations.FixedTimeEquals(current, replacement.Span))
                    throw new MtkResourceException("seccfg readback");
                return 0;
            }
            catch (Exception ex)when (writing)
            {
                c.Invalidate();
                throw new MtkSecurityWriteException(ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(current);
            }
        }, cancellationToken);
    }
}
