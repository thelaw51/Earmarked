namespace Earmarked.Core.Providers.BookLookup;

/// <summary>
/// Runs one catalogue's requests one at a time, at least <see cref="Gap"/> apart, so an adapter stays inside the
/// catalogue's rate limit. The adapter is a singleton shared by the matcher and review search, so this one gate covers
/// both. The gap counts from when a request finished, failed or not.
/// </summary>
internal sealed class CatalogPacer(TimeProvider time, TimeSpan gap) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    /// <summary>The minimum gap between requests. Tests set it to zero.</summary>
    public TimeSpan Gap { get; set; } = gap;

    public async Task<T> RunAsync<T>(Func<Task<T>> send, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var wait = _nextRequestAt - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, cancellationToken);
            }

            try
            {
                return await send();
            }
            finally
            {
                _nextRequestAt = time.GetUtcNow() + Gap;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
