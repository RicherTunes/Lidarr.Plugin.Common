using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class GenericResilienceExecutorCovTests
    {
        private static readonly Func<HttpRequestMessage, Task<HttpRequestMessage>> _cloneRequest =
            r => Task.FromResult(new HttpRequestMessage(r.Method, r.RequestUri));

        private static readonly Func<HttpRequestMessage, string?> _getHost =
            r => r.RequestUri?.Host;

        private static readonly Func<HttpResponseMessage, int> _getStatusCode =
            r => (int)r.StatusCode;

        private static readonly Func<HttpResponseMessage, TimeSpan?> _getRetryAfter =
            r => r.Headers.RetryAfter?.Delta;

        // Source line 25
        [Fact]
        public async Task ExecuteWithResilienceAsync_PolicyOverload_NullPolicy_Throws()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://a.example.com/");
            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                    request, (req, ct) => Task.FromResult(response),
                    _cloneRequest, _getHost, _getStatusCode, _getRetryAfter, policy: null!));
            Assert.Equal("policy", ex.ParamName);
        }

        // Source line 53
        [Fact]
        public async Task ExecuteWithResilienceAsync_TimeProvider_NullPolicy_Throws()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://b.example.com/");
            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            var fakeTime = new FakeTimeProvider();
            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                    request, (req, ct) => Task.FromResult(response),
                    _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                    policy: null!, timeProvider: fakeTime));
            Assert.Equal("policy", ex.ParamName);
        }

        // Source line 54
        [Fact]
        public async Task ExecuteWithResilienceAsync_TimeProvider_NullTimeProvider_Throws()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://c.example.com/");
            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                    request, (req, ct) => Task.FromResult(response),
                    _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                    ResiliencePolicy.Default, timeProvider: null!));
            Assert.Equal("timeProvider", ex.ParamName);
        }

        // Source line 145
        [Fact]
        public async Task ExecuteWithResilienceAsync_NullSendAsync_Throws()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://d.example.com/");
            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                    request, sendAsync: null!, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                    ResiliencePolicy.Default));
            Assert.Equal("sendAsync", ex.ParamName);
        }

        // Source line 146
        [Fact]
        public async Task ExecuteWithResilienceAsync_NullCloneRequestAsync_Throws()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://e.example.com/");
            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                    request, (req, ct) => Task.FromResult(response),
                    cloneRequestAsync: null!, _getHost, _getStatusCode, _getRetryAfter,
                    ResiliencePolicy.Default));
            Assert.Equal("cloneRequestAsync", ex.ParamName);
        }

        // Source line 147
        [Fact]
        public async Task ExecuteWithResilienceAsync_NullGetHost_Throws()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://f.example.com/");
            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                    request, (req, ct) => Task.FromResult(response),
                    _cloneRequest, getHost: null!, _getStatusCode, _getRetryAfter,
                    ResiliencePolicy.Default));
            Assert.Equal("getHost", ex.ParamName);
        }

        // Source line 148
        [Fact]
        public async Task ExecuteWithResilienceAsync_NullGetStatusCode_Throws()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://g.example.com/");
            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                    request, (req, ct) => Task.FromResult(response),
                    _cloneRequest, _getHost, getStatusCode: null!, _getRetryAfter,
                    ResiliencePolicy.Default));
            Assert.Equal("getStatusCode", ex.ParamName);
        }

        // Source line 149
        [Fact]
        public async Task ExecuteWithResilienceAsync_NullGetRetryAfterDelay_Throws()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://h.example.com/");
            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                GenericResilienceExecutor.ExecuteWithResilienceAsync<HttpRequestMessage, HttpResponseMessage>(
                    request, (req, ct) => Task.FromResult(response),
                    _cloneRequest, _getHost, _getStatusCode, getRetryAfterDelay: null!,
                    ResiliencePolicy.Default));
            Assert.Equal("getRetryAfterDelay", ex.ParamName);
        }

        // Source lines 196-199: retryable status codes
        [Fact]
        public async Task ExecuteWithResilienceAsync_RetriesOn408()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://408c.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new HttpResponseMessage((HttpStatusCode)408)
                    : new HttpResponseMessage(HttpStatusCode.OK));
            };
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                maxRetries: 3, retryBudget: TimeSpan.FromSeconds(10),
                maxConcurrencyPerHost: 2, perRequestTimeout: null, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, attempts);
            HostGateRegistry.Clear("408c.example.com");
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_RetriesOn500()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://500c.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : new HttpResponseMessage(HttpStatusCode.OK));
            };
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                maxRetries: 3, retryBudget: TimeSpan.FromSeconds(10),
                maxConcurrencyPerHost: 2, perRequestTimeout: null, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, attempts);
            HostGateRegistry.Clear("500c.example.com");
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_RetriesOn502()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://502c.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                    : new HttpResponseMessage(HttpStatusCode.OK));
            };
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                maxRetries: 3, retryBudget: TimeSpan.FromSeconds(10),
                maxConcurrencyPerHost: 2, perRequestTimeout: null, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, attempts);
            HostGateRegistry.Clear("502c.example.com");
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_RetriesOn503()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://503c.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK));
            };
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                maxRetries: 3, retryBudget: TimeSpan.FromSeconds(10),
                maxConcurrencyPerHost: 2, perRequestTimeout: null, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, attempts);
            HostGateRegistry.Clear("503c.example.com");
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_RetriesOn599()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://599c.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new HttpResponseMessage((HttpStatusCode)599)
                    : new HttpResponseMessage(HttpStatusCode.OK));
            };
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                maxRetries: 3, retryBudget: TimeSpan.FromSeconds(10),
                maxConcurrencyPerHost: 2, perRequestTimeout: null, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, attempts);
            HostGateRegistry.Clear("599c.example.com");
        }

        // Source line 200: non-retryable codes return immediately
        [Fact]
        public async Task ExecuteWithResilienceAsync_NoRetryOn200()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://200c.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            };
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                maxRetries: 3, retryBudget: TimeSpan.FromSeconds(10),
                maxConcurrencyPerHost: 2, perRequestTimeout: null, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, attempts);
            HostGateRegistry.Clear("200c.example.com");
        }

        [Fact]
        public async Task ExecuteWithResilienceAsync_NoRetryOn404()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://404c.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            };
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                maxRetries: 3, retryBudget: TimeSpan.FromSeconds(10),
                maxConcurrencyPerHost: 2, perRequestTimeout: null, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(1, attempts);
            HostGateRegistry.Clear("404c.example.com");
        }

        // Source line 200: max retries exhausted
        [Fact]
        public async Task ExecuteWithResilienceAsync_MaxRetriesExhausted()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://maxc.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            };
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                maxRetries: 2, retryBudget: TimeSpan.FromSeconds(10),
                maxConcurrencyPerHost: 2, perRequestTimeout: null, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(2, attempts);
            HostGateRegistry.Clear("maxc.example.com");
        }

        // Source line 169-170: null host
        [Fact]
        public async Task ExecuteWithResilienceAsync_NullHost_Works()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://nullc.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            };
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, r => null, _getStatusCode, _getRetryAfter,
                maxRetries: 2, retryBudget: TimeSpan.FromSeconds(5),
                maxConcurrencyPerHost: 2, perRequestTimeout: null, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, attempts);
        }

        // Source lines 190-192: TimeoutException
        [Fact]
        public async Task ExecuteWithResilienceAsync_Timeout_ThrowsTimeoutException()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://toc.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = async (req, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };
            var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
                GenericResilienceExecutor.ExecuteWithResilienceAsync(
                    request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                    maxRetries: 2, retryBudget: TimeSpan.FromSeconds(30),
                    maxConcurrencyPerHost: 2, perRequestTimeout: TimeSpan.FromMilliseconds(100),
                    cancellationToken: CancellationToken.None));
            Assert.Contains("per-request timeout", ex.Message);
        }

        // Source lines 186-193: caller cancellation
        [Fact]
        public async Task ExecuteWithResilienceAsync_CallerCancel_Propagates()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://cc.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = async (req, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            };
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                GenericResilienceExecutor.ExecuteWithResilienceAsync(
                    request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                    maxRetries: 2, retryBudget: TimeSpan.FromSeconds(30),
                    maxConcurrencyPerHost: 2, perRequestTimeout: TimeSpan.FromMilliseconds(100),
                    cancellationToken: cts.Token));
        }

        // Source lines 216-219: deadline exceeded
        [Fact]
        public async Task ExecuteWithResilienceAsync_DeadlineExceeded_NoRetry()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://dlc.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            };
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, r => TimeSpan.FromDays(1),
                maxRetries: 5, retryBudget: TimeSpan.FromMilliseconds(1),
                maxConcurrencyPerHost: 2, perRequestTimeout: null,
                cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(1, attempts);
            HostGateRegistry.Clear("dlc.example.com");
        }

        // Source lines 15-39: policy overload
        [Fact]
        public async Task ExecuteWithResilienceAsync_PolicyOverload_Works()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://poc.example.com/");
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
            };
            var policy = ResiliencePolicy.Default.With(maxRetries: 2);
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter, policy);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(1, attempts);
            HostGateRegistry.Clear("poc.example.com");
        }

        // Source lines 42-66: TimeProvider+policy overload
        [Fact]
        public async Task ExecuteWithResilienceAsync_TimeProviderPolicy_Works()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://tpc.example.com/");
            var fakeTime = new FakeTimeProvider();
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            };
            var policy = ResiliencePolicy.Default.With(maxRetries: 2);
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter, policy, fakeTime);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(1, attempts);
            HostGateRegistry.Clear("tpc.example.com");
        }

        // Source lines 100-130: TimeProvider+explicit params overload
        [Fact]
        public async Task ExecuteWithResilienceAsync_TimeProviderExplicit_Works()
        {
            var attempts = 0;
            var request = new HttpRequestMessage(HttpMethod.Get, "http://epc.example.com/");
            var fakeTime = new FakeTimeProvider();
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send = (req, ct) =>
            {
                attempts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            };
            var response = await GenericResilienceExecutor.ExecuteWithResilienceAsync(
                request, send, _cloneRequest, _getHost, _getStatusCode, _getRetryAfter,
                maxRetries: 2, retryBudget: TimeSpan.FromSeconds(5),
                maxConcurrencyPerHost: 2, perRequestTimeout: null,
                timeProvider: fakeTime, cancellationToken: CancellationToken.None);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(1, attempts);
            HostGateRegistry.Clear("epc.example.com");
        }
    }
}
