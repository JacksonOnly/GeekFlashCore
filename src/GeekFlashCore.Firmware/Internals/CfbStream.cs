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
    internal CfbStream(Stream source, long encryptedLength, byte[] key, byte[] iv, CancellationToken ct) : base(ct)
    {
        _source = source; _encryptedLength = encryptedLength; _iv = iv.ToArray();
        Aes? aes = null;
        try { aes = Aes.Create(); aes.Key = key; _aes = aes; }
        catch
        {
            try { source.Dispose(); }
            finally { aes?.Dispose(); CryptographicOperations.ZeroMemory(_iv); ArrayPool<byte>.Shared.Return(_cipher, true); }
            throw;
        }
    }
    public override long Length { get { Check(); return _source.Length; } }
    public override int Read(Span<byte> buffer)
    {
        Check(); int count = (int)Math.Min(buffer.Length, Length - Cursor); if (count == 0) return 0;
        if (Cursor >= _encryptedLength)
        { _source.Position = Cursor; int read = _source.Read(buffer[..count]); Cursor += read; return read; }
        count = (int)Math.Min(count, _encryptedLength - Cursor);
        long block = Cursor / 16 * 16; int within = (int)(Cursor - block);
        Span<byte> feedback = stackalloc byte[16];
        if (block == 0) _iv.CopyTo(feedback); else { _source.Position = block - 16; _source.ReadExactly(feedback); }
        if (within == 0 && count >= 16)
        {
            int size = Math.Min(65536, count / 16 * 16); _source.Position = Cursor; _source.ReadExactly(_cipher.AsSpan(0, size));
            _aes.DecryptCfb(_cipher.AsSpan(0, size), feedback, buffer, PaddingMode.None, 128); Cursor += size; return size;
        }
        Span<byte> mask = stackalloc byte[16]; _aes.EncryptEcb(feedback, mask, PaddingMode.None);
        int n = Math.Min(count, 16 - within); _source.Position = Cursor; _source.ReadExactly(buffer[..n]);
        for (int i = 0; i < n; i++) buffer[i] ^= mask[within + i]; Cursor += n; return n;
    }
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (disposing && CanRead)
            {
                try { _source.Dispose(); }
                finally { _aes.Dispose(); CryptographicOperations.ZeroMemory(_iv); ArrayPool<byte>.Shared.Return(_cipher, true); }
            }
        }
        finally { base.Dispose(disposing); }
    }
}
