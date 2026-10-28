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

            await middleware.Invoke(context, CreateConfiguration(enabled: true));

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

            await middleware.Invoke(context, CreateConfiguration(enabled: false));

            Assert.True(nextCalled);
            Assert.Empty(context.Response.Headers);
        }

        private static IServerConfigurationManager CreateConfiguration(bool enabled)
        {
            var configuration = new Mock<IServerConfigurationManager>();
            configuration.SetupGet(c => c.Configuration).Returns(new ServerConfiguration { EnableSecurityHeaders = enabled });
            return configuration.Object;
        }
    }
}
