using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Models;
using GeekFlashCore.Transport.Abstractions;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom.Internals;

internal class SaharaProtocol : IDisposable
{
    private const int MaxRamDumpRead = 0x100000;
    private const long ProgressReportInterval = 0x100000;
    private const int MemoryTableEntrySize32Bit = 52;
    private const int MemoryTableEntrySize64Bit = 64;
    private const int MemoryRegionStringLength = 20;

    private bool _disposed;
    private readonly ITransport _transport;
    private readonly ILogger _logger = Log.ForContext<SaharaProtocol>();
    private readonly SaharaPacketReceiver _receiver;
    private readonly SaharaPacketSender _sender;
    private SaharaTargetInfo _targetInfo;

    public SaharaTargetInfo TargetInfo => _targetInfo;

    public bool IsConnected { get; private set; }

    public SaharaProtocol(ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        if (!transport.IsOpen)
            throw new InvalidOperationException(Strings.Common_TransportIsClosed);
        _sender = new SaharaPacketSender(Log.ForContext<SaharaPacketSender>(), _transport);
        _receiver = new SaharaPacketReceiver(Log.ForContext<SaharaPacketReceiver>(), _transport);
        _targetInfo = new SaharaTargetInfo();
    }

    public void Connect(IProgress<ProgressRecord>? progress = null)
    {
        ThrowIfDisposed();
        _logger.Information(Strings.FormatCommon_ConnectingToMode(nameof(SaharaProtocol)));
        progress?.Report(new ProgressRecord(3, 0, Strings.Progress_Connecting));
        ReceiveHello(out var helloRequest);
        SetTargetInfoFromHelloRequest(in helloRequest);
        SendHelloResponse(SaharaMode.Command);
        progress?.Report(new ProgressRecord(3, 1, Strings.Progress_HelloReceived));
        _logger.Information(Strings.Common_ReadingTargetInfo);
        progress?.Report(new ProgressRecord(3, 2, Strings.Progress_ReadingTargetInfo));
        ReadTargetInfo();
        IsConnected = true;
        progress?.Report(new ProgressRecord(3, 3, Strings.Progress_Connected));
        _logger.Information("TargetInfo: {TargetInfo}", TargetInfo);
    }

