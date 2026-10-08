// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard GCPU sequences: B. Kerler, mtkclient hwcrypto_gcpu.py, GPLv3.
using System.Buffers.Binary;
using System.Security.Cryptography;
using GeekFlashCore.Protocol.Mtk.Abstractions;
using GeekFlashCore.Protocol.Mtk.Extensions.Localization;
using static GeekFlashCore.Protocol.Mtk.Extensions.MtkGcpuRegisters;

namespace GeekFlashCore.Protocol.Mtk.Extensions;

/// <summary>Standalone GCPU AES using internal key/data slots and explicit DMA scratch.
/// No memory protection changes, firmware injection or arbitrary DMA destinations.</summary>
public sealed class MtkGcpu
{
    private readonly IMtkHardwareAccess _access;
    private readonly MtkHardwareCryptoProfile _profile;
    public MtkGcpu(IMtkHardwareAccess access, MtkHardwareCryptoProfile profile)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        _access = access;
        _profile = profile;
    }

    private uint Read(uint offset) => _access.Read32(checked(_profile.BaseAddress + offset));
    private void Write(uint offset, uint value) => _access.Write32(checked(_profile.BaseAddress + offset), value);
    private void Slot(uint slot, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i += 4)
            Write(MemoryCommand + slot * 4 + (uint)i, BinaryPrimitives.ReadUInt32LittleEndian(data[i..]));
    }

    private void Command(MtkGcpuCommand command, HardwareOperation operation)
    {
        operation.Check();
        Write(InterruptClear, _profile.HardwareCode == 0x8167 ? 1u : 3);
        Write(InterruptEnable, _profile.HardwareCode == 0x8167 ? 0u : 3);
        Write(MemoryCommand, (uint)command);
        Write(ProgramCounterControl, 0);
        uint status = operation.Wait(InterruptStatus, v => v != 0);
        if ((status & 2) != 0)
            throw new InvalidOperationException(Strings.HardwareFailure);
        operation.Wait(DramMonitor, v => (v & 1) != 0);
        Write(InterruptClear, _profile.HardwareCode == 0x8167 ? 1u : 3);
    }

    private void Initialize(HardwareOperation operation)
    {
        operation.Clock(true);
        uint ctl = Read(Control) & 0xfffffff0;
        Write(Control, ctl);
        Write(Control, ctl | 0xf);
        Write(Control, Read(Control) & 0xffffffe0);
        Write(Misc, Read(Misc) | 0x10000);
        Write(Control, Read(Control) | 0x1f);
        Write(Misc, Read(Misc) | 0x2000);
        if (_profile.HardwareCode == 0x8167)
        {
            Write(Misc, (Read(Misc) & 0x7ff0bf7f) | 0x34080);
            Write(Axi, 0x885b);
            Write(Unknown2, Read(Unknown2) & 0xfffdfffd);
            Write(MemoryAddress, 0x80002000);
        }
        else if (_profile.HardwareCode is 0x8172 or 0x8127)
        {
            Write(Control, (Read(Control) & 0xfffffff0) | 0xf);
            Write(Misc, Read(Misc) & 0xffffdfff);
        }
        else if (_profile.HardwareCode == 0x335)
        {
            Write(Control, Read(Misc) & 0xffffdfff);
            Write(Control, Read(Control) | 7);
            Write(Misc, 0x80ff1800);
            Write(Axi, 0x887f);
            Write(Unknown2, 0);
        }

        for (uint i = 1; i < 64; i++)
            Write(MemoryCommand + i * 4, 0);
    }

    private void Cleanup(HardwareOperation operation, int scratchLength = 0)
    {
        for (uint i = 1; i < 64; i++)
            Write(MemoryCommand + i * 4, 0);
        Write(InterruptClear, 3);
        Write(Control, (Read(Control) & 0xfffffff0) | 0xf);
        if (scratchLength > 0)
            ClearScratch(scratchLength);
        operation.Clock(false);
    }

    private void ClearScratch(int length)
    {
        Span<byte> zero = stackalloc byte[256];
        zero.Clear();
        for (int offset = 0; offset < length; offset += zero.Length)
            _access.WriteMemory(checked(_profile.Scratch.Address + (uint)offset), zero[..Math.Min(zero.Length, length - offset)]);
    }

    /// <summary>Transforms full blocks with the hardware key or a supplied 128-bit internal-slot key.</summary>
    public MtkSensitiveBuffer TransformEcb(ReadOnlySpan<byte> data, bool encrypt, ReadOnlySpan<byte> key = default, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty || data.Length % 16 != 0 || data.Length > _profile.MaximumInputSize || key.Length is not (0 or 16))
            throw new ArgumentException(nameof(data));
        var operation = new HardwareOperation(_access, _profile, cancellationToken);
        operation.Check();
        byte[] result = new byte[data.Length];
        try
        {
            Initialize(operation);
            if (key.IsEmpty)
            {
                Write(Parameter0, 0x58);
                Write(Parameter1, 0x30);
                Write(Parameter2, 4);
                Command(MtkGcpuCommand.LoadHardwareKey, operation);
            }
            else
                Slot(0x30, key);
            for (int pos = 0; pos < data.Length; pos += 16)
            {
                operation.Check();
                Slot(0x12, data.Slice(pos, 16));
                Write(Parameter0, 1);
                Write(Parameter1, 0x30);
                Write(Parameter2, 0x12);
                Write(Parameter3, 0x1a);
                Command(encrypt ? MtkGcpuCommand.EncryptEcb : MtkGcpuCommand.DecryptEcb, operation);
                for (uint i = 0; i < 4; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + (int)i * 4), Read(OutputSlot + i * 4));
            }

            Cleanup(operation);
            operation.Check();
            return new(result);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            try
            {
                Cleanup(operation);
            }
            catch
            {
            }

            try
            {
                _access.Invalidate();
            }
            catch
            {
            }

            throw;
        }
    }

    /// <summary>DMA CBC is confined to two disjoint windows inside the caller-confirmed scratch region.</summary>
    public MtkSensitiveBuffer TransformCbc(ReadOnlySpan<byte> data, bool encrypt, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> key = default, bool encryptedKey = false, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty || data.Length % 16 != 0 || data.Length > _profile.MaximumInputSize || iv.Length != 16 || key.Length is not (0 or 16) || !_profile.Scratch.Contains(_profile.Scratch.Address, checked((uint)data.Length * 2)))
            throw new ArgumentException(nameof(data));
        var operation = new HardwareOperation(_access, _profile, cancellationToken);
        operation.Check();
        byte[] result = new byte[data.Length];
        int scratchLength = checked(data.Length * 2);
        try
        {
            Initialize(operation);
            if (key.IsEmpty)
            {
                Write(Parameter0, 0x58);
                Write(Parameter1, 0x30);
                Write(Parameter2, 4);
                Command(MtkGcpuCommand.LoadHardwareKey, operation);
            }
            else
                Slot(0x30, key);
            Slot(0x1a, iv);
            uint output = checked(_profile.Scratch.Address + (uint)data.Length);
            _access.WriteMemory(_profile.Scratch.Address, data);
            Write(Parameter0, _profile.Scratch.Address);
            Write(Parameter1, output);
            Write(Parameter2, (uint)data.Length / 16);
            Write(Parameter4, 0x30);
            Write(Parameter5, 0x1a);
            Write(Parameter6, 0x1a);
            Command(encrypt ? MtkGcpuCommand.EncryptCbc : encryptedKey ? MtkGcpuCommand.DecryptCbcWithEncryptedKey : MtkGcpuCommand.DecryptCbc, operation);
            _access.ReadMemory(output, result);
            Cleanup(operation, scratchLength);
            operation.Check();
            return new(result);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            try
            {
                Cleanup(operation, scratchLength);
            }
            catch
            {
            }

            try
            {
                _access.Invalidate();
            }
            catch
            {
            }

            throw;
        }
    }

    /// <summary>MT6735 packet ECB. Both DMA buffers are confined to the supplied scratch region.</summary>
    public MtkSensitiveBuffer TransformPacketEcb(ReadOnlySpan<byte> data, bool encrypt, CancellationToken cancellationToken = default)
    {
        if (_profile.HardwareCode != 0x335)
            throw new MtkCapabilityException("GCPU packet ECB chip profile");
        if (data.IsEmpty || data.Length % 16 != 0 || data.Length > _profile.MaximumInputSize || !_profile.Scratch.Contains(_profile.Scratch.Address, checked((uint)data.Length * 2)))
            throw new ArgumentException(nameof(data));
        var operation = new HardwareOperation(_access, _profile, cancellationToken);
        operation.Check();
        byte[] result = new byte[data.Length];
        int scratchLength = checked(data.Length * 2);
        try
        {
            Initialize(operation);
            Write(Control, (Read(Control) & 0xfffffff8) | 7);
            Write(Misc, 0x80ff1800);
            Write(Axi, 0x887f);
            Write(Unknown2, 0);
            Write(Unknown3, uint.MaxValue);
            Write(Unknown3, uint.MaxValue);
            Write(Unknown3, uint.MaxValue);
            Write(Unknown3, 2);
            Write(Misc, Read(Misc) | 0x2000);
            uint output = checked(_profile.Scratch.Address + (uint)data.Length);
            _access.WriteMemory(_profile.Scratch.Address, data);
            Write(MemoryCommand, (uint)(encrypt ? MtkGcpuCommand.EncryptPacketEcb : MtkGcpuCommand.DecryptPacketEcb));
            Write(Parameter0, _profile.Scratch.Address);
            Write(Parameter1, output);
            Write(Parameter2, (uint)data.Length / 16);
            for (uint i = 3; i <= 13; i++)
                Write(Parameter0 + i * 4, 0);
            Write(ProgramCounterControl, 0);
            uint status = operation.Wait(InterruptClear, v => v != 0);
            if ((status & 2) != 0)
                throw new InvalidOperationException(Strings.HardwareFailure);
            Write(InterruptClear, status);
            _access.ReadMemory(output, result);
            for (uint i = 0; i < 0xe0; i++)
                Write(MemoryCommand + i * 4, 0);
            Write(InterruptEnable, 0);
            Write(Misc, 0x80fe1800);
            ClearScratch(scratchLength);
            operation.Clock(false);
            operation.Check();
            return new(result);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            try
            {
                Cleanup(operation, scratchLength);
                Write(InterruptEnable, 0);
                Write(Misc, 0x80fe1800);
            }
            catch
            {
            }

            try
            {
                _access.Invalidate();
            }
            catch
            {
            }

            throw;
        }
    }

    public MtkSensitiveBuffer DeriveMtee(CancellationToken cancellationToken = default) => _profile.HardwareCode == 0x335 ? TransformPacketEcb("www.mediatek.com0123456789ABCDEF"u8, true, cancellationToken) : TransformEcb("KeymasterMaster\0"u8, true, cancellationToken: cancellationToken);
    /// <summary>Decrypts a normal MTEE image using supplied seed/key material. Input DMA never leaves scratch.</summary>
    public MtkSensitiveBuffer DecryptMteeImage(ReadOnlySpan<byte> data, ReadOnlySpan<byte> keySeed, ReadOnlySpan<byte> ivSeed, ReadOnlySpan<byte> aesKey1, ReadOnlySpan<byte> aesKey2, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty || data.Length % 16 != 0 || data.Length > _profile.MaximumInputSize || keySeed.Length != 16 || ivSeed.Length != 16 || aesKey1.Length != 16 || aesKey2.Length != 16 || !_profile.Scratch.Contains(_profile.Scratch.Address, checked((uint)data.Length * 2)))
            throw new ArgumentException(nameof(data));
        using var iv = TransformEcb(keySeed, false, aesKey1, cancellationToken);
        Span<byte> wrappedKey = stackalloc byte[16];
        for (int i = 0; i < wrappedKey.Length; i++)
            wrappedKey[i] = (byte)(aesKey2[i] ^ ivSeed[i]);
        try
        {
            return TransformCbc(data, false, iv.Memory.Span, wrappedKey, true, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappedKey);
        }
    }

    /// <summary>Reference device-key HMAC truncated to 16 bytes; the seed includes caller-confirmed devinfo words.</summary>
    public MtkSensitiveBuffer ComputeDeviceHmac(ReadOnlySpan<byte> data, ReadOnlySpan<byte> seed, CancellationToken cancellationToken = default)
    {
        if (seed.Length != 16 || data.Length > _profile.MaximumInputSize)
            throw new ArgumentException(nameof(seed));
        using var key = TransformEcb(seed, false, cancellationToken: cancellationToken);
        byte[] digest = HMACSHA256.HashData(key.Memory.Span, data);
        try
        {
            return new(digest.AsSpan(0, 16).ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    /// <summary>Derives the reference reversed RPMB key from a 16-byte CID and explicitly supplied devinfo seed.</summary>
    public MtkSensitiveBuffer DeriveRpmb(ReadOnlySpan<byte> cid, ReadOnlySpan<byte> seed, CancellationToken cancellationToken = default)
    {
        if (cid.Length != 16 || seed.Length != 16)
            throw new ArgumentException(nameof(cid));
        Span<byte> expanded = stackalloc byte[64];
        for (int i = 0; i < 64; i++)
            expanded[i] = cid[i % 16];
        try
        {
            using var key = ComputeDeviceHmac(expanded, seed, cancellationToken);
            byte[] result = HMACSHA256.HashData(key.Memory.Span, "RPMB\0"u8);
            Array.Reverse(result);
            return new(result);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expanded);
        }
    }
}
