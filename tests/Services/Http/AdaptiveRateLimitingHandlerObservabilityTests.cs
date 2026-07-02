using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Services.Performance;

using Microsoft.Extensions.Logging;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Http;

/// <summary>
/// The limiter adapts its per-endpoint budget silently; the handler is the
/// one place with both a logger and visibility of every gated request, so it
/// is responsible for making adaptations observable (perf-program Phase 0).
/// </summary>
public sealed class AdaptiveRateLimitingHandlerObservabilityTests
{
    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"{logLevel}|{formatter(state, exception)}");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public StubHandler(HttpStatusCode status) => _status = status;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status));
    }

    [Fact]
    public async Task TooManyRequests_LogsBudgetAdaptation_WithOldAndNewRpm()
    {
        var logger = new ListLogger();
        using var limiter = new UniversalAdaptiveRateLimiter();
        using var handler = new AdaptiveRateLimitingHandler(limiter, "Qobuz", logger)
        {
            InnerHandler = new StubHandler(HttpStatusCode.TooManyRequests),
        };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://api.example.com/albums/123");

        // Qobuz default 500 RPM tightens to 375 on a 429.
        Assert.Contains(logger.Lines, l =>
            l.Contains("budget adapted") && l.Contains("500") && l.Contains("375"));
    }

    [Fact]
    public async Task SuccessResponse_WithNoBudgetChange_LogsNoAdaptation()
    {
        var logger = new ListLogger();
        using var limiter = new UniversalAdaptiveRateLimiter();
        using var handler = new AdaptiveRateLimitingHandler(limiter, "Qobuz", logger)
        {
            InnerHandler = new StubHandler(HttpStatusCode.OK),
        };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://api.example.com/albums/123");

        Assert.DoesNotContain(logger.Lines, l => l.Contains("budget adapted"));
    }
}
