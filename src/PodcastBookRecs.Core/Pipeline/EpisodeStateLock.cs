namespace PodcastBookRecs.Core.Pipeline;

/// <summary>
/// Serialises episode state changes inside the Worker: the poller's link and URL refresh, the transcriber's claim and
/// completion, and "Reset and retry". All three run in one process, so an in-memory lock is enough to stop one
/// overwriting another's state change.
/// </summary>
public sealed class EpisodeStateLock : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new Releaser(_gate);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
