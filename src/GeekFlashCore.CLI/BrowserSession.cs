using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using GeekFlashCore.Android.Lp;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.FileSystem.Abstractions;
using GeekFlashCore.FileSystem.Erofs;
using GeekFlashCore.FileSystem.Ext;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal sealed class BrowserSession(int slot = 0, ILpBlockDeviceResolver? resolver = null, string? sourcePath = null,
    ILpWritableBlockDeviceResolver? writableResolver = null) : IDisposable
{
    private const int MaximumPrintBytes = 24 * 1024;
    private readonly Stack<IDisposable> _resources = new();
    private readonly Dictionary<string, BrowserMount> _mounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LpMetadataDocument> _lpDocuments = new(StringComparer.Ordinal);
    private readonly FileSystemReadLimits _limits = new(maximumCacheBytes: 1024 * 1024, maximumWorkingBytes: 4 * 1024 * 1024);
    private bool _disposed;
    private CancellationToken _operationToken;
    internal BrowserRoot Root { get; } = new();
    internal BrowserNode Current { get; private set; } = null!;

    internal void SetOperationToken(CancellationToken ct) => _operationToken = ct;

    internal void AddMount(string name, Func<IReadableBlockDevice> open)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Root.Mounts.Any(x => x.Name == name)) throw new ArgumentException(Strings.Cli_BrowserInvalidPath);
        Root.Mounts.Add(GetMount(name, Root, open));
        Current ??= Root;
    }

    private BrowserMount GetMount(string name, BrowserNode parent, Func<IReadableBlockDevice> open, long size = 0)
    {
        BrowserPath.ValidateNodeName(name);
        string path = parent.Path.TrimEnd('/') + "/" + name;
        if (_mounts.TryGetValue(path, out var existing)) return existing;
        if (_mounts.Count >= 128) throw new IOException(Strings.Cli_BrowserLimit);
        var mount = new BrowserMount(name, parent, node => Mount(node, open), size,
            () => new BlockDeviceStream(new BrowserReadDevice(open(), this), DeviceOwnership.Transfer), open);
        _mounts.Add(path, mount);
        return mount;
    }

    private T Own<T>(T resource) where T : IDisposable { _resources.Push(resource); return resource; }

    private BrowserNode Mount(BrowserNode node, Func<IReadableBlockDevice> open)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int baseline = _resources.Count;
        try
        {
            _operationToken.ThrowIfCancellationRequested();
            IReadableBlockDevice source = Own(new BrowserReadDevice(open(), this));
            ((BrowserMount)node).SetSize(source.Length);
            if (IsLp(source))
            {
                var set = Own(LpMetadataSet.Open(source, DeviceOwnership.Borrow));
                var document = Own(set.OpenPreferredSlot(slot));
                _lpDocuments[node.Path] = document;
                return new BrowserLpNode(node, document, this);
            }
            foreach (IFileSystemDriver driver in new IFileSystemDriver[] { new ErofsFileSystemDriver(), new ExtFileSystemDriver() })
            {
                if (driver.Probe(source, _limits).Status == FileSystemProbeStatus.NotRecognized) continue;
                var volume = Own(driver.Open(source, DeviceOwnership.Borrow, _limits));
                return new BrowserFileSystemNode(node.Name, node.Parent, volume, volume.Root);
            }
            return new BrowserRawNode(node, source);
        }
        catch (Exception exception)
        {
            if (ReleaseResources(baseline) is { } failures)
                throw new AggregateException(Strings.Cli_BrowserCleanupFailed, new[] { exception }.Concat(failures));
            throw;
        }
    }

    private static bool IsLp(IReadableBlockDevice source)
    {
        Span<byte> magic = stackalloc byte[4];
        foreach (int offset in new[] { LpFormat.ReservedBytes, LpFormat.ReservedBytes + LpFormat.GeometryBlockSize })
        {
            if (source.Length < offset + 4) continue;
            BlockDeviceIO.ReadExactlyAt(source, offset, magic);
            if (BinaryPrimitives.ReadUInt32LittleEndian(magic) == LpFormat.GeometryMagic) return true;
        }
        return false;
    }

    internal BrowserNode Resolve(string path, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        _operationToken = ct;
        string absolute = BrowserPath.Normalize(Current?.Path ?? "/", path);
        BrowserNode node = Root;
        foreach (string component in absolute.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            node = node.Children(ct).FirstOrDefault(x => x.Name == component) ??
                throw new FileNotFoundException(Strings.FormatCli_BrowserNotFound(absolute));
        }
        return node;
    }

    internal void ChangeDirectory(string path, CancellationToken ct)
    {
        var node = Resolve(path, ct);
        if (!node.IsDirectory) throw new IOException(Strings.Cli_BrowserNotDirectory);
        Current = node;
    }

    internal IEnumerable<BrowserNode> Find(string pattern, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains('/') || pattern.Contains('\0'))
            throw new ArgumentException(Strings.Cli_BrowserInvalidPath);
        var visited = new HashSet<object>();
        int count = 0;
        return Walk(Resolve(path, ct), 0);

        IEnumerable<BrowserNode> Walk(BrowserNode node, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 1_000_000 || depth > 128) throw new IOException(Strings.Cli_BrowserLimit);
            // Search filesystem files only; raw partitions and links are not search results.
            if (!node.IsDirectory)
            {
                if (node.Kind == "file" && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, node.Name, ignoreCase: false)) yield return node;
                yield break;
            }
            if (node.DirectoryIdentity is { } identity && !visited.Add(identity)) yield break;
            foreach (var child in node.Children(ct))
                foreach (var match in Walk(child, depth + 1)) yield return match;
        }
    }

    internal string ReadText(BrowserNode node, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        _operationToken = ct;
        if (node.IsDirectory || node.Kind != "file") throw new IOException(Strings.Cli_BrowserPrintNotFile);
        long size = node.Size;
        if (size is < 0 or > MaximumPrintBytes) throw new IOException(Strings.Cli_BrowserPrintTooLarge);
        int length = checked((int)size);
        using Stream input = node.OpenRead();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            int copied = 0;
            while (copied < length)
            {
                ct.ThrowIfCancellationRequested();
                // Keep each filesystem read and its complete Firehose RAW/ACK exchange synchronous.
                int read = input.Read(buffer.AsSpan(copied, length - copied));
                ct.ThrowIfCancellationRequested();
                if (read == 0) throw new EndOfStreamException(Strings.Cli_BrowserShortRead);
                copied = checked(copied + read);
            }
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = DecodeText(buffer.AsSpan(0, length)); }
            catch (DecoderFallbackException) { throw new IOException(Strings.Cli_BrowserPrintInvalidText); }
            // Files are untrusted terminal input. Keep text layout, but display other controls literally.
            if (!text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) return text;
            var safe = new StringBuilder(text.Length);
            foreach (char c in text)
                if (char.IsControl(c) && c is not ('\r' or '\n' or '\t')) safe.Append("\\u").Append(((int)c).ToString("X4"));
                else safe.Append(c);
            return safe.ToString();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        Encoding encoding;
        int prefix;
        if (bytes.StartsWith<byte>([0xFF, 0xFE, 0x00, 0x00])) { encoding = new UTF32Encoding(false, false, true); prefix = 4; }
        else if (bytes.StartsWith<byte>([0x00, 0x00, 0xFE, 0xFF])) { encoding = new UTF32Encoding(true, false, true); prefix = 4; }
        else if (bytes.StartsWith<byte>([0xFF, 0xFE])) { encoding = new UnicodeEncoding(false, false, true); prefix = 2; }
        else if (bytes.StartsWith<byte>([0xFE, 0xFF])) { encoding = new UnicodeEncoding(true, false, true); prefix = 2; }
        else { encoding = new UTF8Encoding(false, true); prefix = bytes.StartsWith<byte>([0xEF, 0xBB, 0xBF]) ? 3 : 0; }
        return encoding.GetString(bytes[prefix..]);
    }

    internal async Task ExportAsync(BrowserNode node, string destination, CancellationToken ct, IProgress<ProgressRecord>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        _operationToken = ct;
        if (node is not BrowserMount && node.IsDirectory) throw new IOException(Strings.Cli_BrowserNotFile);
        destination = Path.GetFullPath(ConsolePath.Normalize(destination)!);
        if (sourcePath is not null && string.Equals(destination, Path.GetFullPath(sourcePath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException(Strings.Cli_BrowserSourceOverwrite);
        RejectLinkedAncestors(destination);
        using Stream input = node.OpenRead();
        long total = node is BrowserMount ? input.Length : node.Size;
        await AtomicReadOutput.WriteAsync(destination, async output =>
        {
            Report(0, ProgressPhase.Started);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                long copied = 0;
                while (copied < total)
                {
                    ct.ThrowIfCancellationRequested();
                    int size = (int)Math.Min(buffer.Length, total - copied);
                    // Keep filesystem and transport reads synchronous; async is only for the local output.
                    int read = input.Read(buffer, 0, size);
                    if (read == 0) throw new EndOfStreamException(Strings.Cli_BrowserShortRead);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied = checked(copied + read);
                    if (copied < total) Report(copied, ProgressPhase.Running);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }, ct).ConfigureAwait(false);
        // Flush and replace the destination before publishing successful completion.
        Report(total, ProgressPhase.Completed);

        void Report(long current, ProgressPhase phase) => progress?.Report(new ProgressRecord(total, current, Strings.Cli_BrowserExportProgress)
            { Unit = ProgressUnit.Bytes, Phase = phase });
    }

    internal static void RejectLinkedAncestors(string destination)
    {
        for (string? path = Path.GetFullPath(destination); path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(Strings.Cli_BrowserInvalidPath);
        }
    }

    internal async Task WritePartitionAsync(string path, string input, ConsoleUi ui, CancellationToken ct)
    {
        if (sourcePath is null && writableResolver is null) throw new NotSupportedException(Strings.Cli_LpNotWritable);
        var node = Resolve(path, ct);
        if (node is not BrowserMount || node.Parent is not BrowserLpNode)
            throw new IOException(Strings.Cli_LpPartitionRequired);
        string container = node.Parent.Path, name = node.Name;
        input = Path.GetFullPath(ConsolePath.Normalize(input)!);
        if (sourcePath is not null && string.Equals(input, Path.GetFullPath(sourcePath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException(Strings.Cli_BrowserSourceOverwrite);
        using var image = LpPartitionImageSource.FromBlockDevice(new FileBlockDevice(input), DeviceOwnership.Transfer);
        await EditLpAsync(container, draft =>
        {
            var partition = draft.FindPartition(name);
            long capacity = draft.GetPartition(partition).LogicalSize;
            if (image.LogicalLength > capacity) throw new IOException(Strings.Cli_LpImageTooLarge);
            draft.ReplacePartitionImage(partition, image);
            draft.ResizePartition(partition, capacity);
        }, ui, ct, allowInPlace: true).ConfigureAwait(false);
    }

    internal async Task EditLpAsync(string path, Action<LpDraft> edit, ConsoleUi ui, CancellationToken ct,
        bool allowInPlace = false)
    {
        var mount = Resolve(path, ct) as BrowserMount;
        if (mount is null || mount.Parent != Root || !IsLpMount(mount))
            throw new IOException(Strings.Cli_LpContainerRequired);
        if (sourcePath is null && writableResolver is null) throw new NotSupportedException(Strings.Cli_LpNotWritable);
        string container = mount.Path;
        Reset();
        try
        {
            using IReadableBlockDevice source = sourcePath is not null ? new WritableFileBlockDevice(sourcePath) :
                new BrowserReadDevice(mount.OpenDevice(), this);
            using var editor = resolver is null ? new LpEditor().Open(source, DeviceOwnership.Borrow, slot) :
                await new LpEditor().OpenAsync(source, DeviceOwnership.Borrow, slot, resolver, cancellationToken: ct).ConfigureAwait(false);
            edit(editor.Draft);
            var result = editor.CreatePlan(new LpPlanOptions { AllowInPlaceDataOverwrite = allowInPlace }, ct);
            if (result is LpPlanFailure failure) throw new IOException(Strings.FormatCli_LpPlanFailed(failure.Failure.ErrorCode));
            var plan = ((LpPlanSuccess)result).Plan;
            ui.WriteLine(Strings.FormatCli_LpPlan(plan.SlotNumber, plan.DataWrites.Length, plan.UsesInPlaceDataOverwrite));
            var committed = await editor.CommitAsync(plan, sourcePath is not null ?
                new SingleFileWritableResolver((IWritableBlockDevice)source) : writableResolver!, cancellationToken: ct).ConfigureAwait(false);
            if (!committed.CommitSucceeded || committed.VerificationStatus != VerificationStatus.Succeeded)
                throw new IOException(Strings.FormatCli_LpCommitFailed(committed.LastCompletedPhase, committed.VerificationStatus));
        }
        finally { Reset(); }
        ChangeDirectory(container, ct);
        ui.WriteLine(Strings.Cli_LpCommitted);
    }

    private static bool IsLpMount(BrowserMount mount) { _ = mount.IsDirectory; return mount.Kind == "lp"; }

    private void Reset()
    {
        var failures = ReleaseResources(0);
        _mounts.Clear();
        _lpDocuments.Clear();
        foreach (BrowserMount mount in Root.Mounts)
        {
            mount.Reset();
            _mounts.Add(mount.Path, mount);
        }
        Current = Root;
        if (failures is not null) throw new AggregateException(Strings.Cli_BrowserCleanupFailed, failures);
    }

    internal LpMetadataDocument GetLpDocument(string path, CancellationToken ct)
    {
        var mount = Resolve(path, ct);
        if (mount is BrowserMount) _ = mount.IsDirectory;
        return _lpDocuments.TryGetValue(mount.Path, out var document) ? document :
            throw new IOException(Strings.Cli_LpContainerRequired);
    }

    private sealed class SingleFileWritableResolver(IWritableBlockDevice device) : ILpWritableBlockDeviceResolver
    {
        public ValueTask<IWritableBlockDeviceLease> ResolveAsync(LpBlockDevice blockDevice, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IWritableBlockDeviceLease>(new WritableBlockDeviceLease(device, DeviceOwnership.Borrow));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? failures;
        try { failures = ReleaseResources(0); }
        finally { _mounts.Clear(); _lpDocuments.Clear(); }
        if (failures is not null) throw new AggregateException(Strings.Cli_BrowserCleanupFailed, failures);
    }

    private List<Exception>? ReleaseResources(int baseline)
    {
        List<Exception>? failures = null;
        while (_resources.Count > baseline)
        {
            try { _resources.Pop().Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        return failures;
    }

    private sealed class BrowserLpNode(BrowserNode mount, LpMetadataDocument document, BrowserSession session)
        : BrowserNode(mount.Name, mount.Parent)
    {
        internal override bool IsDirectory => true;
        internal override string Kind => "lp";
        internal override IEnumerable<BrowserNode> Children(CancellationToken ct)
        {
            for (int i = 0; i < document.Partitions.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                LpPartition partition = document.Partitions.Span[i];
                yield return session.GetMount(partition.Name, this,
                    () => session.OpenPartition(document, partition.Name, session._operationToken), partition.LogicalSize);
            }
        }
    }

    private IReadableBlockDevice OpenPartition(LpMetadataDocument document, string name, CancellationToken ct) =>
        resolver is null ? document.OpenPartition(name) : document.OpenPartitionAsync(name, resolver, ct).AsTask().GetAwaiter().GetResult();

    private sealed class BrowserReadDevice(IReadableBlockDevice device, BrowserSession session) : IReadableBlockDevice
    {
        public BlockDeviceId Id => device.Id;
        public long Length => device.Length;
        public int LogicalBlockSize => device.LogicalBlockSize;
        public int ReadAt(long offset, Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(session._disposed, session);
            session._operationToken.ThrowIfCancellationRequested();
            // The search token never reaches Firehose RAW I/O. Finish the complete
            // underlying byte read and its ACK before observing a requested stop.
            int read = device.ReadAt(offset, destination);
            session._operationToken.ThrowIfCancellationRequested();
            return read;
        }
        public void Dispose() => device.Dispose();
    }
}
