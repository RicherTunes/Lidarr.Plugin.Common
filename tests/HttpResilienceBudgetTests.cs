using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class HttpResilienceBudgetTests
    {
        [Theory]
        [InlineData("backward", 6000L, -36000000000L, 1)]
        [InlineData("forward", 1000L, 36000000000L, 2)]
        [InlineData("equality", 5000L, 0L, 1)]
        [InlineData("just-before", 4999L, 0L, 2)]
        [InlineData("just-after", 5001L, 0L, 1)]
        public async Task Should_UseElapsedBudget_IndependentlyOfUtc(
            string scenario, long elapsedUnits, long utcChangeTicks, int expectedSends)
        {
            // Timestamp units are milliseconds, deliberately not TimeSpan ticks.
            var clock = new IndependentClock();
            using var original = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("retain this response")
            };
            original.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            using var success = new HttpResponseMessage(HttpStatusCode.OK);
            var sends = 0;
            using var client = new HttpClient(new StubHandler(_ =>
            {
                if (++sends == 1)
                {
                    clock.Timestamp += elapsedUnits;
                    clock.UtcNow = clock.UtcNow.AddTicks(utcChangeTicks);
                    return original;
                }
                return success;
            }));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://typed-budget-{scenario}.test/resource");

            using var response = await client.ExecuteWithResilienceAsync(
                request, maxRetries: 2, retryBudget: TimeSpan.FromSeconds(5),
                maxConcurrencyPerHost: 1, perRequestTimeout: null, timeProvider: clock,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(expectedSends, sends);
            Assert.Same(expectedSends == 1 ? original : success, response);
            if (expectedSends == 1)
            {
                Assert.Equal("retain this response", await response.Content.ReadAsStringAsync());
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Should_AcceptMaxValueBudget_WithoutConstructingCalendarDeadline(bool suppliedClock)
        {
            var sends = 0;
            using var client = new HttpClient(new StubHandler(_ =>
            {
                sends++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://max-typed-budget-{suppliedClock}.test/resource");
            using var response = await ExecuteAsync(client, request, TimeSpan.MaxValue, suppliedClock);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, sends);
        }

        [Fact]
        public async Task Should_AcceptOrdinaryBudget_WhenUtcIsNearMaxValue()
        {
            var clock = new IndependentClock { UtcNow = DateTimeOffset.MaxValue.AddSeconds(-1) };
            using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://max-utc-typed-budget.test/resource");
            using var response = await client.ExecuteWithResilienceAsync(
                request, 2, TimeSpan.FromSeconds(5), 1, null, clock, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Theory]
        [InlineData(false, 0L)]
        [InlineData(true, 0L)]
        [InlineData(false, long.MinValue)]
        [InlineData(true, long.MinValue)]
        public async Task Should_PreserveFirstResponse_ForNonpositiveBudget(bool suppliedClock, long ticks)
        {
            var sends = 0;
            using var client = new HttpClient(new StubHandler(_ =>
            {
                sends++;
                var result = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("no retry admitted")
                };
                result.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return result;
            }));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://empty-typed-budget-{suppliedClock}-{ticks}.test/resource");
            using var response = await ExecuteAsync(client, request, TimeSpan.FromTicks(ticks), suppliedClock);
            Assert.Equal(1, sends);
            Assert.Equal("no retry admitted", await response.Content.ReadAsStringAsync());
        }

        [Theory]
        [InlineData(301)]
        [InlineData(302)]
        [InlineData(303)]
        [InlineData(307)]
        [InlineData(308)]
        public async Task Should_FollowValidatedRedirect_WithMaxValueBudget(int status)
        {
            var sends = 0;
            using var client = new HttpClient(new StubHandler(request =>
            {
                sends++;
                if (request.RequestUri!.AbsolutePath == "/start")
                {
                    var redirect = new HttpResponseMessage((HttpStatusCode)status);
                    redirect.Headers.Location = new Uri("/finish", UriKind.Relative);
                    return redirect;
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://max-redirect-budget-{status}.test/start");
            var validations = 0;
            using var response = await client.ExecuteWithResilienceAsync(
                request, ResiliencePolicy.Default.With(retryBudget: TimeSpan.MaxValue),
                validateRedirectTarget: target =>
                {
                    validations++;
                    return target.AbsolutePath == "/finish";
                });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, sends);
            Assert.Equal(1, validations);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Should_ChargeGateWaitToBudget(bool generic)
        {
            var host = generic ? "gate-budget-generic.test" : "gate-budget-typed.test";
            var clock = new IndependentClock();
            var gate = HostGateRegistry.Get(host, 1);
            await gate.WaitAsync();
            var held = true;
            var sends = 0;
            using var original = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("gate consumed budget")
            };
            original.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            using var client = new HttpClient(new StubHandler(_ => { sends++; return original; }));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/resource");
            using var cancellation = new CancellationTokenSource();
            Task<HttpResponseMessage>? pending = null;
            try
            {
                pending = generic
                    ? GenericResilienceExecutor.ExecuteWithResilienceAsync(
                        request, (_, _) => { sends++; return Task.FromResult(original); },
                        r => Task.FromResult(r), r => r.RequestUri!.Host, r => (int)r.StatusCode,
                        _ => TimeSpan.Zero, ResiliencePolicy.Default.With(maxRetries: 2, retryBudget: TimeSpan.FromSeconds(5), maxConcurrencyPerHost: 1),
                        clock, cancellation.Token)
                    : client.ExecuteWithResilienceAsync(request, 2, TimeSpan.FromSeconds(5), 1, null, clock, cancellation.Token);
                Assert.False(pending.IsCompleted);
                Assert.Equal(0, sends);
                clock.Timestamp = 6000;
                held = false;
                gate.Release();
                using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, sends);
                Assert.Same(original, response);
                Assert.Equal("gate consumed budget", await response.Content.ReadAsStringAsync());
            }
            finally
            {
                cancellation.Cancel();
                if (held) gate.Release();
                if (pending is not null)
                {
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (OperationCanceledException) { }
                }
                HostGateRegistry.Clear(host);
            }
        }

        [Fact]
        public async Task Should_StillInterpretAbsoluteRetryAfterUsingUtc()
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var sends = 0;
            using var client = new HttpClient(new StubHandler(_ =>
            {
                if (++sends != 1) return new HttpResponseMessage(HttpStatusCode.OK);
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(4));
                clock.Advance(TimeSpan.FromSeconds(1));
                return response;
            }));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://absolute-date-budget.test/resource");
            using var cancellation = new CancellationTokenSource();
            var pending = client.ExecuteWithResilienceAsync(request, 2, TimeSpan.FromSeconds(10), 1, null, clock, cancellation.Token);
            try
            {
                Assert.Equal(1, sends);
                clock.Advance(TimeSpan.FromSeconds(2));
                Assert.False(pending.IsCompleted);
                Assert.Equal(1, sends);
                clock.Advance(TimeSpan.FromSeconds(1));
                using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(2, sends);
            }
            finally
            {
                cancellation.Cancel();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
            }
        }

        private static Task<HttpResponseMessage> ExecuteAsync(
            HttpClient client, HttpRequestMessage request, TimeSpan budget, bool suppliedClock)
        {
            return suppliedClock
                ? client.ExecuteWithResilienceAsync(request, 2, budget, 1, null, new IndependentClock(), CancellationToken.None)
                : client.ExecuteWithResilienceAsync(request, 2, budget, 1, null, CancellationToken.None);
        }

        private sealed class IndependentClock : TimeProvider
        {
            public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            public long Timestamp { get; set; }
            public override long TimestampFrequency => 1000;
            public override DateTimeOffset GetUtcNow() => UtcNow;
            public override long GetTimestamp() => Timestamp;
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;
            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) => _send = send;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_send(request));
        }
    }
}
