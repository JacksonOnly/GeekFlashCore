using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Exceptions;
using GeekFlashCore.Transport.Abstractions;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal readonly struct SaharaPacketReceiver
{
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
        Span<byte> headerBuffer = stackalloc byte[8];
        int headerRead = _transport.ReadExact(headerBuffer);
        uint commandRaw = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.Slice(0, 4));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer.Slice(4, 4));
        if (commandRaw == 0x6D783F3C)
            throw new TargetAlreadyIsFirehoseException();
        int remainingDataLength = (int)(length - headerRead);
        command = (SaharaCommand)commandRaw;
        _logger.Debug("Receive PacketHeader" +
                      " {Command}" +
                      " {PacketLength}" +
                      " {RemainingDataLength}", command.ToName(), length, remainingDataLength);
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
        _logger.Debug("Receive HelloRequest" +
                      " {Version}" +
                      " {VersionSupported}" +
                      " {CommandPacketLength}" +
                      " {Mode}" +
                      " {Reserved0}" +
                      " {Reserved1}" +
                      " {Reserved2}" +
                      " {Reserved3}" +
                      " {Reserved4}" +
                      " {Reserved5}", request.Version, request.VersionSupported, request.CommandPacketLength,
            request.Mode.ToName(), request.Reserved0, request.Reserved1, request.Reserved2, request.Reserved3,
            request.Reserved4, request.Reserved5);
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
        _logger.Debug("Receive HelloResponse" +
                      " {Version}" +
                      " {VersionSupported}" +
                      " {Status}" +
                      " {Mode}" +
                      " {Reserved0}" +
                      " {Reserved1}" +
                      " {Reserved2}" +
                      " {Reserved3}" +
                      " {Reserved4}" +
                      " {Reserved5}", response.Version, response.VersionSupported, response.Status.ToName(),
            response.Mode.ToName(), response.Reserved0, response.Reserved1, response.Reserved2, response.Reserved3,
            response.Reserved4, response.Reserved5);
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
        _logger.Debug("Receive ExecuteRequest" +
                      " {ClientCommand}", request.ClientCommand.ToName());
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
        _logger.Debug("Receive ExecuteResponse" +
                      " {ClientCommand}" +
                      " {DataLength}", response.ClientCommand.ToName(), response.DataLength);
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
        _logger.Debug("Receive ExecuteDataResponse" +
                      " {ClientCommand}", response.ClientCommand.ToName());
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
        _logger.Debug("Receive ReadData32BitRequest" +
                      " {ImageId}" +
                      " {DataOffset}" +
                      " {DataLength}", request.ImageId, request.DataOffset, request.DataLength);
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
        _logger.Debug("Receive ReadData64BitRequest" +
                      " {ImageId}" +
                      " {DataOffset}" +
                      " {DataLength}", request.ImageId, request.DataOffset, request.DataLength);
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
        _logger.Debug("Receive EndImageTxResponse" +
                      " {ImageId}" +
                      " {Status}", response.ImageId, response.Status.ToName());
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadDoneRequest(out SaharaDoneRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaDoneRequest();
        _logger.Debug("Receive DoneRequest");
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
        _logger.Debug("Receive DoneResponse" +
                      " {ImageTxStatus}", response.ImageTxStatus.ToName());
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadResetRequest(out SaharaResetRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaResetRequest();
        _logger.Debug("Receive ResetRequest");
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadResetResponse(out SaharaResetResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        response = new SaharaResetResponse();
        _logger.Debug("Receive ResetResponse");
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadResetStateMachineRequest(out SaharaResetStateMachineRequest request, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        request = new SaharaResetStateMachineRequest();
        _logger.Debug("Receive ResetStateMachineRequest");
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
        _logger.Debug("Receive MemoryDebug32BitRequest" +
                      " {MemoryTableAddress}" +
                      " {MemoryTableLength}", request.MemoryTableAddress, request.MemoryTableLength);
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
        _logger.Debug("Receive MemoryDebug64BitRequest" +
                      " {MemoryTableAddress}" +
                      " {MemoryTableLength}", request.MemoryTableAddress, request.MemoryTableLength);
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
        _logger.Debug("Receive MemoryRead32BitRequest" +
                      " {MemoryAddress}" +
                      " {MemoryLength}", request.MemoryAddress, request.MemoryLength);
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
        _logger.Debug("Receive MemoryRead64BitRequest" +
                      " {MemoryAddress}" +
                      " {MemoryLength}", request.MemoryAddress, request.MemoryLength);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadReadyResponse(out SaharaReadyResponse response, int length)
    {
        Span<byte> buffer = stackalloc byte[length];
        _transport.ReadExact(buffer);
        response = new SaharaReadyResponse();
        _logger.Debug("Receive ReadyResponse");
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
        _logger.Debug("Receive SwitchModeRequest" +
                      " {Mode}", request.Mode.ToName());
    }
}