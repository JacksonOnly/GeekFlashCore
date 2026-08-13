using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Exceptions;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal readonly ref struct SaharaPacketReceiver
{
    private readonly ITransport _transport;

    public SaharaPacketReceiver(ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
    }

    public int ReceivePacketHeader(out SaharaCommand command)
    {
        Span<byte> headerBuffer = stackalloc byte[8];
        int headerRead = _transport.ReadExact(headerBuffer);
        uint commandRaw = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.Slice(0, 4));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.Slice(4, 4));
        if (commandRaw == 0x6D783F3C)
            throw new TargetAlreadyIsFirehoseException();
        int remainingDataLength = (int)(length - headerRead);
        command = (SaharaCommand)commandRaw;
        return remainingDataLength;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadHelloRequest(out SaharaHelloRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaHelloRequest
        (
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
            (SaharaMode)BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[36..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadHelloResponse(out SaharaHelloResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        response = new SaharaHelloResponse
        (
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
            (SaharaStatus)BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
            (SaharaMode)BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[28..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[36..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadExecuteRequest(out SaharaExecuteRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaExecuteRequest
        (
            (SaharaExecuteCommand)BinaryPrimitives.ReadUInt32LittleEndian(buffer)
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadExecuteResponse(out SaharaExecuteResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        response = new SaharaExecuteResponse
        (
            (SaharaExecuteCommand)BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadExecuteDataResponse(out SaharaExecuteDataResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        response = new SaharaExecuteDataResponse
        (
            (SaharaExecuteCommand)BinaryPrimitives.ReadUInt32LittleEndian(buffer)
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadReadData32BitRequest(out SaharaReadData32BitRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaReadData32BitRequest
        (
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadReadData64BitRequest(out SaharaReadData64BitRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaReadData64BitRequest
        (
            BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadEndImageTxResponse(out SaharaEndImageTxResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        response = new SaharaEndImageTxResponse
        (
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            (SaharaStatus)BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadDoneRequest(out SaharaDoneRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaDoneRequest();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadDoneResponse(out SaharaDoneResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        response = new SaharaDoneResponse
        (
            (SaharaMode)BinaryPrimitives.ReadUInt32LittleEndian(buffer)
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadResetRequest(out SaharaResetRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaResetRequest();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadResetResponse(out SaharaResetResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        response = new SaharaResetResponse();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadResetStateMachineRequest(out SaharaResetStateMachineRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaResetStateMachineRequest();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadMemoryDebug32BitRequest(out SaharaMemoryDebug32BitRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaMemoryDebug32BitRequest
        (
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadMemoryDebug64BitRequest(out SaharaMemoryDebug64BitRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaMemoryDebug64BitRequest
        (
            BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadMemoryRead32BitRequest(out SaharaMemoryRead32BitRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaMemoryRead32BitRequest
        (
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadMemoryRead64BitRequest(out SaharaMemoryRead64BitRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaMemoryRead64BitRequest
        (
            BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadReadyResponse(out SaharaReadyResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        response = new SaharaReadyResponse();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadSwitchModeRequest(out SaharaSwitchModeRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaSwitchModeRequest
        (
            (SaharaMode)BinaryPrimitives.ReadUInt32LittleEndian(buffer)
        );
    }
}