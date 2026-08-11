using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace GeekFlashCore.IO.BlockDevice;

public sealed class BudgetedArrayPool
{
    private readonly ByteBudget _budget;
    private readonly ArrayPool<byte> _pool;

    public BudgetedArrayPool(ByteBudget budget, ArrayPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        _budget = budget;
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    public PooledBufferLease Rent(
        int minimumLength,
        CancellationToken cancellationToken = default)
    {
        ValidateMinimumLength(minimumLength);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] initial = RentAndValidate(minimumLength);
        int requiredCharge = initial.Length;
        if (requiredCharge > _budget.Capacity)
        {
            _pool.Return(initial);
            throw CreateOversizedBucketException(requiredCharge);
        }

        if (_budget.TryAcquire(requiredCharge, out ByteBudgetLease? immediate))
        {
            return new PooledBufferLease(_pool, initial, minimumLength, immediate);
        }

        _pool.Return(initial);

        while (true)
        {
            ByteBudgetLease reservation = _budget.Acquire(requiredCharge, cancellationToken);
            byte[]? rented = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                rented = RentAndValidate(minimumLength);
                int actualLength = rented.Length;
                if (actualLength > _budget.Capacity)
                {
                    _pool.Return(rented);
                    rented = null;
                    throw CreateOversizedBucketException(actualLength);
                }

                if (actualLength > reservation.ByteCount)
                {
                    _pool.Return(rented);
                    rented = null;
                    requiredCharge = actualLength;
                    reservation.Dispose();
                    continue;
                }

                if (actualLength < reservation.ByteCount)
                {
                    reservation.ReduceTo(actualLength);
                }

                PooledBufferLease result = new(_pool, rented, minimumLength, reservation);
                rented = null;
                reservation = null!;
                return result;
            }
            finally
            {
                if (rented is not null)
                {
                    try
                    {
                        _pool.Return(rented);
                    }
                    finally
                    {
                        reservation?.Dispose();
                    }
                }
                else
                {
                    reservation?.Dispose();
                }
            }
        }
    }

    public bool TryRent(
        int minimumLength,
        [NotNullWhen(true)] out PooledBufferLease? lease)
    {
        ValidateMinimumLength(minimumLength);
        byte[] rented = RentAndValidate(minimumLength);
        int requiredCharge = rented.Length;
        if (requiredCharge > _budget.Capacity)
        {
            _pool.Return(rented);
            throw CreateOversizedBucketException(requiredCharge);
        }

        if (!_budget.TryAcquire(requiredCharge, out ByteBudgetLease? reservation))
        {
            _pool.Return(rented);
            lease = null;
            return false;
        }

        lease = new PooledBufferLease(_pool, rented, minimumLength, reservation);
        return true;
    }

    public ValueTask<PooledBufferLease> RentAsync(
        int minimumLength,
        CancellationToken cancellationToken = default)
    {
        ValidateMinimumLength(minimumLength);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] initial = RentAndValidate(minimumLength);
        int requiredCharge = initial.Length;
        if (requiredCharge > _budget.Capacity)
        {
            _pool.Return(initial);
            throw CreateOversizedBucketException(requiredCharge);
        }

        if (_budget.TryAcquire(requiredCharge, out ByteBudgetLease? immediate))
        {
            return ValueTask.FromResult(
                new PooledBufferLease(_pool, initial, minimumLength, immediate));
        }

        _pool.Return(initial);
        return RentSlowAsync(minimumLength, requiredCharge, cancellationToken);
    }

    private async ValueTask<PooledBufferLease> RentSlowAsync(
        int minimumLength,
        int requiredCharge,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ByteBudgetLease? reservation = await _budget
                .AcquireAsync(requiredCharge, cancellationToken)
                .ConfigureAwait(false);
            byte[]? rented = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                rented = RentAndValidate(minimumLength);
                int actualLength = rented.Length;
                if (actualLength > _budget.Capacity)
                {
                    _pool.Return(rented);
                    rented = null;
                    throw CreateOversizedBucketException(actualLength);
                }

                if (actualLength > reservation.ByteCount)
                {
                    _pool.Return(rented);
                    rented = null;
                    requiredCharge = actualLength;
                    reservation.Dispose();
                    reservation = null;
                    continue;
                }

                if (actualLength < reservation.ByteCount)
                {
                    reservation.ReduceTo(actualLength);
                }

                PooledBufferLease result = new(_pool, rented, minimumLength, reservation);
                rented = null;
                reservation = null;
                return result;
            }
            finally
            {
                if (rented is not null)
                {
                    try
                    {
                        _pool.Return(rented);
                    }
                    finally
                    {
                        reservation?.Dispose();
                    }
                }
                else
                {
                    reservation?.Dispose();
                }
            }
        }
    }

    private byte[] RentAndValidate(int minimumLength)
    {
        byte[] rented = _pool.Rent(minimumLength) ??
            throw new BlockDeviceException(Strings.ArrayPoolReturnedNull);
        if (rented.Length >= minimumLength)
        {
            return rented;
        }

        _pool.Return(rented);
        throw new BlockDeviceException(Strings.ArrayPoolBufferTooSmall);
    }

    private static void ValidateMinimumLength(int minimumLength) =>
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumLength, 1);

    private ArgumentOutOfRangeException CreateOversizedBucketException(int actualLength) =>
        new(
            "minimumLength",
            actualLength,
            Strings.FormatArrayPoolBucketExceedsBudget(_budget.Capacity));
}
