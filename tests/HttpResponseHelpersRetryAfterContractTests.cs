using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using Lidarr.Plugin.Common.Services.Http;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

[Trait("Category", "Unit")]
public sealed class HttpResponseHelpersRetryAfterContractTests
{
    private static readonly DateTimeOffset FixedNow = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void Should_ParseIntegerSecondsInvariantly_WhenCurrentCultureUsesDifferentPositiveSign()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var customCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        customCulture.NumberFormat.PositiveSign = "p";

        try
        {
            CultureInfo.CurrentCulture = customCulture;
            var headers = Header("+12");

            Assert.Equal(TimeSpan.FromSeconds(12), HttpResponseHelpers.ParseRetryAfter(headers));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.Same(originalCulture, CultureInfo.CurrentCulture);
    }

    [Fact]
    public void Should_SkipUnrelatedAndEmptyMatches_ThenUseFirstValidValue()
    {
        var headers = new[]
        {
            new KeyValuePair<string, string>("X-Retry-After", "99"),
            new KeyValuePair<string, string>("Retry-After", null!),
            new KeyValuePair<string, string>("Retry-After", string.Empty),
            new KeyValuePair<string, string>("retry-after", "   "),
            new KeyValuePair<string, string>("RETRY-AFTER", "+12")
        };

        Assert.Equal(TimeSpan.FromSeconds(12), HttpResponseHelpers.ParseRetryAfter(headers));
    }

    [Fact]
    public void Should_StopAfterFirstNonEmptyMalformedMatch()
    {
        var headers = new ThrowAfterEntriesEnumerable(
            new KeyValuePair<string, string>("Retry-After", "not a retry value"));

        Assert.Null(HttpResponseHelpers.ParseRetryAfter(headers));
        Assert.Equal(1, headers.MoveNextCalls);
    }

