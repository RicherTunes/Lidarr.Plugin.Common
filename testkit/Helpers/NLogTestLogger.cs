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
        var config = new LoggingConfiguration();

        var nullTarget = new NullTarget("testNull");
        config.AddTarget(nullTarget);
        config.AddRuleForAllLevels(nullTarget);

        var savedConfig = LogManager.Configuration;
        LogManager.Configuration = config;
        var logger = LogManager.GetLogger(name);
        LogManager.Configuration = savedConfig;

        return logger;
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
        return memoryTarget?.Logs ?? new List<string>();
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
