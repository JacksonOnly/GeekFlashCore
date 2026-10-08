// SPDX-License-Identifier: AGPL-3.0-or-later
// Penumbra 2.0 normal communication with an already loaded extension; no loading or patching.
using System.Buffers.Binary;
using System.Security.Cryptography;
using GeekFlashCore.Protocol.Mtk.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

public sealed partial class MtkDaExtension
{
    private static int KeyBytes(MtkKeySize size) => size switch
    {
        MtkKeySize.Key128 => 16,
        MtkKeySize.Key192 => 24,
        MtkKeySize.Key256 => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(size))};
    public MtkSensitiveBuffer DeriveKey(MtkKeyDeriveId id, MtkKeySize size, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(id))
            throw new ArgumentOutOfRangeException(nameof(id));
        _ = KeyBytes(size);
        return _access.UseSession(c => DeriveKeyCore(c, id, size, [], []), cancellationToken);
    }

    public MtkSensitiveBuffer DeriveKey(ReadOnlySpan<byte> label, ReadOnlySpan<byte> salt, MtkKeySize size, CancellationToken cancellationToken = default)
    {
        if (label.Length > 32 || salt.Length > 32)
            throw new ArgumentOutOfRangeException(nameof(label));
        _ = KeyBytes(size);
        byte[] labelCopy = label.ToArray(), saltCopy = salt.ToArray();
        try
        {
            return _access.UseSession(c => DeriveKeyCore(c, null, size, labelCopy, saltCopy), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(labelCopy);
            CryptographicOperations.ZeroMemory(saltCopy);
        }
    }

    private MtkSensitiveBuffer DeriveKeyCore(IMtkDaChannel c, MtkKeyDeriveId? id, MtkKeySize size, byte[] label, byte[] salt)
    {
        Ready(c);
        if (_context!.Abi != MtkExtensionAbi.Penumbra2)
            throw new MtkCapabilityException("Penumbra2 key derivation ABI");
        if (_context.SejBase == 0 && _context.TzccBase == 0 && _context.SsrBase == 0)
            throw new MtkCapabilityException("key derivation profile");
        int length = KeyBytes(size);
        byte[] key = new byte[length];
        try
        {
            if (c.Kind == MtkDaKind.XFlash)
            {
                if (id.HasValue)
                    Control(c, MtkXFlashCommand.ExtKeyDerive, LE((uint)id.Value), LE((uint)length));
                else
                    Control(c, MtkXFlashCommand.ExtKeyDerive, LE(0xff), LE((uint)length), LE((uint)label.Length), LE((uint)salt.Length), label, salt);
                if (c.ReceiveData(key) != length)
                    throw new MtkResourceException("derived key length");
                c.CheckStatus();
            }
            else
            {
                string type = id switch
                {
                    MtkKeyDeriveId.Rpmb => "RPMB",
                    MtkKeyDeriveId.Fde => "FDE",
                    MtkKeyDeriveId.Tee => "TEE",
                    MtkKeyDeriveId.AesImageEncryption => "AES_IMG_ENC",
                    MtkKeyDeriveId.AesCustom => "AES_CUSTOM",
                    MtkKeyDeriveId.Motorola => "MOTOROLA",
                    MtkKeyDeriveId.RootOfTrust => "ROT",
                    null => "INPUT",
                    _ => throw new ArgumentOutOfRangeException(nameof(id))};
                c.BeginXmlCommand(MtkXmlCommand.ExtKeyDerive, Args(("key_type", type), ("key_length", Hex((uint)length)), ("label", Convert.ToHexString(label)), ("salt", Convert.ToHexString(salt))));
                using var output = new MemoryStream();
                byte[]? xml = null;
                try
                {
                    c.ReceiveXmlFile(output, null, 4096);
                    c.EndXmlCommand();
                    xml = output.ToArray();
                    string value = XmlValue(xml, "result");
                    if (value.Length != length * 2)
                        throw new MtkResourceException("derived key length");
                    byte[] decoded = Convert.FromHexString(value);
                    try
                    {
                        decoded.CopyTo(key, 0);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(decoded);
                    }
                }
                finally
                {
                    if (xml != null)
                        CryptographicOperations.ZeroMemory(xml);
                    CryptographicOperations.ZeroMemory(output.GetBuffer());
                }
            }

            return new(key);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    public MtkSensitiveBuffer TransformSej(ReadOnlySpan<byte> data, MtkSejParameters parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ValidateSej(data, parameters);
        byte[] copy = data.ToArray();
        try
        {
            return _access.UseSession(c => new MtkSensitiveBuffer(TransformSejCore(c, copy, parameters)), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static void ValidateSej(ReadOnlySpan<byte> data, MtkSejParameters p)
    {
        if (data.IsEmpty || data.Length > 65536 || data.Length % 16 != 0 || !Enum.IsDefined(p.Mode) || !Enum.IsDefined(p.Key) || !Enum.IsDefined(p.KeySize))
            throw new ArgumentOutOfRangeException(nameof(data));
    }

    private byte[] TransformSejCore(IMtkDaChannel c, ReadOnlySpan<byte> data, MtkSejParameters p)
    {
        ValidateSej(data, p);
        Ready(c);
        if (_context!.Abi != MtkExtensionAbi.Penumbra2 || _context.SejBase == 0)
            throw new MtkCapabilityException("Penumbra2 SEJ profile");
        if (c.Kind == MtkDaKind.Xml && (p.Legacy || p.Xor))
            throw new MtkCapabilityException("XML legacy SEJ");
        byte[] copy = data.ToArray(), result = new byte[data.Length];
        try
        {
            using var input = new MemoryStream(copy, false);
            using var output = new MemoryStream(result, true);
            if (c.Kind == MtkDaKind.XFlash)
            {
                byte[] fields = new byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(fields, (uint)data.Length);
                fields[4] = p.Encrypt ? (byte)1 : (byte)0;
                fields[5] = p.AntiClone ? (byte)1 : (byte)0;
                fields[6] = p.Xor ? (byte)1 : (byte)0;
                fields[7] = p.Legacy ? (byte)1 : (byte)0;
                fields[8] = p.Mode == MtkAesMode.Cbc ? (byte)1 : (byte)0;
                fields[9] = (byte)p.Key;
                fields[10] = (byte)p.KeySize;
                Control(c, MtkXFlashCommand.ExtSej, fields);
                Download(c, copy.Length, input, c.WritePacketLength);
                Upload(c, copy.Length, output);
                c.CheckStatus();
            }
            else
            {
                string key = p.Key switch
                {
                    MtkSejKeyId.Software => "SW_KEY",
                    MtkSejKeyId.Hardware => "HW_KEY",
                    MtkSejKeyId.HardwareWrapped => "HW_WRAPPED_KEY",
                    MtkSejKeyId.Rid => "RID_KEY",
                    MtkSejKeyId.Custom => "CUSTOM_KEY",
                    _ => throw new ArgumentOutOfRangeException(nameof(p))};
                string size = p.KeySize switch
                {
                    MtkKeySize.Key128 => "KEY_128",
                    MtkKeySize.Key192 => "KEY_192",
                    _ => "KEY_256"
                };
                c.BeginXmlCommand(MtkXmlCommand.ExtSej, Args(("encrypt", p.Encrypt ? "yes" : "no"), ("ac", p.AntiClone ? "yes" : "no"), ("length", Hex((uint)data.Length)), ("cbc", p.Mode == MtkAesMode.Cbc ? "yes" : "no"), ("key_id", key), ("key_size", size)));
                c.SendXmlFile(input, copy.Length);
                c.ReceiveXmlFile(output, copy.Length, copy.Length);
                c.EndXmlCommand();
            }

            return result;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }
}
