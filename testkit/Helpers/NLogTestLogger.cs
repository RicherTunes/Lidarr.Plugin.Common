using System.Collections.Generic;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace Lidarr.Plugin.Common.TestKit.Helpers;

/// <summary>
/// Test helper that provides a real NLog <see cref="Logger"/> instance configured for testing.
/// </summary>
/// <remarks>
/// <para>
/// NLog's <see cref="Logger"/> is a sealed class and cannot be mocked directly.
/// This helper creates a real logger wired to an in-memory <see cref="MemoryTarget"/>
/// so that log output can be captured and asserted against in tests.
/// </para>
/// <para>
/// Thread safety: <see cref="Create"/> uses a lock to ensure the shared static
/// instance and NLog configuration are initialized only once. <see cref="GetLoggedMessages"/>
/// and <see cref="ClearLoggedMessages"/> operate on the same <c>testMemory</c> target
/// and are safe to call from any thread after <see cref="Create"/> has been invoked.
/// </para>
/// <para>
/// Isolation: For tests that must not share log state, call <see cref="ClearLoggedMessages"/>
/// in each test's constructor or <c>Dispose</c>. If two test classes need
/// independent NLog configurations, use the
/// <c>[Collection("...")]</c> xUnit attribute to prevent parallel execution.
/// </para>
/// </remarks>
public static class NLogTestLogger
{
    private static Logger? _testLogger;
    private static readonly object Lock = new();

    /// <summary>
    /// Gets a real NLog <see cref="Logger"/> configured with a <see cref="MemoryTarget"/>
    /// for in-process log capture. Calling this method a second time returns the same
    /// logger instance without reinitializing NLog.
    /// </summary>
    /// <param name="name">Logger name; defaults to <c>"TestLogger"</c>.</param>
    /// <returns>A <see cref="Logger"/> whose output can be read via <see cref="GetLoggedMessages"/>.</returns>
    public static Logger Create(string name = "TestLogger")
    {
        lock (Lock)
        {
            if (_testLogger is not null)
                return _testLogger;

            var config = new LoggingConfiguration();

            var memoryTarget = new MemoryTarget("testMemory")
            {
                Layout = "${level:uppercase=true}: ${message} ${exception:format=tostring}",
            };

            config.AddTarget(memoryTarget);
            config.AddRuleForAllLevels(memoryTarget);

            LogManager.Configuration = config;
            _testLogger = LogManager.GetLogger(name);
            return _testLogger;
        }
    }

    /// <summary>
    /// Creates a no-op logger that discards all log messages.
    /// Use this when the test exercises code paths that require a logger but
    /// does not need to assert on log output.
    /// </summary>
    /// <param name="name">Logger name; defaults to <c>"NullLogger"</c>.</param>
    /// <returns>A <see cref="Logger"/> that silently drops all log events.</returns>
    public static Logger CreateNullLogger(string name = "NullLogger")
    {
        // Use an ISOLATED LogFactory so creating a null logger never mutates the process-global
        // LogManager.Configuration. The previous implementation swapped LogManager.Configuration
        // (save -> null-config -> restore); since many tests call this concurrently, that swap
        // raced the shared "testMemory" config installed by Create() — transiently dropping or
        // permanently clobbering captured log output and flaking log-assertion tests.
        var factory = new LogFactory();
        var config = new LoggingConfiguration(factory);

        var nullTarget = new NullTarget("testNull");
        config.AddTarget(nullTarget);
        config.AddRuleForAllLevels(nullTarget);

        factory.Configuration = config;
        return factory.GetLogger(name);
    }

    /// <summary>
    /// Returns all log lines captured by the <c>testMemory</c> target since the last
    /// <see cref="ClearLoggedMessages"/> call (or since <see cref="Create"/> was first called).
    /// </summary>
    /// <returns>
    /// A read-only snapshot of log lines. Returns an empty list if <see cref="Create"/> has not
    /// been called yet or if the target has no logs.
    /// </returns>
    public static IList<string> GetLoggedMessages()
    {
        var memoryTarget = LogManager.Configuration?.FindTargetByName<MemoryTarget>("testMemory");
        if (memoryTarget is null)
        {
            return new List<string>();
        }

        // Return a defensive COPY, not the live list. The shared target captures ALL loggers
        // (AddRuleForAllLevels) and its Logs getter is unsynchronized w.r.t. Add, so a logger on
        // another thread may Add while we snapshot.
        var logs = memoryTarget.Logs;

        // Fast path: bulk snapshot. `new List<string>(Logs)` reads Logs.Count, allocates, then
        // CopyTo's — a concurrent Add that grows the source between those steps makes Array.Copy
        // throw ArgumentException ("destination array not long enough") (or IndexOutOfRangeException
        // on a torn read); a caller enumerating a live reference would throw InvalidOperationException.
        // A few retries clear the common light-contention case.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                return new List<string>(logs);
            }
            catch (System.Exception ex) when (
                ex is System.ArgumentException or System.IndexOutOfRangeException or System.InvalidOperationException)
            {
                System.Threading.Thread.Yield();
            }
        }

        // Fallback (sustained contention): a tolerant element-wise copy that CANNOT throw. Snapshot
        // the count ONCE so the loop is strictly bounded and always terminates (without this, a
        // never-pausing writer could grow the list faster than i advances and spin unbounded). The
        // list only grows during a test, so the captured prefix is the correct snapshot; any
        // boundary/torn-read race (e.g. a concurrent Clear) just stops the copy early.
        var snapshot = new List<string>();
        var count = logs.Count;
        for (var i = 0; i < count; i++)
        {
            try
            {
                snapshot.Add(logs[i]);
            }
            catch (System.Exception)
            {
                break;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Clears all messages in the <c>testMemory</c> target.
    /// </summary>
    public static void ClearLoggedMessages()
    {
        var memoryTarget = LogManager.Configuration?.FindTargetByName<MemoryTarget>("testMemory");
        memoryTarget?.Logs.Clear();
    }
}
