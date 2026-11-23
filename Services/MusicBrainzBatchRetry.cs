using System.Net;

namespace MattsRecords.Services;

internal sealed class MusicBrainzBatchRetry : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != "musicbrainz.org")
            return await base.SendAsync(request, cancellationToken);

        for (var attempt = 0; ; attempt++)
        {
            var delay = TimeSpan.FromSeconds(attempt == 0 ? 3 : 9);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var response = await base.SendAsync(request, timeout.Token);
                if (attempt >= 2 || !IsTransient(response.StatusCode))
                    return response;

                var retryAfter = response.Headers.RetryAfter;
                var requested = retryAfter?.Delta ?? retryAfter?.Date - DateTimeOffset.UtcNow;
                if (requested > TimeSpan.FromMinutes(1))
                    return response;
                if (requested > delay)
                    delay = requested.Value;
                response.Dispose();
            }
            catch (HttpRequestException error) when (attempt < 2 && (error.StatusCode is null || IsTransient(error.StatusCode.Value)))
            {
                // Retry failed connections and transient HTTP errors.
            }
            catch (OperationCanceledException) when (attempt < 2 && !cancellationToken.IsCancellationRequested)
            {
                // Retry an individual request timeout.
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout || status == HttpStatusCode.TooManyRequests || (int)status >= 500;
}
