using System;
using System.Linq;
using System.Net.Http;
using Lidarr.Plugin.Common.Services.Download;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Download;

public sealed class DownloadCompatibilityTests
{
    [Fact]
    public void ChunkedHttpAssembler_OriginalConstructorSignature_IsRetained()
    {
        var signature = typeof(ChunkedHttpAssembler)
            .GetConstructor(new[] { typeof(HttpClient), typeof(ILogger<ChunkedHttpAssembler>) });

        Assert.NotNull(signature);
    }

    [Fact]
    public void HttpFileDownloadService_OriginalConstructorSignature_IsRetained()
    {
        var signature = typeof(HttpFileDownloadService)
            .GetConstructor(new[] { typeof(HttpClient), typeof(ILogger<HttpFileDownloadService>) });

        Assert.NotNull(signature);
    }

    [Fact]
    public void MediaPolicyConstructors_AreAvailableWithoutReplacingOriginalSignatures()
    {
        var assemblerPolicyCtor = typeof(ChunkedHttpAssembler)
            .GetConstructors()
            .SingleOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 2 &&
                       p[0].ParameterType == typeof(HttpClient) &&
                       p[1].ParameterType == typeof(RemoteMediaUriPolicy);
            });

        var downloadPolicyCtor = typeof(HttpFileDownloadService)
            .GetConstructors()
            .SingleOrDefault(c =>
            {
                var p = c.GetParameters();
                return p.Length == 2 &&
                       p[0].ParameterType == typeof(HttpClient) &&
                       p[1].ParameterType == typeof(RemoteMediaUriPolicy);
            });

        Assert.NotNull(assemblerPolicyCtor);
        Assert.NotNull(downloadPolicyCtor);
    }

    [Fact]
    public void DefaultHttpHandler_DisablesAutomaticRedirects()
    {
        using var handler = HttpHandlerFactory.CreateDefaultHandler();
        var allowAutoRedirect = handler.GetType().GetProperty("AllowAutoRedirect");

        Assert.NotNull(allowAutoRedirect);
        Assert.False((bool)allowAutoRedirect.GetValue(handler)!);
    }
}
