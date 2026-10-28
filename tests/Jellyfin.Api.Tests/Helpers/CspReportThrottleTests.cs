using System;
using Jellyfin.Api.Helpers;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers
{
    public class CspReportThrottleTests
    {
        [Fact]
        public void Register_DistinctWithinLimit_LogsEachOnce()
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
            var throttle = new CspReportThrottle(3, TimeSpan.FromMinutes(1), clock);

            Assert.Equal(CspReportVerdict.Log, throttle.Register("a", out var dropped));
            Assert.Equal(0, dropped);
            Assert.Equal(CspReportVerdict.Log, throttle.Register("b", out _));
            Assert.Equal(CspReportVerdict.Dropped, throttle.Register("a", out _));
            Assert.Equal(CspReportVerdict.Log, throttle.Register("c", out _));
        }

        [Fact]
        public void Register_OverLimit_SaysSoOnceThenStaysQuiet()
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
            var throttle = new CspReportThrottle(2, TimeSpan.FromMinutes(1), clock);

            throttle.Register("a", out _);
            throttle.Register("b", out _);

            Assert.Equal(CspReportVerdict.LimitReached, throttle.Register("c", out _));
            Assert.Equal(CspReportVerdict.Dropped, throttle.Register("d", out _));
            Assert.Equal(CspReportVerdict.Dropped, throttle.Register("c", out _));
        }

        [Fact]
        public void Register_NewWindow_ReportsWhatWasDroppedAndStartsOver()
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
            var throttle = new CspReportThrottle(1, TimeSpan.FromMinutes(1), clock);

            throttle.Register("a", out _);
            throttle.Register("a", out _); // repeat
            throttle.Register("b", out _); // limit reached
            throttle.Register("c", out _); // dropped

            clock.Now += TimeSpan.FromSeconds(59);
            Assert.Equal(CspReportVerdict.Dropped, throttle.Register("d", out var dropped));
            Assert.Equal(0, dropped);

            clock.Now += TimeSpan.FromSeconds(1);
            Assert.Equal(CspReportVerdict.Log, throttle.Register("a", out dropped));
            Assert.Equal(4, dropped);

            Assert.Equal(CspReportVerdict.LimitReached, throttle.Register("b", out dropped));
            Assert.Equal(0, dropped);
        }

        private sealed class FixedClock : TimeProvider
        {
            public FixedClock(DateTimeOffset now)
            {
                Now = now;
            }

            public DateTimeOffset Now { get; set; }

            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
