using System.Buffers;
using System.Buffers.Binary;
using GeekFlashCore.Android.Lp;
using GeekFlashCore.Android.Lp.Abstractions;
using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.FileSystem.Abstractions;
using GeekFlashCore.FileSystem.Erofs;
using GeekFlashCore.FileSystem.Ext;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal sealed class BrowserSession(int slot = 0, ILpBlockDeviceResolver? resolver = null, string? sourcePath = null) : IDisposable
{
    private readonly Stack<IDisposable> _resources = new();
    private readonly Dictionary<string, BrowserMount> _mounts = new(StringComparer.Ordinal);
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
        var mount = new BrowserMount(name, parent, node => Mount(node, open), size);
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

    internal async Task ExportAsync(BrowserNode node, string destination, CancellationToken ct, Action<long, long>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        _operationToken = ct;
        if (node.IsDirectory) throw new IOException(Strings.Cli_BrowserNotFile);
        destination = Path.GetFullPath(ConsolePath.Normalize(destination)!);
        if (sourcePath is not null && string.Equals(destination, Path.GetFullPath(sourcePath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException(Strings.Cli_BrowserSourceOverwrite);
        RejectLinkedAncestors(destination);
        using Stream input = node.OpenRead();
        await AtomicReadOutput.WriteAsync(destination, async output =>
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                long copied = 0;
                while (copied < node.Size)
                {
                    ct.ThrowIfCancellationRequested();
                    int size = (int)Math.Min(buffer.Length, node.Size - copied);
                    // Keep filesystem and transport reads synchronous; async is only for the local output.
                    int read = input.Read(buffer, 0, size);
                    if (read == 0) throw new EndOfStreamException(Strings.Cli_BrowserShortRead);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied = checked(copied + read);
                    progress?.Invoke(copied, node.Size);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }, ct).ConfigureAwait(false);
        if (node.Size == 0) progress?.Invoke(0, 0);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<Exception>? failures;
        try { failures = ReleaseResources(0); }
        finally { _mounts.Clear(); }
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
