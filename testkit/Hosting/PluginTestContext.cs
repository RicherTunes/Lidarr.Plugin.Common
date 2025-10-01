using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.TestKit.Hosting;

/// <summary>
/// Minimal implementation of <see cref="IPluginContext"/> used during automated tests.
/// Captures emitted logs and exposes host semantics without depending on the real host process.
/// </summary>
public sealed class PluginTestContext : IPluginContext, IDisposable
{
    private readonly bool _ownsFactory;

    public PluginTestContext(Version hostVersion, ILoggerFactory? loggerFactory = null, IServiceProvider? services = null)
    {
        HostVersion = hostVersion ?? throw new ArgumentNullException(nameof(hostVersion));
        Services = services;

        if (loggerFactory is null)
        {
            var factory = new TestLoggerFactory();
            LoggerFactory = factory;
            LogEntries = factory.Sink;
            _ownsFactory = true;
        }
        else
        {
            LoggerFactory = loggerFactory;
            LogEntries = loggerFactory is TestLoggerFactory factory ? factory.Sink : TestLogSink.Empty;
        }
    }

    public Version HostVersion { get; }

    public ILoggerFactory LoggerFactory { get; }

    public IServiceProvider? Services { get; }

    /// <summary>Collected log entries when the context owns the logger pipeline.</summary>
    public TestLogSink LogEntries { get; }

    public void Dispose()
    {
        if (_ownsFactory)
        {
            LoggerFactory.Dispose();
        }
    }
}

/// <summary>Simple sink that stores log entries emitted during testing.</summary>
public sealed class TestLogSink
{
    private readonly ConcurrentQueue<TestLogEntry> _entries = new();

    public static TestLogSink Empty { get; } = new();

    public void Add(TestLogEntry entry) => _entries.Enqueue(entry);

    public IReadOnlyCollection<TestLogEntry> Snapshot() => _entries.ToArray();
}

/// <summary>Represents a single structured log entry captured during testing.</summary>
public sealed record TestLogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Provides programmatic access to the captured sink when callers prefer to inject a factory instance explicitly.
/// </summary>
public sealed class TestLoggerFactory : ILoggerFactory
{
    private readonly List<ILoggerProvider> _providers = new();

    public TestLoggerFactory()
    {
        Sink = new TestLogSink();
        _providers.Add(new TestLoggerProvider(Sink));
    }

    public TestLogSink Sink { get; }

    public void AddProvider(ILoggerProvider provider)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        _providers.Add(provider);
    }

    public ILogger CreateLogger(string categoryName)
    {
        var providers = _providers.Select(p => p.CreateLogger(categoryName)).ToArray();
        return new MultiplexLogger(providers);
    }

    public void Dispose()
    {
        foreach (var provider in _providers.OfType<IDisposable>())
        {
            provider.Dispose();
        }
    }

    private sealed class MultiplexLogger : ILogger
    {
        private readonly ILogger[] _loggers;

        public MultiplexLogger(ILogger[] loggers) => _loggers = loggers;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var scopes = _loggers.Select(logger => logger.BeginScope(state) ?? NullScope.Instance).ToArray();
            return new Scope(scopes);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter is null)
            {
                throw new ArgumentNullException(nameof(formatter));
            }

            if (state is null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            foreach (var logger in _loggers)
            {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly IDisposable[] _scopes;

            public Scope(IDisposable[] scopes) => _scopes = scopes;

            public void Dispose()
            {
                foreach (var scope in _scopes)
                {
                    scope?.Dispose();
                }
            }
        }
    }
}

internal sealed class TestLoggerProvider : ILoggerProvider
{
    private readonly TestLogSink _sink;

    public TestLoggerProvider(TestLogSink sink) => _sink = sink;

    public ILogger CreateLogger(string categoryName) => new SinkLogger(categoryName, _sink);

    public void Dispose()
    {
    }

    private sealed class SinkLogger : ILogger
    {
        private readonly string _category;
        private readonly TestLogSink _sink;

        public SinkLogger(string category, TestLogSink sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter is null)
            {
                throw new ArgumentNullException(nameof(formatter));
            }

            if (state is null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var message = formatter(state, exception);
            _sink.Add(new TestLogEntry(_category, logLevel, message, exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

internal sealed class NullScope : IDisposable
{
    public static NullScope Instance { get; } = new();

    public void Dispose()
    {
    }
}



