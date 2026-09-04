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
    private int _busy;
    private int _disposed;
    private int _state = (int)FirehoseSessionState.Created;
    private FirehoseSessionState _stateBeforeRaw;

    public FirehoseSession(ITransport transport, int readTimeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (readTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(readTimeoutMilliseconds));

        ILogger logger = Log.ForContext<FirehoseSession>();
        _receiver = new FirehoseCmdReceiver(logger, transport, readTimeoutMilliseconds);
        _executor = new FirehoseCommandExecutor(new FirehoseCmdSender(logger, transport), _receiver);
    }

    public FirehoseSessionState State => (FirehoseSessionState)Volatile.Read(ref _state);

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

    public FirehoseCommandResult Execute(BaseCommand command, bool expectedRawMode = false)
    {
        ArgumentNullException.ThrowIfNull(command);
        using OperationLease _ = EnterCommand();
        FirehoseSessionState initialState = State;
        try
        {
            FirehoseCommandResult result = _executor.Execute(command, expectedRawMode);
            CompleteCommand(command is ConfigureCommand, result, initialState);
            return result;
        }
        catch (FirehoseNakException)
        {
            throw;
        }
        catch
        {
            SetState(FirehoseSessionState.Faulted);
            throw;
        }
    }

    public FirehoseCommandResult ExecuteXml(string xml, bool expectedRawMode = false)
    {
        using OperationLease _ = EnterCommand();
        FirehoseSessionState initialState = State;
        try
        {
            FirehoseCommandResult result = _executor.ExecuteXml(xml, expectedRawMode);
            CompleteCommand(configured: false, result, initialState);
            return result;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (FirehoseNakException)
        {
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
        catch (FirehoseNakException)
        {
            SetState(_stateBeforeRaw);
            throw;
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
        catch (FirehoseNakException)
        {
            SetState(_stateBeforeRaw);
            throw;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Volatile.Write(ref _state, (int)FirehoseSessionState.Disposed);
        if (Volatile.Read(ref _busy) == 0)
            _receiver.Dispose();
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
        catch (FirehoseNakException)
        {
            SetState(_stateBeforeRaw);
            throw;
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
        ThrowIfDisposed();
        FirehoseSessionState state = State;
        if (state is not (FirehoseSessionState.Started or FirehoseSessionState.Configured))
            throw new InvalidOperationException($"Firehose command is not valid while the session is {state}.");
        return EnterBusy();
    }

    private OperationLease Enter(FirehoseSessionState expectedState)
    {
        ThrowIfDisposed();
        FirehoseSessionState state = State;
        if (state != expectedState)
            throw new InvalidOperationException(
                $"Firehose operation requires state {expectedState}, but the session is {state}.");
        return EnterBusy();
    }

    private OperationLease EnterBusy()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Another Firehose operation is already in progress.");
        if (Volatile.Read(ref _disposed) != 0)
        {
            Volatile.Write(ref _busy, 0);
            throw new ObjectDisposedException(nameof(FirehoseSession));
        }
        return new OperationLease(this);
    }

    private void Exit()
    {
        if (Volatile.Read(ref _disposed) != 0)
            _receiver.Dispose();
        Volatile.Write(ref _busy, 0);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(FirehoseSession));
    }

    private void SetState(FirehoseSessionState state)
    {
        if (Volatile.Read(ref _disposed) == 0)
            Volatile.Write(ref _state, (int)state);
    }

    private readonly struct OperationLease : IDisposable
    {
        private readonly FirehoseSession _owner;

        public OperationLease(FirehoseSession owner) => _owner = owner;

        public void Dispose() => _owner.Exit();
    }
}
