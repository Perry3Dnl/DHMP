namespace DHMP.RawIpv6;

internal static class DhmpHandshakeDeadline
{
    internal static async Task<T> RunAsync<T>(
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> exchange)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);

        try
        {
            return await exchange(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("DHMP handshake deadline expired.", exception);
        }
    }
}
