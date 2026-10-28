using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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

        [Fact]
        public async Task DefaultMode_SendsTheFullPolicyAsReportOnly()
        {
            var client = _factory.CreateClient();

            using var response = await client.GetAsync("System/Info/Public", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy-Report-Only"));
            Assert.StartsWith("default-src 'self'; ", policy, StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'self'; ", policy, StringComparison.Ordinal);
            Assert.EndsWith("; report-uri /System/CspReport", policy, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("application/csp-report", "{\"csp-report\":{\"document-uri\":\"http://localhost/web/\",\"violated-directive\":\"img-src\",\"blocked-uri\":\"http://cdn.example/x.jpg\"}}")]
        [InlineData("application/reports+json", "[{\"type\":\"csp-violation\",\"body\":{\"documentURL\":\"http://localhost/web/\",\"effectiveDirective\":\"img-src\",\"blockedURL\":\"http://cdn.example/x.jpg\"}}]")]
        public async Task CspReport_Anonymous_IsAccepted(string contentType, string body)
        {
            var client = _factory.CreateClient();

            using var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            using var response = await client.PostAsync("System/CspReport", content, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        [Fact]
        public async Task CspReport_OtherContentType_IsRefused()
        {
            var client = _factory.CreateClient();

            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("System/CspReport", content, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
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
