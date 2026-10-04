using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Internals;
using GeekFlashCore.Protocol.Qcom.Vendors;
using GeekFlashCore.Protocol.Qcom.Vendors.Oplus;
using GeekFlashCore.Transport.Abstractions;
using Serilog;

namespace GeekFlashCore.Protocol.Qcom.Firehose;

public enum FirehoseSessionState
{
    Created,
    Started,
    Configured,
    RawTransfer,
    Faulted,
    Disposed
}

public sealed class FirehoseSession : IDisposable
{
    private readonly FirehoseCmdReceiver _receiver;
    private readonly FirehoseCommandExecutor _executor;
    private readonly int _defaultReadTimeoutMilliseconds;
    private readonly object _lifecycleLock = new();
    private int _busy;
    private int _disposed;
    private int _state = (int)FirehoseSessionState.Created;
    private FirehoseSessionState _stateBeforeRaw;
    private Action<FirehoseSession, CancellationToken>? _beforeCommand;
    private Action<FirehoseSession, FirehoseCommandExecutor, CancellationToken>? _legacyBeforeCommand;
    private Action? _commandSent;
    private string? _xmlDeclarationAttribute;
    private OplusDigestCommandCounter? _legacyCounter;

    public FirehoseSession(ITransport transport, int readTimeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (readTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(readTimeoutMilliseconds));

        _defaultReadTimeoutMilliseconds = readTimeoutMilliseconds;

        ILogger logger = Log.ForContext<FirehoseSession>();
        _receiver = new FirehoseCmdReceiver(logger, transport, readTimeoutMilliseconds);
        _executor = new FirehoseCommandExecutor(new FirehoseCmdSender(logger, transport), _receiver);
    }

    public FirehoseSessionState State => (FirehoseSessionState)Volatile.Read(ref _state);
    internal FirehoseStorage PreferredInitialStorage => _executor.UsesLegacyBootstrap ? FirehoseStorage.Ufs : FirehoseStorage.Emmc;
    internal int LegacyConfirmationTimeout => _defaultReadTimeoutMilliseconds;

    internal void SetBeforeCommand(Action<FirehoseSession, CancellationToken>? callback) =>
        _beforeCommand = callback;

    internal void SetLegacyBeforeCommand(Action<FirehoseSession, FirehoseCommandExecutor, CancellationToken> callback) =>
        _legacyBeforeCommand = callback;

    internal void SetCommandSent(Action? callback) =>
        _commandSent = callback;

    internal void SetXmlDeclarationAttribute(string? attribute) =>
        _xmlDeclarationAttribute = attribute;

    internal OplusDigestCommandCounter ConfigureLegacyWire(OplusDigestConfiguration configuration,
        OplusDigestCommandCounter? counter = null)
    {
        if (_legacyCounter is not null) return _legacyCounter;
        _legacyCounter = counter ?? new OplusDigestCommandCounter(configuration.MaxCommandsBeforeDigest,
            configuration.InitialPacketCount);
        _executor.ConfigureLegacy(configuration, _legacyCounter.CommandSent);
        return _legacyCounter;
    }

    internal FirehoseCommandResult? SendLegacyDigest(Stream source, long length, int bufferSize,
        int timeout, CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterCommand();
        try { return _executor.SendLegacyDigest(source, length, bufferSize, timeout, cancellationToken); }
        catch { SetState(FirehoseSessionState.Faulted); throw; }
    }

    internal FirehoseCommandResult ConfirmLegacyNop(string xml, bool requireHandler, CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterCommand();
        try { return _executor.ConfirmLegacyNop(xml, requireHandler, _defaultReadTimeoutMilliseconds, cancellationToken); }
        catch { SetState(FirehoseSessionState.Faulted); throw; }
    }

    internal bool StartupDataReceived => _executor.StartupDataReceived;

    public FirehoseResponse Start(int? startupTimeoutMilliseconds = null)
    {
        using OperationLease _ = Enter(FirehoseSessionState.Created);
        try
        {
            FirehoseResponse response = _executor.ReceiveStartupLogs(startupTimeoutMilliseconds);
            SetState(FirehoseSessionState.Started);
            return response;
        }
        catch
        {
            SetState(FirehoseSessionState.Faulted);
            throw;
        }
    }

    public void StartWithoutStartupLogs()
    {
        using OperationLease _ = Enter(FirehoseSessionState.Created);
        SetState(FirehoseSessionState.Started);
    }

