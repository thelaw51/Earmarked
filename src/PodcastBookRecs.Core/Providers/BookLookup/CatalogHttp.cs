using System.Net;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.BookLookup;

namespace PodcastBookRecs.Core.Providers.BookLookup;

/// <summary>
/// Sends one catalogue request and reads the answer with the rules every catalogue adapter shares. Can't connect, a
/// timeout, 429 and 5xx mean the catalogue is unavailable, so the matcher pauses; any other error status or an answer
/// over <see cref="MaxResponseBytes"/> is about this request, so it fails only this title. Messages name the catalogue
/// and the status, never the request URL, which holds the title being looked up.
/// </summary>
internal static class CatalogHttp
{
    public const int MaxResponseBytes = 1024 * 1024;

    public static async Task<string> SendAsync(HttpClient client, HttpRequestMessage request, string label, CancellationToken cancellationToken)
    {
        using var sent = request;
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(sent, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BackendUnavailableException($"Can't reach {label}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackendUnavailableException($"{label} didn't answer in time.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new BackendUnavailableException($"{label} returned HTTP {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CatalogLookupFailedException($"{label} returned HTTP {(int)response.StatusCode}.");
            }

            return await ReadCappedAsync(response.Content, label, cancellationToken);
        }
    }

    /// <summary>
    /// Reads the body, refusing one over <see cref="MaxResponseBytes"/>. Too big is about this answer, so it fails the
    /// title; a connection that drops while reading is about the catalogue, so it pauses.
    /// </summary>
    private static async Task<string> ReadCappedAsync(HttpContent content, string label, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new CatalogLookupFailedException($"{label}'s answer is over {MaxResponseBytes / 1024} KB.");
        }

        using var buffer = new MemoryStream();
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw new CatalogLookupFailedException($"{label}'s answer is over {MaxResponseBytes / 1024} KB.");
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new BackendUnavailableException($"{label} dropped the connection: {ex.Message}", ex);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
