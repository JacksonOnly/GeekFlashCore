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
using Serilog;

namespace GeekFlashCore.Protocol.Qcom;

/// <summary>A serialized Qualcomm session. Resource providers are asynchronous; all wire I/O is synchronous.</summary>
public sealed partial class QcomProtocol : IQcomProtocol, IBlockDeviceProvider, IDisposable
{
    private const int MaxProtocolDetectionAttempts = 4;
    private const int DefaultProtocolProbeTimeoutMilliseconds = 250;
    private bool _resumeAwaitingDigestUsed;
    private readonly QcomProtocolOptions _options;
    private OplusDigestConfiguration _oplusConfiguration;
    private readonly ISaharaImageProvider? _imageProvider;
    private readonly IOplusDigestProvider? _digestProvider;
    private readonly IFirehoseDigestProvider? _firehoseDigestProvider;
    private readonly IFirehoseVipProvider? _firehoseVipProvider;
    private readonly IVendorAuthenticationProvider? _authenticationProvider;
    private readonly IFirehoseConfigurationProvider? _configurationProvider;
    private readonly IVendorSelectionProvider? _vendorSelectionProvider;
    private readonly IQcomProgrammerInspector _inspector;
    private readonly QcomResourceResolver _resourceResolver;
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
    private IDataSource? _oplusDigest;
    private OplusDigestIndex? _oplusIndex;
    private bool _oplusAuthenticated;
    private OnePlusAuthenticationContext? _onePlusAuthentication;
    private QcomVendorKind? _selectedVendor;

    public QcomProtocol(ITransport transport, QcomProtocolOptions? options = null,
        ISaharaImageProvider? imageProvider = null, IOplusDigestProvider? digestProvider = null,
        IVendorAuthenticationProvider? authenticationProvider = null,
        IFirehoseConfigurationProvider? configurationProvider = null,
        bool leaveTransportOpen = false, IQcomProgrammerInspector? programmerInspector = null,
        IFirehoseDigestProvider? firehoseDigestProvider = null,
        IFirehoseVipProvider? firehoseVipProvider = null)
        : this(transport, options, imageProvider, digestProvider, authenticationProvider,
            configurationProvider, leaveTransportOpen, programmerInspector, firehoseDigestProvider,
            firehoseVipProvider, null)
    {
    }

