using System.Net;
using System.Threading.RateLimiting;
using Jellyfin.Api.Constants;
using Jellyfin.Server.Extensions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Server.Tests.RateLimiting
{
    public class AuthenticationRateLimitTests
    {
        [Fact]
        public void Partition_WithoutRemoteAddress_IsNotLimited()
        {
            var partition = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(new DefaultHttpContext());

            using var limiter = partition.Factory(partition.PartitionKey);
            for (var i = 0; i < AuthenticationRateLimit.PermitLimit * 5; i++)
            {
                using var lease = limiter.AttemptAcquire();
                Assert.True(lease.IsAcquired);
            }
        }

        [Fact]
        public void Partition_WithRemoteAddress_RejectsAfterPermitLimitWithRetryAfter()
        {
            var partition = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(CreateContext(IPAddress.Parse("203.0.113.7")));

            Assert.Equal(IPAddress.Parse("203.0.113.7"), partition.PartitionKey);

            using var limiter = partition.Factory(partition.PartitionKey);
            for (var i = 0; i < AuthenticationRateLimit.PermitLimit; i++)
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
        public void Partition_IPv4MappedAddress_SharesTheIPv4Partition()
        {
            var plain = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(CreateContext(IPAddress.Parse("203.0.113.7")));
            var mapped = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(CreateContext(IPAddress.Parse("::ffff:203.0.113.7")));

            Assert.Equal(plain.PartitionKey, mapped.PartitionKey);
        }

        [Fact]
        public void Partition_DifferentAddresses_AreCountedApart()
        {
            var first = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(CreateContext(IPAddress.Parse("203.0.113.7")));
            var second = ApiServiceCollectionExtensions.GetAuthenticationRateLimitPartition(CreateContext(IPAddress.Parse("203.0.113.8")));

            Assert.NotEqual(first.PartitionKey, second.PartitionKey);
        }

        private static HttpContext CreateContext(IPAddress remoteAddress)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = remoteAddress;
            return context;
        }
    }
}
