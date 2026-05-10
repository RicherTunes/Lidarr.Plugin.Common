using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Xunit;

using ObservabilityClass = Lidarr.Plugin.Common.Utilities.Observability;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Unit")]
    public class ObservabilityCovTests
    {
        [Fact]
        public void ActivitySource_HasCorrectName()
        {
            Assert.Equal("Lidarr.Plugin.Common", ObservabilityClass.Activity.Name);
        }

        [Fact]
        public void ActivitySource_StartActivity_WithListener_ReturnsActivity()
        {
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Lidarr.Plugin.Common",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
            };
            ActivitySource.AddActivityListener(listener);

            using var activity = ObservabilityClass.Activity.StartActivity("test-operation");

            Assert.NotNull(activity);
            Assert.Equal("test-operation", activity!.OperationName);
            Assert.Equal("Lidarr.Plugin.Common", activity.Source.Name);
        }

        [Fact]
        public void CacheHit_Counter_EmitsCorrectMetricName()
        {
            string? observedName = null;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Lidarr.Plugin.Common" && instrument.Name == "cache.hit")
                {
                    observedName = instrument.Name;
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.Start();

            // Force the counter to be observed by adding a measurement
            ObservabilityClass.Metrics.CacheHit.Add(1);

            Assert.Equal("cache.hit", observedName);
        }

        [Fact]
        public void CacheMiss_Counter_EmitsCorrectMetricName()
        {
            string? observedName = null;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Lidarr.Plugin.Common" && instrument.Name == "cache.miss")
                {
                    observedName = instrument.Name;
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.Start();

            ObservabilityClass.Metrics.CacheMiss.Add(1);

            Assert.Equal("cache.miss", observedName);
        }

        [Fact]
        public void CacheHit_Counter_RecordsMeasurements()
        {
            long total = 0;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Lidarr.Plugin.Common" && instrument.Name == "cache.hit")
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            {
                if (instrument.Name == "cache.hit") total += measurement;
            });
            listener.Start();

            ObservabilityClass.Metrics.CacheHit.Add(3);
            ObservabilityClass.Metrics.CacheHit.Add(7);
            listener.RecordObservableInstruments();

            Assert.Equal(10, total);
        }

        [Fact]
        public void RetryCount_Counter_RecordsMeasurements()
        {
            long total = 0;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Lidarr.Plugin.Common" && instrument.Name == "retry.count")
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            {
                if (instrument.Name == "retry.count") total += measurement;
            });
            listener.Start();

            ObservabilityClass.Metrics.RetryCount.Add(2);
            ObservabilityClass.Metrics.RetryCount.Add(5);

            Assert.Equal(7, total);
        }

        [Fact]
        public void AuthRefreshes_Counter_RecordsMeasurements()
        {
            long total = 0;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Lidarr.Plugin.Common" && instrument.Name == "auth.refreshes")
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            {
                if (instrument.Name == "auth.refreshes") total += measurement;
            });
            listener.Start();

            ObservabilityClass.Metrics.AuthRefreshes.Add(1);

            Assert.Equal(1, total);
        }

        [Theory]
        [InlineData("cache.hit")]
        [InlineData("cache.miss")]
        [InlineData("cache.revalidate")]
        [InlineData("retry.count")]
        [InlineData("auth.refreshes")]
        [InlineData("resilience.non_di")]
        public void AllCounters_AreRegisteredWithCorrectNames(string expectedName)
        {
            var observedNames = new List<string>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Lidarr.Plugin.Common")
                    observedNames.Add(instrument.Name);
            };
            listener.Start();

            // Touch all counters to force registration
            ObservabilityClass.Metrics.CacheHit.Add(0);
            ObservabilityClass.Metrics.CacheMiss.Add(0);
            ObservabilityClass.Metrics.CacheRevalidate.Add(0);
            ObservabilityClass.Metrics.RetryCount.Add(0);
            ObservabilityClass.Metrics.AuthRefreshes.Add(0);
            ObservabilityClass.Metrics.ResilienceNonDI.Add(0);

            Assert.Contains(expectedName, observedNames);
        }

#if NET8_0_OR_GREATER
        [Fact]
        public void RateLimiterInflight_UpDownCounter_TracksNetValue()
        {
            long total = 0;
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == "Lidarr.Plugin.Common" && instrument.Name == "ratelimiter.inflight")
                    meterListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            {
                if (instrument.Name == "ratelimiter.inflight") total += measurement;
            });
            listener.Start();

            ObservabilityClass.Metrics.RateLimiterInflight.Add(3);
            ObservabilityClass.Metrics.RateLimiterInflight.Add(-1);

            Assert.Equal(2, total);
        }
#endif
    }
}
