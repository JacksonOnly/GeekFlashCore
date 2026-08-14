namespace GeekFlashCore.Protocol.Qcom.Abstractions;

public ref struct SaharaHelloRequest()
{
    public const SaharaCommand Command = SaharaCommand.Hello;
    public const int Length = SaharaPacketConstants.HelloSize;
    public uint Version;
    public uint VersionSupported;
    public uint CommandPacketLength;
    public SaharaMode Mode;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
    public uint Reserved4;
    public uint Reserved5;

    // PS: Rider的代码生成真的很好
    public SaharaHelloRequest(uint version, uint versionSupported, uint commandPacketLength, SaharaMode mode,
        uint reserved0, uint reserved1, uint reserved2, uint reserved3, uint reserved4, uint reserved5) : this()
    {
        Version = version;
        VersionSupported = versionSupported;
        CommandPacketLength = commandPacketLength;
        Mode = mode;
        Reserved0 = reserved0;
        Reserved1 = reserved1;
        Reserved2 = reserved2;
        Reserved3 = reserved3;
        Reserved4 = reserved4;
        Reserved5 = reserved5;
    }
}

public ref struct SaharaHelloResponse()
{
    public const SaharaCommand Command = SaharaCommand.HelloResponse;
    public const int Length = SaharaPacketConstants.HelloResponseSize;
    public uint Version;
    public uint VersionSupported;
    public SaharaStatus Status;
    public SaharaMode Mode;
    public uint Reserved0;
    public uint Reserved1;
    public uint Reserved2;
    public uint Reserved3;
    public uint Reserved4;
    public uint Reserved5;

    public SaharaHelloResponse(uint version, uint versionSupported, SaharaStatus status, SaharaMode mode,
        uint reserved0, uint reserved1, uint reserved2, uint reserved3, uint reserved4, uint reserved5) : this()
    {
        Version = version;
        VersionSupported = versionSupported;
        Status = status;
        Mode = mode;
        Reserved0 = reserved0;
        Reserved1 = reserved1;
        Reserved2 = reserved2;
        Reserved3 = reserved3;
        Reserved4 = reserved4;
        Reserved5 = reserved5;
    }
}

public ref struct SaharaExecuteRequest()
{
    public const SaharaCommand Command = SaharaCommand.Execute;
    public const int Length = SaharaPacketConstants.ExecuteSize;
    public SaharaExecuteCommand ClientCommand;

    public SaharaExecuteRequest(SaharaExecuteCommand clientCommand) : this()
    {
        ClientCommand = clientCommand;
    }
}

public ref struct SaharaExecuteResponse()
{
    public const SaharaCommand Command = SaharaCommand.ExecuteResponse;
    public const int Length = SaharaPacketConstants.ExecuteResponseSize;
    public SaharaExecuteCommand ClientCommand;
    public uint DataLength;

    public SaharaExecuteResponse(SaharaExecuteCommand clientCommand, uint dataLength) : this()
    {
        ClientCommand = clientCommand;
        DataLength = dataLength;
    }
}

public ref struct SaharaExecuteDataResponse()
{
    public const SaharaCommand Command = SaharaCommand.ExecuteDataResponse;
    public const int Length = SaharaPacketConstants.ExecuteDataResponseSize;
    public SaharaExecuteCommand ClientCommand;

    public SaharaExecuteDataResponse(SaharaExecuteCommand clientCommand) : this()
    {
        ClientCommand = clientCommand;
    }
}

public ref struct SaharaReadData32BitRequest()
{
    public const SaharaCommand Command = SaharaCommand.ReadData32Bit;
    public const int Length = SaharaPacketConstants.ReadData32BitSize;
    public uint ImageId;
    public uint DataOffset;
    public uint DataLength;

    public SaharaReadData32BitRequest(uint imageId, uint dataOffset, uint dataLength) : this()
    {
        ImageId = imageId;
        DataOffset = dataOffset;
        DataLength = dataLength;
    }
}

public ref struct SaharaReadData64BitRequest()
{
    public const SaharaCommand Command = SaharaCommand.ReadData64Bit;
    public const int Length = SaharaPacketConstants.ReadData64BitSize;
    public ulong ImageId;
    public ulong DataOffset;
    public ulong DataLength;

    public SaharaReadData64BitRequest(ulong imageId, ulong dataOffset, ulong dataLength) : this()
    {
        ImageId = imageId;
        DataOffset = dataOffset;
        DataLength = dataLength;
    }
}

