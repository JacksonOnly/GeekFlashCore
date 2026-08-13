using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal readonly struct SaharaPacketSender
{
    private readonly ITransport _transport;

    public SaharaPacketSender(ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendHelloRequest(uint version, uint versionSupported, uint commandPacketLength, SaharaMode mode,
        uint reserved0, uint reserved1, uint reserved2, uint reserved3, uint reserved4, uint reserved5)
    {
        Span<byte> buffer = stackalloc byte[SaharaHelloRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaHelloRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaHelloRequest.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], version);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], versionSupported);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[16..], commandPacketLength);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[20..], (uint)mode);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[24..], reserved0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[28..], reserved1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[32..], reserved2);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[36..], reserved3);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[40..], reserved4);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[44..], reserved5);
        _transport.Write(buffer);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendExecuteRequest(SaharaExecuteCommand clientCommand)
    {
        Span<byte> buffer = stackalloc byte[SaharaExecuteRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaExecuteRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaExecuteRequest.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], (uint)clientCommand);
        _transport.Write(buffer);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendReadData32BitRequest(uint imageId, uint dataOffset, uint dataLength)
    {
        Span<byte> buffer = stackalloc byte[SaharaReadData32BitRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaReadData32BitRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaReadData32BitRequest.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], imageId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[16..], dataLength);
        _transport.Write(buffer);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendReadData64BitRequest(ulong imageId, ulong dataOffset, ulong dataLength)
    {
        Span<byte> buffer = stackalloc byte[SaharaReadData64BitRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaReadData64BitRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaReadData64BitRequest.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[8..], imageId);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[16..], dataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[24..], dataLength);
        _transport.Write(buffer);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendDoneRequest()
    {
        Span<byte> buffer = stackalloc byte[SaharaDoneRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaDoneRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaDoneRequest.Length);
        _transport.Write(buffer);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendResetRequest()
    {
        Span<byte> buffer = stackalloc byte[SaharaResetRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaResetRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaResetRequest.Length);
        _transport.Write(buffer);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendResetStateMachineRequest()
    {
        Span<byte> buffer = stackalloc byte[SaharaResetStateMachineRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaResetStateMachineRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaResetStateMachineRequest.Length);
        _transport.Write(buffer);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendMemoryDebug32BitRequest(uint memoryTableAddress, uint memoryTableLength)
    {
        Span<byte> buffer = stackalloc byte[SaharaMemoryDebug32BitRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaMemoryDebug32BitRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaMemoryDebug32BitRequest.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], memoryTableAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], memoryTableLength);
        _transport.Write(buffer);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendMemoryDebug64BitRequest(ulong memoryTableAddress, ulong memoryTableLength)
    {
        Span<byte> buffer = stackalloc byte[SaharaMemoryDebug64BitRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaMemoryDebug64BitRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaMemoryDebug64BitRequest.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[8..], memoryTableAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[16..], memoryTableLength);
        _transport.Write(buffer);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendMemoryRead32BitRequest(uint memoryAddress, uint memoryLength)
    {
        Span<byte> buffer = stackalloc byte[SaharaMemoryRead32BitRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaMemoryRead32BitRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaMemoryRead32BitRequest.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], memoryAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], memoryLength);
        _transport.Write(buffer);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendMemoryRead64BitRequest(ulong memoryAddress, ulong memoryLength)
    {
        Span<byte> buffer = stackalloc byte[SaharaMemoryRead64BitRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaMemoryRead64BitRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaMemoryRead64BitRequest.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[8..], memoryAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[16..], memoryLength);
        _transport.Write(buffer);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SendSwitchModeRequest(SaharaMode mode)
    {
        Span<byte> buffer = stackalloc byte[SaharaSwitchModeRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)SaharaSwitchModeRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], SaharaSwitchModeRequest.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], (uint)mode);
        _transport.Write(buffer);
    }
}