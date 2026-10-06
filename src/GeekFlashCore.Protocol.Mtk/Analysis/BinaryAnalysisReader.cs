using System.Buffers;
using System.Buffers.Binary;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Analysis;

/// <summary>One query's owned stream and bounded random-access cache.</summary>
internal sealed class BinaryAnalysisReader : IDisposable
{
    private const int CacheSize = 65536;
    private readonly IDataSource _source;
    private readonly Stream _stream;
    private readonly CancellationToken _token;
    private readonly byte[] _cache;
    private long _page = -1;
    private int _count;
    private bool _disposed;
    public long Length { get; }
    public BinaryAnalysisReader(IDataSource source, long length, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.Length != length) throw new MtkResourceException("analysis source length");
        _source = source; Length = length; _token = token;
        _stream = source.OpenStream() ?? throw new MtkResourceException("analysis source stream");
        try
        {
            token.ThrowIfCancellationRequested();
            if (!_stream.CanRead || !_stream.CanSeek || _stream.Length != length || source.Length != length)
                throw new MtkResourceException("analysis source stream");
            _cache = ArrayPool<byte>.Shared.Rent(CacheSize);
        }
        catch { _stream.Dispose(); throw; }
    }
    public void Check()
    {
        _token.ThrowIfCancellationRequested();
        if (_source.Length != Length || _stream.Length != Length) throw new MtkResourceException("analysis source length");
    }
    private void Load(long offset)
    {
        Check();
        long page = offset - offset % CacheSize;
        if (_page == page) return;
        _stream.Position = page;
        _count = (int)Math.Min(CacheSize, Length - page);
        int read = 0;
        while (read < _count)
        {
            _token.ThrowIfCancellationRequested();
            int n = _stream.Read(_cache.AsSpan(read, _count - read));
            if (n <= 0 || n > _count - read) throw new MtkResourceException("analysis source read");
            read += n;
        }
        Check(); _page = page;
    }
    private byte ReadByte(long offset) { Load(offset); return _cache[(int)(offset - _page)]; }
    public ushort? ReadUInt16(long offset)
    {
        Check(); if (offset < 0 || offset > Length - 2) return null;
        Load(offset); int index = (int)(offset - _page);
        return index <= _count - 2 ? BinaryPrimitives.ReadUInt16LittleEndian(_cache.AsSpan(index, 2))
            : (ushort)(ReadByte(offset) | (ReadByte(offset + 1) << 8));
    }
    public uint? ReadUInt32(long offset)
    {
        Check(); if (offset < 0 || offset > Length - 4) return null;
        Load(offset); int index = (int)(offset - _page);
        if (index <= _count - 4) return BinaryPrimitives.ReadUInt32LittleEndian(_cache.AsSpan(index, 4));
        uint value = 0; for (int i = 0; i < 4; i++) value |= (uint)ReadByte(offset + i) << (8 * i);
        return value;
    }
    public long? FindPattern(ReadOnlySpan<byte> pattern)
    {
        var prefix = new int[pattern.Length];
        for (int i = 1, matched = 0; i < pattern.Length; i++)
        {
            while (matched > 0 && pattern[i] != pattern[matched]) matched = prefix[matched - 1];
            if (pattern[i] == pattern[matched]) matched++;
            prefix[i] = matched;
        }
        int match = 0;
        for (long offset = 0; offset < Length;)
        {
            Load(offset); int start = (int)(offset - _page);
            for (int i = start; i < _count; i++, offset++)
            {
                if ((i & 4095) == 0) Check();
                byte value = _cache[i];
                while (match > 0 && value != pattern[match]) match = prefix[match - 1];
                if (value == pattern[match]) match++;
                if (match == pattern.Length) { Check(); return offset - pattern.Length + 1; }
            }
        }
        Check(); return null;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { _stream.Dispose(); } finally { ArrayPool<byte>.Shared.Return(_cache, true); }
    }
}
