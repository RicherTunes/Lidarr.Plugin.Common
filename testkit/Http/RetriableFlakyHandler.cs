using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.TestKit.Http;

/// <summary>
/// Emits a configurable number of transient errors (429/503) before returning success.
/// Useful for exercising retry and backoff policies.
/// </summary>
public sealed class RetriableFlakyHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _failureStatus;
    private readonly HttpStatusCode _successStatus;
    private readonly string? _successContent;
    private readonly string? _contentType;
    private readonly TimeSpan? _retryAfter;
    private int _remainingFailures;

    public RetriableFlakyHandler(int failureCount = 1, HttpStatusCode failureStatus = HttpStatusCode.TooManyRequests, HttpStatusCode successStatus = HttpStatusCode.OK, string? successContent = null, string? contentType = "application/json", TimeSpan? retryAfter = null)
    {
        if (failureCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failureCount), "Failure count cannot be negative.");
        }

        _remainingFailures = failureCount;
        _failureStatus = failureStatus;
        _successStatus = successStatus;
        _successContent = successContent;
        _contentType = contentType;
        _retryAfter = retryAfter;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref _remainingFailures) >= 0)
        {
            var failure = new HttpResponseMessage(_failureStatus);
            if (_retryAfter is { } retry)
            {
                failure.Headers.RetryAfter = new RetryConditionHeaderValue(retry);
            }

            return Task.FromResult(failure);
        }

        var success = new HttpResponseMessage(_successStatus);
        if (_successContent is not null)
        {
            success.Content = new StringContent(_successContent);
            if (!string.IsNullOrEmpty(_contentType))
            {
                success.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType!);
            }
        }

        return Task.FromResult(success);
    }
}
