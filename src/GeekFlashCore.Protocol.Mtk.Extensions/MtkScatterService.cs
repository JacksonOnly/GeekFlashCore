// SPDX-License-Identifier: AGPL-3.0-or-later
// Scatter backup/restore and GPT ordering based on penumbra-main normal flashing workflows.
using System.Buffers;
using System.Security.Cryptography;
using GeekFlashCore.Android.Sparse;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Preflighted scatter flashing with streamed backups, protected-data preservation,
/// optional GPT rebuild, sparse expansion and readback. No DA patching or payload boot.</summary>
public sealed class MtkScatterService
{
    private readonly IMtkSessionAccess _access;
    public MtkScatterService(IMtkProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        _access = protocol as IMtkSessionAccess ?? throw new MtkCapabilityException("scoped DA channel");
    }

    public MtkScatterPlan Plan(MtkScatterManifest manifest, CancellationToken cancellationToken = default) => _access.UseSession(c => MtkScatterPlanner.Create(manifest, c.Storage, c.Generation), cancellationToken);
    /// <summary>Compares USER names and byte geometry before selecting a GPT update. Performs no writes.</summary>
    public IReadOnlyList<string> CompareLayout(MtkScatterPlan plan, CancellationToken cancellationToken = default) => _access.UseSession(c =>
    {
        if (plan.Generation != c.Generation) throw new InvalidOperationException(Localization.Strings.ExtensionUnavailable);
        var existing = (c as IMtkDaPartitionChannel ?? throw new MtkCapabilityException("partition snapshot")).GetPartitionRanges()
            .Where(p => p.Range.RegionId == c.Storage.UserRegionId && !MtkScatterGptConverter.IsMetadata(p.Name)).ToArray();
        var desired = plan.Partitions.Where(p => p.Range.RegionId == c.Storage.UserRegionId && !MtkScatterGptConverter.IsMetadata(p.Name)).ToArray();
        var names = existing.Select(p => p.Name).Concat(desired.Select(p => p.Name)).Distinct(StringComparer.OrdinalIgnoreCase);
        return (IReadOnlyList<string>)names.Where(name =>
        {
            var a = existing.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var b = desired.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            return a.Length != 1 || b.Length != 1 || a[0].Range != b[0].Range;
        }).ToArray();
    }, cancellationToken);

