using System.Diagnostics.CodeAnalysis;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom;

internal sealed class QcomResourceResolver
{
    private readonly CancellationToken _lifetimeToken;
    private readonly int _timeoutMilliseconds;

    internal QcomResourceResolver(
        int timeoutMilliseconds,
        CancellationToken lifetimeToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);

        _lifetimeToken = lifetimeToken;
        _timeoutMilliseconds = timeoutMilliseconds;
    }

    internal async ValueTask<T> ResolveAsync<T>(
        Func<CancellationToken, ValueTask<T>> request,
        CancellationToken cancellationToken,
        Action<T>? disposeLateResult = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        CancellationTokenSource? linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeToken);
        linked.CancelAfter(_timeoutMilliseconds);
        Task<T>? pending = null;
        try
        {
            pending = request(linked.Token).AsTask();
            T result = await pending.WaitAsync(linked.Token).ConfigureAwait(false);
            return result ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        }
        catch (OperationCanceledException)
        {
            if (pending is not null)
            {
                ObserveLate(pending, linked, disposeLateResult);
                linked = null;
            }

            throw;
        }
        catch (Exception exception) when (exception is not QcomProtocolException)
        {
            throw new QcomResourceException(Strings.Qcom_InvalidResource, exception);
        }
        finally
        {
            linked?.Dispose();
        }
    }

    internal T Resolve<T>(
        Func<CancellationToken, ValueTask<T>> request,
        Action<T>? disposeLateResult = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        CancellationTokenSource? linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        linked.CancelAfter(_timeoutMilliseconds);
        Task<T>? pending = null;
        try
        {
            pending = request(linked.Token).AsTask();
            T result = pending.WaitAsync(linked.Token).GetAwaiter().GetResult();
            return result ?? throw new QcomResourceException(Strings.Qcom_InvalidResource);
        }
        catch (OperationCanceledException)
        {
            if (pending is not null)
            {
                ObserveLate(pending, linked, disposeLateResult);
                linked = null;
            }

            throw;
        }
        catch (Exception exception) when (exception is not QcomProtocolException)
        {
            throw new QcomResourceException(Strings.Qcom_InvalidResource, exception);
        }
        finally
        {
            linked?.Dispose();
        }
    }

    private static void ObserveLate<T>(
        Task<T> pending,
        CancellationTokenSource linked,
        Action<T>? disposeLateResult) =>
        new LateResourceObservation<T>(pending, linked, disposeLateResult).Start();

    private sealed class LateResourceObservation<T>(
        Task<T> pending,
        CancellationTokenSource linked,
        Action<T>? disposeLateResult)
    {
        internal void Start() => _ = ObserveAsync();

        [SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "A cancelled provider must be observed without surfacing a second failure or retaining sensitive data.")]
        private async Task ObserveAsync()
        {
            try
            {
                T result = await pending.ConfigureAwait(false);
                disposeLateResult?.Invoke(result);
            }
            catch
            {
                // The original request has already completed through cancellation.
            }
            finally
            {
                linked.Dispose();
            }
        }
    }
}
