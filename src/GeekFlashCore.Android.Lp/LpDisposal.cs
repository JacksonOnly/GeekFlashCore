using System.Runtime.ExceptionServices;

namespace GeekFlashCore.Android.Lp;

internal static class LpDisposal
{
    internal static void TryDispose(IDisposable? resource, ref Exception? firstFailure)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }
    }

    internal static void ThrowIfFailed(Exception? failure)
    {
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