    [Fact]
    public void Should_StopAfterFirstValidIntegerWithoutConsumingLaterEntry()
    {
        var headers = new ThrowAfterEntriesEnumerable(
            new KeyValuePair<string, string>("Retry-After", "12"));

        Assert.Equal(TimeSpan.FromSeconds(12), HttpResponseHelpers.ParseRetryAfter(headers));
        Assert.Equal(1, headers.MoveNextCalls);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("12, 13")]
    public void Should_PreserveBroadInvariantWholeValueDateFallback(string value)
    {
        Assert.NotNull(HttpResponseHelpers.ParseRetryAfter(Header(value)));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("-1", 0)]
    [InlineData("+12", 12)]
    [InlineData("2147483647", 2147483647)]
    public void Should_ParseSignedInt32SecondsAndClampNegative(string value, long seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), HttpResponseHelpers.ParseRetryAfter(Header(value)));
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("1e5")]
    [InlineData("١٢")]
    public void Should_ReturnNullForUnsupportedIntegerFormsThatAreNotDates(string value)
    {
        Assert.Null(HttpResponseHelpers.ParseRetryAfter(Header(value)));
    }

    [Fact]
    public void Should_ExposeInternalClockAwareOverloadWithExactSignature()
    {
        var method = FindClockAwareOverload();
        Assert.NotNull(method);
        Assert.True(method.IsAssembly);
        Assert.Equal(typeof(TimeSpan?), method.ReturnType);
    }

    [Fact]
    public void Should_ReadClockOnceForSelectedValidDateAndReturnExactDelta()
    {
        var clock = new RecordingTimeProvider(FixedNow);
        var date = FixedNow.AddSeconds(37).ToString("R", CultureInfo.InvariantCulture);

        var result = InvokeClockAware(Header(date), clock);

        Assert.Equal(TimeSpan.FromSeconds(37), result);
        Assert.Equal(1, clock.Reads);
    }

    [Fact]
    public void Should_NotReadClockForPathsWithoutSuccessfulDateParse()
    {
        var clock = new RecordingTimeProvider(FixedNow);

        Assert.Null(InvokeClockAware(null, clock));
        Assert.Null(InvokeClockAware(Array.Empty<KeyValuePair<string, string>>(), clock));
        Assert.Null(InvokeClockAware(Header(""), clock));
        Assert.Equal(TimeSpan.FromSeconds(12), InvokeClockAware(Header("12"), clock));
        Assert.Null(InvokeClockAware(Header("not a retry value"), clock));
        Assert.Null(InvokeClockAware(new ThrowOnGetEnumeratorEnumerable(), clock));
        Assert.Null(InvokeClockAware(new ThrowOnMoveNextEnumerable(), clock));
        Assert.Null(InvokeClockAware(new ThrowOnCurrentEnumerable(), clock));
        Assert.Equal(0, clock.Reads);
    }

    [Fact]
    public void Should_StopAfterFirstValidDateWithoutConsumingLaterEntry()
    {
        var clock = new RecordingTimeProvider(FixedNow);
        var date = FixedNow.AddSeconds(37).ToString("R", CultureInfo.InvariantCulture);
        var headers = new ThrowAfterEntriesEnumerable(
            new KeyValuePair<string, string>("Retry-After", date));

        Assert.Equal(TimeSpan.FromSeconds(37), InvokeClockAware(headers, clock));
        Assert.Equal(1, clock.Reads);
        Assert.Equal(1, headers.MoveNextCalls);
    }

    [Fact]
    public void Should_ReturnAdvisoryNullWhenClockReadFails()
    {
        var clock = new RecordingTimeProvider(FixedNow, throwOnRead: true);
        var date = FixedNow.AddSeconds(37).ToString("R", CultureInfo.InvariantCulture);

        Assert.Null(InvokeClockAware(Header(date), clock));
        Assert.Equal(1, clock.Reads);
    }

    [Fact]
    public void Should_ReturnAdvisoryNullWhenEnumeratorDisposeFailsAfterReadingDateClockOnce()
    {
        var clock = new RecordingTimeProvider(FixedNow);
        var date = FixedNow.AddSeconds(37).ToString("R", CultureInfo.InvariantCulture);
        var headers = new ThrowOnDisposeEnumerable(new KeyValuePair<string, string>("Retry-After", date));

        Assert.Null(InvokeClockAware(headers, clock));
        Assert.Equal(1, clock.Reads);
        Assert.True(headers.DisposeCalled);
    }

    [Fact]
    public void Should_ReturnZeroForExpiredDateWhileKeepingRawAbsenceNullable()
    {
        var clock = new RecordingTimeProvider(FixedNow);
        var date = FixedNow.AddSeconds(-1).ToString("R", CultureInfo.InvariantCulture);

        Assert.Equal(TimeSpan.Zero, InvokeClockAware(Header(date), clock));
        Assert.Null(InvokeClockAware(null, clock));
        Assert.Equal(TimeSpan.Zero, RateLimitHeaderUtilities.ResolveRetryAfter((RetryConditionHeaderValue?)null));
        Assert.Equal(1, clock.Reads);
    }

    [Fact]
    public void Should_RejectNullTimeProviderConsistentlyWithTypedResolver()
    {
        var method = RequireClockAwareOverload();
        var thrown = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object?[] { Header("12"), null }));

        Assert.IsType<ArgumentNullException>(thrown.InnerException);
    }

    private static KeyValuePair<string, string>[] Header(string value)
        => new[] { new KeyValuePair<string, string>("Retry-After", value) };

    private static MethodInfo? FindClockAwareOverload()
        => typeof(HttpResponseHelpers).GetMethod(
            "ParseRetryAfter",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(IEnumerable<KeyValuePair<string, string>>), typeof(TimeProvider) },
            modifiers: null);

    private static MethodInfo RequireClockAwareOverload()
        => FindClockAwareOverload() ?? throw new Xunit.Sdk.XunitException(
            "Missing internal ParseRetryAfter(IEnumerable<KeyValuePair<string,string>>?, TimeProvider) overload.");

    private static TimeSpan? InvokeClockAware(
        IEnumerable<KeyValuePair<string, string>>? headers,
        TimeProvider timeProvider)
        => (TimeSpan?)RequireClockAwareOverload().Invoke(null, new object?[] { headers, timeProvider });

    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        private readonly bool _throwOnRead;

        public RecordingTimeProvider(DateTimeOffset now, bool throwOnRead = false)
        {
            _now = now;
            _throwOnRead = throwOnRead;
        }

        public int Reads { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            if (_throwOnRead) throw new InvalidOperationException("clock failed");
            return _now;
        }
    }

    private sealed class ThrowOnGetEnumeratorEnumerable : IEnumerable<KeyValuePair<string, string>>
    {
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => throw new InvalidOperationException("enumeration failed");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowOnMoveNextEnumerable : IEnumerable<KeyValuePair<string, string>>
    {
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => new Enumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<KeyValuePair<string, string>>
        {
            public KeyValuePair<string, string> Current => throw new InvalidOperationException("Current must not be read");
            object IEnumerator.Current => Current;
            public bool MoveNext() => throw new InvalidOperationException("MoveNext failed");
            public void Reset() => throw new NotSupportedException();
            public void Dispose() { }
        }
    }

    private sealed class ThrowOnCurrentEnumerable : IEnumerable<KeyValuePair<string, string>>
    {
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => new Enumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<KeyValuePair<string, string>>
        {
            private bool _moved;
            public KeyValuePair<string, string> Current => throw new InvalidOperationException("Current failed");
            object IEnumerator.Current => Current;
            public bool MoveNext()
            {
                if (_moved) return false;
                _moved = true;
                return true;
            }
            public void Reset() => throw new NotSupportedException();
            public void Dispose() { }
        }
    }

    private sealed class ThrowAfterEntriesEnumerable : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly KeyValuePair<string, string>[] _entries;

        public ThrowAfterEntriesEnumerable(params KeyValuePair<string, string>[] entries) => _entries = entries;
        public int MoveNextCalls { get; private set; }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            foreach (var entry in _entries)
            {
                MoveNextCalls++;
                yield return entry;
            }

            MoveNextCalls++;
            throw new InvalidOperationException("later entry must not be consumed");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowOnDisposeEnumerable : IEnumerable<KeyValuePair<string, string>>
    {
        private readonly KeyValuePair<string, string> _entry;

        public ThrowOnDisposeEnumerable(KeyValuePair<string, string> entry) => _entry = entry;
        public bool DisposeCalled { get; private set; }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => new Enumerator(this, _entry);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator : IEnumerator<KeyValuePair<string, string>>
        {
            private readonly ThrowOnDisposeEnumerable _owner;
            private readonly KeyValuePair<string, string> _entry;
            private bool _moved;

            public Enumerator(ThrowOnDisposeEnumerable owner, KeyValuePair<string, string> entry)
            {
                _owner = owner;
                _entry = entry;
            }

            public KeyValuePair<string, string> Current => _entry;
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_moved) return false;
                _moved = true;
                return true;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
                _owner.DisposeCalled = true;
                throw new InvalidOperationException("dispose failed");
            }
        }
    }
}
