using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public sealed class HttpResilienceGateCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_ReturnAggregatePermit_WhenProfileWaitIsCancelled(bool suppliedClock)
    {
        var host = $"partial-acquire-{suppliedClock}.test".ToLowerInvariant();
        var profile = HostGateRegistry.Get(host, 1);
        var aggregate = HostGateRegistry.GetAggregate(host, 1);
        await profile.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/resource");
        var sends = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        Task<HttpResponseMessage>? pending = null;
        try
        {
            pending = suppliedClock
                ? client.ExecuteWithResilienceAsync(request, 1, TimeSpan.FromSeconds(30), 1, null, TimeProvider.System, cancellation.Token)
                : client.ExecuteWithResilienceAsync(request, 1, TimeSpan.FromSeconds(30), 1, null, cancellation.Token);
            Assert.Equal(0, aggregate.CurrentCount); // The operation owns the first permit, not the second.
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, aggregate.CurrentCount);
            Assert.Equal(0, profile.CurrentCount); // Our independent holder still owns this permit.
            Assert.Equal(0, sends);
        }
        finally
        {
            cancellation.Cancel();
            if (pending is not null)
            {
                try { (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Dispose(); }
                catch (OperationCanceledException) { }
            }
            profile.Release();
            HostGateRegistry.Clear(host);
        }
    }

    [Theory]
    [InlineData(301, false)]
    [InlineData(302, false)]
    [InlineData(303, false)]
    [InlineData(307, false)]
    [InlineData(308, false)]
    [InlineData(301, true)]
    [InlineData(307, true)]
    public async Task Should_PropagateRedirectCancellation_WithoutLeakingOrOverReleasingPermits(int status, bool blockAggregate)
    {
        var source = $"redirect-source-{status}-{blockAggregate}.test".ToLowerInvariant();
        var target = $"redirect-target-{status}-{blockAggregate}.test".ToLowerInvariant();
        var sourceProfile = HostGateRegistry.Get(source + "|download", 1);
        var sourceAggregate = HostGateRegistry.GetAggregate(source, 1);
        var targetProfile = HostGateRegistry.Get(target + "|download", 1);
        var targetAggregate = HostGateRegistry.GetAggregate(target, 1);
        var held = blockAggregate ? targetAggregate : targetProfile;
        await held.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{source}/resource");
        request.Options.Set(PluginHttpOptions.ProfileKey, "download");
        var content = new TrackingContent();
        using var redirect = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        redirect.Headers.Location = new Uri($"https://{target}/resource");
        var sends = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            sends++;
            return Task.FromResult(redirect);
        }));
        Task<HttpResponseMessage>? pending = null;
        try
        {
            pending = client.ExecuteWithResilienceAsync(request, 1, TimeSpan.FromMinutes(1), 1, null, cancellation.Token);
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, sends);
            Assert.Equal(1, sourceProfile.CurrentCount);
            Assert.Equal(1, sourceAggregate.CurrentCount);
            cancellation.Cancel();
            var error = await Record.ExceptionAsync(async () =>
            {
                using var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            });
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Equal(1, sourceProfile.CurrentCount);
            Assert.Equal(1, sourceAggregate.CurrentCount);
            Assert.Equal(blockAggregate ? 0 : 1, targetAggregate.CurrentCount);
            Assert.Equal(blockAggregate ? 1 : 0, targetProfile.CurrentCount);
            Assert.True(content.WasDisposed);
            Assert.Equal(1, sends);
        }
        finally
        {
            cancellation.Cancel();
            if (pending is not null)
            {
                try { (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Dispose(); }
                catch (OperationCanceledException) { }
            }
            held.Release();
            HostGateRegistry.Clear(source + "|download");
            HostGateRegistry.Clear(source);
            HostGateRegistry.Clear(target + "|download");
            HostGateRegistry.Clear(target);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TrackingContent : StringContent
    {
        public TrackingContent() : base("redirect") { }
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
