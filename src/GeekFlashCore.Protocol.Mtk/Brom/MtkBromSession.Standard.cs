// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard BROM methods: B. Kerler, mtkclient/Library/mtk_preloader.py, 2018-2024, GPLv3.
using System.Security.Cryptography;
using System.Text;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Mtk.Internals;

namespace GeekFlashCore.Protocol.Mtk.Brom;

internal sealed partial class MtkBromSession
{
    private void Command(MtkBromCommand command)
    {
        wire.TraceCommand((byte)command, command.ToString());
        wire.EchoByte((byte)command);
    }
    private byte Version(MtkBromCommand command)
    {
        wire.TraceCommand((byte)command, command.ToString());
        wire.WriteByte((byte)command);
        return wire.ReadByte();
    }
    private void CheckStatus(ushort maximum = MaximumSuccessfulStatus) => CheckStatusValue(wire.Read16(), maximum);
    private void CheckStatusValue(ushort status, ushort maximum = MaximumSuccessfulStatus)
    {
        wire.TraceStatus(status, status <= maximum);
        if (status > maximum)
            throw wire.Failure(status);
    }
    private void LittleEndianStatus()
    {
        Span<byte> bytes = stackalloc byte[2];
        wire.Read(bytes);
        ushort status = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        wire.TraceStatus(status, status == 0);
        if (status != 0)
            throw wire.Failure(status);
    }
    private void MemoryRange(uint address, int count, int width)
    {
        if (count <= 0 || (long)count * width > options.MaximumFrameSize ||
            address % width != 0 || (ulong)address + (ulong)count * (uint)width > (ulong)uint.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(count));
    }
    private void MemoryCommand(MtkBromCommand command, uint address, int count, int width)
    {
        MemoryRange(address, count, width);
        Command(command);
        wire.Echo32(address);
        wire.Echo32((uint)count);
    }
    public MtkBromHardwareCode GetHardwareCode()
    {
        Command(MtkBromCommand.GetHardwareCode);
        return new(wire.Read16(), wire.Read16());
    }
    public MtkBromHardwareSoftwareVersion GetHardwareSoftwareVersion()
    {
        Command(MtkBromCommand.GetHardwareSoftwareVersion);
        var result = new MtkBromHardwareSoftwareVersion(wire.Read16(), wire.Read16(), wire.Read16());
        CheckStatus();
        return result;
    }
    public MtkSecurityConfiguration GetTargetConfiguration()
    {
        Command(MtkBromCommand.GetTargetConfiguration);
        uint value = wire.Read32();
        CheckStatus();
        return new(value);
    }
    public byte GetBootLoaderVersion() => Version(MtkBromCommand.GetBootLoaderVersion);
    public byte GetBromVersion() => Version(MtkBromCommand.GetBromVersion);
    public MtkPreloaderCapabilities GetPreloaderCapabilities()
    {
        Command(MtkBromCommand.GetPreloaderCapabilities);
        return new(wire.Read32(), wire.Read32());
    }
    private MtkSensitiveBuffer? Identifier(MtkBromCommand command)
    {
        byte version = GetBootLoaderVersion();
        if (version != (byte)MtkBromCommand.GetBootLoaderVersion && version <= 2)
            return null;
        Command(command);
        uint length = wire.Read32();
        if (length is 0 or > 256)
            throw new MtkResourceException("BROM identifier length");
        byte[] data = new byte[(int)length];
        try
        {
            wire.Read(data);
            LittleEndianStatus();
            return new(data);
        }
        catch { CryptographicOperations.ZeroMemory(data); throw; }
    }
    public MtkSensitiveBuffer? GetMeId() => Identifier(MtkBromCommand.GetMeId);
    public MtkSensitiveBuffer? GetSocId() => Identifier(MtkBromCommand.GetSocId);
    public MtkSensitiveBuffer GetBromLog(bool newCommand)
    {
        Command(newCommand ? MtkBromCommand.GetBromLogNew : MtkBromCommand.GetBromLog);
        uint length = wire.Read32();
        if (length > options.MaximumFrameSize)
            throw new MtkResourceException("BROM log length");
        byte[] data = new byte[(int)length];
        try
        {
            wire.Read(data);
            if (newCommand)
                CheckStatus(0);
            return new(data);
        }
        catch { CryptographicOperations.ZeroMemory(data); throw; }
    }
    public ushort[] Read16(uint address, int count)
    {
        MemoryCommand(MtkBromCommand.Read16, address, count, 2);
        CheckStatus();
        var result = new ushort[count];
        try
        {
            for (int i = 0; i < count; i++)
                result[i] = wire.Read16();
            CheckStatus();
            return result;
        }
        catch { Array.Clear(result); throw; }
    }
    public uint[] Read32(uint address, int count)
    {
        MemoryCommand(MtkBromCommand.Read32, address, count, 4);
        CheckStatus();
        var result = new uint[count];
        try
        {
            for (int i = 0; i < count; i++)
                result[i] = wire.Read32();
            CheckStatus();
            return result;
        }
        catch { Array.Clear(result); throw; }
    }
    public ushort ReadA2(uint address)
    {
        MemoryCommand(MtkBromCommand.Read16A2, address, 1, 2);
        return wire.Read16();
    }
    public void Write16(uint address, ReadOnlySpan<ushort> values)
    {
        MemoryCommand(MtkBromCommand.Write16, address, values.Length, 2);
        CheckStatus(3);
        Span<byte> value = stackalloc byte[2];
        foreach (ushort item in values)
        {
            BinaryPrimitives.WriteUInt16BigEndian(value, item);
            wire.Echo(value);
        }
        CheckStatus();
    }
    public void Write32(uint address, ReadOnlySpan<uint> values)
    {
        MemoryCommand(MtkBromCommand.Write32, address, values.Length, 4);
        CheckStatus(3);
        foreach (uint item in values)
            wire.Echo32(item);
        CheckStatus();
    }
    public void WriteMemory(uint address, ReadOnlySpan<byte> data, bool padFinalWord)
    {
        if (!padFinalWord && data.Length % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(data));
        int count = checked((data.Length + 3) / 4);
        MemoryRange(address, count, 4);
        Span<byte> bytes = stackalloc byte[4];
        try
        {
            for (int i = 0; i < count; i++)
            {
                bytes.Clear();
                data.Slice(i * 4, Math.Min(4, data.Length - i * 4)).CopyTo(bytes);
                Write32(checked(address + (uint)i * 4), [BinaryPrimitives.ReadUInt32LittleEndian(bytes)]);
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private void RegisterCommand(uint address, int length, bool writing)
    {
        MemoryRange(address, length / 4, 4);
        if (length % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        Command(MtkBromCommand.RegisterAccess);
        wire.Echo32(writing ? 1u : 0u);
        wire.Echo32(address);
        wire.Echo32((uint)length);
        LittleEndianStatus();
    }
    public void ReadRegisters(uint address, Span<byte> destination)
    {
        RegisterCommand(address, destination.Length, false);
        try
        {
            wire.Read(destination);
            LittleEndianStatus();
        }
        catch { destination.Clear(); throw; }
    }
    public void WriteRegisters(uint address, ReadOnlySpan<byte> data)
    {
        RegisterCommand(address, data.Length, true);
        for (int offset = 0; offset < data.Length;)
        {
            int length = Math.Min(options.BufferSize, data.Length - offset);
            wire.Write(data.Slice(offset, length));
            offset += length;
        }
        LittleEndianStatus();
    }
    public void ConfigureBromReset(uint miscLockAddress, bool enabled, int timeoutMilliseconds)
    {
        if (miscLockAddress < 0x20 || miscLockAddress % 4 != 0 || miscLockAddress > uint.MaxValue - 8 ||
            timeoutMilliseconds < 0 || timeoutMilliseconds > 0x3ffe * 1000)
            throw new ArgumentOutOfRangeException(nameof(miscLockAddress));
        uint seconds = timeoutMilliseconds == 0 ? 0x3fff : (uint)Math.Max(1, timeoutMilliseconds / 1000);
        uint flags = 0x444c0000 | seconds << 2 | (enabled ? 1u : 0u);
        Write32(miscLockAddress, [0xad98]);
        Write32(miscLockAddress + 8, [1]);
        Write32(miscLockAddress, [0]);
        Write32(miscLockAddress - 0x20, [flags]);
    }
    public MtkBromCacheResult RunCacheDeinitialize()
    {
        Command(MtkBromCommand.CacheControl);
        wire.EchoByte((byte)MtkBromCommand.I2cDeinitialize);
        byte response = wire.ReadByte();
        return new(response, wire.Read16());
    }
    public void EnableUart1Log()
    {
        Command(MtkBromCommand.EnableUart1Log);
        CheckStatus(0);
    }
    public void SetUart1BaudRate(uint baudRate)
    {
        if (baudRate == 0)
            throw new ArgumentOutOfRangeException(nameof(baudRate));
        Command(MtkBromCommand.SetUart1BaudRate);
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, baudRate);
        wire.Write(value);
        CheckStatus(0);
    }
    public void Authenticate(Func<MtkAuthenticationKind, ReadOnlyMemory<byte>, MtkSensitiveBuffer> signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        byte[]? challenge = StartSla();
        if (challenge is null)
            return;
        try
        {
            MtkDiagnostics.Summary(wire.Logger, Strings.AuthenticationStarted, MtkAuthenticationKind.BromSla);
            using var response = signer(MtkAuthenticationKind.BromSla, challenge);
            FinishSla(response.Memory.Span);
            wire.Check();
            MtkDiagnostics.Summary(wire.Logger, Strings.AuthenticationEvidence, MtkAuthenticationKind.BromSla, MtkDaAuthenticationState.Authenticated);
        }
        finally { CryptographicOperations.ZeroMemory(challenge); }
    }
    private static void DownloadRange(uint address, uint size, uint signatureLength)
    {
        ulong padded = (ulong)size + (size & 1);
        if (size == 0 || signatureLength >= size || padded > uint.MaxValue ||
            (ulong)address + padded > (ulong)uint.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(size));
    }
    public bool BeginDownloadAgent(uint address, uint size, uint signatureLength)
    {
        DownloadRange(address, size, signatureLength);
        MtkDiagnostics.Summary(wire.Logger, Strings.BromUploadStarted, size, address,
            options.BromUploadChunkSize == 0 ? options.BufferSize : options.BromUploadChunkSize, options.BromUploadZeroLengthPacket);
        Command(MtkBromCommand.SendDownloadAgent);
        wire.Echo32(address);
        wire.Echo32(checked(size + (size & 1)));
        wire.Echo32(signatureLength);
        ushort status = wire.Read16();
        if (status == DownloadAgentRequiresSla)
        {
            wire.TraceStatus(status, true);
            return true;
        }
        CheckStatusValue(status);
        return false;
    }
    public void FinishDownloadAgent(uint size, Stream source)
    {
        ushort checksum = UploadBytes(source, size), actual = wire.Read16();
        CheckStatus();
        Checksum(checksum, actual);
        wire.Check();
        MtkDiagnostics.Summary(wire.Logger, Strings.BromUploadCompleted, size);
    }
    public void SendDownloadAgent(uint address, uint size, uint signatureLength, IDataSource source,
        Func<MtkAuthenticationKind, ReadOnlyMemory<byte>, MtkSensitiveBuffer>? signer)
    {
        ArgumentNullException.ThrowIfNull(source);
        DownloadRange(address, size, signatureLength);
        if (source.Length != size)
            throw new MtkResourceException("DA source length");
        using var stream = source.OpenStream();
        if (!stream.CanRead || stream.CanSeek && stream.Length - stream.Position != size)
            throw new MtkResourceException("DA source");
        if (BeginDownloadAgent(address, size, signatureLength))
            Authenticate(signer ?? throw new MtkResourceException("in-command BROM SLA signer"));
        FinishDownloadAgent(size, stream);
    }
    public void JumpDownloadAgent(uint address, bool is64Bit = false)
    {
        if (address == 0)
            throw new ArgumentOutOfRangeException(nameof(address));
        MtkDiagnostics.Summary(wire.Logger, Strings.BromJump, address);
        Command(is64Bit ? MtkBromCommand.JumpDownloadAgent64 : MtkBromCommand.JumpDownloadAgent);
        wire.Echo32(address);
        if (is64Bit)
            wire.EchoByte(1);
        CheckStatus(0);
        wire.Stage = MtkBootStage.Da1;
    }
    public void JumpBootLoader()
    {
        Command(MtkBromCommand.JumpBootLoader);
        CheckStatus();
        CheckStatus();
        wire.Stage = MtkBootStage.Preloader;
    }
    private static byte[] PartitionName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('\0') || Encoding.UTF8.GetByteCount(name) > 64)
            throw new ArgumentOutOfRangeException(nameof(name));
        byte[] bytes = new byte[64];
        Encoding.UTF8.GetBytes(name, bytes);
        return bytes;
    }
    public void JumpToPartition(string name)
    {
        byte[] bytes = PartitionName(name);
        Command(MtkBromCommand.JumpToPartition);
        wire.Write(bytes);
        CheckStatus();
        wire.Stage = MtkBootStage.Preloader;
    }
    public void SendPartitionData(string name, IDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] partition = PartitionName(name);
        if (source.Length <= 0 || source.Length > uint.MaxValue)
            throw new MtkResourceException("partition source length");
        using var stream = source.OpenStream();
        if (!stream.CanRead || stream.CanSeek && stream.Length - stream.Position != source.Length)
            throw new MtkResourceException("partition source");
        Command(MtkBromCommand.SendPartitionData);
        wire.Write(partition);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)source.Length);
        wire.Write(number);
        CheckStatus();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(512);
        uint sum = 0;
        long remaining = source.Length;
        try
        {
            while (remaining > 0)
            {
                wire.Check();
                int count = (int)Math.Min(512, remaining);
                stream.ReadExactly(buffer.AsSpan(0, count));
                int i = 0;
                for (; i + 4 <= count; i += 4)
                    sum = unchecked(sum + BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i, 4)));
                for (; i < count; i++)
                    sum = unchecked(sum + buffer[i]);
                wire.Write(buffer.AsSpan(0, count));
                remaining -= count;
            }
            BinaryPrimitives.WriteUInt32BigEndian(number, sum);
            wire.Write(number); // Reference command has no final status; do not invent a confirmation.
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, true); }
    }
}