    /// <summary>Probes an already running Firehose session with a bounded NOP exchange.</summary>
    internal bool TryProbe(int timeoutMilliseconds, out FirehoseResponse? response)
    {
        response = null;
        using OperationLease _ = Enter(FirehoseSessionState.Created);
        _receiver.SetReadTimeout(timeoutMilliseconds);
        try
        {
            FirehoseCommandResult result = _executor.Execute(new NopCommand(), expectedRawMode: false,
                xmlDeclarationAttribute: _xmlDeclarationAttribute);
            if (!result.IsSuccess)
                return false;
            response = new FirehoseResponse(result.Logs, result.Attributes, result.Status, result.RawMode,
                result.PayloadElements);
            SetState(FirehoseSessionState.Started);
            return true;
        }
        catch (TimeoutException)
        {
            SetState(FirehoseSessionState.Faulted);
            return false;
        }
        catch (FirehoseProtocolException)
        {
            SetState(FirehoseSessionState.Faulted);
            return false;
        }
        finally
        {
            _receiver.SetReadTimeout(_defaultReadTimeoutMilliseconds);
        }
    }

    public FirehoseCommandResult Execute(BaseCommand command, bool expectedRawMode = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        using OperationLease _ = EnterCommand();
        FirehoseSessionState initialState = State;
        bool mainCommandMayHaveChangedWire = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _legacyBeforeCommand?.Invoke(this, _executor, cancellationToken);
            _beforeCommand?.Invoke(this, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            mainCommandMayHaveChangedWire = true;
            FirehoseCommandResult result = _executor.Execute(
                command,
                expectedRawMode,
                _xmlDeclarationAttribute,
                _commandSent,
                cancellationToken);
            CompleteCommand(command is ConfigureCommand, result, initialState);
            return result;
        }
        catch (OperationCanceledException) when (!mainCommandMayHaveChangedWire && State != FirehoseSessionState.Faulted)
        {
            SetState(initialState);
            throw;
        }
        catch (FirehoseNakException exception)
        {
            CompleteNak(exception, initialState);
            throw;
        }
        catch
        {
            SetState(FirehoseSessionState.Faulted);
            throw;
        }
    }

