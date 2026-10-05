using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Bundle-confined image sources and non-overwriting durable backup files.
/// Reparse points are rejected. Hosts must prevent concurrent replacement of bundle directories.</summary>
public sealed class MtkScatterDirectoryStore : IMtkScatterBackupStore
{
    private readonly string _images, _backups;
    public MtkScatterDirectoryStore(string imageDirectory,string backupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageDirectory);ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        _images=Path.GetFullPath(imageDirectory);_backups=Path.GetFullPath(backupDirectory);
        CheckParents(_images);CheckParents(_backups);
        if(!Directory.Exists(_images))throw new MtkResourceException("scatter image directory");
        Directory.CreateDirectory(_backups);CheckParents(_backups);
    }
    public IDataSource OpenImage(string name)=>Source(_images,name);
    public IDataSource OpenRead(string name)=>Source(_backups,name);
    public Stream Create(string name)
    {
        // Backups use a flat registry, so callers cannot create arbitrary directory trees.
        if(name.Contains('/') || name.Contains('\\'))throw new MtkResourceException("scatter backup path");
        return new FileStream(Resolve(_backups,name),FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.SequentialScan);
    }
    private static IDataSource Source(string root,string name)=>new DirectorySource(Resolve(root,name));
    private sealed class DirectorySource(string path):IDataSource
    {
        public long Length=>new FileInfo(path).Length;
        public Stream OpenStream(){CheckParents(path);return File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read);}
        public ValueTask<Stream> OpenStreamAsync(CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();return ValueTask.FromResult(OpenStream());}
    }
    private static string Resolve(string root,string name)
    {
        if(string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Any(c=>char.IsControl(c) || c==':') ||
            name.Replace('\\','/').Split('/').Any(p=>p is "" or "." or ".."))throw new MtkResourceException("scatter bundle path");
        string path=Path.GetFullPath(Path.Combine(root,name.Replace('/',Path.DirectorySeparatorChar).Replace('\\',Path.DirectorySeparatorChar)));
        string prefix=Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar;
        if(!path.StartsWith(prefix,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new MtkResourceException("scatter bundle path");
        CheckParents(path);return path;
    }
    private static void CheckParents(string path)
    {
        for(string? current=path;current!=null;current=Path.GetDirectoryName(current))
            if((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new MtkResourceException("scatter reparse point");
    }
}
