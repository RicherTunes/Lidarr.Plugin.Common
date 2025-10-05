using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Utilities
{
    internal static partial class ResilienceLogging
    {
        private static readonly ConcurrentDictionary<string, byte> WarnedProviders = new(StringComparer.OrdinalIgnoreCase);

        [LoggerMessage(EventId = 12001, Level = LogLevel.Warning,
            Message = "Provider '{Provider}' is using the static HTTP resilience path; DI pipeline not active.")]
        private static partial void ProviderResilienceFallbackCore(ILogger logger, string provider);

        public static void WarnOnceFallback(ILogger logger, string provider)
        {
            if (logger == null) return;
            if (string.IsNullOrWhiteSpace(provider)) provider = "<unknown>";
            if (WarnedProviders.TryAdd(provider, 0))
            {
                ProviderResilienceFallbackCore(logger, provider);
            }
        }
    }
}