    public FirehoseCommandResult ExecuteXml(string xml, bool expectedRawMode = false,
        CancellationToken cancellationToken = default)
    {
        using OperationLease _ = EnterCommand();
        FirehoseSessionState initialState = State;
        bool mainCommandMayHaveChangedWire = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _legacyBeforeCommand?.Invoke(this, _executor, cancellationToken);
            _beforeCommand?.Invoke(this, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            mainCommandMayHaveChangedWire = true;
            FirehoseCommandResult result = _executor.ExecuteXml(
                xml,
                expectedRawMode,
                _xmlDeclarationAttribute,
                _commandSent,
                cancellationToken);
            CompleteCommand(configured: false, result, initialState);
            return result;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (FirehoseNakException exception)
        {
            CompleteNak(exception, initialState);
            throw;
        }
        catch (OperationCanceledException) when (!mainCommandMayHaveChangedWire && State != FirehoseSessionState.Faulted)
        {
            SetState(initialState);
            throw;
        }
        catch
        {
            SetState(FirehoseSessionState.Faulted);
            throw;
        }
    }

    public FirehoseCommandResult ExecuteCustomXml(
        string xml,
        IVendorFirehoseStrategy strategy,
        bool expectedRawMode = false)
    {
        ValidatedCustomCommand command = CustomCommandValidator.Validate(xml, strategy);
        return ExecuteXml(command.Xml, expectedRawMode);
    }

    public FirehoseCommandResult SendRaw(
        ReadOnlySpan<byte> source,
        int bufferSize,
        CancellationToken cancellationToken = default,
        Action? packetSent = null)
    {
        using OperationLease _ = Enter(FirehoseSessionState.RawTransfer);
        try
        {
            FirehoseCommandResult result = _executor.SendRaw(source, bufferSize, cancellationToken, packetSent);
            SetState(_stateBeforeRaw);
            return result;
        }
        catch
        {
            SetState(FirehoseSessionState.Faulted);
            throw;
        }
    }

    public FirehoseCommandResult SendRaw(
        Stream source,
        long sourceLength,
        long wireLength,
        int bufferSize,
        byte paddingByte = 0,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        bool computeDigest = false,
        Action? packetSent = null)
    {
        using OperationLease _ = Enter(FirehoseSessionState.RawTransfer);
        return CompleteRaw(() => _executor.SendRaw(
            source,
            sourceLength,
            wireLength,
            bufferSize,
            paddingByte,
            progress,
            cancellationToken,
            computeDigest,
            packetSent));
    }

    public FirehoseCommandResult ReceiveRaw(
        Span<byte> destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using OperationLease _ = Enter(FirehoseSessionState.RawTransfer);
        try
        {
            FirehoseCommandResult result = _executor.ReceiveRaw(destination, progress, cancellationToken);
            SetState(_stateBeforeRaw);
            return result;
        }
        catch
        {
            SetState(FirehoseSessionState.Faulted);
            throw;
        }
    }

    public FirehoseCommandResult ReceiveRaw(
        Stream destination,
        long length,
        int bufferSize,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using OperationLease _ = Enter(FirehoseSessionState.RawTransfer);
        return CompleteRaw(() => _executor.ReceiveRaw(destination, length, bufferSize, progress, cancellationToken));
    }

    /// <summary>Sends a digest directly, without an XML or raw-mode preamble.</summary>
    public FirehoseCommandResult SendDigest(Stream source, long length, int bufferSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException(Strings.Qcom_DigestSourceNotReadable, nameof(source));
        if (length is <= 0 or > FirehoseConstants.MaximumRawTransferLength)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        cancellationToken.ThrowIfCancellationRequested();
        using OperationLease operation = EnterCommand();
        try
        {
            return _executor.SendRaw(source, length, length, bufferSize, 0, null, cancellationToken, false, null);
        }
        catch
        {
            SetState(FirehoseSessionState.Faulted);
            throw;
        }
    }

    // False is a complete, explicitly observed transition to signed-table receive;
    // the caller may reopen and send the same initial table once under its resume policy.
    internal bool TrySendOplusInitialDigest(Stream source, long length, int bufferSize,
        bool allowResumeRecovery, int rejectionTimeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException(Strings.Qcom_DigestSourceNotReadable, nameof(source));
        if (length is <= 0 or > FirehoseConstants.MaximumRawTransferLength)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        using OperationLease operation = EnterCommand();
        try
        {
            try
            {
                _executor.SendRaw(source, length, length, bufferSize, 0, null, cancellationToken, false, null);
                return true;
            }
            catch (FirehoseNakException exception) when (allowResumeRecovery && length <= bufferSize &&
                !exception.Result.RawMode && exception.Result.Attributes.TryGetValue("value", out string? value) &&
                string.Equals(value, "NAK", StringComparison.OrdinalIgnoreCase) &&
                exception.Result.Logs.Any(static log =>
                    log.Message.Contains("Hash of data doesn't match the expected hash", StringComparison.Ordinal)))
            {
                FirehoseCommandResult rejected = _executor.ReadOplusRejectionDetails(exception.Result,
                    rejectionTimeout, cancellationToken);
                bool mismatchSeen = false;
                bool awaitingTable = false;
                foreach (FirehoseResponseLog log in rejected.Logs)
                {
                    if (log.Message.Contains("Hash of data doesn't match the expected hash", StringComparison.Ordinal))
                    {
                        mismatchSeen = true;
                        awaitingTable = false;
                    }
                    else if (mismatchSeen && log.Message.Contains("VIP is enabled, receiving the signed table", StringComparison.OrdinalIgnoreCase))
                        awaitingTable = true;
                }
                // A startup banner preceding the rejection is stale evidence. Only a
                // waiting-state log after the last mismatch permits a second table.
                if (!awaitingTable)
                    throw;
                cancellationToken.ThrowIfCancellationRequested();
                Log.Warning(Strings.Qcom_LogOplusResumeDigestRetry);
                return false;
            }
        }
        catch { SetState(FirehoseSessionState.Faulted); throw; }
    }

    internal void Invalidate() => SetState(FirehoseSessionState.Faulted);

    internal FirehoseCommandResult SendOplusSign(ReadOnlySpan<byte> signature, CancellationToken cancellationToken)
    {
        using OperationLease operation = Enter(FirehoseSessionState.RawTransfer);
        try
        {
            FirehoseCommandResult result = _executor.SendOplusSign(signature, cancellationToken);
            SetState(_stateBeforeRaw);
            return result;
        }
        catch { SetState(FirehoseSessionState.Faulted); throw; }
    }

    internal void BeginOplusVerify(CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterCommand();
        FirehoseSessionState initial = State;
        try
        {
            _executor.BeginOplusVerify(_xmlDeclarationAttribute, cancellationToken);
            _stateBeforeRaw = initial;
            SetState(FirehoseSessionState.RawTransfer);
        }
        catch (FirehoseNakException exception) { CompleteNak(exception, initial); throw; }
        catch { SetState(FirehoseSessionState.Faulted); throw; }
    }

    internal void ResetLegacyPacketCount() => _legacyCounter?.Reset();

    internal void InitializeOplusSha256(CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterCommand();
        try
        {
            if (_executor.InitializeOplusSha256(_xmlDeclarationAttribute, cancellationToken))
                Log.Warning(Strings.Qcom_LogOplusSha256Compatibility);
        }
        catch { SetState(FirehoseSessionState.Faulted); throw; }
    }

    internal FirehoseCommandResult ReadOplusRejectionDetails(FirehoseCommandResult result, int timeout,
        CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterCommand();
        try { return _executor.ReadOplusRejectionDetails(result, timeout, cancellationToken); }
        catch { SetState(FirehoseSessionState.Faulted); throw; }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            Volatile.Write(ref _state, (int)FirehoseSessionState.Disposed);
            if (_busy == 0)
                _receiver.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private FirehoseCommandResult CompleteRaw(Func<FirehoseCommandResult> operation)
    {
        try
        {
            FirehoseCommandResult result = operation();
            SetState(_stateBeforeRaw);
            return result;
        }
        catch
        {
            SetState(FirehoseSessionState.Faulted);
            throw;
        }
    }

    private void CompleteCommand(bool configured, FirehoseCommandResult result, FirehoseSessionState initialState)
    {
        if (result.RawMode)
        {
            _stateBeforeRaw = configured ? FirehoseSessionState.Configured : initialState;
            SetState(FirehoseSessionState.RawTransfer);
        }
        else if (configured)
        {
            SetState(FirehoseSessionState.Configured);
        }
    }

    private OperationLease EnterCommand()
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            FirehoseSessionState state = State;
            if (state is not (FirehoseSessionState.Started or FirehoseSessionState.Configured))
                throw new InvalidOperationException(Strings.FormatQcom_FirehoseOperationStateInvalid(state));
            return EnterBusy();
        }
    }

