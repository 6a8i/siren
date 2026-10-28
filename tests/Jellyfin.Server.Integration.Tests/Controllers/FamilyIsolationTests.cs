using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Models.FamilyDtos;
using Jellyfin.Api.Models.PlaylistDtos;
using Jellyfin.Api.Models.SyncPlayDtos;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

/// <summary>
/// Two families on one server: a member of one must not see or reach the other through sessions,
/// users, SyncPlay groups, playlist sharing or remote control.
/// </summary>
public sealed class FamilyIsolationTests : IClassFixture<JellyfinApplicationFactory>
{
    private const string AliceDevice = "family-a-tv";
    private const string AmyDevice = "family-a-phone";
    private const string BobDevice = "family-b-tv";

    private static Families? _families;

    private readonly JellyfinApplicationFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

    public FamilyIsolationTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetSessions_FamilyMember_NeverSeesTheOtherFamily()
    {
        var f = await GetFamiliesAsync();

        // A family administrator sees the sessions of their family ...
        var seenByAlice = (await GetSessionsAsync(ClientFor(f.AliceToken, AliceDevice))).Select(s => s.UserId).ToList();
        Assert.Contains(f.Alice.Id, seenByAlice);
        Assert.Contains(f.Amy.Id, seenByAlice);
        Assert.DoesNotContain(f.Bob.Id, seenByAlice);

        // ... a plain member only their own ...
        var seenByAmy = (await GetSessionsAsync(ClientFor(f.AmyToken, AmyDevice))).Select(s => s.UserId).ToList();
        Assert.Contains(f.Amy.Id, seenByAmy);
        Assert.DoesNotContain(f.Alice.Id, seenByAmy);
        Assert.DoesNotContain(f.Bob.Id, seenByAmy);

        // ... and the server administrator everything.
        var seenByAdmin = (await GetSessionsAsync(AdminClient(f))).Select(s => s.UserId).ToList();
        Assert.Contains(f.Alice.Id, seenByAdmin);
        Assert.Contains(f.Amy.Id, seenByAdmin);
        Assert.Contains(f.Bob.Id, seenByAdmin);
    }

