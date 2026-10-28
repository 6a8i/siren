using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Jellyfin.Api.Middleware.Tests
{
    public class SecurityHeadersMiddlewareTests
    {
        private const string FullPolicy =
            "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; style-src 'self' 'unsafe-inline'; "
            + "img-src 'self' data: blob: https:; media-src 'self' blob: data:; font-src 'self' data:; "
            + "connect-src 'self' ws: wss: https:; worker-src 'self' blob:; child-src 'self' blob:; frame-src 'self' https:; "
            + "frame-ancestors 'self'; base-uri 'self'; form-action 'self'; report-uri /System/CspReport";

        [Fact]
        public async Task Invoke_Enabled_SetsEveryHeaderAndCallsNext()
        {
            var nextCalled = false;
            var middleware = new SecurityHeadersMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
            var context = new DefaultHttpContext();

            await middleware.Invoke(context, CreateConfiguration(enabled: true, ContentSecurityPolicyMode.Off));

            Assert.True(nextCalled);
            var headers = context.Response.Headers;
            Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
            Assert.Equal("strict-origin-when-cross-origin", headers["Referrer-Policy"].ToString());
            Assert.Equal("SAMEORIGIN", headers.XFrameOptions.ToString());
            Assert.Equal("frame-ancestors 'self'", headers.ContentSecurityPolicy.ToString());
            Assert.Equal("camera=(), microphone=(), geolocation=()", headers["Permissions-Policy"].ToString());
        }

        [Fact]
        public async Task Invoke_Disabled_SetsNothingAndCallsNext()
        {
            var nextCalled = false;
            var middleware = new SecurityHeadersMiddleware(_ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });
            var context = new DefaultHttpContext();

            await middleware.Invoke(context, CreateConfiguration(enabled: false, ContentSecurityPolicyMode.Enforce));

            Assert.True(nextCalled);
            Assert.Empty(context.Response.Headers);
        }

        [Fact]
        public async Task Invoke_Off_SendsOnlyFrameAncestors()
        {
            var context = new DefaultHttpContext();

            await new SecurityHeadersMiddleware(_ => Task.CompletedTask)
                .Invoke(context, CreateConfiguration(enabled: true, ContentSecurityPolicyMode.Off));

            var headers = context.Response.Headers;
            Assert.Equal("frame-ancestors 'self'", headers.ContentSecurityPolicy.ToString());
            Assert.False(headers.ContainsKey("Content-Security-Policy-Report-Only"));
        }

        [Fact]
        public async Task Invoke_ReportOnly_SendsFullPolicyAsReportOnlyAndKeepsFrameAncestors()
        {
            var context = new DefaultHttpContext();

            await new SecurityHeadersMiddleware(_ => Task.CompletedTask)
                .Invoke(context, CreateConfiguration(enabled: true, ContentSecurityPolicyMode.ReportOnly));

            var headers = context.Response.Headers;
            Assert.Equal("frame-ancestors 'self'", headers.ContentSecurityPolicy.ToString());
            Assert.Equal(FullPolicy, headers.ContentSecurityPolicyReportOnly.ToString());
        }

        [Fact]
        public async Task Invoke_Enforce_SendsFullPolicyAsEnforcing()
        {
            var context = new DefaultHttpContext();

            await new SecurityHeadersMiddleware(_ => Task.CompletedTask)
                .Invoke(context, CreateConfiguration(enabled: true, ContentSecurityPolicyMode.Enforce));

            var headers = context.Response.Headers;
            Assert.Equal(FullPolicy, headers.ContentSecurityPolicy.ToString());
            Assert.False(headers.ContainsKey("Content-Security-Policy-Report-Only"));
        }

        [Fact]
        public async Task Invoke_WithBaseUrl_ReportsBelowTheBaseUrl()
        {
            var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);
            var configuration = CreateConfiguration(enabled: true, ContentSecurityPolicyMode.Enforce);

            var context = new DefaultHttpContext();
            context.Request.PathBase = "/jellyfin";
            await middleware.Invoke(context, configuration);

            Assert.EndsWith("; report-uri /jellyfin/System/CspReport", context.Response.Headers.ContentSecurityPolicy.ToString(), System.StringComparison.Ordinal);

            // The same middleware instance answers every request with the same base URL from its cache.
            var second = new DefaultHttpContext();
            second.Request.PathBase = "/jellyfin";
            await middleware.Invoke(second, configuration);

            Assert.Equal(context.Response.Headers.ContentSecurityPolicy.ToString(), second.Response.Headers.ContentSecurityPolicy.ToString());
        }

        [Fact]
        public void BuildPolicy_NoBaseUrl_IsTheDocumentedPolicy()
        {
            Assert.Equal(FullPolicy, SecurityHeadersMiddleware.BuildPolicy(PathString.Empty));
        }

        private static IServerConfigurationManager CreateConfiguration(bool enabled, ContentSecurityPolicyMode mode)
        {
            var configuration = new Mock<IServerConfigurationManager>();
            configuration
                .SetupGet(c => c.Configuration)
                .Returns(new ServerConfiguration { EnableSecurityHeaders = enabled, ContentSecurityPolicyMode = mode });
            return configuration.Object;
        }
    }
}
