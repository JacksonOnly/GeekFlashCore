using System.Buffers;
using System.Security.Cryptography;

namespace GeekFlashCore.Firmware.Internals;

// CFB128 can resume at any block using the previous ciphertext as feedback.
internal sealed class CfbStream : ReadOnlyStream
{
    private readonly Stream _source;
    private readonly long _encryptedLength;
    private readonly Aes _aes;
    private readonly byte[] _iv;
    private readonly byte[] _cipher = ArrayPool<byte>.Shared.Rent(65536);
    private readonly byte[] _plain = ArrayPool<byte>.Shared.Rent(65536);
    private long _cacheOffset = -1;
    private int _cacheLength;
    internal CfbStream(Stream source, long encryptedLength, byte[] key, byte[] iv, CancellationToken ct) : base(ct)
    {
        _source = source; _encryptedLength = encryptedLength; _iv = iv.ToArray();
        Aes? aes = null;
        try { aes = Aes.Create(); aes.Key = key; _aes = aes; }
        catch
        {
            try { source.Dispose(); }
            finally { aes?.Dispose(); CryptographicOperations.ZeroMemory(_iv); ArrayPool<byte>.Shared.Return(_cipher, true); ArrayPool<byte>.Shared.Return(_plain, true); }
            throw;
        }
    }
    public override long Length { get { Check(); return _source.Length; } }
    public override int Read(Span<byte> buffer)
    {
        Check(); int count = (int)Math.Min(buffer.Length, Length - Cursor); if (count == 0) return 0;
        if (Cursor >= _encryptedLength)
        { _source.Position = Cursor; int read = _source.Read(buffer[..count]); Cursor += read; return read; }
        long block = Cursor / 65536 * 65536;
        if (_cacheOffset != block) Fill(block);
        int within = (int)(Cursor - block); count = Math.Min(count, _cacheLength - within);
        _plain.AsSpan(within, count).CopyTo(buffer); Cursor += count; return count;
    }
    private void Fill(long block)
    {
        Span<byte> feedback = stackalloc byte[16];
        if (block == 0) _iv.CopyTo(feedback);
        else if (_cacheOffset + _cacheLength == block && _cacheLength >= 16 && _cacheLength % 16 == 0)
            _cipher.AsSpan(_cacheLength - 16, 16).CopyTo(feedback);
        else { _source.Position = block - 16; _source.ReadExactly(feedback); }
        _cacheOffset = -1;
        int count = (int)Math.Min(65536, _encryptedLength - block), complete = count / 16 * 16;
        _source.Position = block; _source.ReadExactly(_cipher.AsSpan(0, count));
        if (complete > 0) _aes.DecryptCfb(_cipher.AsSpan(0, complete), feedback, _plain, PaddingMode.None, 128);
        if (complete < count)
        {
            if (complete >= 16) _cipher.AsSpan(complete - 16, 16).CopyTo(feedback);
            Span<byte> mask = stackalloc byte[16]; _aes.EncryptEcb(feedback, mask, PaddingMode.None);
            for (int i = complete; i < count; i++) _plain[i] = (byte)(_cipher[i] ^ mask[i - complete]);
        }
        _cacheLength = count; _cacheOffset = block;
    }
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && CanRead)
            {
                try { _source.Dispose(); }
                finally { _aes.Dispose(); CryptographicOperations.ZeroMemory(_iv); ArrayPool<byte>.Shared.Return(_cipher, true); ArrayPool<byte>.Shared.Return(_plain, true); }
            }
        }
        finally { base.Dispose(disposing); }
    }
}
