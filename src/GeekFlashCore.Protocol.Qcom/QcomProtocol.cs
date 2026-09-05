using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using GeekFlashCore.BlockDevice.Abstractions;
using GeekFlashCore.Protocol.Abstractions;
using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Firehose;
using GeekFlashCore.Protocol.Qcom.Firehose.Configuration;
using GeekFlashCore.Protocol.Qcom.Firehose.Storage;
using GeekFlashCore.Protocol.Qcom.Internals;
using GeekFlashCore.Protocol.Qcom.Loaders;
using GeekFlashCore.Protocol.Qcom.Vendors;
using GeekFlashCore.Protocol.Qcom.Vendors.Oplus;
using GeekFlashCore.Protocol.Qcom.Vendors.Xiaomi;
using GeekFlashCore.Protocol.Qcom.Vendors.Nothing;
using GeekFlashCore.Protocol.Qcom.Vendors.OnePlus;
using GeekFlashCore.Transport.Abstractions;

namespace GeekFlashCore.Protocol.Qcom;

/// <summary>A serialized Qualcomm session. Resource providers are asynchronous; all wire I/O is synchronous.</summary>
public sealed partial class QcomProtocol : IQcomProtocol, IBlockDeviceProvider, IDisposable
{
    private const int MaxProtocolDetectionAttempts = 4;
    private readonly QcomProtocolOptions _options;
    private readonly ISaharaImageProvider? _imageProvider;
    private readonly IOplusDigestProvider? _digestProvider;
    private readonly IFirehoseDigestProvider? _firehoseDigestProvider;
    private readonly IFirehoseVipProvider? _firehoseVipProvider;
    private readonly IVendorAuthenticationProvider? _authenticationProvider;
    private readonly IFirehoseConfigurationProvider? _configurationProvider;
    private readonly IQcomProgrammerInspector _inspector;
    private readonly bool _leaveTransportOpen;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;
    private long _generation;
    private bool _connected;
    private ITransport? _wire;
    private SaharaProtocol? _sahara;
    private FirehoseSession? _firehose;
    private FirehoseStorageService? _storage;
    private QcomProgrammerInfo? _programmer;
    private FirehoseResponse? _startup;
    private QcomTargetInfo? _targetInfo;
    private FirehoseVipTransferPolicy? _vipPolicy;

