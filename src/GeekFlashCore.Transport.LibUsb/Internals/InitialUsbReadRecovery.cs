using LibUsbDotNet;

namespace GeekFlashCore.Transport.LibUsb.Internals;

internal delegate Error UsbEndpointRead(Span<byte> destination, int timeout, out int transferred);

// One zero-byte IN stall may be cleared before the first handshake response. Never resends OUT data.
internal sealed class InitialUsbReadRecovery(Func<long>? clock = null)
{
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    public bool IsPending { get; private set; }
    public void Reset(bool enabled) => IsPending = enabled;
    public void Suppress() => IsPending = false;

    public Error Read(Span<byte> destination, int timeout, UsbEndpointRead read, Func<Error> clearHalt,
        out int transferred)
    {
        transferred = 0;
        if (destination.IsEmpty) return Error.Success;
        bool recover = IsPending && destination.Length == 1 && timeout > 0;
        IsPending = false;
        long started = recover ? _clock() : 0;
        Error error = read(destination, timeout, out transferred);
        if (!recover || error != Error.Pipe || transferred != 0) return error;
        if (Remaining(started, timeout) == 0) return Error.Timeout;
        error = clearHalt();
        if (error != Error.Success) return error;
        int remaining = Remaining(started, timeout);
        if (remaining == 0) return Error.Timeout;
        return read(destination, remaining, out transferred);
    }

    private int Remaining(long started, int timeout) =>
        (int)(timeout - Math.Clamp(_clock() - started, 0, timeout));
}