    [Fact]
    public async Task GetUsers_FamilyMember_SeesOnlyTheirFamily()
    {
        var f = await GetFamiliesAsync();
        var alice = ClientFor(f.AliceToken, AliceDevice);

        using var response = await alice.GetAsync("Users", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var users = await response.Content.ReadFromJsonAsync<UserDto[]>(_jsonOptions, Ct);
        Assert.NotNull(users);

        Assert.All(users, user => Assert.Equal(f.FamilyAId, user.FamilyId));
        Assert.Contains(users, user => user.Id.Equals(f.Alice.Id));
        Assert.Contains(users, user => user.Id.Equals(f.Amy.Id));
        Assert.DoesNotContain(users, user => user.Id.Equals(f.Bob.Id));
    }

    [Fact]
    public async Task GetUserById_OtherFamily_IsForbidden()
    {
        var f = await GetFamiliesAsync();
        var alice = ClientFor(f.AliceToken, AliceDevice);

        using var sibling = await alice.GetAsync("Users/" + N(f.Amy.Id), Ct);
        Assert.Equal(HttpStatusCode.OK, sibling.StatusCode);

        using var stranger = await alice.GetAsync("Users/" + N(f.Bob.Id), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
    }

    [Fact]
    public async Task SyncPlayList_FamilyMember_DoesNotSeeTheOtherFamilysGroup()
    {
        var f = await GetFamiliesAsync();
        var bob = ClientFor(f.BobToken, BobDevice);
        var alice = ClientFor(f.AliceToken, AliceDevice);

        var group = await PostAsync<GroupSummary>(bob, "SyncPlay/New", new NewGroupRequestDto { GroupName = "Family B movie night" });
        try
        {
            Assert.Contains(await GetGroupIdsAsync(bob), id => id.Equals(group.GroupId));
            Assert.DoesNotContain(await GetGroupIdsAsync(alice), id => id.Equals(group.GroupId));

            using var byId = await alice.GetAsync("SyncPlay/" + N(group.GroupId), Ct);
            Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);
        }
        finally
        {
            using var leave = await bob.PostAsync("SyncPlay/Leave", null, Ct);
        }
    }

    [Fact]
    public async Task Playlists_SharingWithTheOtherFamily_IsRefused()
    {
        var f = await GetFamiliesAsync();
        var alice = ClientFor(f.AliceToken, AliceDevice);

        using var shared = await alice.PostAsJsonAsync(
            "Playlists",
            new CreatePlaylistDto { Name = "Shared with B", Users = [new PlaylistUserPermissions(f.Bob.Id)] },
            _jsonOptions,
            Ct);
        Assert.Equal(HttpStatusCode.Forbidden, shared.StatusCode);

        var playlist = await PostAsync<PlaylistSummary>(alice, "Playlists", new CreatePlaylistDto { Name = "Family A" });
        var share = new UpdatePlaylistUserDto { CanEdit = false };

        using var withBob = await alice.PostAsJsonAsync($"Playlists/{playlist.Id}/Users/{N(f.Bob.Id)}", share, _jsonOptions, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, withBob.StatusCode);

        using var withAmy = await alice.PostAsJsonAsync($"Playlists/{playlist.Id}/Users/{N(f.Amy.Id)}", share, _jsonOptions, Ct);
        Assert.Equal(HttpStatusCode.NoContent, withAmy.StatusCode);

        using var users = await alice.GetAsync($"Playlists/{playlist.Id}/Users", Ct);
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        var shares = await users.Content.ReadFromJsonAsync<PlaylistUserPermissions[]>(_jsonOptions, Ct);
        Assert.NotNull(shares);
        Assert.Equal(new[] { f.Amy.Id }, shares.Select(s => s.UserId));
    }

    [Fact]
    public async Task SessionCommands_SessionOfTheOtherFamily_AreForbidden()
    {
        var f = await GetFamiliesAsync();
        var bob = ClientFor(f.BobToken, BobDevice);
        var alice = ClientFor(f.AliceToken, AliceDevice);

        var bobSession = (await GetSessionsAsync(bob)).First(s => s.UserId.Equals(f.Bob.Id)).Id;
        var itemId = N(Guid.NewGuid());

        var requests = new (string Method, string Url, object? Body)[]
        {
            ("POST", $"Sessions/{bobSession}/Viewing?itemType=Movie&itemId={itemId}&itemName=Movie", null),
            ("POST", $"Sessions/{bobSession}/Playing?playCommand=PlayNow&itemIds={itemId}", null),
            ("POST", $"Sessions/{bobSession}/Playing/Pause", null),
            ("POST", $"Sessions/{bobSession}/System/GoHome", null),
            ("POST", $"Sessions/{bobSession}/Command/Back", null),
            ("POST", $"Sessions/{bobSession}/Command", new GeneralCommand { Name = GeneralCommandType.Back }),
            ("POST", $"Sessions/{bobSession}/Message", new MessageCommand { Header = "Hi", Text = "Hello from family A" }),
            ("POST", $"Sessions/{bobSession}/User/{N(f.Alice.Id)}", null),
            ("DELETE", $"Sessions/{bobSession}/User/{N(f.Alice.Id)}", null),
            ("POST", $"Sessions/Capabilities?id={bobSession}", null),
            ("POST", $"Sessions/Viewing?sessionId={bobSession}&itemId={itemId}", null)
        };

        foreach (var (method, url, body) in requests)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: _jsonOptions);
            }