    public void UploadImage(
        IReadOnlyList<SaharaImageEntry> saharaImages,
        Action<SaharaMemoryRegion, ReadOnlyMemory<byte>>? onMemoryData = null,
        IProgress<ProgressRecord>? progress = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(saharaImages);
        if (saharaImages.Count == 0)
            throw new ArgumentException(Strings.Sahara_EmptyImageList, nameof(saharaImages));
        if (_targetInfo.Mode != SaharaMode.ImageTxPending)
            SwitchModeTo(SaharaMode.ImageTxPending);

        var imageDict = saharaImages.ToDictionary(static image => image.Id);
        long totalImageBytes = saharaImages.Sum(static image => image.Length);
        long lastReportedBytes = 0;
        int currentImageId = -1;
        Stream? stream = null;
        long totalBytes = 0;
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var bodyLength = _receiver.ReceivePacketHeader(out var cmd);

            switch (cmd)
            {
                case SaharaCommand.ReadData32Bit:
                case SaharaCommand.ReadData64Bit:
                {
                    long dataOffset;
                    int dataLength;
                    int imageId;

                    if (cmd == SaharaCommand.ReadData32Bit)
                    {
                        _receiver.ReadReadData32BitRequest(out var rd, bodyLength);
                        dataOffset = rd.DataOffset;
                        dataLength = (int)rd.DataLength;
                        imageId = (int)rd.ImageId;
                    }
                    else
                    {
                        _receiver.ReadReadData64BitRequest(out var rd, bodyLength);
                        dataOffset = (long)rd.DataOffset;
                        dataLength = (int)rd.DataLength;
                        imageId = (int)rd.ImageId;
                    }

                    if (!imageDict.TryGetValue(imageId, out var image))
                        throw new FileNotFoundException(Strings.FormatSahara_ImageNotFound(nameof(imageId)));
                    currentImageId = imageId;
                    stream ??= image.DataSource.OpenStream();

                    _logger.Debug("Transfer image {ImageId} offset {Offset} length {Length}",
                        imageId, dataOffset, dataLength);
                    byte[] rented = ArrayPool<byte>.Shared.Rent(dataLength);
                    try
                    {
                        if (dataOffset >= image.Length)
                        {
                            Array.Fill(rented, (byte)0xFF, 0, dataLength);
                        }
                        else
                        {
                            stream.Seek(dataOffset, SeekOrigin.Begin);
                            int read = 0;
                            while (read < dataLength)
                            {
                                int current = stream.Read(rented, read, dataLength - read);
                                if (current == 0) break;
                                read += current;
                            }

                            if (read < dataLength)
                                Array.Fill(rented, (byte)0xFF, read, dataLength - read);
                        }

                        _transport.Write(rented, 0, dataLength);
                        totalBytes += dataLength;
                        if (progress != null && totalBytes - lastReportedBytes >= ProgressReportInterval)
                        {
                            lastReportedBytes = totalBytes;
                            progress.Report(new ProgressRecord(totalImageBytes, totalBytes,
                                Strings.FormatProgress_UploadingImage(currentImageId)));
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }

                    break;
                }
                case SaharaCommand.MemoryDebug32Bit:
                case SaharaCommand.MemoryDebug64Bit:
                {
                    _logger.Warning("Target entered memory debug mode, dumping memory");
                    progress?.Report(new ProgressRecord(totalImageBytes, totalBytes, Strings.Progress_MemoryDump));
                    bool is64Bit = cmd == SaharaCommand.MemoryDebug64Bit;
                    if (is64Bit)
                    {
                        _receiver.ReadMemoryDebug64BitRequest(out var memoryDebug, bodyLength);
                        DumpMemoryCore(memoryDebug.MemoryTableAddress, memoryDebug.MemoryTableLength,
                            true, onMemoryData);
                    }
                    else
                    {
                        _receiver.ReadMemoryDebug32BitRequest(out var memoryDebug, bodyLength);
                        DumpMemoryCore(memoryDebug.MemoryTableAddress, memoryDebug.MemoryTableLength,
                            false, onMemoryData);
                    }

                    stream?.Dispose();
                    stream = null;
                    break;
                }
                case SaharaCommand.EndImageTransmit:
                {
                    ThrowIfGetEndImageTxError(cmd, bodyLength);
                    _sender.SendDoneRequest();
                    var doneResponseLength = _receiver.ReceivePacketHeader(out var doneCmd);
                    ThrowIfGetInvalidResponse(doneCmd, SaharaCommand.DoneResponse, doneResponseLength);
                    _receiver.ReadDoneResponse(out var done, doneResponseLength);
                    if (done.ImageTxStatus == SaharaMode.ImageTxComplete)
                    {
                        _targetInfo.Mode = SaharaMode.ImageTxComplete;
                        LogThroughput("UploadImage", totalBytes, stopwatch.Elapsed);
                        progress?.Report(new ProgressRecord(totalImageBytes, totalBytes,
                            Strings.Progress_UploadComplete));
                        return;
                    }

                    if (done.ImageTxStatus != SaharaMode.ImageTxPending)
                    {
                        throw new SaharaProtocolException(
                            Strings.FormatSahara_UnexpectedStatus(done.ImageTxStatus.ToName()));
                    }

                    stream?.Dispose();
                    stream = null;
                    if (progress != null && totalBytes != lastReportedBytes)
                    {
                        lastReportedBytes = totalBytes;
                        progress.Report(new ProgressRecord(totalImageBytes, totalBytes,
                            Strings.FormatProgress_UploadingImage(currentImageId)));
                    }

                    ReceiveHello(out var hello, false);
                    SetTargetInfoFromHelloRequest(in hello);
                    SendHelloResponse(SaharaMode.ImageTxPending);
                    break;
                }

                default:
                    throw new SaharaProtocolException(Strings.FormatSahara_UnexpectedCommand(cmd));
            }
        }
    }

    public ReadOnlyMemory<byte> ExecuteCommand(SaharaExecuteCommand command)
    {
        ThrowIfDisposed();
        ThrowIfNotConnected();
        _logger.Information("Execute command {Command}", command.ToName());
        return ExecuteData(command, static data => data.ToArray());
    }

    public void SwitchMode(SaharaMode mode)
    {
        ThrowIfDisposed();
        ThrowIfNotConnected();
        SwitchModeTo(mode);
    }

    public void Reset()
    {
        ThrowIfDisposed();
        _logger.Information("Sending reset request");
        _sender.SendResetRequest();
        while (true)
        {
            var bodyLength = _receiver.ReceivePacketHeader(out var command);
            if (command == SaharaCommand.ResetResponse)
            {
                _receiver.ReadResetResponse(out _, bodyLength);
                IsConnected = false;
                _logger.Information("Reset acknowledged");
                return;
            }

            _logger.Warning("Waiting for ResetResponse, received {Command}", command.ToName());
        }
    }

