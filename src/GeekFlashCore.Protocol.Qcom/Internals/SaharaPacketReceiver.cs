using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Exceptions;
using GeekFlashCore.Transport.Abstractions;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal readonly struct SaharaPacketReceiver
{
    private const int PacketHeaderLength = 8;

    private readonly ILogger _logger;
    private readonly ITransport _transport;

    public SaharaPacketReceiver(ILogger logger, ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _logger = logger;
        _transport = transport;
    }

    public int ReceivePacketHeader(out SaharaCommand command)
    {
        Span<byte> headerBuffer = stackalloc byte[PacketHeaderLength];
        int headerRead = _transport.ReadExact(headerBuffer);
        if (headerRead != PacketHeaderLength)
            throw new EndOfStreamException(Strings.SaharaNak_TimeoutRx);
        uint commandRaw = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.Slice(0, 4));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.Slice(4, 4));
        if (commandRaw == 0x6D783F3C)
            throw new TargetAlreadyIsFirehoseException();
        if (length < PacketHeaderLength || length > int.MaxValue)
            throw InvalidPacketLength(length);
        int remainingDataLength = (int)length - PacketHeaderLength;
        command = (SaharaCommand)commandRaw;
        _logger.Debug(Strings.Qcom_LogSaharaReceivePacket, command.ToName());
        return remainingDataLength;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadHelloRequest(out SaharaHelloRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[SaharaHelloRequest.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaHelloRequest.Length);
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
        Span<byte> buffer = stackalloc byte[SaharaHelloResponse.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaHelloResponse.Length);
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
        Span<byte> buffer = stackalloc byte[SaharaExecuteRequest.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaExecuteRequest.Length);
        request = new SaharaExecuteRequest
        (
            (SaharaExecuteCommand)BinaryPrimitives.ReadUInt32LittleEndian(buffer)
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadExecuteResponse(out SaharaExecuteResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[SaharaExecuteResponse.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaExecuteResponse.Length);
        response = new SaharaExecuteResponse
        (
            (SaharaExecuteCommand)BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadExecuteDataResponse(out SaharaExecuteDataResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[SaharaExecuteDataResponse.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaExecuteDataResponse.Length);
        response = new SaharaExecuteDataResponse
        (
            (SaharaExecuteCommand)BinaryPrimitives.ReadUInt32LittleEndian(buffer)
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadReadData32BitRequest(out SaharaReadData32BitRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[SaharaReadData32BitRequest.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaReadData32BitRequest.Length);
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
        Span<byte> buffer = stackalloc byte[SaharaReadData64BitRequest.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaReadData64BitRequest.Length);
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
        Span<byte> buffer = stackalloc byte[SaharaEndImageTxResponse.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaEndImageTxResponse.Length);
        response = new SaharaEndImageTxResponse
        (
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            (SaharaStatus)BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadDoneRequest(out SaharaDoneRequest request, int length)
    {
        ValidateBodyLength(length, SaharaDoneRequest.Length);
        request = new SaharaDoneRequest();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadDoneResponse(out SaharaDoneResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[SaharaDoneResponse.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaDoneResponse.Length);
        response = new SaharaDoneResponse
        (
            (SaharaMode)BinaryPrimitives.ReadUInt32LittleEndian(buffer)
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadResetRequest(out SaharaResetRequest request, int length)
    {
        ValidateBodyLength(length, SaharaResetRequest.Length);
        request = new SaharaResetRequest();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadResetResponse(out SaharaResetResponse response, int length)
    {
        ValidateBodyLength(length, SaharaResetResponse.Length);
        response = new SaharaResetResponse();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadResetStateMachineRequest(out SaharaResetStateMachineRequest request, int length)
    {
        ValidateBodyLength(length, SaharaResetStateMachineRequest.Length);
        request = new SaharaResetStateMachineRequest();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadMemoryDebug32BitRequest(out SaharaMemoryDebug32BitRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[SaharaMemoryDebug32BitRequest.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaMemoryDebug32BitRequest.Length);
        request = new SaharaMemoryDebug32BitRequest
        (
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadMemoryDebug64BitRequest(out SaharaMemoryDebug64BitRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[SaharaMemoryDebug64BitRequest.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaMemoryDebug64BitRequest.Length);
        request = new SaharaMemoryDebug64BitRequest
        (
            BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadMemoryRead32BitRequest(out SaharaMemoryRead32BitRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[SaharaMemoryRead32BitRequest.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaMemoryRead32BitRequest.Length);
        request = new SaharaMemoryRead32BitRequest
        (
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadMemoryRead64BitRequest(out SaharaMemoryRead64BitRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[SaharaMemoryRead64BitRequest.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaMemoryRead64BitRequest.Length);
        request = new SaharaMemoryRead64BitRequest
        (
            BinaryPrimitives.ReadUInt64LittleEndian(buffer),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..])
        );
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadReadyResponse(out SaharaReadyResponse response, int length)
    {
        ValidateBodyLength(length, SaharaReadyResponse.Length);
        response = new SaharaReadyResponse();
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadSwitchModeRequest(out SaharaSwitchModeRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[SaharaSwitchModeRequest.Length - PacketHeaderLength];
        ReadBody(buffer, length, SaharaSwitchModeRequest.Length);
        request = new SaharaSwitchModeRequest
        (
            (SaharaMode)BinaryPrimitives.ReadUInt32LittleEndian(buffer)
        );
    }

    private void ReadBody(Span<byte> buffer, int actualBodyLength, int packetLength)
    {
        ValidateBodyLength(actualBodyLength, packetLength);
        int read = _transport.ReadExact(buffer);
        if (read != buffer.Length)
            throw new EndOfStreamException(Strings.SaharaNak_TimeoutRx);
    }

    private static void ValidateBodyLength(int actualBodyLength, int packetLength)
    {
        int expectedBodyLength = packetLength - PacketHeaderLength;
        if (actualBodyLength != expectedBodyLength)
            throw InvalidPacketLength(actualBodyLength + PacketHeaderLength);
    }

    private static SaharaProtocolException InvalidPacketLength(long length) =>
        new(Strings.FormatSahara_InvalidPacketLength(length));
}