            using var response = await alice.SendAsync(request, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} returned {response.StatusCode}");
        }

        // Bob keeps control of his own session.
        using var own = await bob.PostAsync($"Sessions/{bobSession}/Command/Back", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
    }

    private static string N(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static string AuthHeader(string deviceId)
        => $"MediaBrowser Client=\"Jellyfin.Server%20Integration%20Tests\", DeviceId=\"{deviceId}\", Device=\"Family%20TV\", Version=\"10.8.0\"";

    private async Task<Families> GetFamiliesAsync()
    {
        if (_families is not null)
        {
            return _families;
        }

        var admin = _factory.CreateClient();
        var adminToken = await AuthHelper.CompleteStartupAsync(admin);
        admin.DefaultRequestHeaders.AddAuthHeader(adminToken);

        var familyA = await PostAsync<FamilyDto>(admin, "Families", new CreateFamilyDto { Name = "Family A" });
        var familyB = await PostAsync<FamilyDto>(admin, "Families", new CreateFamilyDto { Name = "Family B" });

        var alice = await PostAsync<UserDto>(admin, $"Families/{N(familyA.Id)}/Members", new CreateFamilyMemberDto { Name = "alice", IsFamilyAdministrator = true });
        var amy = await PostAsync<UserDto>(admin, $"Families/{N(familyA.Id)}/Members", new CreateFamilyMemberDto { Name = "amy" });
        var bob = await PostAsync<UserDto>(admin, $"Families/{N(familyB.Id)}/Members", new CreateFamilyMemberDto { Name = "bob", IsFamilyAdministrator = true });

        _families = new Families(
            adminToken,
            familyA.Id,
            familyB.Id,
            alice,
            amy,
            bob,
            await AuthenticateAsync("alice", AliceDevice),
            await AuthenticateAsync("amy", AmyDevice),
            await AuthenticateAsync("bob", BobDevice));

        return _families;
    }

    private async Task<string> AuthenticateAsync(string username, string deviceId)
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHeader(deviceId));
        request.Content = JsonContent.Create(new AuthenticateUserByName { Username = username, Pw = string.Empty }, options: _jsonOptions);

        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<TokenResult>(_jsonOptions, Ct);
        Assert.NotNull(result);
        return result.AccessToken;
    }

    private HttpClient ClientFor(string token, string deviceId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuthHelper.AuthHeaderName, AuthHeader(deviceId) + ", Token=" + token);
        return client;
    }

    private HttpClient AdminClient(Families families)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(families.AdminToken);
        return client;
    }

    private async Task<T> PostAsync<T>(HttpClient client, string url, object body)
    {
        using var content = JsonContent.Create(body, body.GetType(), options: _jsonOptions);
        using var response = await client.PostAsync(url, content, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST {url} returned {response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        var result = await response.Content.ReadFromJsonAsync<T>(_jsonOptions, Ct);
        Assert.NotNull(result);
        return result;
    }

    private async Task<SessionSummary[]> GetSessionsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("Sessions", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = await response.Content.ReadFromJsonAsync<SessionSummary[]>(_jsonOptions, Ct);
        Assert.NotNull(sessions);
        return sessions;
    }

    private async Task<List<Guid>> GetGroupIdsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("SyncPlay/List", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var groups = await response.Content.ReadFromJsonAsync<GroupSummary[]>(_jsonOptions, Ct);
        Assert.NotNull(groups);
        return groups.Select(g => g.GroupId).ToList();
    }

    private sealed record Families(
        string AdminToken,
        Guid FamilyAId,
        Guid FamilyBId,
        UserDto Alice,
        UserDto Amy,
        UserDto Bob,
        string AliceToken,
        string AmyToken,
        string BobToken);

    private sealed class TokenResult
    {
        public string AccessToken { get; set; } = string.Empty;
    }

    private sealed class SessionSummary
    {
        public string Id { get; set; } = string.Empty;

        public Guid UserId { get; set; }
    }

    private sealed class GroupSummary
    {
        public Guid GroupId { get; set; }
    }

    private sealed class PlaylistSummary
    {
        public string Id { get; set; } = string.Empty;
    }
}