    /// <summary>Creates a serialized session with an optional host vendor selection provider.</summary>
    public QcomProtocol(ITransport transport, QcomProtocolOptions? options,
        ISaharaImageProvider? imageProvider, IOplusDigestProvider? digestProvider,
        IVendorAuthenticationProvider? authenticationProvider,
        IFirehoseConfigurationProvider? configurationProvider,
        bool leaveTransportOpen, IQcomProgrammerInspector? programmerInspector,
        IFirehoseDigestProvider? firehoseDigestProvider,
        IFirehoseVipProvider? firehoseVipProvider,
        IVendorSelectionProvider? vendorSelectionProvider)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new QcomProtocolOptions();
        _options.Validate();
        _oplusConfiguration = _options.OplusDigest;
        _resourceResolver = new QcomResourceResolver(
            _options.ResourceRequestTimeoutMilliseconds,
            _lifetime.Token);
        _imageProvider = imageProvider;
        _digestProvider = digestProvider;
        _firehoseDigestProvider = firehoseDigestProvider;
        _firehoseVipProvider = firehoseVipProvider;
        _authenticationProvider = authenticationProvider;
        _configurationProvider = configurationProvider;
        _vendorSelectionProvider = vendorSelectionProvider;
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
                var response = await _resourceResolver.ResolveAsync(token => _imageProvider.ResolveAsync(
                    new SaharaImageEntryRequest(Snapshot(_targetInfo)!.Sahara!) { VendorHint = _options.VendorOverride }, token), ct).ConfigureAwait(false);
                if (response.Entries is null || response.Entries.Count == 0)
                    throw new QcomResourceException(Strings.Qcom_LoaderProviderRequired);
                UploadCore(response.Entries.ToArray(), progress, ct);
            }
            StartFirehose();
            if (NeedsVendorSelection)
                ApplyVendorSelection(await _resourceResolver.ResolveAsync(token => _vendorSelectionProvider!.ResolveAsync(
                    new VendorSelectionRequest(TargetInfo!), token), ct).ConfigureAwait(false));
            await PrepareOplusAsync(ct).ConfigureAwait(false);
            await PrepareVipAsync(ct).ConfigureAwait(false);
            FirehoseConfiguration configuration = _options.Firehose;
            if (_configurationProvider is not null)
            {
                var response = await _resourceResolver.ResolveAsync(token => _configurationProvider.ResolveAsync(
                    new FirehoseConfigurationRequest(TargetInfo!, configuration), token), ct).ConfigureAwait(false);
                configuration = response.Configuration ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
            }
            configuration = LimitConfiguration(configuration);
            ct.ThrowIfCancellationRequested();
            await SendGenericDigestAsync(ct).ConfigureAwait(false);
            FirehoseConfigureResult configured = await new ConfigureNegotiator(_firehose!).NegotiateAsync(
                configuration, _targetInfo!.Vendor, GetXiaomiAuthenticationAsync(), ct).ConfigureAwait(false);
            try
            {
                await InitializeStorageAsync(configured, ct).ConfigureAwait(false);
            }
            catch (FirehoseNakException exception) when (TryGetStorageSectorSize(configuration, exception, out uint sectorSize))
            {
                configuration = configuration with { SectorSizeInBytes = sectorSize };
                configured = await new ConfigureNegotiator(_firehose!).NegotiateAsync(
                    LimitConfiguration(configuration), _targetInfo!.Vendor, GetXiaomiAuthenticationAsync(), ct)
                    .ConfigureAwait(false);
                await InitializeStorageAsync(configured, ct).ConfigureAwait(false);
            }
            catch (FirehoseNakException exception) when (ShouldRetryStorageAsUfs(configuration, configured, exception))
            {
                configuration = CreateUfsFallbackConfiguration(configuration);
                configured = await new ConfigureNegotiator(_firehose!).NegotiateAsync(
                    LimitConfiguration(configuration), _targetInfo!.Vendor, GetXiaomiAuthenticationAsync(), ct)
                    .ConfigureAwait(false);
                await InitializeStorageAsync(configured, ct).ConfigureAwait(false);
            }
            CacheAdditionalStorageInfos(ct);
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
            if (NeedsVendorSelection)
                ApplyVendorSelection(_resourceResolver.Resolve(token => _vendorSelectionProvider!.ResolveAsync(
                    new VendorSelectionRequest(TargetInfo!), token)));
            PrepareOplus();
            PrepareVip();
            FirehoseConfiguration configuration = _options.Firehose;
            if (_configurationProvider is not null)
            {
                var response = _resourceResolver.Resolve(token => _configurationProvider.ResolveAsync(
                    new FirehoseConfigurationRequest(TargetInfo!, configuration), token));
                configuration = response.Configuration ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
            }
            configuration = LimitConfiguration(configuration);
            SendGenericDigest();
            var result = new ConfigureNegotiator(_firehose!).Negotiate(
                configuration, _targetInfo!.Vendor, GetXiaomiAuthentication());
            try
            {
                InitializeStorage(result);
            }
            catch (FirehoseNakException exception) when (TryGetStorageSectorSize(configuration, exception, out uint sectorSize))
            {
                configuration = configuration with { SectorSizeInBytes = sectorSize };
                result = new ConfigureNegotiator(_firehose!).Negotiate(
                    LimitConfiguration(configuration), _targetInfo!.Vendor, GetXiaomiAuthentication());
                InitializeStorage(result);
            }
            catch (FirehoseNakException exception) when (ShouldRetryStorageAsUfs(configuration, result, exception))
            {
                configuration = CreateUfsFallbackConfiguration(configuration);
                result = new ConfigureNegotiator(_firehose!).Negotiate(
                    LimitConfiguration(configuration), _targetInfo!.Vendor, GetXiaomiAuthentication());
                InitializeStorage(result);
            }
            CacheAdditionalStorageInfos(CancellationToken.None);
            VerifyVendor();
            _connected = true;
            progress?.Report(new ProgressRecord(1, 1, Strings.Progress_Connected));
            return result.CommandResult;
        }
        catch { Cleanup(); throw; }
    }

    public long Program(FirehoseProgramRequest request, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = EnterConnected();
        cancellationToken.ThrowIfCancellationRequested();
        request.Validate();
        ValidateLun(request.PhysicalPartitionNumber);
        if (request.SectorSizeInBytes != _storage!.Configuration.SectorSizeInBytes)
            throw new ArgumentException(Strings.Qcom_TargetSectorSizeMismatch, nameof(request));
        ValidateCachedSectorRange(new TargetRange(request.PhysicalPartitionNumber, request.StartSector,
            request.SectorCount, request.SectorSizeInBytes, request.Label));
        return _storage!.Program(request, ProgramProgress(progress), cancellationToken);
    }

    public long Read(FirehoseReadRequest request, Stream destination, IProgress<ProgressRecord>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var operation = EnterConnected();
        cancellationToken.ThrowIfCancellationRequested();
        request.Validate();
        ValidateLun(request.PhysicalPartitionNumber);
        if (request.SectorSizeInBytes != _storage!.Configuration.SectorSizeInBytes)
            throw new ArgumentException(Strings.Qcom_TargetSectorSizeMismatch, nameof(request));
        ValidateCachedSectorRange(new TargetRange(request.PhysicalPartitionNumber, request.StartSector,
            request.SectorCount, request.SectorSizeInBytes, request.Label));
        return ReadWithProgress(request, destination, progress, cancellationToken);
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
        _storage!.Power(value, cancellationToken: ct);
        Cleanup();
        return Task.FromResult(true);
    }

    private void DetectProtocol(bool probeFirehoseOnTimeout = true)
    {
        if (!Transport.IsOpen) Transport.Open();
        // Only the caller can identify a silent loader as waiting for its first table.
        // This opt-in performs no probe, reset, flush or XML before the Digest.
        if (probeFirehoseOnTimeout && _options.OplusDigest.ResumeAwaitingDigest && !_resumeAwaitingDigestUsed)
        {
            _resumeAwaitingDigestUsed = true;
            _wire = new QcomSessionTransport(Transport, [], _options.ReadTimeoutMilliseconds);
            _firehose = CreateFirehoseSession(_wire);
            _firehose.StartWithoutStartupLogs();
            _targetInfo = new QcomTargetInfo { Vendor = QcomEvidenceMerger.ResolveVendor(_options.VendorOverride, null, QcomVendorKind.Generic) };
            Log.Warning(Strings.Qcom_LogOplusResume);
            return;
        }
        bool allowRecovery = probeFirehoseOnTimeout && _options.ProbeFirehoseOnSaharaTimeout &&
            _options.OplusDigest.Mode == OplusDigestMode.None;
        Exception? lastException = null;
        for (int attempt = 0; attempt < MaxProtocolDetectionAttempts; attempt++)
        {
            byte[] prefix = new byte[8];
            try
            {
                Transport.ReadExact(prefix, _options.OplusDigest.Mode == OplusDigestMode.None
                    ? GetProtocolProbeTimeout() : _options.ConnectTimeoutMilliseconds);
            }
            catch (TimeoutException exception)
            {
                lastException = exception;
                if (!allowRecovery)
                {
                    if (probeFirehoseOnTimeout && _options.OplusDigest.Mode != OplusDigestMode.None)
                        Log.Warning(Strings.Qcom_LogOplusSilent);
                    throw;
                }
                if (attempt == 0)
                    SendSaharaHelloProbe();
                else if (attempt == 1 && TryDetectRunningFirehose())
                    break;
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
                _firehose = CreateFirehoseSession(_wire);
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

    private int GetProtocolProbeTimeout() =>
        Math.Min(_options.ConnectTimeoutMilliseconds, DefaultProtocolProbeTimeoutMilliseconds);

    private bool TryDetectRunningFirehose()
    {
        try
        {
            _wire = new QcomSessionTransport(Transport, [], _options.ReadTimeoutMilliseconds);
            _firehose = CreateFirehoseSession(_wire);
            if (_firehose.TryProbe(GetProtocolProbeTimeout(), out _startup))
                return true;
        }
        catch (Exception exception) when (exception is TimeoutException or FirehoseProtocolException or InvalidOperationException)
        {
            // Continue with the bounded Sahara reset/retry path.
        }

        _firehose?.Dispose();
        _firehose = null;
        _wire = null;
        return false;
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
        _firehose = CreateFirehoseSession(_wire!);
        _targetInfo = _targetInfo! with
        {
            ProgrammerCaHash = _programmer?.RootCaHash,
            ProgrammerMaxPayloadSizeToTargetInBytes = _programmer?.MaxPayloadSizeToTargetInBytesSupported,
            ProgrammerSupportedCommands = _programmer?.SupportedCommands ?? new HashSet<string>(),
            OemName = _programmer?.OemName, SocName = _programmer?.SocName
        };
    }

    private FirehoseSession CreateFirehoseSession(ITransport wire)
    {
        var session = new FirehoseSession(wire, _options.ReadTimeoutMilliseconds);
        if (_oplusConfiguration.Mode == OplusDigestMode.OplusDigestLegacy)
        {
            session.ConfigureLegacyWire(_oplusConfiguration);
            session.SetXmlDeclarationAttribute("chimerais=\"power\"");
        }
        return session;
    }

    private void StartFirehose()
    {
        if (_firehose is null) throw new InvalidOperationException(Strings.Qcom_InvalidSessionState);
        _firehose.SetXmlDeclarationAttribute(
            _oplusConfiguration.Mode == OplusDigestMode.OplusDigestLegacy
                ? "chimerais=\"power\""
                : null);
        if (_firehose.State == FirehoseSessionState.Created)
        {
            try
            {
                _startup = _firehose.Start(_options.ReadTimeoutMilliseconds);
            }
            catch (TimeoutException) when (_oplusConfiguration.Mode == OplusDigestMode.None &&
                _sahara is null && !_firehose.StartupDataReceived)
            {
                // Probe only a silent, already running session. A fresh loader or any startup
                // bytes may be waiting for a signed table and must not receive XML here.
                _firehose.Dispose();
                _firehose = CreateFirehoseSession(_wire!);
                if (!_firehose.TryProbe(GetProtocolProbeTimeout(), out _startup))
                    throw;
            }
        }
        var vendor = VendorStrategyResolver.Resolve(_options.VendorOverride, _startup?.Logs.Select(x => x.Message),
            _programmer?.Vendor ?? QcomVendorKind.Generic, VendorStrategyResolver.DetectSaharaVendor(_targetInfo?.Sahara));
        _targetInfo = (_targetInfo ?? new QcomTargetInfo()) with
        {
            Vendor = _selectedVendor ?? vendor.Vendor,
            SecureBoot = SecureBootEvaluator.Evaluate((_targetInfo?.Sahara?.CaHash ?? ReadOnlyMemory<byte>.Empty).Span, (_programmer?.RootCaHash ?? ReadOnlyMemory<byte>.Empty).Span, _programmer is not null),
            Firehose = new FirehoseTargetInfo
            {
                BasicDevCharacteristics = FirehoseStorageInfoParser.ParseBasicInfo(new FirehoseCommandResult
                {
                    Logs = _startup?.Logs ?? [],
                    Attributes = _startup?.Attributes ?? new Dictionary<string, string>()
                })
            }
        };
    }

    private bool NeedsVendorSelection => _vendorSelectionProvider is not null && _selectedVendor is null &&
        _options.VendorOverride == QcomVendorKind.Auto && _targetInfo!.Vendor == QcomVendorKind.Generic;

    private void ApplyVendorSelection(VendorSelectionResponse response)
    {
        if (!Enum.IsDefined(response.Vendor) || response.Vendor == QcomVendorKind.Auto)
            throw new QcomResourceException(Strings.Qcom_InvalidResource);
        _targetInfo = _targetInfo! with { Vendor = response.Vendor };
        _selectedVendor = response.Vendor;
    }

    private FirehoseConfiguration LimitConfiguration(FirehoseConfiguration configuration)
    {
        configuration.Validate();
        if (configuration.MemoryName == FirehoseStorage.None && _startup?.Logs.Any(static log =>
                log.Message.TrimStart().StartsWith("ufs:", StringComparison.OrdinalIgnoreCase)) == true)
            configuration = CreateUfsFallbackConfiguration(configuration);
        return configuration with { MaxPayloadSizeToTargetInBytes = QcomEvidenceMerger.ResolveMaxPayload(
            Math.Min(configuration.MaxPayloadSizeToTargetInBytes, _options.Firehose.MaxPayloadSizeToTargetInBytes), null, _programmer?.MaxPayloadSizeToTargetInBytesSupported) };
    }

    private static FirehoseConfiguration CreateUfsFallbackConfiguration(FirehoseConfiguration configuration) =>
        configuration with { MemoryName = FirehoseStorage.Ufs, SectorSizeInBytes = 4096 };

    private static bool ShouldRetryStorageAsUfs(
        FirehoseConfiguration requested,
        FirehoseConfigureResult configured,
        FirehoseNakException exception)
    {
        if (requested.MemoryName != FirehoseStorage.None)
            return false;
        if (configured.Configuration.Storage != FirehoseStorage.Emmc)
            return false;

        return NakEvidence(exception).Any(message =>
            message.Contains("Failed to open the SDCC Device", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> NakEvidence(FirehoseNakException exception) =>
        exception.Result.Logs.Select(static log => log.Message)
            .Append(exception.Result.Attributes.TryGetValue("reason", out string? reason) ? reason : exception.Message);

    private static bool TryGetStorageSectorSize(
        FirehoseConfiguration requested,
        FirehoseNakException exception,
        out uint sectorSize)
    {
        sectorSize = 0;
        if (requested.MemoryName != FirehoseStorage.None)
            return false;

        IEnumerable<string> messages = NakEvidence(exception);
        foreach (string message in messages)
        {
            if (message.Contains("device sector size (512)", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("disk sector size 512", StringComparison.OrdinalIgnoreCase))
            {
                sectorSize = 512;
                return true;
            }
            if (message.Contains("device sector size (4096)", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("disk sector size 4096", StringComparison.OrdinalIgnoreCase))
            {
                sectorSize = 4096;
                return true;
            }
        }
        return false;
    }

    private void SetStorage(FirehoseConfigureResult result, IFirehoseStoragePolicy? policy = null)
    {
        _storage = new FirehoseStorageService(_firehose!, result.Configuration, policy);
        var info = _storage.GetStorageInfo(0);
        FirehoseBasicDevInfo? basic = _targetInfo?.Firehose?.BasicDevCharacteristics;
        if (basic is not null)
        {
            uint serial = basic.SerialNumber;
            if (serial == 0 && info.Properties.TryGetValue("serial_num", out string? value))
            {
                string text = value.Trim();
                NumberStyles style = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
                if (style == NumberStyles.AllowHexSpecifier) text = text[2..];
                if (uint.TryParse(text, style, CultureInfo.InvariantCulture, out uint parsed)) serial = parsed;
            }
            DateTime build = basic.BuildDate;
            if (build == default && result.Configuration.DateTime != default)
                build = result.Configuration.DateTime;
            uint? chipId = basic.ChipId ?? _targetInfo?.Sahara?.MsmHwInfo?.MsmId;
            string? chipName = basic.ChipName;
            if (string.IsNullOrWhiteSpace(chipName))
                chipName = _targetInfo?.SocName ?? (chipId is { } id ? $"MSM 0x{id:X8}" : null);
            basic = basic with { SerialNumber = serial, BuildDate = build, ChipId = chipId, ChipName = chipName };
        }
        string? ufsName = _targetInfo?.Firehose?.UfsName;
        if (_storage.Configuration.Storage == FirehoseStorage.Ufs &&
            info.Properties.TryGetValue("prod_name", out string? product) && !string.IsNullOrWhiteSpace(product))
            ufsName = product.Trim();
        _targetInfo = _targetInfo! with
        {
            Firehose = _targetInfo.Firehose! with
            {
                Configuration = _storage.Configuration, StorageInfos = [info], BasicDevCharacteristics = basic,
                UfsName = ufsName,
                TargetName = string.IsNullOrWhiteSpace(result.Configuration.TargetName)
                    ? _targetInfo.Firehose.TargetName : result.Configuration.TargetName
            }
        };
    }

    // Configure owns the storage snapshot; later partition and range operations only consume it.
    private void CacheAdditionalStorageInfos(CancellationToken cancellationToken)
    {
        FirehoseStorageInfo primary = _targetInfo?.Firehose?.StorageInfos
            .FirstOrDefault(static item => item.PhysicalPartitionNumber == 0)
            ?? throw new InvalidOperationException(Strings.Qcom_FirehoseNotConfigured);
        if (!primary.Properties.TryGetValue("num_physical", out string? value))
            return;
        if (!uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint count) ||
            count is 0 or > FirehoseConstants.MaximumPhysicalPartitionCount)
            throw new QcomProtocolException(Strings.Qcom_InvalidLunCount);

        for (uint lun = 1; lun < count; lun++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FirehoseStorageInfo info = _storage!.GetStorageInfo(lun);
            _targetInfo = _targetInfo! with
            {
                Firehose = _targetInfo.Firehose! with
                {
                    StorageInfos = _targetInfo.Firehose.StorageInfos
                        .Where(item => item.PhysicalPartitionNumber != lun)
                        .Append(info)
                        .OrderBy(item => item.PhysicalPartitionNumber)
                        .ToArray()
                }
            };
        }
    }

    private ValueTask InitializeStorageAsync(FirehoseConfigureResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        InitializeStorage(result);
        return ValueTask.CompletedTask;
    }

    private void InitializeStorage(FirehoseConfigureResult result)
    {
        if (_oplusConfiguration.Mode == OplusDigestMode.None)
        {
            SetStorage(result);
            return;
        }

        IDataSource digest = _oplusDigest ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        int bufferSize = FirehosePayloadLimits.GetTransferBufferSize(result.Configuration);
        IFirehoseStoragePolicy policy = _oplusConfiguration.Mode == OplusDigestMode.OplusDigestLegacy
            ? new OplusDigestLegacyPolicy(digest, _oplusConfiguration, bufferSize)
            : new OplusDigestPtPolicy(_oplusIndex ?? throw new QcomResourceException(Strings.Qcom_InvalidResource));
        SetStorage(result, policy);
    }

    private async ValueTask SendGenericDigestAsync(CancellationToken ct)
    {
        if (!_options.FirehoseDigest.Enabled || !_options.FirehoseDigest.SendOnConnect)
            return;
        if (_firehoseDigestProvider is null)
            throw new QcomResourceException(Strings.Qcom_InvalidResource);
        FirehoseDigestResourceResponse response = await _resourceResolver.ResolveAsync(
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
        FirehoseDigestResourceResponse response = _resourceResolver.Resolve(
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
        if (_oplusConfiguration.Mode != OplusDigestMode.None) return;
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
        FirehoseVipResourceResponse response = await _resourceResolver.ResolveAsync(
            token => _firehoseVipProvider.ResolveAsync(new FirehoseVipResourceRequest(TargetInfo!), token), ct)
            .ConfigureAwait(false);
        _vipPolicy = new FirehoseVipTransferPolicy(response);
        _firehose!.SetBeforeCommand(_vipPolicy.BeforeCommand);
        _firehose.SetCommandSent(_vipPolicy.CommandSent);
    }

    private void PrepareVip()
    {
        if (_oplusConfiguration.Mode != OplusDigestMode.None) return;
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
        FirehoseVipResourceResponse response = _resourceResolver.Resolve(
            token => _firehoseVipProvider.ResolveAsync(new FirehoseVipResourceRequest(TargetInfo!), token));
        _vipPolicy = new FirehoseVipTransferPolicy(response);
        _firehose!.SetBeforeCommand(_vipPolicy.BeforeCommand);
        _firehose.SetCommandSent(_vipPolicy.CommandSent);
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

    private ValueTask VerifyVendorAsync(CancellationToken ct)
    {
        VerifyVendorCore(ct);
        return ValueTask.CompletedTask;
    }

    private void VerifyVendor()
    {
        VerifyVendorCore(CancellationToken.None);
    }

    private void VerifyVendorCore(CancellationToken cancellationToken)
    {
        if (_targetInfo!.Vendor is not (QcomVendorKind.Nothing or QcomVendorKind.OnePlus))
            return;
        ulong serial = _targetInfo.Sahara?.Serial ??
                       _targetInfo.Firehose?.BasicDevCharacteristics?.SerialNumber ?? 0;
        if (serial == 0)
            throw new QcomAuthenticationException(Strings.Qcom_InvalidAuthentication);

        if (_targetInfo.Vendor == QcomVendorKind.Nothing)
        {
            string verified = new NothingProjectVerifier(_firehose!).VerifyBuiltIn(serial,
                cancellationToken: cancellationToken);
            Log.ForContext<QcomProtocol>().Information(Strings.Qcom_LogNothingProjectVerified, verified);
            return;
        }

        IReadOnlyList<string> candidates = GetOnePlusProjectCandidates(cancellationToken);
        IReadOnlySet<string> functions = GetOnePlusSupportedFunctions();
        _onePlusAuthentication = new OnePlusProjectVerifier(_firehose!).VerifyBuiltIn(
            serial, candidates, functions, cancellationToken);
        if (functions.Contains("setprojmodel") || functions.Contains("setswprojmodel"))
            _storage!.SetOnePlusTokenFactory(_onePlusAuthentication.Value.CreateProgramToken);
        Log.ForContext<QcomProtocol>().Information(
            Strings.Qcom_LogOnePlusProjectVerified, _onePlusAuthentication.Value.Profile.ProjectId);
    }

    private IReadOnlySet<string> GetOnePlusSupportedFunctions() =>
        (_targetInfo!.Firehose?.BasicDevCharacteristics?.SupportedFunctions ?? [])
        .Concat(_targetInfo.ProgrammerSupportedCommands)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> GetOnePlusProjectCandidates(CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? configured = ParseConfiguredProjectIds(_options.OnePlusProjectId);
        if (configured is { Count: > 0 })
            return configured;

        string? detected = TryReadOnePlusProjectId(cancellationToken);
        if (detected is not null)
        {
            Log.ForContext<QcomProtocol>().Information(Strings.Qcom_LogOnePlusParamProject, detected);
            return [detected];
        }
        Log.ForContext<QcomProtocol>().Warning(Strings.Qcom_LogOnePlusParamUnavailable);
        return OnePlusDeviceProfiles.ProjectIds;
    }

    private string? TryReadOnePlusProjectId(CancellationToken cancellationToken)
    {
        try
        {
            (string Name, TargetRange Range) match = ReadPartitions(cancellationToken)
                .FirstOrDefault(static item => item.Name.Equals("param", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(match.Name))
                return null;
            TargetRange range = match.Range;
            byte[] sector = new byte[range.SectorSize];
            _storage!.Read(new FirehoseReadRequest
            {
                PhysicalPartitionNumber = range.Partition,
                StartSector = range.Start,
                SectorCount = 1,
                SectorSizeInBytes = range.SectorSize,
                Label = range.Label
            }, sector, cancellationToken: cancellationToken);
            if (sector.Length < 29)
                return null;
            string value = Encoding.ASCII.GetString(sector, 24, 5).Trim('\0', ' ');
            return value.Length == 5 && value.All(char.IsAsciiLetterOrDigit) &&
                   OnePlusDeviceProfiles.TryGet(value, out _) ? value : null;
        }
        catch (Exception exception) when (exception is GeekFlashCore.Gpt.Abstractions.GptException or QcomProtocolException or
                                          EndOfStreamException or TimeoutException or ArgumentException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? ParseConfiguredProjectIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string[] values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 0 ? null : values;
    }

    private static string RequiredProperty(VendorAuthenticationResourceResponse response, string name) =>
        response.Properties.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : throw new QcomAuthenticationException(Strings.Qcom_InvalidAuthentication);

    private ValueTask<VendorAuthenticationResourceResponse> AuthenticationResourceAsync(VendorAuthenticationResourceRequest request, CancellationToken ct)
    {
        if (_authenticationProvider is null) throw new QcomAuthenticationException(Strings.Qcom_AuthenticationProviderRequired);
        return _resourceResolver.ResolveAsync(
            token => _authenticationProvider.ResolveAsync(request, token),
            ct,
            DisposeLateAuthentication);
    }

    private VendorAuthenticationResourceResponse AuthenticationResource(VendorAuthenticationResourceRequest request)
    {
        if (_authenticationProvider is null)
            throw new QcomAuthenticationException(Strings.Qcom_AuthenticationProviderRequired);
        return _resourceResolver.Resolve(
            token => _authenticationProvider.ResolveAsync(request, token),
            DisposeLateAuthentication);
    }

    private static void DisposeLateAuthentication(VendorAuthenticationResourceResponse response) =>
        response.Payload.Dispose();

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
        _selectedVendor = null;
        _vipPolicy = null; _onePlusAuthentication = null;
        _oplusDigest = null; _oplusIndex = null;
        _oplusAuthenticated = false;
        _oplusConfiguration = _options.OplusDigest;
        if (_wire is QcomSessionTransport sessionTransport) sessionTransport.DiscardBuffered();
        if (hadFirehose && _wire is not null)
            _firehose = CreateFirehoseSession(_wire);
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
            _lifetime.Cancel();
            try { Cleanup(); }
            finally { if (!_leaveTransportOpen) Transport.Dispose(); }
        }
        finally { _gate.Release(); }
        GC.SuppressFinalize(this);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            try { Cleanup(); }
            finally { if (!_leaveTransportOpen) Transport.Dispose(); }
        }
        finally { _gate.Release(); }
        GC.SuppressFinalize(this);
    }
    private sealed class Operation(QcomProtocol owner) : IDisposable
    {
        public void Dispose()
        {
            try { if (owner._firehose?.State == FirehoseSessionState.Faulted) owner.Cleanup(); }
            finally { owner._gate.Release(); }
        }
    }
    private static IProgress<long>? ProgramProgress(IProgress<ProgressRecord>? progress) =>
        progress is null ? null : new TransferProgress(progress, Strings.Progress_Writing);

    private long ReadWithProgress(FirehoseReadRequest request, Stream destination, IProgress<ProgressRecord>? progress, CancellationToken ct)
    {
        var transfer = progress is null ? null : new TransferProgress(progress, Strings.Progress_Reading);
        transfer?.Start(request.GetByteLength());
        long read = _storage!.Read(request, destination, transfer, ct);
        transfer?.Complete(read);
        return read;
    }
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
