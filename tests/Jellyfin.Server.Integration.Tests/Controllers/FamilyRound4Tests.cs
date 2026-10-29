using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.FamilyDtos;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.v3.Priority;

namespace Jellyfin.Server.Integration.Tests.Controllers;

/// <summary>
/// The fourth round of the families feature through the API: the child profile flag, a profile's
/// watch history, the family's devices and signing one out. The tests run in priority order and
/// share the state they build.
/// </summary>
[TestCaseOrderer(typeof(PriorityOrderer))]
public sealed class FamilyRound4Tests : IClassFixture<JellyfinApplicationFactory>
{
    private const string FamilyName = "Pereira";
    private const string FamilyAdminName = "r4Admin";
    private const string FamilyAdminPin = "1111";
    private const string KidName = "r4Kid";
    private const string KidPin = "2222";
    private const string OtherFamilyName = "Costa";
    private const string OtherMemberName = "r4Other";
    private const string OtherMemberPin = "3333";
    private const string LibraryName = "Round 4 movies";
    private const string TabletDeviceId = "r4-tablet";
    private const string TabletAuthHeader = "MediaBrowser Client=\"Jellyfin.Server%20Integration%20Tests\", DeviceId=\"" + TabletDeviceId + "\", Device=\"Tablet\", Version=\"10.8.0\"";

    private static string? _adminToken;
    private static string? _familyAdminToken;
    private static string? _kidToken;
    private static string? _otherMemberToken;
    private static Guid _familyId;
    private static Guid _familyAdminId;
    private static Guid _kidId;
    private static Guid _olderMovieId;
    private static Guid _newerMovieId;

    private readonly JellyfinApplicationFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

