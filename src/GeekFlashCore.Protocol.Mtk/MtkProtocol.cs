using System.Security.Cryptography;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Mtk.Brom;
using GeekFlashCore.Protocol.Mtk.Da;
using GeekFlashCore.Protocol.Mtk.Internals;
using GeekFlashCore.Protocol.Mtk.Loaders;
using GeekFlashCore.Transport.Abstractions;
using GeekFlashCore.Transport.LibUsb;
using Serilog;

namespace GeekFlashCore.Protocol.Mtk;

/// <summary>MediaTek USB protocol. All wire operations are synchronous and share one session gate.</summary>
public sealed partial class MtkProtocol : IMtkProtocol, IMtkSessionAccess, IDisposable
{
    private readonly IUsbTransport _transport;
    private readonly MtkProtocolOptions _options;
    private readonly IMtkDaProvider? _daProvider;
    private readonly IMtkEmiProvider? _emiProvider;
    private readonly IMtkAuthenticationProvider? _signer;
    private readonly Func<MtkTargetInfo, MtkConnectionResources>? _resources;
    private readonly IMtkExploitStrategy? _exploitStrategy;
    private readonly MtkExploitDescriptor? _exploitDescriptor;
    private readonly bool _leaveTransportOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _inside = new();
    private readonly MtkWire _wire;
    private readonly MtkBromSession _brom;
    private IMtkDaSession? _da;
    private MtkStorageInfo? _storage;
    private MtkTargetInfo? _target;
    private MtkTargetInfo? _initialTarget;
    private MtkDaImage? _image;
    private long _generation;
    private volatile MtkSessionState _state;
    private bool _openedHere;
    /// <summary>Creates a serialized session. Provider sources are borrowed; sensitive buffers returned by
    /// the resource factory transfer ownership. Borrowed transports are still closed after wire failures.
    /// An optional host strategy is invoked only at its declared checkpoints; the core owns no strategies.</summary>
    public MtkProtocol(IUsbTransport transport, MtkProtocolOptions? options = null, IMtkDaProvider? daProvider = null,
        IMtkEmiProvider? emiProvider = null, IMtkAuthenticationProvider? authenticationProvider = null,
        Func<MtkTargetInfo, MtkConnectionResources>? resources = null, bool leaveTransportOpen = false,
        IMtkExploitStrategy? exploitStrategy = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _options = options ?? new();
        _options.Validate();
        _exploitStrategy = exploitStrategy;
        if (exploitStrategy is not null)
        {
            _exploitDescriptor = exploitStrategy.Descriptor ?? throw new MtkResourceException("extension descriptor");
            _exploitDescriptor.Validate();
        }
        _daProvider = daProvider;
        _emiProvider = emiProvider;
        _signer = authenticationProvider;
        _resources = resources;
        _leaveTransportOpen = leaveTransportOpen;
        _wire = new(transport, _options);
        _brom = new(_wire, _options);
    }
    /// <summary>Creates the production backend exclusively through LibUsb. The optional strategy must be
    /// explicitly supplied by the host; no built-in strategies are available.</summary>
    public static MtkProtocol CreateUsb(LibUsbConnectionOptions connection, MtkProtocolOptions? options = null,
        IMtkDaProvider? daProvider = null, IMtkEmiProvider? emiProvider = null, IMtkAuthenticationProvider? signer = null,
        IMtkExploitStrategy? exploitStrategy = null)
    {
        options ??= new();
        options.Validate();
        var transport = LibUsbTransportFactory.Create(connection);
        try
        {
            return new(transport, options, daProvider, emiProvider, signer, exploitStrategy: exploitStrategy);
        }
        catch { try { transport.Dispose(); } catch { } throw; }
    }
    public ProtocolType Type => ProtocolType.Mtk;
    public ITransport Transport => _transport;
    public bool IsConnected => _state == MtkSessionState.StorageReady;
    public MtkTargetInfo? TargetInfo => _target;
    public MtkDaImage? DownloadAgent => _image;
    public MtkSessionState SessionState => _state;
    public long Generation => Interlocked.Read(ref _generation);
    public MtkCapabilities Capabilities => new(IsConnected ? MtkCapabilitySupport.Supported : MtkCapabilitySupport.Unknown,
        MtkCapabilitySupport.RequiresExtension, MtkCapabilitySupport.RequiresExtension,
        MtkCapabilitySupport.RequiresExtension, MtkCapabilitySupport.Unknown);
    private void State(MtkSessionState state)
    {
        _state = state;
        Log.ForContext<MtkProtocol>().Information(Strings.Phase, state);
    }
    private IDisposable Enter(CancellationToken token)
    {
        if (_inside.Value)
            throw new InvalidOperationException(Strings.Reentry);
        token.ThrowIfCancellationRequested();
        _gate.Wait(token);
        _inside.Value = true;
        if (_state == MtkSessionState.Disposed)
        {
            _inside.Value = false;
            _gate.Release();
            throw new ObjectDisposedException(nameof(MtkProtocol));
        }
        return new GateLease(this);
    }
    private sealed class GateLease(MtkProtocol owner) : IDisposable
    {
        public void Dispose()
        {
            owner._inside.Value = false;
            owner._gate.Release();
        }
    }
    private T Execute<T>(Func<T> action, CancellationToken token = default)
    {
        using var gate = Enter(token);
        _wire.Begin(token, _options.OperationTimeoutMilliseconds);
        try
        {
            return action();
        }
        catch { if (_wire.HasWritten && _state != MtkSessionState.Faulted) Fault(); throw; }
    }
    private void Ready()
    {
        if (!IsConnected || _da is null || _storage is null)
            throw new InvalidOperationException(Strings.SessionUnavailable);
    }
    private MtkTargetInfo ProbeCore()
    {
        if (_state == MtkSessionState.Probed)
            return _target!;
        if (_state != MtkSessionState.Disconnected)
            throw new InvalidOperationException(Strings.SessionUnavailable);
        State(MtkSessionState.Opening);
        try
        {
            if (!_transport.IsOpen)
            {
                _transport.Open();
                _openedHere = true;
            }
        }
        catch { _state = MtkSessionState.Disconnected; throw; }
        _wire.Stage = MtkBootStage.Unknown;
        _wire.Command = 0;
        State(MtkSessionState.Handshaking);
        if (_transport.ControlInterfaceNumber is { } controlInterface)
            _wire.ConfigureCdc(controlInterface);
        _target = _brom.Probe();
        _initialTarget = _target;
        State(MtkSessionState.Probed);
        return _target;
    }
    public MtkTargetInfo Probe(CancellationToken cancellationToken = default) => Execute(ProbeCore, cancellationToken);
    public void Connect(MtkConnectionResources resources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        Execute(() =>
        {
            _wire.Begin(cancellationToken, _options.ConnectTimeoutMilliseconds);
            MtkTargetInfo target = ProbeCore();
            resources = PrepareBootResources(resources, target);
            target = _target!;
            SendBootResources(resources, target);
            if (target.Security.Sla)
            {
                if (resources.SynchronousSigner is null)
                    throw new MtkResourceException("synchronous BROM SLA signer");
                _brom.Authenticate(resources.SynchronousSigner);
            }
            LoadDa(resources, target);
            byte[]? daChallenge = _da!.GetAuthenticationChallenge();
            if (daChallenge is not null)
            {
                try
                {
                    if (resources.SynchronousSigner is null)
                        throw new MtkResourceException("synchronous DA SLA signer");
                    using var response = resources.SynchronousSigner(MtkAuthenticationKind.DaSla, daChallenge);
                    _da.Authenticate(response.Memory.Span);
                }
                finally { CryptographicOperations.ZeroMemory(daChallenge); }
            }
            RunExploitCheckpoint(MtkExploitStage.Da2Authenticated, resources);
            CompleteConnection();
            return 0;
        }, cancellationToken);
    }
    public async Task ConnectAsync(IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        if (_inside.Value)
            throw new InvalidOperationException(Strings.Reentry);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        if (_state == MtkSessionState.Disposed)
        {
            _gate.Release();
            throw new ObjectDisposedException(nameof(MtkProtocol));
        }
        _inside.Value = true;
        using var gate = new GateLease(this);
        _wire.Begin(ct, _options.ConnectTimeoutMilliseconds);
        MtkConnectionResources? transferred = null;
        try
        {
            var target = ProbeCore();
            MtkConnectionResources resources;
            if (_resources is not null)
                resources = transferred = _resources(target);
            else
            {
                if (_daProvider is null)
                    throw new MtkResourceException("DA provider");
                var image = await MtkResourceRequest.Get(t => _daProvider.GetDownloadAgentAsync(target, t), ResourceBudget(), ct).ConfigureAwait(false);
                var emi = _emiProvider is null ? null : await MtkResourceRequest.Get(t => _emiProvider.GetEmiAsync(target, t), ResourceBudget(), ct).ConfigureAwait(false);
                resources = new(image, emi, Signer: _signer);
            }
            resources = PrepareBootResources(resources, target);
            target = _target!;
            SendBootResources(resources, target);
            if (target.Security.Sla)
                await AuthenticateAsync(MtkAuthenticationKind.BromSla, _brom.StartSla(), resources, ct).ConfigureAwait(false);
            var entry = resources.DownloadAgent.Entry;
            var da1 = entry.Regions[entry.EntryRegionIndex];
            State(MtkSessionState.UploadingDa1);
            using (Stream stream = new MtkDataWindow(resources.DownloadAgent.Source, da1.FileOffset, da1.Length).OpenStream())
            {
                if (_brom.BeginDownloadAgent(da1.Address, da1.Length, da1.SignatureLength))
                    await AuthenticateAsync(MtkAuthenticationKind.BromSla, _brom.StartSla(), resources, ct).ConfigureAwait(false);
                _brom.FinishDownloadAgent(da1.Length, stream);
                _brom.JumpDownloadAgent(checked(da1.Address + da1.EntryOffset));
            }
            LoadDa(resources, target, upload: false);
            var challenge = _da!.GetAuthenticationChallenge();
            if (challenge is not null)
                await AuthenticateAsync(MtkAuthenticationKind.DaSla, challenge, resources, ct).ConfigureAwait(false);
            RunExploitCheckpoint(MtkExploitStage.Da2Authenticated, resources);
            CompleteConnection();
            progress?.Report(new(1, 1, Strings.FormatPhase(_state))
            {
                Phase = ProgressPhase.Completed
            });
        }
        catch { if (_wire.HasWritten && _state != MtkSessionState.Faulted) Fault(); throw; }
        finally { transferred?.Authentication?.Dispose(); transferred?.Certificate?.Dispose(); }
    }
    private async ValueTask AuthenticateAsync(MtkAuthenticationKind kind, byte[]? challenge, MtkConnectionResources resources, CancellationToken ct)
    {
        if (challenge is null)
            return;
        State(MtkSessionState.Authenticating);
        var signer = resources.Signer ?? _signer;
        if (signer is null && resources.SynchronousSigner is { } synchronousSigner)
        {
            try
            {
                _wire.Check();
                using var synchronousResponse = synchronousSigner(kind, challenge);
                _wire.Check();
                if (kind == MtkAuthenticationKind.BromSla)
                    _brom.FinishSla(synchronousResponse.Memory.Span);
                else
                    _da!.Authenticate(synchronousResponse.Memory.Span);
            }
            finally { CryptographicOperations.ZeroMemory(challenge); }
            return;
        }
        if (signer is null)
        {
            CryptographicOperations.ZeroMemory(challenge);
            throw new MtkResourceException("SLA signer");
        }
        int budget;
        try
        {
            budget = ResourceBudget();
        }
        catch { CryptographicOperations.ZeroMemory(challenge); throw; }
        async ValueTask<MtkSensitiveBuffer> Request(CancellationToken token)
        {
            try
            {
                return await signer.SignAsync(kind, _target!, challenge, token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(challenge); }
        }
        using var response = await MtkResourceRequest.Get(Request, budget, ct).ConfigureAwait(false);
        _wire.Check();
        if (kind == MtkAuthenticationKind.BromSla)
            _brom.FinishSla(response.Memory.Span);
        else
            _da!.Authenticate(response.Memory.Span);
    }
    private int ResourceBudget() => Math.Min(_options.ResourceTimeoutMilliseconds, _wire.RemainingTimeoutMilliseconds);
    private void ValidateResources(MtkConnectionResources resources, MtkTargetInfo target, bool requireBootAuthentication = true)
    {
        var image = resources.DownloadAgent ?? throw new MtkResourceException("DA");
        var entry = image.Entry;
        if (entry.HardwareCode != (_options.DaHardwareCode ?? target.HardwareCode) ||
            entry.HardwareSubCode != 0 && entry.HardwareSubCode != target.HardwareSubCode ||
            entry.HardwareVersion > target.HardwareVersion || entry.SoftwareVersion > target.SoftwareVersion ||
            !Enum.IsDefined(entry.Kind) || _options.DaKind is { } kind && kind != entry.Kind ||
            entry.EntryRegionIndex >= entry.Regions.Count - 1)
            throw new MtkResourceException("DA selection");
        foreach (var region in entry.Regions.Skip(entry.EntryRegionIndex).Take(2))
        {
            if (region.Length == 0 || region.SignatureLength >= region.Length || region.EntryOffset >= region.Length ||
                region.FileOffset < 0 || region.FileOffset > image.Source.Length - region.Length ||
                (ulong)region.Address + region.Length > (ulong)uint.MaxValue + 1)
                throw new MtkResourceException("DA region");
            using var stream = new MtkDataWindow(image.Source, region.FileOffset, region.Length).OpenStream();
        }
        if (requireBootAuthentication && target.Security.Daa && resources.Authentication is null)
            throw new MtkResourceException("DAA authentication");
        if (requireBootAuthentication && target.Security.CertificateRequired && resources.Certificate is null)
            throw new MtkResourceException("certificate");
        foreach (var sensitive in new[] { resources.Authentication, resources.Certificate })
            if (sensitive is not null && (sensitive.Memory.Length == 0 ||
                sensitive.Memory.Length + (sensitive.Memory.Length & 1) > _options.MaximumFrameSize))
                throw new MtkResourceException("authentication length");
        if (resources.Emi is { } emi)
        {
            if (emi.Source.Length <= 0 || emi.Source.Length > _options.MaximumFrameSize)
                throw new MtkResourceException("EMI length");
            using var stream = emi.Source.OpenStream();
            if (!stream.CanRead || stream.CanSeek && stream.Length != emi.Source.Length)
                throw new MtkResourceException("EMI source");
        }
        if (_options.ChipProfile is { } profile && (profile.HardwareCode != target.HardwareCode ||
            profile.WatchdogAddress == 0 || profile.WatchdogWidth is not (16 or 32) ||
            profile.WatchdogAddress % (profile.WatchdogWidth / 8) != 0 ||
            profile.WatchdogWidth == 16 && profile.WatchdogValue > ushort.MaxValue))
            throw new MtkResourceException("chip profile");
    }
    private void SendBootResources(MtkConnectionResources resources, MtkTargetInfo target)
    {
        _brom.DisableWatchdog(target);
        if (resources.Certificate is { } certificate)
            _brom.SendResource(MtkBromCommand.SendCertificate, certificate.Memory.Span);
        if (resources.Authentication is { } auth)
            _brom.SendResource(MtkBromCommand.SendAuthentication, auth.Memory.Span);
    }
    private void LoadDa(MtkConnectionResources resources, MtkTargetInfo target, bool upload = true)
    {
        _image = resources.DownloadAgent;
        if (upload)
        {
            State(MtkSessionState.UploadingDa1);
            _brom.Upload(resources.DownloadAgent, resources.SynchronousSigner);
        }
        State(MtkSessionState.Da1Ready);
        _da = resources.DownloadAgent.Entry.Kind switch
        {
            MtkDaKind.XFlash => new XFlashSession(_wire, _options),
            MtkDaKind.Xml => new XmlSession(_wire, _options),
            _ => new LegacySession(_wire, _options)
        };
        _da.Initialize(resources.DownloadAgent, resources.Emi, target, stage =>
        {
            var state = stage == MtkExploitStage.Da1Ready ? MtkSessionState.Da1Ready : MtkSessionState.Da2Ready;
            if (_state != state)
                State(state);
            RunExploitCheckpoint(stage, resources);
            if (stage == MtkExploitStage.Da1Ready)
                State(MtkSessionState.UploadingDa2);
            return _image!;
        });
    }
    private void CompleteConnection()
    {
        _storage = _da!.GetStorage();
        Interlocked.Increment(ref _generation);
        State(MtkSessionState.StorageReady);
    }
    private void Fault()
    {
        _da = null;
        _storage = null;
        _partitions = null;
        _image = null;
        Interlocked.Increment(ref _generation);
        _state = MtkSessionState.Faulted;
        try
        {
            _transport.Close();
        }
        catch { /* Primary wire failure remains the reported cause. */ }
        Log.ForContext<MtkProtocol>().Error(Strings.Faulted);
    }
    public MtkStorageInfo GetStorageInfo() => Execute(() => { Ready(); return _storage!; });
    private MtkStorageRegion Range(MtkFlashRange range)
    {
        Ready();
        var region = _storage!.Regions.SingleOrDefault(r => r.WireId == range.RegionId) ?? throw new MtkCapabilityException("storage region");
        if (range.Offset < 0 || range.Length <= 0 || range.Offset > region.Length - range.Length ||
            range.Offset % region.BlockSize != 0 || range.Length % region.BlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(range));
        return region;
    }
    public void Read(MtkFlashRange range, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Execute(() =>
        {
            var region = Range(range);
            if (!destination.CanWrite)
                throw new ArgumentException(nameof(destination));
            _da!.Read(region, range.Offset, range.Length, destination);
            return 0;
        }, cancellationToken);
    }
    public void Write(MtkFlashRange range, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        Execute(() =>
        {
            var region = Range(range);
            if (!source.CanRead || source.CanSeek && source.Length - source.Position < range.Length)
                throw new MtkResourceException("write stream");
            _da!.Write(region, range.Offset, range.Length, source);
            _partitions = null;
            return 0;
        }, cancellationToken);
    }
    public void Erase(MtkFlashRange range, CancellationToken cancellationToken = default) =>
        Execute(() => { var region = Range(range); _da!.Erase(region, range.Offset, range.Length); _partitions = null; return 0; }, cancellationToken);
    public void Disconnect()
    {
        using var gate = Enter(default);
        _da = null;
        _storage = null;
        _target = null;
        _initialTarget = null;
        _partitions = null;
        _image = null;
        Interlocked.Increment(ref _generation);
        try
        {
            if (!_leaveTransportOpen || _openedHere)
                _transport.Close();
        }
        finally { _openedHere = false; _state = MtkSessionState.Disconnected; }
    }
    public Task DisconnectAsync(IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Disconnect();
        return Task.CompletedTask;
    }
    public Task<bool> RebootAsync(ProtocolRebootMode mode, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        bool done = Execute(() =>
        {
            Ready();
            if (!Enum.IsDefined(mode))
                throw new ArgumentOutOfRangeException(nameof(mode));
            _da!.Reboot(mode);
            _transport.Close();
            _da = null;
            _storage = null;
            _target = null;
            _initialTarget = null;
            _image = null;
            _partitions = null;
            Interlocked.Increment(ref _generation);
            _state = MtkSessionState.Disconnected;
            return true;
        }, ct);
        return Task.FromResult(done);
    }
    public T UseSession<T>(Func<IMtkDaChannel, T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Execute(() => { Ready(); var channel = new Channel(this); try { return action(channel); } finally { channel.Expire(); } }, cancellationToken);
    }
    private sealed class Channel(MtkProtocol owner, Action? guard = null) : IMtkDaChannel
    {
        private bool _valid = true;
        private void Check()
        {
            guard?.Invoke();
            if (!_valid)
                throw new InvalidOperationException(Strings.SessionUnavailable);
            owner._wire.Check();
        }
        public void Expire() => _valid = false;
        public MtkDaKind Kind
        {
            get
            {
                Check();
                return owner._da!.Kind;
            }
        }
        public MtkTargetInfo Target
        {
            get
            {
                Check();
                return owner._target!;
            }
        }
        public MtkDaImage DownloadAgent
        {
            get
            {
                Check();
                return owner._image!;
            }
        }
        public MtkStorageInfo Storage
        {
            get
            {
                Check();
                return owner._storage ?? throw new InvalidOperationException(Strings.SessionUnavailable);
            }
        }
        public long Generation
        {
            get
            {
                Check();
                return owner.Generation;
            }
        }
        public int WritePacketLength
        {
            get
            {
                Check();
                return owner._wire.WritePacketLength;
            }
        }
        public void SendCommand(uint command)
        {
            Check();
            if (owner._da is not XFlashSession x)
                throw new MtkCapabilityException("XFlash channel");
            x.Command(command);
        }
        public void SendData(ReadOnlySpan<byte> data)
        {
            Check();
            owner._wire.SendFrame(data);
        }
        public int ReceiveData(Span<byte> destination)
        {
            Check();
            return owner._wire.ReadFrame(destination);
        }
        public void CheckStatus()
        {
            Check();
            owner._wire.ReadStatus();
        }
        public void BeginXmlCommand(string command, IReadOnlyDictionary<string, string> parameters)
        {
            Check();
            if (owner._da is not XmlSession xml)
                throw new MtkCapabilityException("XML channel");
            xml.Begin(command, parameters);
        }
        public void EndXmlCommand()
        {
            Check();
            if (owner._da is not XmlSession xml)
                throw new MtkCapabilityException("XML channel");
            xml.Lifetime("END");
        }
        public void AcknowledgeXml()
        {
            Check();
            if (owner._da is not XmlSession xml)
                throw new MtkCapabilityException("XML channel");
            xml.Ack();
        }
        public long ReceiveXmlFile(Stream destination, long? expectedLength, long maximumLength)
        {
            Check();
            if (owner._da is not XmlSession xml)
                throw new MtkCapabilityException("XML channel");
            return xml.Upload(destination, expectedLength, maximumLength);
        }
        public void SendXmlFile(Stream source, long length)
        {
            Check();
            if (owner._da is not XmlSession xml)
                throw new MtkCapabilityException("XML channel");
            xml.Download(length, source);
        }
        public void ReadFlash(MtkFlashRange range, Stream destination)
        {
            Check();
            var region = owner.Range(range);
            if (!destination.CanWrite)
                throw new ArgumentException(nameof(destination));
            owner._da!.Read(region, range.Offset, range.Length, destination);
        }
        public void WriteFlash(MtkFlashRange range, Stream source)
        {
            Check();
            var region = owner.Range(range);
            if (!source.CanRead)
                throw new ArgumentException(nameof(source));
            owner._da!.Write(region, range.Offset, range.Length, source);
            owner._partitions = null;
        }
        public void Invalidate()
        {
            guard?.Invoke();
            if (!_valid)
                throw new InvalidOperationException(Strings.SessionUnavailable);
            owner.Fault();
            _valid = false;
        }
    }
    public void Dispose()
    {
        if (_state == MtkSessionState.Disposed)
            return;
        using var gate = Enter(default);
        _da = null;
        _storage = null;
        _target = null;
        _initialTarget = null;
        _image = null;
        _partitions = null;
        Interlocked.Increment(ref _generation);
        try
        {
            if (!_leaveTransportOpen)
                _transport.Dispose();
            else if (_openedHere)
                _transport.Close();
        }
        finally { _state = MtkSessionState.Disposed; }
    }
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