    private OperationLease Enter(FirehoseSessionState expectedState)
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            FirehoseSessionState state = State;
            if (state != expectedState)
                throw new InvalidOperationException(
                    Strings.FormatQcom_FirehoseExpectedState(expectedState, state));
            return EnterBusy();
        }
    }

    private OperationLease EnterBusy()
    {
        if (_busy != 0)
            throw new InvalidOperationException(Strings.Qcom_FirehoseOperationInProgress);
        _busy = 1;
        return new OperationLease(this);
    }

    private void Exit()
    {
        lock (_lifecycleLock)
        {
            _busy = 0;
            if (_disposed != 0)
                _receiver.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(FirehoseSession));
    }

    private void SetState(FirehoseSessionState state)
    {
        lock (_lifecycleLock)
        {
            if (_disposed == 0 && State != FirehoseSessionState.Faulted)
                Volatile.Write(ref _state, (int)state);
        }
    }

    internal FirehoseCommandResult SendAuxiliaryRaw(
        Stream source,
        long length,
        int bufferSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return _executor.SendRaw(source, length, length, bufferSize, 0, null, cancellationToken, false, null);
        }
        catch
        {
            SetState(FirehoseSessionState.Faulted);
            throw;
        }
    }

    private void CompleteNak(FirehoseNakException exception, FirehoseSessionState commandState) =>
        SetState(State == FirehoseSessionState.Faulted || exception.Result.RawMode ||
                 (_legacyCounter is not null && exception.Result.Logs.Any(static log =>
                     log.Message.Contains("Hash of new table doesn't match the expected hash", StringComparison.Ordinal)))
            ? FirehoseSessionState.Faulted : commandState);

    private readonly struct OperationLease : IDisposable
    {
        private readonly FirehoseSession _owner;

        public OperationLease(FirehoseSession owner) => _owner = owner;

        public void Dispose() => _owner.Exit();
    }
}