    public IReadOnlyList<SaharaMemoryRegion> DumpMemory(
        Action<SaharaMemoryRegion, ReadOnlyMemory<byte>>? onData = null,
        bool resetAfterDump = false)
    {
        ThrowIfDisposed();
        _logger.Information("Entering memory debug mode");
        ReceiveHello(out var hello);
        SetTargetInfoFromHelloRequest(in hello);
        _sender.SendHelloResponse(hello.Version, hello.Version, SaharaStatus.StatusSuccess,
            _targetInfo.Mode, 1, 2, 3, 4, 5, 6);
        IsConnected = true;

        var bodyLength = _receiver.ReceivePacketHeader(out var command);
        IReadOnlyList<SaharaMemoryRegion> regions;
        switch (command)
        {
            case SaharaCommand.MemoryDebug32Bit:
            {
                _receiver.ReadMemoryDebug32BitRequest(out var memoryDebug, bodyLength);
                regions = DumpMemoryCore(memoryDebug.MemoryTableAddress, memoryDebug.MemoryTableLength,
                    false, onData);
                break;
            }
            case SaharaCommand.MemoryDebug64Bit:
            {
                _receiver.ReadMemoryDebug64BitRequest(out var memoryDebug, bodyLength);
                regions = DumpMemoryCore(memoryDebug.MemoryTableAddress, memoryDebug.MemoryTableLength,
                    true, onData);
                break;
            }
            default:
                ThrowIfGetInvalidResponse(command, SaharaCommand.MemoryDebug32Bit, bodyLength);
                throw new SaharaProtocolException(Strings.FormatSahara_UnexpectedCommand(command));
        }

        if (resetAfterDump)
            Reset();
        else
            SwitchModeTo(SaharaMode.ImageTxPending);
        return regions;
    }