    /// <summary>Persists the current primary/backup GPT ranges before host approval. Never overwrites backup files.</summary>
    public void BackupPartitionTable(MtkScatterPlan plan, IMtkScatterBackupStore backups, CancellationToken cancellationToken = default) => _access.UseSession(c =>
    {
        if (plan.Generation != c.Generation) throw new InvalidOperationException(Localization.Strings.ExtensionUnavailable);
        var copies = (c as IMtkDaPartitionChannel ?? throw new MtkCapabilityException("partition snapshot")).GetPartitionRanges()
            .Where(p => p.Range.RegionId == c.Storage.UserRegionId && MtkScatterGptConverter.IsMetadata(p.Name)).ToArray();
        if (copies.Length != 2) throw new MtkResourceException("current GPT backup ranges");
        foreach (var copy in copies) Backup(c, copy.Range, $"current-{copy.Name}.bin", backups);
        return 0;
    }, cancellationToken);
    /// <summary>All selected images are opened and validated before backups or writes. Backup files are never overwritten.
    /// GPT updates write the backup copy first; raw metadata and images are read back before success.
    /// DA-managed BOOTLOADERS use native header handling and final status; their transformed bytes are not host-compared.</summary>
    public void Apply(MtkScatterPlan plan, Func<string, IDataSource> images, IMtkScatterBackupStore backups, bool rebuildGpt = false, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(backups);
        _access.UseSession(c =>
        {
            if (plan.Generation != c.Generation)
                throw new InvalidOperationException(Localization.Strings.ExtensionUnavailable);
            if (plan.Partitions is null || plan.Partitions.Count is < 1 or > 4096)
                throw new MtkResourceException("scatter plan count");
            var parts = plan.Partitions.ToArray();
            Validate(parts, c.Storage);
            List<Image> opened = [];
            List<(MtkFlashRange Range, string Backup)> restores = [];
            List<(MtkFlashRange Range, byte[] Data, string Name)> metadata = [];
            bool writing = false;
            long total = 0, done = 0;
            try
            {
                foreach (var part in parts.Where(p => p.Download && p.FileName != null))
                {
                    var image = Open(images(part.FileName!), part, c.Storage, cancellationToken);
                    opened.Add(image);
                    total = checked(total + image.ExpandedLength);
                }

                if (opened.Count == 0 && !rebuildGpt)
                    throw new MtkResourceException("scatter no downloadable image");
                if (c.Kind is MtkDaKind.XFlash or MtkDaKind.Xml && opened.Any(p => p.Part.Operation == MtkScatterOperation.Bootloaders) && c is not IMtkDaPartitionChannel)
                    throw new MtkCapabilityException("native bootloader channel");
                if (rebuildGpt)
                {
                    if (opened.Any(i => i.Part.Range.RegionId == c.Storage.UserRegionId && (i.Part.Name.Equals("pgpt", StringComparison.OrdinalIgnoreCase) || i.Part.Name.Equals("sgpt", StringComparison.OrdinalIgnoreCase))))
                        throw new MtkResourceException("scatter GPT image conflicts with rebuild");
                    BuildGpt(parts, c.Storage, metadata);
                }

                progress?.Report(new(total, 0, Localization.Strings.ScatterProgress) { Unit = ProgressUnit.Bytes, Phase = ProgressPhase.Started });
                // Back up every range that will be overwritten, and any protected partition moved by the new layout.
                foreach (var image in opened)
                    Backup(c, image.Part.Range, $"original-{image.Part.Range.RegionId}-{image.Part.Name}.bin", backups);
                foreach (var item in metadata)
                    Backup(c, item.Range, $"original-{item.Name}.bin", backups);
                var protectedParts = parts.Where(p => p.Operation is MtkScatterOperation.Protected or MtkScatterOperation.BinRegion).ToArray();
                if (rebuildGpt && protectedParts.Length > 0)
                {
                    var existing = (c as IMtkDaPartitionChannel ?? throw new MtkCapabilityException("partition snapshot")).GetPartitionRanges();
                    foreach (var part in protectedParts)
                    {
                        var matches = existing.Where(p => p.Range.RegionId == part.Range.RegionId && p.Name.Equals(part.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (matches.Length != 1 || matches[0].Range.Length != part.Range.Length)
                            throw new MtkResourceException("scatter protected geometry");
                        string name = $"protected-{part.Range.RegionId}-{part.Name}.bin";
                        Backup(c, matches[0].Range, name, backups);
                        restores.Add((part.Range, name));
                    }
                }

                // Reopen persisted backups before the first mutation; truncated or non-reopenable stores fail now.
                foreach (var restore in restores)
                {
                    var data = backups.OpenRead(restore.Backup);
                    if (data.Length != restore.Range.Length)
                        throw new MtkResourceException("scatter backup length");
                    using Stream input = data.OpenStream();
                    if (!input.CanRead)
                        throw new MtkResourceException("scatter backup source");
                }

                foreach (var item in metadata)
                {
                    using var source = new MemoryStream(item.Data, false);
                    writing = true;
                    WriteVerified(c, item.Range, source, cancellationToken);
                }

                foreach (var restore in restores)
                {
                    using Stream source = backups.OpenRead(restore.Backup).OpenStream();
                    writing = true;
                    WriteVerified(c, restore.Range, source, cancellationToken);
                }

                HashSet<string> nativeWritten = new(StringComparer.Ordinal);
                foreach (var image in opened)
                {
                    if (image.Part.Operation == MtkScatterOperation.Bootloaders && c.Kind is MtkDaKind.XFlash or MtkDaKind.Xml)
                    {
                        if (nativeWritten.Add(image.Part.Name))
                        {
                            writing = true;
                            image.WriteNative((IMtkDaPartitionChannel)c);
                        }
                    }
                    else
                        foreach (var(range, source)in image.Windows())
                        {
                            using (source)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                writing = true;
                                WriteVerified(c, range, source, cancellationToken);
                            }
                        }

                    done = checked(done + image.ExpandedLength);
                    progress?.Report(new(total, done, Localization.Strings.ScatterProgress) { Unit = ProgressUnit.Bytes, Phase = ProgressPhase.Running });
                }

                // Generation access checks the final wire deadline even when the caller did not provide progress.
                _ = c.Generation;
                progress?.Report(new(total, total, Localization.Strings.ScatterProgress) { Unit = ProgressUnit.Bytes, Phase = ProgressPhase.Completed });
                return 0;
            }
            catch (Exception ex)when (writing)
            {
                try
                {
                    c.Invalidate();
                }
                catch
                {
                }

                throw new MtkScatterWriteException(ex);
            }
            finally
            {
                foreach (var image in opened)
                    image.Dispose();
                foreach (var item in metadata)
                    CryptographicOperations.ZeroMemory(item.Data);
            }
        }, cancellationToken);
    }

    private static void Validate(IReadOnlyList<MtkScatterPlannedPartition> parts, MtkStorageInfo storage)
    {
        if (parts.Count is < 1 or > 4096)
            throw new MtkResourceException("scatter plan count");
        foreach (var p in parts)
        {
            if (p is null || p.Name is null)
                throw new MtkResourceException("scatter plan entry");
            if (p.Download && p.FileName is null)
                throw new MtkResourceException("scatter selected image");
            if (p.Download && p.Operation == MtkScatterOperation.Logic)
                throw new MtkCapabilityException("logical scatter download");
            var region = storage.Regions.SingleOrDefault(r => r.WireId == p.Range.RegionId) ?? throw new MtkResourceException("scatter region");
            if (!region.CanWrite || p.Range.Offset < 0 || p.Range.Length <= 0 || p.Range.Offset > region.Length - p.Range.Length || p.Range.Offset % region.BlockSize != 0 || p.Range.Length % region.BlockSize != 0 || p.Name.Length is < 1 or > 128 || p.Name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-' or '.')) || !Enum.IsDefined(p.Operation))
                throw new MtkResourceException("scatter plan range");
        }

        foreach (var group in parts.GroupBy(p => p.Range.RegionId))
        {
            var sorted = group.OrderBy(p => p.Range.Offset).ToArray();
            for (int i = 1; i < sorted.Length; i++)
                if (sorted[i - 1].Range.Offset + sorted[i - 1].Range.Length > sorted[i].Range.Offset)
                    throw new MtkResourceException("scatter plan overlap");
            if (sorted.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sorted.Length)
                throw new MtkResourceException("scatter plan duplicate");
        }
    }

    private static void Backup(IMtkDaChannel c, MtkFlashRange range, string name, IMtkScatterBackupStore backups)
    {
        using Stream output = backups.Create(name);
        if (!output.CanWrite)
            throw new MtkResourceException("scatter backup stream");
        c.ReadFlash(range, output);
        if (output is FileStream file)
            file.Flush(true);
        else
            output.Flush();
    }

    private static void BuildGpt(IReadOnlyList<MtkScatterPlannedPartition> parts, MtkStorageInfo storage, List<(MtkFlashRange, byte[], string)> metadata)
    {
        var region = storage.Regions.Single(r => r.WireId == storage.UserRegionId);
        if (region.Kind is not (MtkStorageKind.Emmc or MtkStorageKind.Ufs or MtkStorageKind.Sdmmc))
            throw new MtkCapabilityException("scatter GPT storage");
        var images = MtkScatterGptConverter.Build(parts, storage);
        byte[] primary = images.Primary, backup = images.Backup;
        metadata.Add((new(region.WireId, region.Length - backup.Length, backup.Length), backup, "sgpt"));
        metadata.Add((new(region.WireId, 0, primary.Length), primary, "pgpt"));
    }

    private static Image Open(IDataSource data, MtkScatterPlannedPartition part, MtkStorageInfo storage, CancellationToken token)
    {
        if (data is null || data.Length <= 0)
            throw new MtkResourceException("scatter source length");
        Stream stream = data.OpenStream();
        Image? image = null;
        try
        {
            if (!stream.CanRead || !stream.CanSeek || stream.Position != 0 || stream.Length != data.Length)
                throw new MtkResourceException("scatter source must be reopenable/seekable");
            var region = storage.Regions.Single(r => r.WireId == part.Range.RegionId);
            image = new Image(part, stream, region.BlockSize);
            image.Initialize(token);
            if (image.ExpandedLength > part.Range.Length)
            {
                image.Dispose();
                throw new MtkResourceException("scatter image capacity");
            }

            return image;
        }
        catch
        {
            if (image is null)
                stream.Dispose();
            else
                image.Dispose();
            throw;
        }
    }

    private static void WriteVerified(IMtkDaChannel c, MtkFlashRange range, Stream input, CancellationToken token)
    {
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var source = new HashStream(input, expected, token))
            c.WriteFlash(range, source);
        byte[] a = expected.GetHashAndReset();
        using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using (var output = new HashStream(null, actual, token))
                c.ReadFlash(range, output);
            byte[] b = actual.GetHashAndReset();
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(a, b))
                    throw new MtkResourceException("scatter readback");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(b);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(a);
        }
    }

    private sealed class Image(MtkScatterPlannedPartition part, Stream stream, int block) : IDisposable
    {
        private StreamBlockDevice? _device;
        private SparseDocument? _sparse;
        public MtkScatterPlannedPartition Part => part;
        public long ExpandedLength { get; private set; }

        public void Initialize(CancellationToken token)
        {
            if (SparseImageParser.IsSparse(stream))
            {
                _device = new(stream, stream.Length, DeviceOwnership.Borrow);
                _sparse = SparseImageParser.Open(_device, DeviceOwnership.Borrow);
                if (_sparse.Header.BlockSize % block != 0)
                    throw new MtkResourceException("scatter sparse alignment");
                _sparse.VerifyChecksum(cancellationToken: token);
                ExpandedLength = _sparse.ExpandedLength;
            }
            else
                ExpandedLength = checked((stream.Length + block - 1) / block * block);
            if (ExpandedLength <= 0)
                throw new MtkResourceException("scatter image empty");
        }

        public IEnumerable<(MtkFlashRange, Stream)> Windows()
        {
            if (_sparse is null)
            {
                stream.Position = 0;
                yield return (new(part.Range.RegionId, part.Range.Offset, ExpandedLength), new PaddedStream(stream, stream.Length, ExpandedLength));
            }
            else
                foreach (var region in _sparse.CreateDataRegions())
                    yield return (new(part.Range.RegionId, checked(part.Range.Offset + region.StartBlock * (long)_sparse.Header.BlockSize), region.Length), region.OpenRead(stream, true));
        }

        public void WriteNative(IMtkDaPartitionChannel channel)
        {
            stream.Position = 0;
            channel.WriteNamedPartition(part.Name, stream, stream.Length);
        }

        public void Dispose()
        {
            _sparse?.Dispose();
            _device?.Dispose();
            stream.Dispose();
        }
    }

    private sealed class PaddedStream(Stream source, long length, long padded) : Stream
    {
        private long _position;
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, padded - _position);
            if (count <= 0)
                return 0;
            int raw = (int)Math.Min(count, Math.Max(0, length - _position));
            if (raw > 0)
                source.ReadExactly(buffer[..raw]);
            buffer.Slice(raw, count - raw).Clear();
            _position += count;
            return count;
        }

        public override int Read(byte[] b, int o, int n) => Read(b.AsSpan(o, n));
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => padded;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }

    private sealed class HashStream(Stream? source, IncrementalHash hash, CancellationToken token) : Stream
    {
        public override int Read(Span<byte> data)
        {
            token.ThrowIfCancellationRequested();
            int n = source!.Read(data);
            hash.AppendData(data[..n]);
            return n;
        }

        public override void Write(ReadOnlySpan<byte> data)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(data);
        }

        public override int Read(byte[] b, int o, int n) => Read(b.AsSpan(o, n));
        public override void Write(byte[] b, int o, int n) => Write(b.AsSpan(o, n));
        public override bool CanRead => source != null;
        public override bool CanWrite => source == null;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
    }
}