public ref struct SaharaEndImageTxResponse()
{
    public const SaharaCommand Command = SaharaCommand.EndImageTransmit;
    public const int Length = SaharaPacketConstants.EndImageTransmitSize;
    public uint ImageId;
    public SaharaStatus Status;

    public SaharaEndImageTxResponse(uint imageId, SaharaStatus status) : this()
    {
        ImageId = imageId;
        Status = status;
    }
}

public ref struct SaharaDoneRequest()
{
    public const SaharaCommand Command = SaharaCommand.Done;
    public const int Length = SaharaPacketConstants.DoneSize;
}

public ref struct SaharaDoneResponse()
{
    public const SaharaCommand Command = SaharaCommand.DoneResponse;
    public const int Length = SaharaPacketConstants.DoneResponseSize;
    public SaharaMode ImageTxStatus;

    public SaharaDoneResponse(SaharaMode imageTxStatus) : this()
    {
        ImageTxStatus = imageTxStatus;
    }
}

public ref struct SaharaResetRequest()
{
    public const SaharaCommand Command = SaharaCommand.Reset;
    public const int Length = SaharaPacketConstants.ResetSize;
}

public ref struct SaharaResetResponse()
{
    public const SaharaCommand Command = SaharaCommand.ResetResponse;
    public const int Length = SaharaPacketConstants.ResetResponseSize;
}

public ref struct SaharaResetStateMachineRequest()
{
    public const SaharaCommand Command = SaharaCommand.ResetStateMachine;
    public const int Length = SaharaPacketConstants.ResetStateMachineSize;
}

public ref struct SaharaMemoryDebug32BitRequest()
{
    public const SaharaCommand Command = SaharaCommand.MemoryDebug32Bit;
    public const int Length = SaharaPacketConstants.MemoryDebug32BitSize;
    public uint MemoryTableAddress;
    public uint MemoryTableLength;

    public SaharaMemoryDebug32BitRequest(uint memoryTableAddress, uint memoryTableLength) : this()
    {
        MemoryTableAddress = memoryTableAddress;
        MemoryTableLength = memoryTableLength;
    }
}

public ref struct SaharaMemoryDebug64BitRequest()
{
    public const SaharaCommand Command = SaharaCommand.MemoryDebug64Bit;
    public const int Length = SaharaPacketConstants.MemoryDebug64BitSize;
    public ulong MemoryTableAddress;
    public ulong MemoryTableLength;

    public SaharaMemoryDebug64BitRequest(ulong memoryTableAddress, ulong memoryTableLength) : this()
    {
        MemoryTableAddress = memoryTableAddress;
        MemoryTableLength = memoryTableLength;
    }
}

public ref struct SaharaMemoryRead32BitRequest()
{
    public const SaharaCommand Command = SaharaCommand.MemoryRead32Bit;
    public const int Length = SaharaPacketConstants.MemoryRead32BitSize;
    public uint MemoryAddress;
    public uint MemoryLength;

    public SaharaMemoryRead32BitRequest(uint memoryAddress, uint memoryLength) : this()
    {
        MemoryAddress = memoryAddress;
        MemoryLength = memoryLength;
    }
}

public ref struct SaharaMemoryRead64BitRequest()
{
    public const SaharaCommand Command = SaharaCommand.MemoryRead64Bit;
    public const int Length = SaharaPacketConstants.MemoryRead64BitSize;
    public ulong MemoryAddress;
    public ulong MemoryLength;

    public SaharaMemoryRead64BitRequest(ulong memoryAddress, ulong memoryLength) : this()
    {
        MemoryAddress = memoryAddress;
        MemoryLength = memoryLength;
    }
}

public ref struct SaharaReadyResponse()
{
    public const SaharaCommand Command = SaharaCommand.ReadyResponse;
    public const int Length = SaharaPacketConstants.ReadyResponseSize;
}

public ref struct SaharaSwitchModeRequest()
{
    public const SaharaCommand Command = SaharaCommand.SwitchMode;
    public const int Length = SaharaPacketConstants.SwitchModeSize;
    public SaharaMode Mode;

    public SaharaSwitchModeRequest(SaharaMode mode) : this()
    {
        Mode = mode;
    }
}