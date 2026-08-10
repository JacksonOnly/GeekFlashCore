using System.Diagnostics.CodeAnalysis;

namespace GeekFlashCore.IO.BlockDevice;

public sealed class ByteBudget
{
    private readonly object _gate = new();
    private readonly LinkedList<Waiter> _waiters = [];
    private long _inUse;

    public ByteBudget(long capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public long Capacity { get; }

    public long InUse
    {
        get
        {
            lock (_gate)
            {
                return _inUse;
            }
        }
    }

    public bool TryAcquire(
        int byteCount,
        [NotNullWhen(true)] out ByteBudgetLease? lease)
    {
        ValidateByteCount(byteCount);
        lock (_gate)
        {
            if (_waiters.Count != 0 || byteCount > Capacity - _inUse)
            {
                lease = null;
                return false;
            }

            _inUse += byteCount;
            lease = new ByteBudgetLease(this, byteCount);
            return true;
        }
    }

    public ByteBudgetLease Acquire(
        int byteCount,
        CancellationToken cancellationToken = default)
    {
        ValidateByteCount(byteCount);
        cancellationToken.ThrowIfCancellationRequested();

        if (TryAcquire(byteCount, out ByteBudgetLease? lease))
        {
            return lease;
        }

        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state =>
            {
                var budget = (ByteBudget)state!;
                lock (budget._gate)
                {
                    Monitor.PulseAll(budget._gate);
                }
            },
            this);

        lock (_gate)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_waiters.Count == 0 && byteCount <= Capacity - _inUse)
                {
                    _inUse += byteCount;
                    return new ByteBudgetLease(this, byteCount);
                }

                Monitor.Wait(_gate);
            }
        }
    }

    public ValueTask<ByteBudgetLease> AcquireAsync(
        int byteCount,
        CancellationToken cancellationToken = default)
    {
        ValidateByteCount(byteCount);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_waiters.Count == 0 && byteCount <= Capacity - _inUse)
            {
                _inUse += byteCount;
                return ValueTask.FromResult(new ByteBudgetLease(this, byteCount));
            }

            var waiter = new Waiter(this, byteCount, cancellationToken);
            waiter.Node = _waiters.AddLast(waiter);
            waiter.Registration = cancellationToken.UnsafeRegister(
                static state => ((Waiter)state!).Cancel(),
                waiter);
            if (waiter.Node is null)
            {
                waiter.Registration.Unregister();
            }
            return new ValueTask<ByteBudgetLease>(waiter.Completion.Task);
        }
    }

    internal void Release(int byteCount)
    {
        List<Waiter>? cancelled = null;
        List<Waiter>? granted = null;

        lock (_gate)
        {
            _inUse -= byteCount;
            if (_inUse < 0)
            {
                _inUse += byteCount;
                throw new InvalidOperationException(Strings.ByteBudgetReleasedMultipleTimes);
            }

            DrainWaiters(ref cancelled, ref granted);
            Monitor.PulseAll(_gate);
        }

        CompleteWaiters(cancelled, granted);
    }

    private void Cancel(Waiter waiter)
    {
        List<Waiter>? cancelled = null;
        List<Waiter>? granted = null;

        lock (_gate)
        {
            if (waiter.Node?.List is null)
            {
                return;
            }

            _waiters.Remove(waiter.Node);
            waiter.Node = null;
            cancelled = [waiter];
            DrainWaiters(ref cancelled, ref granted);
            Monitor.PulseAll(_gate);
        }

        CompleteWaiters(cancelled, granted);
    }

    private void DrainWaiters(ref List<Waiter>? cancelled, ref List<Waiter>? granted)
    {
        while (_waiters.First is { } node)
        {
            Waiter waiter = node.Value;
            if (waiter.CancellationToken.IsCancellationRequested)
            {
                _waiters.RemoveFirst();
                waiter.Node = null;
                (cancelled ??= []).Add(waiter);
                continue;
            }

            if (waiter.ByteCount > Capacity - _inUse)
            {
                break;
            }

            _waiters.RemoveFirst();
            waiter.Node = null;
            _inUse += waiter.ByteCount;
            (granted ??= []).Add(waiter);
        }
    }

    private static void CompleteWaiters(List<Waiter>? cancelled, List<Waiter>? granted)
    {
        if (cancelled is not null)
        {
            foreach (Waiter waiter in cancelled)
            {
                waiter.Registration.Unregister();
                waiter.Completion.TrySetCanceled(waiter.CancellationToken);
            }
        }

        if (granted is not null)
        {
            foreach (Waiter waiter in granted)
            {
                waiter.Registration.Dispose();
                waiter.Completion.TrySetResult(new ByteBudgetLease(waiter.Budget, waiter.ByteCount));
            }
        }
    }

    private void ValidateByteCount(int byteCount)
    {
        if (byteCount < 1 || byteCount > Capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteCount),
                byteCount,
                Strings.FormatExpectedRange(1, Capacity));
        }
    }

    private sealed class Waiter(
        ByteBudget budget,
        int byteCount,
        CancellationToken cancellationToken)
    {
        public ByteBudget Budget { get; } = budget;
        public int ByteCount { get; } = byteCount;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<ByteBudgetLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }

        public void Cancel() => Budget.Cancel(this);
    }
}
