// SPDX-License-Identifier: AGPL-3.0-or-later
// seccfg layouts: B. Kerler, bkerler/mtkclient Hardware/seccfg.py, 2018-2024, GPLv3.
using System.Buffers.Binary;
using System.Security.Cryptography;
using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Validates the original v3/v4 configuration before generating a byte-preserving change.</summary>
public static class MtkSecurityCodec
{
    public static int DeclaredSize(ReadOnlySpan<byte> data)
    {
        bool v3 = data.Length >= 44 && data[..16].SequenceEqual("AND_SECCFG_v\0\0\0\0"u8);
        int start = v3 ? 16 : 0;
        if (data.Length < start + 28 || U32(data, start) != 0x4d4d4d4d || U32(data, start + 4) != (v3 ? 3 : 4))
            throw new MtkResourceException("seccfg magic/version");
        uint size = U32(data, start + 8);
        if (size < (v3 ? 0x1860 : 60) || size > 65536 || v3 && size != 0x1860)
            throw new MtkResourceException("seccfg size");
        return (int)size;
    }
    public static byte[] Change(ReadOnlySpan<byte> original, bool locked, IReadOnlyList<IMtkSecurityCipher> ciphers, out string algorithm)
        => Change(original, locked, ciphers, null, out algorithm);
    internal static byte[] Change(ReadOnlySpan<byte> original, bool locked, IReadOnlyList<IMtkSecurityCipher> ciphers, IMtkDaChannel? channel, out string algorithm)
    {
        ArgumentNullException.ThrowIfNull(ciphers);
        if (original.Length > 65536)
            throw new ArgumentOutOfRangeException(nameof(original));
        if (ciphers.Count is 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(ciphers));
        int size = DeclaredSize(original);
        if (size > original.Length)
            throw new MtkResourceException("seccfg window");
        bool v3 = original[..16].SequenceEqual("AND_SECCFG_v\0\0\0\0"u8);
        if (U32(original, v3 ? size - 4 : 24) != 0x45454545)
            throw new MtkResourceException("seccfg end flag");
        int offset = v3 ? 44 : size - 32, length = v3 ? size - 48 : 32;
        byte[]? plaintext = null;
        IMtkSecurityCipher? matched = null;
        byte[] hash = v3 ? [] : SHA256.HashData(original[..28]);
        try
        {
            foreach (var cipher in ciphers)
            {
                if(v3 && cipher is MtkPlainSecurityCipher)continue;
                byte[] candidate = Transform(cipher, channel, original.Slice(offset, length), false);
                bool valid = candidate.Length == length && (v3 ? ValidV3(candidate) : CryptographicOperations.FixedTimeEquals(candidate, hash));
                if (valid)
                {
                    plaintext = candidate;
                    matched = cipher;
                    break;
                }
                CryptographicOperations.ZeroMemory(candidate);
            }
            if (matched is null)
                throw new MtkResourceException("seccfg algorithm/digest");
            byte[] replacement = original.ToArray();
            byte[]? encrypted = null;
            try
            {
                if (v3)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(32), locked ? 0x01000000u : 0x07f20000u);
                    BinaryPrimitives.WriteUInt32LittleEndian(plaintext!.AsSpan(0x828), locked ? 0x33333333u : 0x44444444u);
                    encrypted = Transform(matched, channel, plaintext, true);
                }
                else
                {
                    if (U32(original, 12) is < 1 or > 6 || U32(original, 16) > 2)
                        throw new MtkResourceException("seccfg lock fields");
                    BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(12), locked ? 1u : 3u);
                    BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(16), locked ? 0u : 1u);
                    CryptographicOperations.ZeroMemory(hash);
                    hash = SHA256.HashData(replacement.AsSpan(0, 28));
                    encrypted = Transform(matched, channel, hash, true);
                }
                if (encrypted.Length != length)
                    throw new MtkResourceException("seccfg crypto length");
                encrypted.CopyTo(replacement, offset);
                algorithm = matched.Name;
                return replacement;
            }
            catch { CryptographicOperations.ZeroMemory(replacement); throw; }
            finally { if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted); }
        }
        finally { CryptographicOperations.ZeroMemory(hash); if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }
    private static bool ValidV3(ReadOnlySpan<byte> data) => data.Length >= 0x82c &&
        U32(data, 0x824) is 0x43434343 or 0x49494949 &&
        U32(data, 0x828) is 0x33333333 or 0x44444444 or 0x6000 or 0x6001 or 0x6002 or 0x6003;
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static byte[] Transform(IMtkSecurityCipher cipher, IMtkDaChannel? channel, ReadOnlySpan<byte> data, bool encrypt) =>
        channel is not null && cipher is MtkSejSecurityCipher sej ? sej.Transform(channel, data, encrypt) : cipher.Transform(data, encrypt);
}
