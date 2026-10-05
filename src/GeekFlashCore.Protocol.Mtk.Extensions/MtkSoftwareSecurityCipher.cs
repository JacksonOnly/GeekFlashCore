// SPDX-License-Identifier: AGPL-3.0-or-later
// Software seccfg AES format: B. Kerler, bkerler/mtkclient hwcrypto_sej.py, 2018-2024, GPLv3.
using System.Security.Cryptography;
using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Public MediaTek software seccfg AES-CBC format; no host secret or device key is embedded.</summary>
public sealed class MtkSoftwareSecurityCipher : IMtkSecurityCipher
{
    public string Name => "SW";
    public byte[] Transform(ReadOnlySpan<byte> data, bool encrypt)
    {
        if (data.IsEmpty || data.Length % 16 != 0)
            throw new ArgumentOutOfRangeException(nameof(data));
        using var aes = Aes.Create();
        aes.Key = "25A1763A21BC854CD569DC23B4782B63"u8.ToArray();
        byte[] iv = Convert.FromHexString("57325A5A125497661254976657325A5A");
        return encrypt ? aes.EncryptCbc(data, iv, PaddingMode.None) : aes.DecryptCbc(data, iv, PaddingMode.None);
    }
}
/// <summary>Explicit device SEJ algorithm candidate. A matching original configuration is required before writes.</summary>
public sealed class MtkSejSecurityCipher(MtkDaExtension extension, bool legacy = false, bool xor = false) : IMtkSecurityCipher
{
    public string Name => legacy ? "SEJ-HWv4" : xor ? "SEJ-HW" : "SEJ-HWv3";
    public byte[] Transform(ReadOnlySpan<byte> data, bool encrypt) => extension.TransformSej(data, encrypt, true, legacy, xor);
    internal byte[] Transform(IMtkDaChannel channel, ReadOnlySpan<byte> data, bool encrypt) =>
        extension.TransformSej(channel, data, encrypt, true, legacy, xor);
}
