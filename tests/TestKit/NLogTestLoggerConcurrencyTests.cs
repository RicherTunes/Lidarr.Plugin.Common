using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.TestKit.Helpers;
using NLog;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.TestKit;

public class NLogTestLoggerConcurrencyTests
{
    /// <summary>
    /// Regression: <see cref="NLogTestLogger.GetLoggedMessages"/> snapshots the shared
    /// <c>MemoryTarget.Logs</c> via <c>new List&lt;string&gt;(Logs)</c>, which reads
    /// <c>Logs.Count</c> then <c>CopyTo</c>s. A concurrent <c>Add</c> that grows the source
    /// between those steps makes <c>Array.Copy</c> throw <see cref="ArgumentException"/>
    /// ("destination array not long enough") — NOT <see cref="InvalidOperationException"/> — so a
    /// retry filter that only caught <c>InvalidOperationException</c> let the real exception
    /// escape. This hammers reads while many loggers write; it must never observe an escape.
    /// </summary>
    [Fact]
    public async Task GetLoggedMessages_UnderConcurrentLogging_NeverThrows()
    {
        _ = NLogTestLogger.Create("ConcurrencyProbe");
        NLogTestLogger.ClearLoggedMessages();

        using var cts = new CancellationTokenSource();
        Exception? escaped = null;
        const int maxLines = 100_000; // bound memory while keeping heavy contention
        var written = 0;

        var workers = new List<Task>();

        // Writers: grow the shared target under contention.
        for (var w = 0; w < 4; w++)
        {
            var name = $"probe-writer-{w}";
            workers.Add(Task.Run(() =>
            {
                var logger = LogManager.GetLogger(name);
                while (!cts.IsCancellationRequested && Interlocked.Increment(ref written) < maxLines)
                {
                    logger.Info("concurrent log line that grows the shared MemoryTarget");
                }
            }));
        }

        // Readers: race the writers; capture the first escaped exception and stop.
        for (var r = 0; r < 3; r++)
        {
            workers.Add(Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 400 && !cts.IsCancellationRequested)
                {
                    try
                    {
                        _ = NLogTestLogger.GetLoggedMessages();
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref escaped, ex, null);
                        cts.Cancel();
                        return;
                    }
                }
            }));
        }

        try
        {
            // All workers self-terminate (writers at maxLines/cancel, readers at 400ms/cancel).
            await Task.WhenAll(workers);
        }
        finally
        {
            cts.Cancel();
            NLogTestLogger.ClearLoggedMessages();
        }

        Assert.True(escaped is null, $"GetLoggedMessages threw under concurrent logging: {escaped}");
    }
}
