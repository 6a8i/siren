using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Api.Middleware.Tests
{
    public class ReadOnlyApiKeyMiddlewareTests
    {
        [Theory]
        [InlineData("GET", true)]
        [InlineData("HEAD", true)]
        [InlineData("OPTIONS", true)]
        [InlineData("POST", false)]
        [InlineData("PUT", false)]
        [InlineData("PATCH", false)]
        [InlineData("DELETE", false)]
        public void IsReadMethod_ClassifiesMethods(string method, bool expected)
        {
            Assert.Equal(expected, ReadOnlyApiKeyMiddleware.IsReadMethod(method));
        }

        [Theory]
        [InlineData("GET")]
        [InlineData("HEAD")]
        [InlineData("OPTIONS")]
        public async Task Invoke_ReadOnlyKeyWithReadMethod_CallsNext(string method)
        {
            var nextCalled = false;
            var context = CreateContext(method, isApiKey: true, isReadOnly: true);

            await CreateMiddleware(() => nextCalled = true).Invoke(context);

            Assert.True(nextCalled);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        public async Task Invoke_ReadOnlyKeyWithWriteMethod_Returns403(string method)
        {
            var nextCalled = false;
            var context = CreateContext(method, isApiKey: true, isReadOnly: true);

            await CreateMiddleware(() => nextCalled = true).Invoke(context);

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        public async Task Invoke_KeyThatIsNotReadOnly_CallsNext(bool isApiKey, bool isReadOnly)
        {
            var nextCalled = false;
            var context = CreateContext("POST", isApiKey, isReadOnly);

            await CreateMiddleware(() => nextCalled = true).Invoke(context);

            Assert.True(nextCalled);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        }

        [Fact]
        public async Task Invoke_Anonymous_CallsNext()
        {
            var nextCalled = false;
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";

            await CreateMiddleware(() => nextCalled = true).Invoke(context);

            Assert.True(nextCalled);
        }

        private static ReadOnlyApiKeyMiddleware CreateMiddleware(System.Action onNext)
            => new(
                _ =>
                {
                    onNext();
                    return Task.CompletedTask;
                },
                NullLogger<ReadOnlyApiKeyMiddleware>.Instance);

        private static DefaultHttpContext CreateContext(string method, bool isApiKey, bool isReadOnly)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(InternalClaimTypes.Client, "tests"),
                    new Claim(InternalClaimTypes.IsApiKey, isApiKey.ToString()),
                    new Claim(InternalClaimTypes.IsReadOnlyApiKey, isReadOnly.ToString())
                },
                "Test"));
            return context;
        }
    }
}
