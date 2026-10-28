using System.Net;
using System.Threading.RateLimiting;
using Jellyfin.Api.Constants;
using Jellyfin.Server.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Server.Tests.RateLimiting
{
    public class AuthenticationRateLimitTests
    {
        [Fact]
        public void Partition_WithoutRemoteAddress_IsNotLimited()
        {
            var partition = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(new DefaultHttpContext(), AuthenticationRateLimit.DefaultPermitLimit);

            using var limiter = partition.Factory(partition.PartitionKey);
            for (var i = 0; i < AuthenticationRateLimit.DefaultPermitLimit * 5; i++)
            {
                using var lease = limiter.AttemptAcquire();
                Assert.True(lease.IsAcquired);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Partition_LimitZeroOrLess_IsNotLimited(int permitLimit)
        {
            var partition = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(CreateContext(IPAddress.Parse("203.0.113.7")), permitLimit);

            using var limiter = partition.Factory(partition.PartitionKey);
            for (var i = 0; i < AuthenticationRateLimit.DefaultPermitLimit * 5; i++)
            {
                using var lease = limiter.AttemptAcquire();
                Assert.True(lease.IsAcquired);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(AuthenticationRateLimit.DefaultPermitLimit)]
        [InlineData(25)]
        public void Partition_WithRemoteAddress_RejectsAfterPermitLimitWithRetryAfter(int permitLimit)
        {
            var partition = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(CreateContext(IPAddress.Parse("203.0.113.7")), permitLimit);

            Assert.Equal(IPAddress.Parse("203.0.113.7"), partition.PartitionKey);

            using var limiter = partition.Factory(partition.PartitionKey);
            for (var i = 0; i < permitLimit; i++)
            {
                using var lease = limiter.AttemptAcquire();
                Assert.True(lease.IsAcquired);
            }

            using var rejected = limiter.AttemptAcquire();
            Assert.False(rejected.IsAcquired);
            Assert.True(rejected.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter));
            Assert.True(retryAfter > System.TimeSpan.Zero);
        }

        [Fact]
        public void Partition_ReadsTheLimitFromTheConfiguration()
        {
            var configuration = new ServerConfiguration { AuthenticationRateLimitPerMinute = 3 };
            var context = CreateContext(IPAddress.Parse("203.0.113.7"), configuration);

            var partition = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(context);

            using var limiter = partition.Factory(partition.PartitionKey);
            for (var i = 0; i < 3; i++)
            {
                using var lease = limiter.AttemptAcquire();
                Assert.True(lease.IsAcquired);
            }

            using var rejected = limiter.AttemptAcquire();
            Assert.False(rejected.IsAcquired);
        }

        [Fact]
        public void Partition_ConfiguredToZero_IsNotLimited()
        {
            var configuration = new ServerConfiguration { AuthenticationRateLimitPerMinute = 0 };
            var context = CreateContext(IPAddress.Parse("203.0.113.7"), configuration);

            var partition = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(context);

            using var limiter = partition.Factory(partition.PartitionKey);
            for (var i = 0; i < AuthenticationRateLimit.DefaultPermitLimit * 5; i++)
            {
                using var lease = limiter.AttemptAcquire();
                Assert.True(lease.IsAcquired);
            }
        }

        [Fact]
        public void Partition_IPv4MappedAddress_SharesTheIPv4Partition()
        {
            var plain = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(CreateContext(IPAddress.Parse("203.0.113.7")), AuthenticationRateLimit.DefaultPermitLimit);
            var mapped = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(CreateContext(IPAddress.Parse("::ffff:203.0.113.7")), AuthenticationRateLimit.DefaultPermitLimit);

            Assert.Equal(plain.PartitionKey, mapped.PartitionKey);
        }

        [Fact]
        public void Partition_DifferentAddresses_AreCountedApart()
        {
            var first = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(CreateContext(IPAddress.Parse("203.0.113.7")), AuthenticationRateLimit.DefaultPermitLimit);
            var second = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(CreateContext(IPAddress.Parse("203.0.113.8")), AuthenticationRateLimit.DefaultPermitLimit);

            Assert.NotEqual(first.PartitionKey, second.PartitionKey);
        }

        private static HttpContext CreateContext(IPAddress remoteAddress, ServerConfiguration? configuration = null)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = remoteAddress;

            if (configuration is not null)
            {
                var configurationManager = new Mock<IServerConfigurationManager>();
                configurationManager.SetupGet(c => c.Configuration).Returns(configuration);
                context.RequestServices = new ServiceCollection()
                    .AddSingleton(configurationManager.Object)
                    .BuildServiceProvider();
            }

            return context;
        }
    }
}