    public FamilyRound4Tests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    [Priority(0)]
    public async Task ChildProfile_RoundTrips_AndNeverAdministers()
    {
        var serverAdmin = await CreateServerAdminClient();

        using var created = await serverAdmin.PostAsJsonAsync("Families", new CreateFamilyDto { Name = FamilyName }, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var family = await created.Content.ReadFromJsonAsync<FamilyDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(family);
        _familyId = family.Id;

        var familyAdmin = await CreateMember(serverAdmin, _familyId, new CreateFamilyMemberDto { Name = FamilyAdminName, Password = FamilyAdminPin, IsFamilyAdministrator = true });
        Assert.False(familyAdmin.Policy.IsChildProfile);
        _familyAdminId = familyAdmin.Id;

        // A child profile cannot be created as a family administrator.
        using var childAdmin = await serverAdmin.PostAsJsonAsync(
            $"Families/{_familyId}/Members",
            new CreateFamilyMemberDto { Name = "r4Impossible", IsChildProfile = true, IsFamilyAdministrator = true },
            _jsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, childAdmin.StatusCode);

        var kid = await CreateMember(serverAdmin, _familyId, new CreateFamilyMemberDto { Name = KidName, Password = KidPin, IsChildProfile = true });
        Assert.True(kid.Policy.IsChildProfile);
        Assert.False(kid.Policy.IsFamilyAdministrator);
        _kidId = kid.Id;

        // The flag comes back through the generic user endpoint too, and can be cleared and set again.
        using var asUser = await serverAdmin.GetAsync($"Users/{_kidId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, asUser.StatusCode);
        var userDto = await asUser.Content.ReadFromJsonAsync<UserDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(userDto);
        Assert.True(userDto.Policy.IsChildProfile);

        var familyAdminClient = await CreateFamilyAdminClient();
        var cleared = await UpdateMember(familyAdminClient, _kidId, new UpdateFamilyMemberDto { Name = KidName, IsChildProfile = false, EnableAllFolders = true });
        Assert.False(cleared.Policy.IsChildProfile);
        var setAgain = await UpdateMember(familyAdminClient, _kidId, new UpdateFamilyMemberDto { Name = KidName, IsChildProfile = true, EnableAllFolders = true });
        Assert.True(setAgain.Policy.IsChildProfile);

        // Nor promoted to one.
        using var promoted = await familyAdminClient.PostAsJsonAsync(
            $"Families/{_familyId}/Members/{_kidId}",
            new UpdateFamilyMemberDto { Name = KidName, IsChildProfile = true, IsFamilyAdministrator = true, EnableAllFolders = true },
            _jsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, promoted.StatusCode);
        Assert.True((await GetMember(familyAdminClient, _kidId)).Policy.IsChildProfile);

        // A server administrator is never a child profile.
        var me = await AuthHelper.GetUserDtoAsync(serverAdmin);
        me.Policy.IsChildProfile = true;
        using var adminAsChild = await serverAdmin.PostAsJsonAsync($"Users/{me.Id}/Policy", me.Policy, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, adminAsChild.StatusCode);
        Assert.False((await AuthHelper.GetUserDtoAsync(serverAdmin)).Policy.IsChildProfile);
    }

    [Fact]
    [Priority(1)]
    public async Task History_ListsWhatTheProfilePlayed_NewestFirst_ToTheFamilyOnly()
    {
        var serverAdmin = await CreateServerAdminClient();
        var familyAdmin = await CreateFamilyAdminClient();
        var kid = await CreateKidClient();
        var other = await CreateOtherFamilyMemberClient();

        // A library with two movies the kid played.
        var libraryManager = _factory.Services.GetRequiredService<ILibraryManager>();
        var libraryPath = Path.Combine(_factory.Services.GetRequiredService<IServerApplicationPaths>().DefaultUserViewsPath, LibraryName);
        using var created = await serverAdmin.PostAsJsonAsync(
            $"Library/VirtualFolders?name={Uri.EscapeDataString(LibraryName)}&collectionType=movies&refreshLibrary=false",
            new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
            _jsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        var library = libraryManager.GetUserRootFolder().Children.OfType<CollectionFolder>().Single(f => string.Equals(f.Name, LibraryName, StringComparison.Ordinal));

        _olderMovieId = CreateMovie(libraryManager, library, libraryPath, "Older movie");
        _newerMovieId = CreateMovie(libraryManager, library, libraryPath, "Newer movie");

        var olderDate = new DateTime(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);
        var newerDate = new DateTime(2026, 10, 2, 21, 0, 0, DateTimeKind.Utc);
        await MarkPlayed(kid, _olderMovieId, olderDate);
        await MarkPlayed(kid, _newerMovieId, newerDate);

        var history = await GetHistory(familyAdmin, _kidId);
        Assert.Equal(2, history.Length);
        Assert.Equal(_newerMovieId, history[0].ItemId);
        Assert.Equal("Newer movie", history[0].Name);
        Assert.Equal(BaseItemKind.Movie, history[0].Type);
        Assert.InRange(history[0].Date, newerDate.AddHours(-14), newerDate.AddHours(14));
        Assert.True(history[0].Completed);
        Assert.Null(history[0].PlayedPercentage);
        Assert.Equal(_olderMovieId, history[1].ItemId);
        Assert.InRange(history[1].Date, olderDate.AddHours(-14), olderDate.AddHours(14));
        Assert.True(history[0].Date > history[1].Date);

        // The limit is honoured; the profile sees its own history; the server administrator sees any.
        var limited = await GetHistory(familyAdmin, _kidId, 1);
        Assert.Single(limited);
        Assert.Equal(_newerMovieId, limited[0].ItemId);
        Assert.Equal(2, (await GetHistory(kid, _kidId)).Length);
        Assert.Equal(2, (await GetHistory(serverAdmin, _kidId)).Length);
        Assert.Empty(await GetHistory(familyAdmin, _familyAdminId));

        // Another family, or a plain member looking at another profile, is refused.
        using var forbidden = await other.GetAsync($"Families/{_familyId}/Members/{_kidId}/History", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var notAdmin = await kid.GetAsync($"Families/{_familyId}/Members/{_familyAdminId}/History", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        using var unknown = await familyAdmin.GetAsync($"Families/{_familyId}/Members/{Guid.NewGuid()}/History", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    [Priority(2)]
    public async Task Devices_ListTheFamilysDevices_AndSigningOneOutRevokesItsToken()
    {
        var serverAdmin = await CreateServerAdminClient();
        var familyAdmin = await CreateFamilyAdminClient();
        var other = await CreateOtherFamilyMemberClient();

        // The kid signs in on a second device.
        var tabletToken = await Authenticate(_factory.CreateClient(), KidName, KidPin, TabletAuthHeader);
        var tablet = _factory.CreateClient();
        tablet.DefaultRequestHeaders.Add(AuthHelper.AuthHeaderName, TabletAuthHeader + $", Token={tabletToken}");
        using var tabletWorks = await tablet.GetAsync("Users/Me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, tabletWorks.StatusCode);

        var devices = await GetDevices(familyAdmin);
        var tabletDevice = Assert.Single(devices, device => string.Equals(device.Id, TabletDeviceId, StringComparison.Ordinal));
        Assert.Equal(_kidId, tabletDevice.UserId);
        Assert.Equal(KidName, tabletDevice.UserName);
        Assert.Equal("Tablet", tabletDevice.Name);
        Assert.Equal("Jellyfin.Server Integration Tests", tabletDevice.AppName);
        Assert.Equal("10.8.0", tabletDevice.AppVersion);
        Assert.False(tabletDevice.IsCurrent);

        // Every test client shares one device id, so that one is the caller's own, for both profiles.
        var own = devices.Where(device => device.IsCurrent).ToArray();
        Assert.NotEmpty(own);
        Assert.All(own, device => Assert.Equal("69420", device.Id));
        Assert.Contains(own, device => device.UserId.Equals(_familyAdminId));
        Assert.Contains(own, device => device.UserId.Equals(_kidId));
        Assert.DoesNotContain(devices, device => !device.UserId.Equals(_familyAdminId) && !device.UserId.Equals(_kidId));

        // Members see the list too; another family does not.
        var kid = await CreateKidClient();
        Assert.Contains(await GetDevices(kid), device => string.Equals(device.Id, TabletDeviceId, StringComparison.Ordinal));
        using var forbidden = await other.GetAsync($"Families/{_familyId}/Devices", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // Only an administrator signs a device out, never the one in use, and only a device of the family.
        using var kidRemoves = await kid.DeleteAsync($"Families/{_familyId}/Devices/{TabletDeviceId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, kidRemoves.StatusCode);
        using var ownDevice = await familyAdmin.DeleteAsync($"Families/{_familyId}/Devices/69420", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, ownDevice.StatusCode);
        using var unknown = await familyAdmin.DeleteAsync($"Families/{_familyId}/Devices/no-such-device", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var otherRemoves = await other.DeleteAsync($"Families/{_familyId}/Devices/{TabletDeviceId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, otherRemoves.StatusCode);

        using var removed = await familyAdmin.DeleteAsync($"Families/{_familyId}/Devices/{TabletDeviceId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        using var tabletRevoked = await tablet.GetAsync("Users/Me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, tabletRevoked.StatusCode);
        Assert.DoesNotContain(await GetDevices(familyAdmin), device => string.Equals(device.Id, TabletDeviceId, StringComparison.Ordinal));
        using var gone = await familyAdmin.DeleteAsync($"Families/{_familyId}/Devices/{TabletDeviceId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // The family administrator's own device still works, and the removal is in the activity log.
        using var stillSignedIn = await familyAdmin.GetAsync("Users/Me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
        using var log = await serverAdmin.GetAsync("System/ActivityLog/Entries?limit=50", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, log.StatusCode);
        var entries = await log.Content.ReadFromJsonAsync<QueryResult<ActivityLogEntry>>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(entries);
        var entry = Assert.Single(entries.Items, e => string.Equals(e.Type, "FamilyDeviceRemoved", StringComparison.Ordinal));
        Assert.Equal(_familyAdminId, entry.UserId);
        Assert.Contains("Tablet", entry.Name, StringComparison.Ordinal);
        Assert.Contains(FamilyName, entry.Name, StringComparison.Ordinal);
    }

    private static Guid CreateMovie(ILibraryManager libraryManager, CollectionFolder library, string libraryPath, string name)
    {
        var path = Path.Combine(libraryPath, name + ".mkv");
        var movie = new Movie
        {
            Id = libraryManager.GetNewItemId(path, typeof(Movie)),
            Name = name,
            Path = path,
            ParentId = library.Id,
            RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
            DateCreated = DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };
        libraryManager.CreateItem(movie, library);
        return movie.Id;
    }

    private async Task MarkPlayed(HttpClient client, Guid itemId, DateTime datePlayed)
    {
        using var response = await client.PostAsync(
            $"UserPlayedItems/{itemId}?datePlayed={Uri.EscapeDataString(datePlayed.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture))}",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<FamilyHistoryEntryDto[]> GetHistory(HttpClient client, Guid userId, int? limit = null)
    {
        var url = $"Families/{_familyId}/Members/{userId}/History" + (limit.HasValue ? $"?limit={limit.Value}" : string.Empty);
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var history = await response.Content.ReadFromJsonAsync<FamilyHistoryEntryDto[]>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(history);
        return history;
    }

    private async Task<FamilyDeviceDto[]> GetDevices(HttpClient client)
    {
        using var response = await client.GetAsync($"Families/{_familyId}/Devices", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var devices = await response.Content.ReadFromJsonAsync<FamilyDeviceDto[]>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(devices);
        return devices;
    }

    private async Task<HttpClient> CreateServerAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_adminToken ??= await AuthHelper.CompleteStartupAsync(client));
        return client;
    }

    private async Task<HttpClient> CreateFamilyAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_familyAdminToken ??= await Authenticate(client, FamilyAdminName, FamilyAdminPin, AuthHelper.DummyAuthHeader));
        return client;
    }

    private async Task<HttpClient> CreateKidClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_kidToken ??= await Authenticate(client, KidName, KidPin, AuthHelper.DummyAuthHeader));
        return client;
    }

    private async Task<HttpClient> CreateOtherFamilyMemberClient()
    {
        var client = _factory.CreateClient();
        if (_otherMemberToken is null)
        {
            var serverAdmin = await CreateServerAdminClient();
            using var response = await serverAdmin.PostAsJsonAsync("Families", new CreateFamilyDto { Name = OtherFamilyName }, _jsonOptions, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var family = await response.Content.ReadFromJsonAsync<FamilyDto>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(family);
            await CreateMember(serverAdmin, family.Id, new CreateFamilyMemberDto { Name = OtherMemberName, Password = OtherMemberPin, IsFamilyAdministrator = true });
            _otherMemberToken = await Authenticate(client, OtherMemberName, OtherMemberPin, AuthHelper.DummyAuthHeader);
        }

        client.DefaultRequestHeaders.AddAuthHeader(_otherMemberToken);
        return client;
    }

    private async Task<string> Authenticate(HttpClient client, string username, string pw, string authHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, authHeader);
        request.Content = JsonContent.Create(new AuthenticateUserByName { Username = username, Pw = pw }, options: _jsonOptions);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.NotNull(result.AccessToken);
        return result.AccessToken;
    }

    private async Task<FamilyMemberDto> CreateMember(HttpClient client, Guid familyId, CreateFamilyMemberDto request)
    {
        using var response = await client.PostAsJsonAsync($"Families/{familyId}/Members", request, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<FamilyMemberDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(member);
        return member;
    }

    private async Task<FamilyMemberDto> UpdateMember(HttpClient client, Guid userId, UpdateFamilyMemberDto request)
    {
        using var response = await client.PostAsJsonAsync($"Families/{_familyId}/Members/{userId}", request, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<FamilyMemberDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(member);
        return member;
    }

    private async Task<FamilyMemberDto> GetMember(HttpClient client, Guid userId)
    {
        using var response = await client.GetAsync($"Families/{_familyId}/Members/{userId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<FamilyMemberDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(member);
        return member;
    }
}
