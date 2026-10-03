using GeekFlashCore.Protocol.Qcom.Abstractions;
using GeekFlashCore.Protocol.Qcom.Internals;
using GeekFlashCore.Protocol.Qcom.Vendors;
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
    private Action? _commandSent;
    private string? _xmlDeclarationAttribute;

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

    internal void SetBeforeCommand(Action<FirehoseSession, CancellationToken>? callback) =>
        _beforeCommand = callback;

    internal void SetCommandSent(Action? callback) =>
        _commandSent = callback;

    internal void SetXmlDeclarationAttribute(string? attribute) =>
        _xmlDeclarationAttribute = attribute;

    internal void ConfigureLegacyWire(OplusDigestConfiguration configuration, Action packetSent) =>
        _executor.ConfigureLegacy(configuration, packetSent);

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

    internal FirehoseCommandResult ExecuteLegacyNop(string xml, CancellationToken cancellationToken)
    {
        using OperationLease operation = EnterCommand();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _executor.ExecuteXml(xml, expectedRawMode: false, cancellationToken: cancellationToken);
        }
        catch (FirehoseNakException exception) { CompleteNak(exception, State); throw; }
        catch { SetState(FirehoseSessionState.Faulted); throw; }
    }

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
        catch (OperationCanceledException) when (!mainCommandMayHaveChangedWire)
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
        catch (OperationCanceledException) when (!mainCommandMayHaveChangedWire)
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

    internal void Invalidate() => SetState(FirehoseSessionState.Faulted);

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
        SetState(exception.Result.RawMode ? FirehoseSessionState.Faulted : commandState);

    private readonly struct OperationLease : IDisposable
    {
        private readonly FirehoseSession _owner;

        public OperationLease(FirehoseSession owner) => _owner = owner;

        public void Dispose() => _owner.Exit();
    }
}
