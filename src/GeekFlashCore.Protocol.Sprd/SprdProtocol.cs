using System.Text;
using GeekFlashCore.Protocol.Sprd.Internals;
using GeekFlashCore.Transport.Abstractions;
using Serilog;

namespace GeekFlashCore.Protocol.Sprd;

/// <summary>SPRD BSL facade. Wire I/O is synchronous and every operation shares one session gate.</summary>
public sealed partial class SprdProtocol : ISprdProtocol, IDisposable
{
    private readonly ITransport _transport;
    private readonly SprdProtocolOptions _options;
    private readonly ISprdLoaderProvider? _provider;
    private readonly bool _leaveTransportOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _inside = new();
    private readonly SprdWire _wire;
    private volatile SprdSessionState _state;
    private long _generation;
    private bool _openedHere;
    private bool _needsClose;
    private SprdTargetInfo? _target;
    private IReadOnlyList<SprdPartition>? _partitions;
    private IReadOnlyList<SprdNativePartition>? _nativeRecords;

    /// <summary>Creates a session over SerialPort, LibUsb or a host transport with bounded synchronous writes.</summary>
    /// <remarks>Normal disposal owns the transport unless leaveTransportOpen is true. Wire failure always closes it.</remarks>
    public SprdProtocol(ITransport transport, SprdProtocolOptions? options = null,
        ISprdLoaderProvider? loaderProvider = null, bool leaveTransportOpen = false)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _options = options ?? new(); _options.Validate();
        _options = _options with { KnownPartitions = Array.AsReadOnly(_options.KnownPartitions.ToArray()) };
        _transport = transport; _provider = loaderProvider; _leaveTransportOpen = leaveTransportOpen;
        _wire = new(transport, _options);
    }
    /// <inheritdoc />
    public ProtocolType Type => ProtocolType.Sprd;
    /// <inheritdoc />
    public ITransport Transport => _transport;
    /// <inheritdoc />
    public bool IsConnected => _state == SprdSessionState.StorageReady;
    /// <inheritdoc />
    public SprdSessionState SessionState => _state;
    /// <inheritdoc />
    public long Generation => Interlocked.Read(ref _generation);
    /// <inheritdoc />
    public SprdTargetInfo? TargetInfo => _target;

    private IDisposable Enter(CancellationToken token)
    {
        if (_inside.Value) throw new InvalidOperationException(Strings.Reentry);
        _gate.Wait(token); _inside.Value = true;
        if (_state == SprdSessionState.Disposed)
        { _inside.Value = false; _gate.Release(); throw new ObjectDisposedException(nameof(SprdProtocol)); }
        return new GateLease(this);
    }
    private sealed class GateLease(SprdProtocol owner) : IDisposable
    {
        public void Dispose() { owner._inside.Value = false; owner._gate.Release(); }
    }
    private T Run<T>(Func<T> action, CancellationToken token, int? budget = null)
    {
        using var gate = Enter(token); _wire.Begin(token, budget ?? _options.OperationTimeoutMilliseconds);
        try { var result = action(); _wire.Check(); return result; }
        catch (SprdNativeUnitsRequiredException) when (_state == SprdSessionState.StorageReady) { throw; }
        catch { if ((_wire.HasWritten || _state == SprdSessionState.Connecting) && _state != SprdSessionState.Disconnected) Fault(); throw; }
    }
    private void State(SprdSessionState state)
    { _state = state; Log.ForContext<SprdProtocol>().Information(Strings.Phase, state); }
    private void Ready()
    { if (!IsConnected) throw new InvalidOperationException(Strings.Unavailable); }
    private void Fault()
    {
        Interlocked.Increment(ref _generation); _target = null; _partitions = null; _nativeRecords = null; _state = SprdSessionState.Faulted;
        try { _transport.Close(); _needsClose = false; }
        catch { _needsClose = true; /* Keep the original failure; disconnect must retry close. */ }
        _openedHere = false; _wire.Reset();
        Log.ForContext<SprdProtocol>().Error(Strings.Faulted);
    }

    /// <inheritdoc />
    public void Connect(SprdConnectionResources resources, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        Run(() => { ConnectCore(resources, progress); return 0; }, cancellationToken, _options.ConnectTimeoutMilliseconds);
    }
    /// <inheritdoc />
    public async Task ConnectAsync(IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        if (_inside.Value) throw new InvalidOperationException(Strings.Reentry);
        await _gate.WaitAsync(ct).ConfigureAwait(false); _inside.Value = true;
        using var gate = new GateLease(this);
        if (_state == SprdSessionState.Disposed) throw new ObjectDisposedException(nameof(SprdProtocol));
        if (_state != SprdSessionState.Disconnected) throw new InvalidOperationException(Strings.Unavailable);
        _wire.Begin(ct, _options.ConnectTimeoutMilliseconds);
        try
        {
            SprdBootStage entry = _options.EntryStage; string? version = null;
            if (entry == SprdBootStage.Auto)
            {
                OpenConnection(); (entry, version) = DetectEntry();
            }
            using var resources = entry == SprdBootStage.Fdl2 ? new SprdConnectionResources() :
                _provider is null ? throw new ArgumentException(Strings.LoaderRequired) :
                await SprdResourceRequest.Get(_provider, entry,
                    Math.Min(_wire.Remaining, _options.ResourceRequestTimeoutMilliseconds), ct).ConfigureAwait(false);
            if (resources is null) throw new ArgumentException(Strings.LoaderRequired);
            _wire.Check();
            if (_options.EntryStage == SprdBootStage.Auto)
            {
                resources.Validate(_options with { EntryStage = entry });
                CompleteConnection(resources, entry, version, progress);
            }
            else ConnectCore(resources, progress);
            _wire.Check();
        }
        catch { if (_wire.HasWritten || _state == SprdSessionState.Connecting) Fault(); throw; }
    }
    private void ConnectCore(SprdConnectionResources resources, IProgress<ProgressRecord>? progress)
    {
        if (_state != SprdSessionState.Disconnected) throw new InvalidOperationException(Strings.Unavailable);
        resources.Validate(_options); OpenConnection();
        SprdBootStage entry = _options.EntryStage; string? version = null;
        if (entry == SprdBootStage.Auto)
        {
            (entry, version) = DetectEntry(); resources.Validate(_options with { EntryStage = entry });
        }
        else if (entry != SprdBootStage.Fdl2) version = Handshake();
        else _wire.Expect(SprdCommand.Connect);
        CompleteConnection(resources, entry, version, progress);
    }
    private void OpenConnection()
    {
        if (_options.RawDataMode != SprdRawDataMode.Disabled && _transport is IUsbTransport && _options.RawDataUsbPacketSize is null)
            throw new ArgumentException(Strings.RawUsbPacketRequired);
        _wire.Reset(); _wire.UseCrc = _options.EntryStage == SprdBootStage.BootRom;
        _wire.Escaped = !_options.EntryTranscodeDisabled;
        State(SprdSessionState.Connecting);
        try
        {
            if (!_transport.IsOpen) { _transport.Open(); _openedHere = true; }
        }
        catch
        {
            _needsClose = true;
            Fault();
            throw;
        }
        Interlocked.Increment(ref _generation);
    }
    private void CompleteConnection(SprdConnectionResources resources, SprdBootStage entry, string? version,
        IProgress<ProgressRecord>? progress)
    {
        SprdLoaderInfo? info = null;
        if (entry == SprdBootStage.BootRom)
        {
            State(SprdSessionState.BootRom); Upload(resources.Fdl1!, _options.BootRomBlockSize, "FDL1", progress);
            _wire.Expect(SprdCommand.Execute); _wire.UseCrc = false;
            _wire.Delay(_options.Fdl1StartDelayMilliseconds);
            version = Handshake();
        }
        if (entry != SprdBootStage.Fdl2)
        {
            State(SprdSessionState.Fdl1);
            if (_options.KeepCharge) _wire.Expect(SprdCommand.KeepCharge);
            Upload(resources.Fdl2!, _options.Fdl2BlockSize, "FDL2", progress);
            var response = _wire.Command(SprdCommand.Execute);
            if (response.Type is not (SprdCommand.Ack or SprdCommand.LoaderInfo))
                throw new SprdProtocolException(SprdCommand.Execute, response.Type);
            if (response.Type == SprdCommand.LoaderInfo && response.Data.IsEmpty)
                throw new SprdProtocolException(SprdCommand.Execute, response.Type);
            Log.ForContext<SprdProtocol>().Debug(Strings.LoaderMetadata,
                response.Type, response.Data.Length,
                response.Data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(response.Data.Span) : (uint?)null);
            info = SprdMetadata.Loader(response.Data.Span);
        }
        if (_options.DisableTranscode && _wire.Escaped)
        {
            // Explicit profile selection also supports an already-loaded FDL2 with no EXEC metadata.
            if (info is { SupportsDisableTranscode: false }) throw new SprdProtocolException(SprdCommand.DisableTranscode);
            _wire.Expect(SprdCommand.DisableTranscode); _wire.Escaped = false;
        }
        if (_options.RawDataMode != SprdRawDataMode.Disabled)
        {
            // Never turn a capability advertisement into an implicit mode selection.
            if (info is not null && (info.RawDataSupport != (byte)_options.RawDataMode ||
                (ulong)info.FlushSizeKiB * 1024 != (ulong)_options.RawDataFlushSizeBytes!.Value))
                throw new NotSupportedException(Strings.RawProfileMismatch);
            _wire.Expect(SprdCommand.EnableRawData);
        }
        _partitions = _options.KnownPartitions.Count == 0 ? null : _options.KnownPartitions;
        _target = new(SprdBootStage.Fdl2, version, info) { EntryStage = entry }; State(SprdSessionState.StorageReady);
    }
    private (SprdBootStage Stage, string? Version) DetectEntry()
    {
        SprdResponse response; bool connectProbe = false;
        try { response = _wire.Command(SprdCommand.CheckBaud, detectChecksum: true,
            responseTimeoutMilliseconds: _options.EntryProbeTimeoutMilliseconds); }
        catch (TimeoutException) when (_wire.CanProbeConnect)
        {
            // One different, non-destructive query is allowed only if no response byte arrived.
            _wire.Check(); _wire.UseCrc = false;
            Log.ForContext<SprdProtocol>().Warning(Strings.EntryProbeConnect);
            connectProbe = true;
            response = _wire.Command(SprdCommand.Connect, detectChecksum: true);
        }
        _wire.UseCrc = response.UseCrc;
        // Validated metadata only; version text, UID and payload bytes never enter this diagnostic.
        string checksum = response.UseCrc ? "CRC16" : "FDL";
        Log.ForContext<SprdProtocol>().Information(Strings.EntryResponse,
            connectProbe ? SprdCommand.Connect : SprdCommand.CheckBaud, response.Type, response.Data.Length, checksum);
        SprdBootStage entry; string? version = null;
        if (!connectProbe && response.Type == SprdCommand.Version)
        {
            entry = response.UseCrc ? SprdBootStage.BootRom : SprdBootStage.Fdl1;
            version = VersionText(response); _wire.Expect(SprdCommand.Connect);
        }
        else if (response.Type == SprdCommand.VerifyError && response.UseCrc && response.Data.IsEmpty)
        {
            // A BootROM checksum rejection proves CRC framing, not an accepted connection.
            // Correct the FDL query checksum once and require its CRC ACK before requesting loaders.
            Log.ForContext<SprdProtocol>().Warning(Strings.EntryCrcCorrection);
            var confirmation = _wire.Expect(SprdCommand.Connect);
            if (!confirmation.Data.IsEmpty) throw new SprdProtocolException(SprdCommand.Connect, confirmation.Type);
            entry = SprdBootStage.BootRom;
        }
        else if (response.Type == SprdCommand.UnsupportedCommand && !response.UseCrc && response.Data.IsEmpty)
        {
            // Confirm the existing FDL2 handshake and switch framing only after its ACK.
            entry = SprdBootStage.Fdl2;
            _wire.Expect(SprdCommand.DisableTranscode); _wire.Escaped = false;
        }
        else throw new InvalidOperationException(Strings.FormatEntryResponseRejected($"0x{response.Type:X4}", response.Data.Length, checksum));
        Log.ForContext<SprdProtocol>().Information(Strings.EntryDetected, entry);
        return (entry, version);
    }
    private static string VersionText(SprdResponse response) =>
        Encoding.ASCII.GetString(response.Data.Span[..Math.Min(256, response.Data.Length)]).TrimEnd('\0');
    private string Handshake()
    {
        var response = _wire.Expect(SprdCommand.CheckBaud, expected: SprdCommand.Version);
        string version = VersionText(response);
        _wire.Expect(SprdCommand.Connect);
        return version;
    }
    private void Upload(SprdLoader loader, int blockSize, string label, IProgress<ProgressRecord>? progress)
    {
        long length = loader.Source.Length;
        if (length < 1 || length > _options.MaximumLoaderBytes || _options.PadOddPayloads && length % 2 != 0 ||
            (ulong)loader.Address + (ulong)length > 0x1_0000_0000UL) throw new IOException(Strings.InvalidSource);
        using Stream stream = loader.Source.OpenStream();
        ValidateStream(stream, length);
        Span<byte> start = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(start, loader.Address);
        BinaryPrimitives.WriteUInt32BigEndian(start[4..], checked((uint)length));
        _wire.Expect(SprdCommand.Start, start);
        SendStream(stream, length, blockSize, label, progress);
        _wire.Expect(SprdCommand.End);
        Report(progress, length, length, label, completed: true);
    }
    private static void ValidateStream(Stream stream, long length)
    {
        if (stream is null || !stream.CanRead || length < 0 || stream.CanSeek && stream.Length - stream.Position != length)
            throw new IOException(Strings.InvalidSource);
    }
    private void SendStream(Stream stream, long length, int blockSize, string label, IProgress<ProgressRecord>? progress,
        ReadOnlySpan<byte> prefix = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            long sent = 0;
            while (sent < length)
            {
                _wire.Check(); int count = (int)Math.Min(blockSize, length - sent), copied = Math.Min(prefix.Length, count);
                prefix[..copied].CopyTo(buffer); prefix = prefix[copied..];
                ReadExactly(stream, buffer.AsSpan(copied, count - copied));
                _wire.Expect(SprdCommand.Midst, buffer.AsSpan(0, count)); sent += count;
                Report(progress, length, sent, label);
            }
            _wire.Check();
            if (stream.ReadByte() != -1) throw new IOException(Strings.InvalidSource);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    private void ReadExactly(Stream stream, Span<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            _wire.Check(); int count = stream.Read(bytes);
            if (count <= 0 || count > bytes.Length) throw new IOException(Strings.InvalidSource);
            bytes = bytes[count..];
        }
    }
    private static void Report(IProgress<ProgressRecord>? progress, long total, long current, string label, bool completed = false) =>
        progress?.Report(new(total, current, label) { Unit = ProgressUnit.Bytes, Phase = completed ? ProgressPhase.Completed : ProgressPhase.Running });

    /// <inheritdoc />
    public void Disconnect(CancellationToken cancellationToken = default)
    { using var gate = Enter(cancellationToken); DisconnectCore(); }
    private void DisconnectCore()
    {
        Interlocked.Increment(ref _generation); _target = null; _partitions = null; _nativeRecords = null;
        try
        {
            if (_needsClose || !_leaveTransportOpen && _openedHere) _transport.Close();
            _needsClose = false; _openedHere = false; _wire.Reset(); _state = SprdSessionState.Disconnected;
        }
        catch
        {
            _needsClose = true; _state = SprdSessionState.Faulted;
            throw;
        }
    }
    /// <inheritdoc />
    public Task DisconnectAsync(IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    { Disconnect(ct); return Task.CompletedTask; }
    /// <summary>Disposes the session and optionally the owned transport, expiring all partition views.</summary>
    public void Dispose()
    {
        if (_inside.Value) throw new InvalidOperationException(Strings.Reentry);
        _gate.Wait();
        try
        {
            if (_state == SprdSessionState.Disposed) return;
            try { DisconnectCore(); }
            finally
            {
                _state = SprdSessionState.Disposed; _wire.Dispose();
                if (!_leaveTransportOpen) _transport.Dispose();
            }
        }
        finally { _gate.Release(); }
    }
    /// <inheritdoc />
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
