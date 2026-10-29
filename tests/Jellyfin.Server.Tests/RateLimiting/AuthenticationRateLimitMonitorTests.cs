using System;
using System.Net;
using Jellyfin.Server.RateLimiting;
using Xunit;

namespace Jellyfin.Server.Tests.RateLimiting
{
    public class AuthenticationRateLimitMonitorTests
    {
        private static readonly IPAddress _address = IPAddress.Parse("203.0.113.7");
        private static readonly DateTime _start = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Record_ReportsOnceWhenTheThresholdIsPassed_ThenNotAgainWithinTheHour()
        {
            var monitor = new AuthenticationRateLimitMonitor();

            for (var i = 1; i < AuthenticationRateLimitMonitor.Threshold; i++)
            {
                Assert.False(monitor.Record(_address, _start.AddSeconds(i), out var count));
                Assert.Equal(i, count);
            }

            Assert.True(monitor.Record(_address, _start.AddMinutes(1), out var reported));
            Assert.Equal(AuthenticationRateLimitMonitor.Threshold, reported);

            // Still over the threshold, but already reported this hour.
            for (var i = 0; i < 20; i++)
            {
                Assert.False(monitor.Record(_address, _start.AddMinutes(2 + i), out _));
            }
        }

        [Fact]
        public void Record_ReportsAgainAfterAnHourOfRejections()
        {
            var monitor = new AuthenticationRateLimitMonitor();
            Fill(monitor, _start);
            Assert.True(monitor.Record(_address, _start.AddMinutes(5), out _));

            // Rejections keep coming for the next hour; the next report is due when the hour is up.
            for (var minute = 6; minute < 65; minute++)
            {
                Assert.False(monitor.Record(_address, _start.AddMinutes(minute), out _));
            }

            Assert.True(monitor.Record(_address, _start.AddMinutes(66), out var count));
            Assert.True(count >= AuthenticationRateLimitMonitor.Threshold);
        }

        [Fact]
        public void Record_OldRejectionsFallOutOfTheWindow()
        {
            var monitor = new AuthenticationRateLimitMonitor();
            for (var i = 0; i < AuthenticationRateLimitMonitor.Threshold - 1; i++)
            {
                monitor.Record(_address, _start.AddSeconds(i), out _);
            }

            // Two hours later those do not count any more.
            Assert.False(monitor.Record(_address, _start.AddHours(2), out var count));
            Assert.Equal(1, count);
        }

        [Fact]
        public void Record_CountsAddressesApart()
        {
            var monitor = new AuthenticationRateLimitMonitor();
            Fill(monitor, _start);

            Assert.False(monitor.Record(IPAddress.Parse("203.0.113.8"), _start.AddMinutes(1), out var count));
            Assert.Equal(1, count);
            Assert.True(monitor.Record(_address, _start.AddMinutes(1), out _));
        }

        [Fact]
        public void Record_DropsQuietAddressesWhenTheTableIsFull()
        {
            var monitor = new AuthenticationRateLimitMonitor();
            for (var i = 0; i < AuthenticationRateLimitMonitor.MaxAddresses; i++)
            {
                monitor.Record(new IPAddress(new byte[] { 10, 0, (byte)(i / 256), (byte)(i % 256) }), _start, out _);
            }

            // Two hours later the table is full of stale addresses; a new one takes their place and is counted.
            var later = _start.AddHours(2);
            Fill(monitor, later);
            Assert.True(monitor.Record(_address, later.AddMinutes(1), out _));
        }

        private static void Fill(AuthenticationRateLimitMonitor monitor, DateTime at)
        {
            for (var i = 0; i < AuthenticationRateLimitMonitor.Threshold - 1; i++)
            {
                monitor.Record(_address, at.AddSeconds(i), out _);
            }
        }
    }
}