    private IReadOnlyList<SaharaMemoryRegion> DumpMemoryCore(
        ulong tableAddress, ulong tableLength, bool is64Bit,
        Action<SaharaMemoryRegion, ReadOnlyMemory<byte>>? onData)
    {
        int entrySize = is64Bit ? MemoryTableEntrySize64Bit : MemoryTableEntrySize32Bit;
        _logger.Information("Memory debug {Width}-bit, table address 0x{Address:X}, length {Length}",
            is64Bit ? 64 : 32, tableAddress, tableLength);

        if (tableLength % (ulong)entrySize != 0 || tableLength > MaxRamDumpRead)
        {
            _logger.Error("Invalid memory table length {TableLength} for {EntrySize}-byte entries",
                tableLength, entrySize);
            Reset();
            throw new SaharaProtocolException(Strings.FormatSahara_InvalidMemoryTable(tableLength));
        }

        var regions = new List<SaharaMemoryRegion>(
            tableLength == 0 ? 0 : (int)(tableLength / (ulong)entrySize));
        if (tableLength > 0)
        {
            SendMemoryReadRequest(tableAddress, tableLength, is64Bit);
            byte[] table = ArrayPool<byte>.Shared.Rent((int)tableLength);
            try
            {
                _transport.ReadExact(table.AsSpan(0, (int)tableLength));
                ParseMemoryTable(table.AsSpan(0, (int)tableLength), is64Bit, regions);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(table);
            }
        }

        foreach (var region in regions)
        {
            _logger.Information(
                "Memory region: base 0x{Base:X}, length 0x{Length:X}, filename '{FileName}', description '{Description}'",
                region.BaseAddress, region.Length, region.FileName, region.Description);
        }

        long totalBytes = 0;
        var stopwatch = Stopwatch.StartNew();
        for (int i = regions.Count - 1; i >= 0; i--)
        {
            var region = regions[i];
            ulong address = region.BaseAddress;
            ulong remaining = region.Length;
            while (remaining > 0)
            {
                uint chunkLength = (uint)Math.Min(remaining, MaxRamDumpRead);
                SendMemoryReadRequest(address, chunkLength, is64Bit);
                byte[] rented = ArrayPool<byte>.Shared.Rent((int)chunkLength);
                try
                {
                    _transport.ReadExact(rented.AsSpan(0, (int)chunkLength));
                    onData?.Invoke(region, new ReadOnlyMemory<byte>(rented, 0, (int)chunkLength));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                address += chunkLength;
                remaining -= chunkLength;
                totalBytes += chunkLength;
            }

            _logger.Information("Dumped region '{FileName}' ({Length} bytes)", region.FileName, region.Length);
        }

        LogThroughput("MemoryDump", totalBytes, stopwatch.Elapsed);
        return regions;
    }

    private static void ParseMemoryTable(ReadOnlySpan<byte> table, bool is64Bit, List<SaharaMemoryRegion> regions)
    {
        int entrySize = is64Bit ? MemoryTableEntrySize64Bit : MemoryTableEntrySize32Bit;
        for (int offset = 0; offset + entrySize <= table.Length; offset += entrySize)
        {
            ReadOnlySpan<byte> entry = table.Slice(offset, entrySize);
            ulong baseAddress;
            ulong length;
            ReadOnlySpan<byte> description;
            ReadOnlySpan<byte> fileName;
            if (is64Bit)
            {
                baseAddress = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(8));
                length = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(16));
                description = entry.Slice(24, MemoryRegionStringLength);
                fileName = entry.Slice(44, MemoryRegionStringLength);
            }
            else
            {
                baseAddress = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4));
                length = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8));
                description = entry.Slice(12, MemoryRegionStringLength);
                fileName = entry.Slice(32, MemoryRegionStringLength);
            }

            regions.Add(new SaharaMemoryRegion(baseAddress, length,
                DecodeMemoryTableString(fileName), DecodeMemoryTableString(description)));
        }
    }

    private static string DecodeMemoryTableString(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.IndexOf((byte)0);
        if (length < 0)
            length = bytes.Length;
        return Encoding.ASCII.GetString(bytes[..length]);
    }

    private void SendMemoryReadRequest(ulong address, ulong length, bool is64Bit)
    {
        if (is64Bit)
            _sender.SendMemoryRead64BitRequest(address, length);
        else
            _sender.SendMemoryRead32BitRequest((uint)address, (uint)length);
    }

    private T ExecuteData<T>(SaharaExecuteCommand executeCommand, Func<ReadOnlyMemory<byte>, T> processData)
    {
        _sender.SendExecuteRequest(executeCommand);
        var bodyLength = _receiver.ReceivePacketHeader(out var command);
        ThrowIfGetInvalidResponse(command, SaharaCommand.ExecuteResponse, bodyLength);
        _receiver.ReadExecuteResponse(out var executeResponse, bodyLength);
        var dataLength = (int)executeResponse.DataLength;
        _sender.SendExecuteDataResponse(executeCommand);
        byte[] rented = ArrayPool<byte>.Shared.Rent(dataLength);
        try
        {
            _transport.ReadExact(rented.AsSpan(0, dataLength));
            return processData(new ReadOnlyMemory<byte>(rented, 0, dataLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private ReadOnlyMemory<byte> ExecuteReadCaHash()
    {
        return ExecuteData(SaharaExecuteCommand.ReadOemPkHash,
            static data => data[..48].ToArray());
    }

    private ulong ExecuteReadSblVersion()
    {
        return ExecuteData(SaharaExecuteCommand.ReadSblVersion,
            static data => data.Length == 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(data.Span)
                : BinaryPrimitives.ReadUInt64LittleEndian(data.Span));
    }

    private ulong ExecuteReadSerial()
    {
        return ExecuteData(SaharaExecuteCommand.ReadSerialNum,
            static data => data.Length == 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(data.Span)
                : BinaryPrimitives.ReadUInt64LittleEndian(data.Span));
    }

    private SaharaMsmHwInfo ExecuteReadMsmHwIdV2()
    {
        return ExecuteData(SaharaExecuteCommand.ReadMsmHwId,
            static data =>
            {
                var span = data.Span;
                var hwId = data.Length == 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(span)
                    : BinaryPrimitives.ReadUInt64LittleEndian(span);
                return new SaharaMsmHwInfo()
                {
                    AntiRollbackVersion = null,
                    SocHwVersion = null,
                    ModelId = (uint?)(hwId & 0xFFFF),
                    OemId = (uint?)(hwId >> 2 * 8 & 0xFFFF),
                    MsmId = (uint?)(hwId >> 4 * 8 & 0xFFFFFFFF),
                };
            });
    }

    private SaharaMsmHwInfo ExecuteReadMsmHwIdV3()
    {
        return ExecuteData(SaharaExecuteCommand.ReadMsmHwIdV3,
            static data =>
            {
                var span = data.Span;
                var info = new SaharaMsmHwInfo();
                if (data.Length >= 4)
                    info.AntiRollbackVersion = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2));
                if (data.Length >= 0x2C)
                {
                    info.SocHwVersion = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x20, 4));
                    info.MsmId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x24, 4));
                    info.OemId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x28, 4));
                    info.ModelId = null;
                }

                return info;
            });
    }

    private void ReadTargetInfo()
    {
        if (_targetInfo.Mode == SaharaMode.Command)
        {
            _targetInfo.Serial = ExecuteReadSerial();
            _targetInfo.CaHash = ExecuteReadCaHash();
            if (_targetInfo.Version > 3)
            {
                _targetInfo.MsmHwInfo = ExecuteReadMsmHwIdV3();
            }
            else
            {
                _targetInfo.SblVersion = ExecuteReadSblVersion();
                _targetInfo.MsmHwInfo = ExecuteReadMsmHwIdV2();
            }
        }
        else
            throw new SaharaProtocolException(Strings.FormatSahara_InvalidMode(_targetInfo.Mode, SaharaMode.Command));
    }

    private void SwitchModeTo(SaharaMode mode)
    {
        _logger.Information("Switching mode to {Mode}", mode.ToName());
        _sender.SendSwitchModeRequest(mode);
        ReceiveHello(out _, false);
        _sender.SendHelloResponse(_targetInfo.Version, _targetInfo.Version,
            SaharaStatus.StatusSuccess, mode, 1, 2, 3, 4, 5, 6);
        _targetInfo.Mode = mode;
    }

    private void SendHelloResponse(SaharaMode mode)
    {
        _sender.SendHelloResponse(_targetInfo.Version, _targetInfo.Version,
            SaharaStatus.StatusSuccess, mode, 1, 2, 3, 4, 5, 6);
        var bodyLength = _receiver.ReceivePacketHeader(out var command);
        ThrowIfGetInvalidResponse(command, SaharaCommand.ReadyResponse, bodyLength);
        _targetInfo.Mode = mode;
    }

    private void ThrowIfGetEndImageTxError(SaharaCommand cmd, int length)
    {
        if (cmd == SaharaCommand.EndImageTransmit)
        {
            _receiver.ReadEndImageTxResponse(out var imageTxResponse, length);
            if (imageTxResponse.Status != SaharaStatus.StatusSuccess)
            {
                var errorMessage = SaharaStatusMapping.MessageMapping.TryGetValue(imageTxResponse.Status, out var msg)
                    ? msg
                    : imageTxResponse.Status.ToName();
                throw new SaharaProtocolException(errorMessage);
            }
        }
    }

    private void ThrowIfGetInvalidResponse(SaharaCommand cmd, SaharaCommand correctCmd, int bodyLength)
    {
        if (cmd != correctCmd)
        {
            ThrowIfGetEndImageTxError(cmd, bodyLength);
            throw new SaharaProtocolException(Strings.FormatSahara_CantReceivePacket(correctCmd.ToName()));
        }
    }

    private void ReceiveHello(out SaharaHelloRequest request, bool isReset = true)
    {
        int retryCount = 1;
        request = new SaharaHelloRequest();
        do
        {
            _logger.Debug("Receive HelloRequest {RemainingRetryCount}", retryCount);
            try
            {
                var bodyLength = _receiver.ReceivePacketHeader(out var command);
                if (command == SaharaCommand.Hello)
                {
                    _receiver.ReadHelloRequest(out request, bodyLength);
                    return;
                }

                retryCount--;
            }
            catch (Exception e) when (e is TimeoutException or InvalidDataException or ArgumentException)
            {
                _logger.Error(e, "Receive HelloRequest failed");
                _transport.Flush();
                if (isReset)
                    _sender.SendResetStateMachineRequest();
                retryCount--;
            }
        } while (retryCount >= 0);

        throw new TimeoutException(Strings.SaharaNak_TimeoutRx);
    }

    private void SetTargetInfoFromHelloRequest(in SaharaHelloRequest hello)
    {
        _targetInfo.Version = hello.Version;
        _targetInfo.MinimumVersionSupported = hello.VersionSupported;
        _targetInfo.MaximumPacketSizeSupported = hello.CommandPacketLength;
        _targetInfo.Mode = hello.Mode;

        _logger.Debug("SetTargetInfoFromHelloRequest {TargetInfo}", _targetInfo);
    }

    private void LogThroughput(string operation, long bytes, TimeSpan elapsed)
    {
        double mbps = elapsed.TotalSeconds > 0
            ? bytes / elapsed.TotalSeconds / (1024.0 * 1024.0)
            : 0;
        _logger.Information("{Operation} complete: {Bytes:N0} bytes in {Elapsed} ({Throughput:F2} MB/s)",
            operation, bytes, elapsed, mbps);
    }

    private void ThrowIfNotConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException(Strings.Sahara_NotConnected);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SaharaProtocol));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsConnected = false;
        _logger.Verbose("Disposing SaharaProtocol");
        _targetInfo = new SaharaTargetInfo();
        GC.SuppressFinalize(this);
    }
}