    public QcomProtocol(ITransport transport, QcomProtocolOptions? options = null,
        ISaharaImageProvider? imageProvider = null, IOplusDigestProvider? digestProvider = null,
        IVendorAuthenticationProvider? authenticationProvider = null,
        IFirehoseConfigurationProvider? configurationProvider = null,
        bool leaveTransportOpen = false, IQcomProgrammerInspector? programmerInspector = null,
        IFirehoseDigestProvider? firehoseDigestProvider = null,
        IFirehoseVipProvider? firehoseVipProvider = null)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new QcomProtocolOptions();
        _options.Validate();
        _imageProvider = imageProvider;
        _digestProvider = digestProvider;
        _firehoseDigestProvider = firehoseDigestProvider;
        _firehoseVipProvider = firehoseVipProvider;
        _authenticationProvider = authenticationProvider;
        _configurationProvider = configurationProvider;
        _leaveTransportOpen = leaveTransportOpen;
        _inspector = programmerInspector ?? new QcomLoaderInspector();
    }

    public ProtocolType Type => ProtocolType.QualcommEdl;
    public ITransport Transport { get; }
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _connected && Transport.IsOpen && _firehose?.State == FirehoseSessionState.Configured;
    public QcomTargetInfo? TargetInfo => Snapshot(_targetInfo);

    public async Task ConnectAsync(IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        using var operation = Enter();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = cancellation.Token;
        ct.ThrowIfCancellationRequested();
        if (IsConnected) return;
        try
        {
            if (_firehose?.State == FirehoseSessionState.Faulted) Cleanup();
            if (_wire is null) DetectProtocol();
            else if (!Transport.IsOpen) Transport.Open();
            if (_sahara is not null && _firehose is null)
            {
                if (!_sahara.IsConnected) ProbeCore(progress);
                if (_imageProvider is null) throw new QcomResourceException(Strings.Qcom_LoaderProviderRequired);
                var response = await ResolveResourceAsync(token => _imageProvider.ResolveAsync(
                    new SaharaImageEntryRequest(Snapshot(_targetInfo)!.Sahara!) { VendorHint = _options.VendorOverride }, token), ct).ConfigureAwait(false);
                if (response.Entries is null || response.Entries.Count == 0)
                    throw new QcomResourceException(Strings.Qcom_LoaderProviderRequired);
                UploadCore(response.Entries.ToArray(), progress, ct);
            }
            StartFirehose();
            await PrepareVipAsync(ct).ConfigureAwait(false);
            FirehoseConfiguration configuration = _options.Firehose;
            if (_configurationProvider is not null)
            {
                var response = await ResolveResourceAsync(token => _configurationProvider.ResolveAsync(
                    new FirehoseConfigurationRequest(TargetInfo!, configuration), token), ct).ConfigureAwait(false);
                configuration = response.Configuration ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
            }
            configuration = LimitConfiguration(configuration);
            ct.ThrowIfCancellationRequested();
            await SendGenericDigestAsync(ct).ConfigureAwait(false);
            FirehoseConfigureResult configured = await new ConfigureNegotiator(_firehose!).NegotiateAsync(
                configuration, _targetInfo!.Vendor, GetXiaomiAuthenticationAsync(), ct).ConfigureAwait(false);
            await InitializeStorageAsync(configured, ct).ConfigureAwait(false);
            await VerifyVendorAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _connected = true;
            progress?.Report(new ProgressRecord(1, 1, Strings.Progress_Connected));
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    public SaharaTargetInfo ProbeSahara(IProgress<ProgressRecord>? progress = null)
    {
        using var operation = Enter();
        if (_firehose is not null || IsConnected) throw new InvalidOperationException(Strings.Qcom_InvalidSessionState);
        try
        {
            if (_wire is null) DetectProtocol(probeFirehoseOnTimeout: false);
            if (_sahara is null) throw new InvalidOperationException(Strings.Qcom_InvalidSessionState);
            if (!_sahara.IsConnected) ProbeCore(progress);
            return TargetInfo!.Sahara!;
        }
        catch { Cleanup(); throw; }
    }

    public void UploadSaharaImages(IReadOnlyList<SaharaImageEntry> images, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        cancellationToken.ThrowIfCancellationRequested();
        if (_sahara?.IsConnected != true || _firehose is not null) throw new InvalidOperationException(Strings.Qcom_InvalidSessionState);
        try { UploadCore(images, progress, cancellationToken); }
        catch { Cleanup(); throw; }
    }

    public FirehoseCommandResult ConfigureFirehose(IProgress<ProgressRecord>? progress = null)
    {
        using var operation = Enter();
        try
        {
            if (_wire is null) DetectProtocol();
            else if (!Transport.IsOpen) Transport.Open();
            if (_sahara is not null && _firehose is null) throw new InvalidOperationException(Strings.Qcom_InvalidSessionState);
            StartFirehose();
            PrepareVip();
            FirehoseConfiguration configuration = _options.Firehose;
            if (_configurationProvider is not null)
            {
                var response = ResolveResourceSync(token => _configurationProvider.ResolveAsync(
                    new FirehoseConfigurationRequest(TargetInfo!, configuration), token));
                configuration = response.Configuration ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
            }
            configuration = LimitConfiguration(configuration);
            SendGenericDigest();
            var result = new ConfigureNegotiator(_firehose!).Negotiate(
                configuration, _targetInfo!.Vendor, GetXiaomiAuthentication());
            InitializeStorage(result);
            VerifyVendor();
            _connected = true;
            progress?.Report(new ProgressRecord(1, 1, Strings.Progress_Connected));
            return result.CommandResult;
        }
        catch { Cleanup(); throw; }
    }

    public long Program(FirehoseProgramRequest request, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default)
    {
        using var operation = EnterConnected();
        cancellationToken.ThrowIfCancellationRequested();
        return _storage!.Program(request, Adapt(progress, request.GetWireLength()), cancellationToken);
    }

    public long Read(FirehoseReadRequest request, Stream destination, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default)
    {
        using var operation = EnterConnected();
        cancellationToken.ThrowIfCancellationRequested();
        return _storage!.Read(request, destination, Adapt(progress, request.GetByteLength()), cancellationToken);
    }

    public FirehoseCommandResult ExecuteFirehoseCommand(BaseCommand command)
    {
        using var operation = EnterConnected();
        if (command is ConfigureCommand or ProgramCommand or ReadCommand)
            throw new ArgumentException(Strings.Qcom_UseDedicatedCommand, nameof(command));
        return _firehose!.Execute(command);
    }

    public FirehoseCommandResult ExecuteFirehoseXml(string xml)
    {
        using var operation = EnterConnected();
        var validated = CustomCommandValidator.Validate(xml, VendorStrategyResolver.ForVendor(_targetInfo!.Vendor));
        return _firehose!.ExecuteXml(validated.Xml);
    }

    public Task DisconnectAsync(IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        using var operation = Enter();
        ct.ThrowIfCancellationRequested();
        Cleanup();
        return Task.CompletedTask;
    }

    public Task<bool> RebootAsync(ProtocolRebootMode mode, IProgress<ProgressRecord>? progress = null, CancellationToken ct = default)
    {
        using var operation = EnterConnected();
        ct.ThrowIfCancellationRequested();
        FirehosePowerValue value = mode switch
        {
            ProtocolRebootMode.System => FirehosePowerValue.Reset,
            ProtocolRebootMode.Download => FirehosePowerValue.ResetToEdl,
            ProtocolRebootMode.PowerOff => FirehosePowerValue.Off,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        _storage!.Power(value);
        Cleanup();
        return Task.FromResult(true);
    }

    private void DetectProtocol(bool probeFirehoseOnTimeout = true)
    {
        if (!Transport.IsOpen) Transport.Open();
        bool allowRecovery = probeFirehoseOnTimeout && _options.ProbeFirehoseOnSaharaTimeout;
        Exception? lastException = null;
        for (int attempt = 0; attempt < MaxProtocolDetectionAttempts; attempt++)
        {
            byte[] prefix = new byte[8];
            try
            {
                Transport.ReadExact(prefix, _options.ConnectTimeoutMilliseconds);
            }
            catch (TimeoutException exception)
            {
                lastException = exception;
                if (!allowRecovery) throw;
                if (attempt == 0)
                    SendSaharaHelloProbe();
                else
                    ResetSaharaStateMachine();
                continue;
            }
            catch (EndOfStreamException exception)
            {
                lastException = exception;
                if (!allowRecovery) throw;
                ResetSaharaStateMachine();
                continue;
            }

            if (LooksLikeXml(prefix))
            {
                _wire = new QcomSessionTransport(Transport, prefix, _options.ReadTimeoutMilliseconds);
                _firehose = new FirehoseSession(_wire, _options.ReadTimeoutMilliseconds);
                break;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) == (uint)SaharaCommand.Hello)
            {
                _wire = new QcomSessionTransport(Transport, prefix, _options.ReadTimeoutMilliseconds);
                _sahara = new SaharaProtocol(_wire);
                break;
            }

            lastException = new QcomProtocolException(Strings.Qcom_UnknownProtocol);
            if (!allowRecovery || attempt == MaxProtocolDetectionAttempts - 1)
                break;
            ResetSaharaStateMachine();
        }

        if (_wire is null)
            throw lastException ?? new QcomProtocolException(Strings.Qcom_UnknownProtocol);
        _targetInfo = new QcomTargetInfo { Vendor = QcomEvidenceMerger.ResolveVendor(_options.VendorOverride, null, QcomVendorKind.Generic) };
    }

    private void ResetSaharaStateMachine()
    {
        try { Transport.Flush(); } catch { }
        Span<byte> reset = stackalloc byte[SaharaResetStateMachineRequest.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(reset, (uint)SaharaResetStateMachineRequest.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(reset[4..], SaharaResetStateMachineRequest.Length);
        Transport.Write(reset);
    }

    private void SendSaharaHelloProbe()
    {
        Span<byte> response = stackalloc byte[SaharaHelloResponse.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(response, (uint)SaharaHelloResponse.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(response[4..], SaharaHelloResponse.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(response[8..], 2); // Sahara protocol version used by qdl.
        BinaryPrimitives.WriteUInt32LittleEndian(response[12..], 1); // Compatible version.
        BinaryPrimitives.WriteUInt32LittleEndian(response[16..], (uint)SaharaStatus.StatusSuccess);
        BinaryPrimitives.WriteUInt32LittleEndian(response[20..], (uint)SaharaMode.ImageTxPending);
        Transport.Write(response);
    }

    private static bool LooksLikeXml(ReadOnlySpan<byte> prefix)
    {
        int offset = 0;
        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF) offset = 3;
        while (offset < prefix.Length && prefix[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') offset++;
        return offset < prefix.Length && prefix[offset] == (byte)'<';
    }

    private void ProbeCore(IProgress<ProgressRecord>? progress)
    {
        _sahara!.Connect(progress);
        _targetInfo = _targetInfo! with { Sahara = _sahara.TargetInfo with { } };
    }

    private void UploadCore(IReadOnlyList<SaharaImageEntry> images, IProgress<ProgressRecord>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);
        var ids = new HashSet<int>();
        foreach (var image in images)
        {
            ct.ThrowIfCancellationRequested();
            if (image is null || image.Id < 0 || image.Length <= 0 || image.DataSource is null || image.DataSource.Length != image.Length || !ids.Add(image.Id))
                throw new QcomResourceException(Strings.Qcom_InvalidResource);
            using (Stream stream = image.DataSource.OpenStream())
                if (stream is null || !stream.CanRead || !stream.CanSeek) throw new QcomResourceException(Strings.Qcom_InvalidResource);
            if (_inspector.TryInspect(image.DataSource, out var info) && info.IsProgrammer) _programmer = info;
        }
        if (images.Count == 0) throw new QcomResourceException(Strings.Qcom_InvalidResource);
        ct.ThrowIfCancellationRequested();
        _sahara!.UploadImageCancelable(images, null, progress, ct);
        _firehose = new FirehoseSession(_wire!, _options.ReadTimeoutMilliseconds);
        _targetInfo = _targetInfo! with
        {
            ProgrammerCaHash = _programmer?.RootCaHash,
            ProgrammerMaxPayloadSizeToTargetInBytes = _programmer?.MaxPayloadSizeToTargetInBytesSupported,
            ProgrammerSupportedCommands = _programmer?.SupportedCommands ?? new HashSet<string>(),
            OemName = _programmer?.OemName, SocName = _programmer?.SocName
        };
    }

    private void StartFirehose()
    {
        if (_firehose is null) throw new InvalidOperationException(Strings.Qcom_InvalidSessionState);
        if (_firehose.State == FirehoseSessionState.Created) _startup = _firehose.Start(_options.ConnectTimeoutMilliseconds);
        var vendor = VendorStrategyResolver.Resolve(_options.VendorOverride, _startup?.Logs.Select(x => x.Message), _programmer?.Vendor ?? QcomVendorKind.Generic);
        _targetInfo = (_targetInfo ?? new QcomTargetInfo()) with
        {
            Vendor = vendor.Vendor,
            SecureBoot = SecureBootEvaluator.Evaluate((_targetInfo?.Sahara?.CaHash ?? ReadOnlyMemory<byte>.Empty).Span, (_programmer?.RootCaHash ?? ReadOnlyMemory<byte>.Empty).Span, _programmer is not null),
            Firehose = new FirehoseTargetInfo
            {
                BasicDevCharacteristics = FirehoseStorageInfoParser.ParseBasicInfo(new FirehoseCommandResult { Logs = _startup?.Logs ?? [] })
            }
        };
    }

    private FirehoseConfiguration LimitConfiguration(FirehoseConfiguration configuration)
    {
        configuration.Validate();
        return configuration with { MaxPayloadSizeToTargetInBytes = QcomEvidenceMerger.ResolveMaxPayload(
            Math.Min(configuration.MaxPayloadSizeToTargetInBytes, _options.Firehose.MaxPayloadSizeToTargetInBytes), null, _programmer?.MaxPayloadSizeToTargetInBytesSupported) };
    }

    private void SetStorage(FirehoseConfigureResult result, IFirehoseStoragePolicy? policy = null)
    {
        _storage = new FirehoseStorageService(_firehose!, result.Configuration, policy);
        var info = _storage.GetStorageInfo(0);
        _targetInfo = _targetInfo! with { Firehose = _targetInfo.Firehose! with { Configuration = _storage.Configuration, StorageInfos = [info] } };
    }

    private async ValueTask InitializeStorageAsync(FirehoseConfigureResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_options.OplusDigest.Mode == OplusDigestMode.None)
        {
            SetStorage(result);
            return;
        }

        if (_digestProvider is null)
            throw new QcomResourceException(Strings.Qcom_InvalidResource);
        var response = await ResolveResourceAsync(token => _digestProvider.ResolveAsync(
            new OplusDigestResourceRequest(TargetInfo!, _options.OplusDigest.Mode), token), ct).ConfigureAwait(false);
        IDataSource digest = response.Digest ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        int bufferSize = checked((int)(result.Configuration.MaxPayloadSizeToTargetInBytesSupported > 0
            ? result.Configuration.MaxPayloadSizeToTargetInBytesSupported
            : result.Configuration.MaxPayloadSizeToTargetInBytes));
        OplusDigestIndex index = new OplusDigestParser().Parse(digest);
        using (Stream stream = digest.OpenStream() ?? throw new QcomResourceException(Strings.Qcom_InvalidResource))
            _firehose!.SendDigest(stream, digest.Length, bufferSize, ct);
        IFirehoseStoragePolicy policy = _options.OplusDigest.Mode == OplusDigestMode.OplusDigestLegacy
            ? new OplusDigestLegacyPolicy(index, digest, _options.OplusDigest, bufferSize)
            : new OplusDigestPtPolicy(index);
        SetStorage(result, policy);
    }

    private void InitializeStorage(FirehoseConfigureResult result)
    {
        if (_options.OplusDigest.Mode == OplusDigestMode.None)
        {
            SetStorage(result);
            return;
        }

        if (_digestProvider is null)
            throw new QcomResourceException(Strings.Qcom_InvalidResource);
        var response = ResolveResourceSync(token => _digestProvider.ResolveAsync(
            new OplusDigestResourceRequest(TargetInfo!, _options.OplusDigest.Mode), token));
        IDataSource digest = response.Digest ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        int bufferSize = checked((int)(result.Configuration.MaxPayloadSizeToTargetInBytesSupported > 0
            ? result.Configuration.MaxPayloadSizeToTargetInBytesSupported
            : result.Configuration.MaxPayloadSizeToTargetInBytes));
        OplusDigestIndex index = new OplusDigestParser().Parse(digest);
        using (Stream stream = digest.OpenStream() ?? throw new QcomResourceException(Strings.Qcom_InvalidResource))
            _firehose!.SendDigest(stream, digest.Length, bufferSize);
        IFirehoseStoragePolicy policy = _options.OplusDigest.Mode == OplusDigestMode.OplusDigestLegacy
            ? new OplusDigestLegacyPolicy(index, digest, _options.OplusDigest, bufferSize)
            : new OplusDigestPtPolicy(index);
        SetStorage(result, policy);
    }

    private async ValueTask SendGenericDigestAsync(CancellationToken ct)
    {
        if (!_options.FirehoseDigest.Enabled || !_options.FirehoseDigest.SendOnConnect)
            return;
        if (_firehoseDigestProvider is null)
            throw new QcomResourceException(Strings.Qcom_InvalidResource);
        FirehoseDigestResourceResponse response = await ResolveResourceAsync(
            token => _firehoseDigestProvider.ResolveAsync(new FirehoseDigestResourceRequest(TargetInfo!), token), ct)
            .ConfigureAwait(false);
        IDataSource digest = response.Digest ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        if (digest.Length is <= 0 or > FirehoseConstants.MaximumRawTransferLength)
            throw new QcomResourceException(Strings.Qcom_InvalidResource);
        int bufferSize = checked((int)(_options.Firehose.MaxPayloadSizeToTargetInBytes));
        using Stream source = digest.OpenStream() ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        if (!source.CanRead)
            throw new QcomResourceException(Strings.Qcom_InvalidResource);
        _firehose!.SendDigest(source, digest.Length, bufferSize, ct);
    }

    private void SendGenericDigest()
    {
        if (!_options.FirehoseDigest.Enabled || !_options.FirehoseDigest.SendOnConnect)
            return;
        if (_firehoseDigestProvider is null)
            throw new QcomResourceException(Strings.Qcom_InvalidResource);
        FirehoseDigestResourceResponse response = ResolveResourceSync(
            token => _firehoseDigestProvider.ResolveAsync(new FirehoseDigestResourceRequest(TargetInfo!), token));
        IDataSource digest = response.Digest ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        if (digest.Length is <= 0 or > FirehoseConstants.MaximumRawTransferLength)
            throw new QcomResourceException(Strings.Qcom_InvalidResource);
        int bufferSize = checked((int)_options.Firehose.MaxPayloadSizeToTargetInBytes);
        using Stream source = digest.OpenStream() ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        if (!source.CanRead)
            throw new QcomResourceException(Strings.Qcom_InvalidResource);
        _firehose!.SendDigest(source, digest.Length, bufferSize);
    }

    private async ValueTask PrepareVipAsync(CancellationToken ct)
    {
        bool announced = _startup?.Logs.Any(log =>
            log.Message.Contains("VIP is enabled, receiving the signed table", StringComparison.OrdinalIgnoreCase)) == true;
        if (!_options.FirehoseVip.Enabled)
        {
            if (announced && _options.FirehoseVip.RequireStartupMarker)
            throw new QcomResourceException(Strings.Qcom_FirehoseVipTablesRequired);
            return;
        }
        if (_firehoseVipProvider is null)
            throw new QcomResourceException(Strings.Qcom_FirehoseVipProviderRequired);
        if (_options.FirehoseVip.RequireStartupMarker && !announced)
            throw new QcomResourceException(Strings.Qcom_FirehoseVipNotAnnounced);
        FirehoseVipResourceResponse response = await ResolveResourceAsync(
            token => _firehoseVipProvider.ResolveAsync(new FirehoseVipResourceRequest(TargetInfo!), token), ct)
            .ConfigureAwait(false);
        _vipPolicy = new FirehoseVipTransferPolicy(response);
        _firehose!.SetBeforeCommand(_vipPolicy.BeforeCommand);
    }

    private void PrepareVip()
    {
        bool announced = _startup?.Logs.Any(log =>
            log.Message.Contains("VIP is enabled, receiving the signed table", StringComparison.OrdinalIgnoreCase)) == true;
        if (!_options.FirehoseVip.Enabled)
        {
            if (announced && _options.FirehoseVip.RequireStartupMarker)
                throw new QcomResourceException(Strings.Qcom_FirehoseVipTablesRequired);
            return;
        }
        if (_firehoseVipProvider is null)
            throw new QcomResourceException(Strings.Qcom_FirehoseVipProviderRequired);
        if (_options.FirehoseVip.RequireStartupMarker && !announced)
            throw new QcomResourceException(Strings.Qcom_FirehoseVipNotAnnounced);
        FirehoseVipResourceResponse response = ResolveResourceSync(
            token => _firehoseVipProvider.ResolveAsync(new FirehoseVipResourceRequest(TargetInfo!), token));
        _vipPolicy = new FirehoseVipTransferPolicy(response);
        _firehose!.SetBeforeCommand(_vipPolicy.BeforeCommand);
    }

    private async ValueTask<bool> AuthenticateXiaomiAsync(FirehoseNakException exception, CancellationToken ct)
    {
        var authentication = new XiaomiAuthentication(_firehose!, checked((int)_options.Firehose.MaxPayloadSizeToTargetInBytes));
        if (_options.AuthenticationKind is null && authentication.TryAuthenticateBuiltIn(ct))
            return true;
        if (_authenticationProvider is null)
            return false;
        var response = await AuthenticationResourceAsync(new VendorAuthenticationResourceRequest(QcomAuthenticationKind.XiaomiSignature, TargetInfo!), ct).ConfigureAwait(false);
        using (response.Payload)
        {
            ct.ThrowIfCancellationRequested();
            authentication.Authenticate(response.Payload.Memory.Span);
        }
        return true;
    }

    private Func<FirehoseNakException, CancellationToken, ValueTask<bool>>? GetXiaomiAuthenticationAsync() =>
        _options.AuthenticationKind is null or QcomAuthenticationKind.XiaomiSignature ? AuthenticateXiaomiAsync : null;

    private bool AuthenticateXiaomi(FirehoseNakException exception)
    {
        var authentication = new XiaomiAuthentication(_firehose!, checked((int)_options.Firehose.MaxPayloadSizeToTargetInBytes));
        if (_options.AuthenticationKind is null && authentication.TryAuthenticateBuiltIn())
            return true;
        if (_authenticationProvider is null)
            return false;
        var response = AuthenticationResource(new VendorAuthenticationResourceRequest(
            QcomAuthenticationKind.XiaomiSignature, TargetInfo!));
        using (response.Payload)
        {
            authentication.Authenticate(response.Payload.Memory.Span);
        }
        return true;
    }

    private Func<FirehoseNakException, bool>? GetXiaomiAuthentication() =>
        _options.AuthenticationKind is null or QcomAuthenticationKind.XiaomiSignature ? AuthenticateXiaomi : null;

    private async ValueTask VerifyVendorAsync(CancellationToken ct)
    {
        if (_targetInfo!.Vendor is not (QcomVendorKind.Nothing or QcomVendorKind.OnePlus)) return;
        QcomAuthenticationKind kind = _targetInfo.Vendor == QcomVendorKind.Nothing ? QcomAuthenticationKind.NothingProjectVerify : QcomAuthenticationKind.OnePlusProjectVerify;
        var verifier = new OnePlusProjectVerifier(_firehose!);
        bool software = _targetInfo.Firehose?.BasicDevCharacteristics?.SupportedFunctions.Contains("setswprojmodel", StringComparer.OrdinalIgnoreCase) == true;
        Dictionary<string, string> properties = [];
        if (software) properties["device_timestamp"] = verifier.BeginSoftwareVerification().ToString(CultureInfo.InvariantCulture);
        var response = await AuthenticationResourceAsync(new VendorAuthenticationResourceRequest(kind, TargetInfo!, properties: properties), ct).ConfigureAwait(false);
        using (response.Payload)
        {
            ct.ThrowIfCancellationRequested();
            if (_targetInfo.Vendor == QcomVendorKind.Nothing)
            {
                string projectId = RequiredProperty(response, "projectId");
                ulong serial = _targetInfo.Sahara?.Serial ?? _targetInfo.Firehose?.BasicDevCharacteristics?.SerialNumber ?? 0;
                if (serial == 0) throw new QcomAuthenticationException(Strings.Qcom_InvalidAuthentication);
                new NothingProjectVerifier(_firehose!).Verify(serial, projectId);
            }
            else
            {
                string publicKey = RequiredProperty(response, "publicKey");
                string token = Encoding.ASCII.GetString(response.Payload.Memory.Span);
                if (software)
                {
                    verifier.VerifySoftwareProject(publicKey, token);
                    verifier.SetNetworkType();
                    verifier.EndSoftwareVerification();
                }
                else if (response.Properties.TryGetValue("mode", out string? mode) && mode == "demacia") verifier.VerifyDemacia(publicKey, token);
                else verifier.VerifyProject(publicKey, token);
            }
        }
    }

    private void VerifyVendor()
    {
        if (_targetInfo!.Vendor is not (QcomVendorKind.Nothing or QcomVendorKind.OnePlus)) return;
        QcomAuthenticationKind kind = _targetInfo.Vendor == QcomVendorKind.Nothing
            ? QcomAuthenticationKind.NothingProjectVerify
            : QcomAuthenticationKind.OnePlusProjectVerify;
        var verifier = new OnePlusProjectVerifier(_firehose!);
        bool software = _targetInfo.Firehose?.BasicDevCharacteristics?.SupportedFunctions.Contains(
            "setswprojmodel", StringComparer.OrdinalIgnoreCase) == true;
        Dictionary<string, string> properties = [];
        if (software)
            properties["device_timestamp"] = verifier.BeginSoftwareVerification().ToString(CultureInfo.InvariantCulture);
        var response = AuthenticationResource(new VendorAuthenticationResourceRequest(kind, TargetInfo!, properties: properties));
        using (response.Payload)
        {
            if (_targetInfo.Vendor == QcomVendorKind.Nothing)
            {
                string projectId = RequiredProperty(response, "projectId");
                ulong serial = _targetInfo.Sahara?.Serial ?? _targetInfo.Firehose?.BasicDevCharacteristics?.SerialNumber ?? 0;
                if (serial == 0) throw new QcomAuthenticationException(Strings.Qcom_InvalidAuthentication);
                new NothingProjectVerifier(_firehose!).Verify(serial, projectId);
            }
            else
            {
                string publicKey = RequiredProperty(response, "publicKey");
                string token = Encoding.ASCII.GetString(response.Payload.Memory.Span);
                if (software)
                {
                    verifier.VerifySoftwareProject(publicKey, token);
                    verifier.SetNetworkType();
                    verifier.EndSoftwareVerification();
                }
                else if (response.Properties.TryGetValue("mode", out string? mode) && mode == "demacia")
                    verifier.VerifyDemacia(publicKey, token);
                else
                    verifier.VerifyProject(publicKey, token);
            }
        }
    }

    private static string RequiredProperty(VendorAuthenticationResourceResponse response, string name) =>
        response.Properties.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new QcomAuthenticationException(Strings.Qcom_InvalidAuthentication);

    private ValueTask<VendorAuthenticationResourceResponse> AuthenticationResourceAsync(VendorAuthenticationResourceRequest request, CancellationToken ct)
    {
        if (_authenticationProvider is null) throw new QcomAuthenticationException(Strings.Qcom_AuthenticationProviderRequired);
        return ResolveResourceAsync(token => _authenticationProvider.ResolveAsync(request, token), ct);
    }

    private VendorAuthenticationResourceResponse AuthenticationResource(VendorAuthenticationResourceRequest request)
    {
        if (_authenticationProvider is null)
            throw new QcomAuthenticationException(Strings.Qcom_AuthenticationProviderRequired);
        return ResolveResourceSync(token => _authenticationProvider.ResolveAsync(request, token));
    }

    private async ValueTask<T> ResolveResourceAsync<T>(Func<CancellationToken, ValueTask<T>> request, CancellationToken ct) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.ResourceRequestTimeoutMilliseconds);
        Task<T>? pending = null;
        try
        {
            pending = request(timeout.Token).AsTask();
            var result = await pending.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (result is null) throw new QcomResourceException(Strings.Qcom_InvalidResource);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (pending is not null) _ = DisposeLateAuthenticationAsync(pending);
            throw;
        }
        catch (Exception exception) when (exception is not QcomProtocolException)
        { throw new QcomResourceException(Strings.Qcom_InvalidResource, exception); }
    }

    private T ResolveResourceSync<T>(Func<CancellationToken, ValueTask<T>> request) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(_options.ResourceRequestTimeoutMilliseconds);
        Task<T>? pending = null;
        try
        {
            pending = request(timeout.Token).AsTask();
            T? result = pending.WaitAsync(timeout.Token).GetAwaiter().GetResult();
            return result ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        }
        catch (OperationCanceledException)
        {
            if (pending is not null)
                _ = DisposeLateAuthenticationAsync(pending);
            throw;
        }
        catch (Exception exception) when (exception is not QcomProtocolException)
        {
            throw new QcomResourceException(Strings.Qcom_InvalidResource, exception);
        }
    }

    private static async Task DisposeLateAuthenticationAsync<T>(Task<T> task)
    {
        try { if (await task.ConfigureAwait(false) is VendorAuthenticationResourceResponse response) response.Payload.Dispose(); }
        catch { /* Observe a provider failure after cancellation without retaining authentication material. */ }
    }

    private Operation Enter()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_gate.Wait(0)) throw new InvalidOperationException(Strings.Qcom_OperationInProgress);
        if (Volatile.Read(ref _disposed) != 0) { _gate.Release(); throw new ObjectDisposedException(nameof(QcomProtocol)); }
        return new Operation(this);
    }
    private Operation EnterConnected()
    {
        var operation = Enter();
        if (!IsConnected) { operation.Dispose(); throw new QcomSessionInvalidException(Strings.Qcom_InvalidSessionState); }
        return operation;
    }
    private void Cleanup()
    {
        _connected = false;
        Interlocked.Increment(ref _generation);
        _storage = null;
        bool hadFirehose = _firehose is not null;
        if (!hadFirehose && _sahara is not null && Transport.IsOpen)
        {
            try { _sahara.ResetStateMachine(); } catch { }
        }
        _firehose?.Dispose(); _firehose = null;
        _sahara?.Dispose(); _sahara = null;
        _startup = null; _programmer = null; _targetInfo = null;
        _vipPolicy = null;
        if (hadFirehose && _wire is not null)
            _firehose = new FirehoseSession(_wire, _options.ReadTimeoutMilliseconds);
        else
            _wire = null;
        if (Transport.IsOpen) Transport.Close();
    }
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_gate.Wait(0)) throw new InvalidOperationException(Strings.Qcom_OperationInProgress);
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel(); Cleanup();
            if (!_leaveTransportOpen) Transport.Dispose();
        }
        finally { _gate.Release(); }
        GC.SuppressFinalize(this);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Cleanup(); if (!_leaveTransportOpen) Transport.Dispose(); }
        finally { _gate.Release(); }
        GC.SuppressFinalize(this);
    }
    private sealed class Operation(QcomProtocol owner) : IDisposable
    {
        public void Dispose()
        {
            if (owner._firehose?.State == FirehoseSessionState.Faulted) owner.Cleanup();
            owner._gate.Release();
        }
    }
    private sealed class InlineProgress(IProgress<ProgressRecord> progress, long total) : IProgress<long>
    { public void Report(long value) => progress.Report(new ProgressRecord(total, value, string.Empty)); }
    private static IProgress<long>? Adapt(IProgress<ProgressRecord>? progress, long total) => progress is null ? null : new InlineProgress(progress, total);
    private static QcomTargetInfo? Snapshot(QcomTargetInfo? info) => info is null ? null : info with
    {
        Sahara = info.Sahara is null ? null : info.Sahara with { MsmHwInfo = info.Sahara.MsmHwInfo is null ? null : info.Sahara.MsmHwInfo with { } },
        Firehose = info.Firehose is null ? null : info.Firehose with
        {
            BasicDevCharacteristics = info.Firehose.BasicDevCharacteristics is null ? null : info.Firehose.BasicDevCharacteristics with { SupportedFunctions = info.Firehose.BasicDevCharacteristics.SupportedFunctions.ToArray() },
            StorageInfos = info.Firehose.StorageInfos.ToArray()
        }
    };
}
