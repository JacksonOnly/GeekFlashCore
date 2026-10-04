using GeekFlashCore.BlockDevice;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.FileSystem.Abstractions;
using GeekFlashCore.CLI.Localization;

namespace GeekFlashCore.CLI;

internal abstract class BrowserNode(string name, BrowserNode? parent)
{
    internal string Name { get; } = name;
    internal BrowserNode? Parent { get; } = parent;
    internal string Path => Parent is null ? "/" : Parent.Path.TrimEnd('/') + "/" + Name;
    internal abstract bool IsDirectory { get; }
    internal virtual long Size => 0;
    internal virtual string Kind => IsDirectory ? "dir" : "file";
    internal virtual IEnumerable<BrowserNode> Children(CancellationToken ct) => throw new IOException(Strings.Cli_BrowserNotDirectory);
    internal virtual Stream OpenRead() => throw new IOException(Strings.Cli_BrowserNotFile);
    internal virtual object? DirectoryIdentity => null;
}

internal sealed class BrowserRoot : BrowserNode
{
    internal BrowserRoot() : base("", null) { }
    internal List<BrowserNode> Mounts { get; } = [];
    internal override bool IsDirectory => true;
    internal override IEnumerable<BrowserNode> Children(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Mounts; }
}

internal sealed class BrowserMount(string name, BrowserNode parent, Func<BrowserNode, BrowserNode> mount, long size = 0)
    : BrowserNode(name, parent)
{
    private BrowserNode? _content;
    private long _size = size;
    internal void SetSize(long value) => _size = value;
    private BrowserNode Content => _content ??= mount(this);
    internal override bool IsDirectory => Content.IsDirectory;
    internal override long Size => _size;
    internal override string Kind => _content?.Kind ?? "mount";
    internal override object? DirectoryIdentity => Content.DirectoryIdentity;
    internal override IEnumerable<BrowserNode> Children(CancellationToken ct) => Content.Children(ct);
    internal override Stream OpenRead() => Content.OpenRead();
}

internal sealed class BrowserRawNode(BrowserNode mount, IReadableBlockDevice device)
    : BrowserNode(mount.Name, mount.Parent)
{
    internal override bool IsDirectory => false;
    internal override long Size => device.Length;
    internal override string Kind => "raw";
    internal override Stream OpenRead() => new BlockDeviceStream(device, DeviceOwnership.Borrow);
}

internal sealed class BrowserFileSystemNode(string name, BrowserNode? parent, IFileSystemVolume volume, FileSystemEntry entry)
    : BrowserNode(name, parent)
{
    internal override bool IsDirectory => entry.NodeType == FileSystemNodeType.Directory;
    internal override long Size => entry.LogicalSize;
    internal override string Kind => entry.NodeType == FileSystemNodeType.SymbolicLink ? "symlink" :
        IsDirectory ? volume.Info.FormatId : entry.NodeType == FileSystemNodeType.RegularFile ? "file" : entry.NodeType.ToString();
    internal override object? DirectoryIdentity => IsDirectory ? (volume, entry.NodeId) : null;
    internal override IEnumerable<BrowserNode> Children(CancellationToken ct)
    {
        if (!IsDirectory) throw new IOException(Strings.Cli_BrowserNotDirectory);
        using var reader = volume.OpenDirectory(entry.NodeId);
        int count = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!reader.MoveNext()) yield break;
            if (++count > 1_000_000) throw new IOException(Strings.Cli_BrowserLimit);
            FileSystemEntry child = reader.Current;
            if (child.Name.Text is "." or "..") continue;
            if (child.Name.State != FileSystemNameState.Plain || child.Name.Text is not { } text)
                throw new IOException(Strings.Cli_BrowserInvalidPath);
            BrowserPath.ValidateNodeName(text);
            yield return new BrowserFileSystemNode(text, this, volume, child);
        }
    }
    internal override Stream OpenRead() => entry.NodeType == FileSystemNodeType.RegularFile
        ? volume.OpenRead(entry.NodeId) : throw new IOException(Strings.Cli_BrowserNotFile);
}
