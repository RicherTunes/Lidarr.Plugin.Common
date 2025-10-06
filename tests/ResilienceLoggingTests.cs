using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class ResilienceLoggingTests
    {
        private sealed class TestLogger : ILogger
        {
            public ConcurrentQueue<EventId> Events { get; } = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => Null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                Events.Enqueue(eventId);
            }

            private sealed class NullDisposable : IDisposable { public void Dispose() { } }
            private static readonly IDisposable Null = new NullDisposable();
        }

        [Fact]
        public void WarnOnce_EmitsSingleEvent_PerProvider()
        {
            var logger = new TestLogger();

            // Fire multiple times for same provider
            Utilities.ResilienceLogging.WarnOnceFallback(logger, "openai");
            Utilities.ResilienceLogging.WarnOnceFallback(logger, "openai");
            Utilities.ResilienceLogging.WarnOnceFallback(logger, "openai");

            // And once for another provider
            Utilities.ResilienceLogging.WarnOnceFallback(logger, "gemini");

            // Expect exactly two events (one per unique provider)
            Assert.Equal(2, logger.Events.Count);

            // EventId should be 12001 (warning) as defined in the logging partial
            foreach (var ev in logger.Events)
            {
                Assert.Equal(12001, ev.Id);
            }
        }
    }
}
