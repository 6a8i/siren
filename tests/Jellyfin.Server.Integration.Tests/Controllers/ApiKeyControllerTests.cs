using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.ApiKeyDtos;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Querying;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers
{
    public sealed class ApiKeyControllerTests : IClassFixture<JellyfinApplicationFactory>
    {
        private readonly JellyfinApplicationFactory _factory;
        private static string? _accessToken;

        public ApiKeyControllerTests(JellyfinApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task ReadWriteKey_WorksForReadsAndWrites()
        {
            var admin = await CreateAdminClientAsync();
            var key = await CreateKeyAsync(admin, "rw-key", isReadOnly: false);
            Assert.False(key.IsReadOnly);

            using var keyClient = CreateKeyClient(key.AccessToken!);

            using var get = await keyClient.GetAsync("System/Info", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);

            using var post = await keyClient.PostAsync("Library/Refresh", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);
        }

        [Fact]
        public async Task ReadOnlyKey_RefusesWritesUntilToggled()
        {
            var admin = await CreateAdminClientAsync();
            var key = await CreateKeyAsync(admin, "ro-key", isReadOnly: true);
            Assert.True(key.IsReadOnly);

            var listed = await ListKeysAsync(admin);
            Assert.True(listed.Single(k => k.AccessToken == key.AccessToken).IsReadOnly);

            using var keyClient = CreateKeyClient(key.AccessToken!);

            using var get = await keyClient.GetAsync("System/Info", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);

            using var refused = await keyClient.PostAsync("Library/Refresh", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("read-only", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

            // The key cannot lift its own restriction either.
            using var selfToggle = await keyClient.PostAsync($"Auth/Keys/{key.AccessToken}/ReadOnly?value=false", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, selfToggle.StatusCode);

            using var toggle = await admin.PostAsync($"Auth/Keys/{key.AccessToken}/ReadOnly?value=false", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, toggle.StatusCode);

            listed = await ListKeysAsync(admin);
            Assert.False(listed.Single(k => k.AccessToken == key.AccessToken).IsReadOnly);

            using var allowed = await keyClient.PostAsync("Library/Refresh", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);

            using var toggleBack = await admin.PostAsync($"Auth/Keys/{key.AccessToken}/ReadOnly?value=true", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, toggleBack.StatusCode);

            using var refusedAgain = await keyClient.PostAsync("Library/Refresh", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, refusedAgain.StatusCode);
        }

        [Fact]
        public async Task SetReadOnly_UnknownKey_Returns404()
        {
            var admin = await CreateAdminClientAsync();

            using var response = await admin.PostAsync("Auth/Keys/does-not-exist/ReadOnly?value=true", null, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        private async Task<HttpClient> CreateAdminClientAsync()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
            return client;
        }

        private HttpClient CreateKeyClient(string token)
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(token);
            return client;
        }

        private static async Task<AuthenticationInfoDto> CreateKeyAsync(HttpClient admin, string app, bool isReadOnly)
        {
            using var response = await admin.PostAsync($"Auth/Keys?app={app}&isReadOnly={isReadOnly}", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var key = await response.Content.ReadFromJsonAsync<AuthenticationInfoDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(key);
            Assert.False(string.IsNullOrEmpty(key.AccessToken));
            return key;
        }

        private static async Task<AuthenticationInfoDto[]> ListKeysAsync(HttpClient admin)
        {
            using var response = await admin.GetAsync("Auth/Keys", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<QueryResult<AuthenticationInfoDto>>(JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            return result.Items.ToArray();
        }
    }
}
