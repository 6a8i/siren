using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Middleware
{
    public sealed class SecurityHeadersMiddlewareTests : IClassFixture<JellyfinApplicationFactory>
    {
        private readonly JellyfinApplicationFactory _factory;

        public SecurityHeadersMiddlewareTests(JellyfinApplicationFactory factory)
        {
            _factory = factory;
        }

        [Theory]
        [InlineData("System/Info/Public")]
        [InlineData("health")]
        public async Task GetResponses_CarrySecurityHeaders(string url)
        {
            var client = _factory.CreateClient();

            using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertSecurityHeaders(response);
        }

        [Fact]
        public async Task BodilessResponses_CarrySecurityHeaders()
        {
            var client = _factory.CreateClient();

            // MVC answers HEAD on a GET-only action with 405; the headers are added before routing, so even
            // that answer, with no body, carries them.
            using var request = new HttpRequestMessage(HttpMethod.Head, "System/Info/Public");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            AssertSecurityHeaders(response);
        }

        private static void AssertSecurityHeaders(HttpResponseMessage response)
        {
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("strict-origin-when-cross-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
            Assert.Equal("SAMEORIGIN", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
            Assert.Equal("frame-ancestors 'self'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("camera=(), microphone=(), geolocation=()", Assert.Single(response.Headers.GetValues("Permissions-Policy")));
        }
    }
}
