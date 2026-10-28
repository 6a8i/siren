using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.FamilyDtos;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.v3.Priority;

namespace Jellyfin.Server.Integration.Tests.Controllers;

/// <summary>
/// Drives a family through its life: created by the server administrator, managed by its family
/// administrator, with library access that never exceeds the family's, a PIN lockout that ends on
/// its own or by the family administrator's hand, the family's libraries listed by name and profile
/// images set by the family administrator. The tests run in priority order and share the state
/// they build.
/// </summary>
[TestCaseOrderer(typeof(PriorityOrderer))]
public sealed class FamilyControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private const string FamilyName = "Silva";
    private const string FamilyAdminName = "familyAdmin";
    private const string FamilyAdminPin = "1234";
    private const string MemberName = "familyKid";
    private const string MemberPin = "0000";
    private const string WrongPin = "9999";
    private const string OtherFamilyName = "Souza";
    private const string OtherMemberName = "otherKid";
    private const string OtherMemberPin = "4321";
    private const string LibraryName = "Family movies";

    /// <summary>
    /// A 1x1 transparent PNG, already base64-encoded the way the image endpoints expect the body.
    /// </summary>
    private const string TinyPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly Guid _libraryA = Guid.NewGuid();
    private static readonly Guid _libraryB = Guid.NewGuid();

    private static string? _adminToken;
    private static string? _familyAdminToken;
    private static string? _memberToken;
    private static string? _otherMemberToken;
    private static Guid _familyId;
    private static Guid _familyAdminId;
    private static Guid _memberId;
    private static Guid _libraryId;

    private readonly JellyfinApplicationFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

    public FamilyControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    [Priority(0)]
    public async Task CreateFamily_AsServerAdministrator_ReachesEveryLibrary()
    {
        var client = await CreateServerAdminClient();

        using var response = await client.PostAsJsonAsync("Families", new CreateFamilyDto { Name = FamilyName }, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var family = await response.Content.ReadFromJsonAsync<FamilyDto>(_jsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(family);
        Assert.Equal(FamilyName, family.Name);
        Assert.Equal(0, family.MemberCount);
        Assert.Equal(Family.MaxMembers, family.MaxMembers);
        Assert.True(family.EnableAllFolders);
        Assert.Empty(family.EnabledFolders);

        _familyId = family.Id;
    }

    [Fact]
    [Priority(1)]
    public async Task CreateMembers_FirstAdministratorAndProfileWithPin_LockOutAfterFiveAttempts()
    {
        var client = await CreateServerAdminClient();

        var familyAdmin = await CreateMember(client, _familyId, new CreateFamilyMemberDto
        {
            Name = FamilyAdminName,
            Password = FamilyAdminPin,
            IsFamilyAdministrator = true
        });
        Assert.True(familyAdmin.Policy.IsFamilyAdministrator);
        Assert.False(familyAdmin.Policy.IsAdministrator);
        Assert.False(familyAdmin.Policy.IsHidden);
        _familyAdminId = familyAdmin.Id;

        var member = await CreateMember(client, _familyId, new CreateFamilyMemberDto
        {
            Name = MemberName,
            Password = MemberPin
        });
        Assert.False(member.Policy.IsFamilyAdministrator);
        _memberId = member.Id;

        foreach (var profile in new[] { familyAdmin, member })
        {
            Assert.Equal(_familyId, profile.FamilyId);
            Assert.True(profile.HasPin);
            Assert.False(profile.IsLockedOut);
            Assert.Null(profile.LockedOutUntil);
            Assert.True(profile.HasAllFamilyFolders);
            Assert.True(profile.Policy.EnableAllFolders);
            Assert.Equal(Family.LoginAttemptsBeforeLockout, profile.Policy.LoginAttemptsBeforeLockout);
        }

        var listed = await GetMembers(client);
        Assert.Equal(2, listed.Length);
        Assert.All(listed, profile => Assert.True(profile.HasPin));
    }

    [Fact]
    [Priority(2)]
    public async Task FamilyAdministrator_SeesOnlyTheFamily()
    {
        var client = await CreateFamilyAdminClient();

        using var mine = await client.GetAsync("Families/Mine", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        var family = await mine.Content.ReadFromJsonAsync<FamilyDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(family);
        Assert.Equal(_familyId, family.Id);
        Assert.Equal(2, family.MemberCount);

        using var users = await client.GetAsync("Users", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        var visible = await users.Content.ReadFromJsonAsync<UserDto[]>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(visible);
        Assert.Equal(2, visible.Length);
        Assert.All(visible, user => Assert.Equal(_familyId, user.FamilyId));
        Assert.Contains(visible, user => user.Id.Equals(_familyAdminId));
        Assert.Contains(visible, user => user.Id.Equals(_memberId));

        using var all = await client.GetAsync("Families", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, all.StatusCode);
    }

    [Fact]
    [Priority(3)]
    public async Task Switch_WrongPinIsUnauthorized_RightPinOpensTheProfile()
    {
        var client = await CreateFamilyAdminClient();

        using var wrong = await Switch(client, _memberId, WrongPin);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var right = await Switch(client, _memberId, MemberPin);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        var session = await right.Content.ReadFromJsonAsync<SwitchResult>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.False(string.IsNullOrEmpty(session.AccessToken));
        Assert.Equal(_memberId, session.User.Id);
    }

    [Fact]
    [Priority(4)]
    public async Task LibraryAccess_ProfilesNeverExceedTheFamily()
    {
        var familyAdmin = await CreateFamilyAdminClient();
        var serverAdmin = await CreateServerAdminClient();

        // Only the server administrator decides what the family reaches; a family administrator may still rename.
        using var forbidden = await familyAdmin.PostAsJsonAsync($"Families/{_familyId}", new UpdateFamilyDto { EnabledFolders = [_libraryA] }, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var renamed = await familyAdmin.PostAsJsonAsync($"Families/{_familyId}", new UpdateFamilyDto { Name = FamilyName + " renamed" }, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);

        using var narrowed = await serverAdmin.PostAsJsonAsync($"Families/{_familyId}", new UpdateFamilyDto { EnableAllFolders = false, EnabledFolders = [_libraryA] }, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, narrowed.StatusCode);

        using var get = await serverAdmin.GetAsync($"Families/{_familyId}", TestContext.Current.CancellationToken);
        var family = await get.Content.ReadFromJsonAsync<FamilyDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(family);
        Assert.Equal(FamilyName + " renamed", family.Name);
        Assert.False(family.EnableAllFolders);
        Assert.Equal(new[] { _libraryA }, family.EnabledFolders);

        // Every existing profile was capped, the family administrator included.
        foreach (var userId in new[] { _familyAdminId, _memberId })
        {
            var profile = await GetMember(serverAdmin, userId);
            Assert.False(profile.Policy.EnableAllFolders);
            Assert.Equal(new[] { _libraryA }, profile.Policy.EnabledFolders);
            Assert.True(profile.HasAllFamilyFolders);
        }

        // "All folders" from a family administrator means every library of the family.
        var updated = await UpdateMember(familyAdmin, _memberId, new UpdateFamilyMemberDto { Name = MemberName, EnableAllFolders = true });
        Assert.False(updated.Policy.EnableAllFolders);
        Assert.Equal(new[] { _libraryA }, updated.Policy.EnabledFolders);
        Assert.True(updated.HasAllFamilyFolders);

        // A library outside the family is dropped.
        updated = await UpdateMember(familyAdmin, _memberId, new UpdateFamilyMemberDto { Name = MemberName, EnabledFolders = [_libraryA, _libraryB] });
        Assert.Equal(new[] { _libraryA }, updated.Policy.EnabledFolders);

        updated = await UpdateMember(familyAdmin, _memberId, new UpdateFamilyMemberDto { Name = MemberName, EnabledFolders = [_libraryB] });
        Assert.Empty(updated.Policy.EnabledFolders);
        Assert.False(updated.HasAllFamilyFolders);

        // The dashboard's generic user page cannot exceed the family either.
        using var userResponse = await serverAdmin.GetAsync($"Users/{_memberId}", TestContext.Current.CancellationToken);
        var user = await userResponse.Content.ReadFromJsonAsync<UserDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        user.Policy.EnableAllFolders = true;
        user.Policy.EnabledFolders = [_libraryA, _libraryB];
        using var policyResponse = await serverAdmin.PostAsJsonAsync($"Users/{_memberId}/Policy", user.Policy, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, policyResponse.StatusCode);

        var capped = await GetMember(familyAdmin, _memberId);
        Assert.False(capped.Policy.EnableAllFolders);
        Assert.Equal(new[] { _libraryA }, capped.Policy.EnabledFolders);
        Assert.True(capped.HasAllFamilyFolders);
    }

    [Fact]
    [Priority(5)]
    public async Task Lockout_AfterFiveWrongPins_LastsAWhile_AndEndsEarlyWhenUnlocked()
    {
        var client = await CreateFamilyAdminClient();

        var before = DateTime.UtcNow;
        await LockOut(client, _memberId);

        // The profile is locked out, not disabled: the switch is refused before any PIN is checked.
        using var lockedOut = await Switch(client, _memberId, MemberPin);
        Assert.Equal(HttpStatusCode.Forbidden, lockedOut.StatusCode);

        var locked = await GetMember(client, _memberId);
        Assert.True(locked.IsLockedOut);
        Assert.False(locked.Policy.IsDisabled);
        Assert.NotNull(locked.LockedOutUntil);
        Assert.InRange(locked.LockedOutUntil.Value, before + Family.LockoutDuration, DateTime.UtcNow + Family.LockoutDuration);
        Assert.Equal(0, locked.Policy.InvalidLoginAttemptCount);

        // The unlock endpoint ends it early without touching the rest of the policy.
        using var unlock = await client.PostAsync($"Families/{_familyId}/Members/{_memberId}/Unlock", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);

        var unlocked = await GetMember(client, _memberId);
        Assert.False(unlocked.IsLockedOut);
        Assert.Null(unlocked.LockedOutUntil);
        Assert.False(unlocked.Policy.IsDisabled);
        Assert.Equal(0, unlocked.Policy.InvalidLoginAttemptCount);

        using var right = await Switch(client, _memberId, MemberPin);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);

        // Enabling the profile through the member update is still an unlock.
        await LockOut(client, _memberId);
        Assert.True((await GetMember(client, _memberId)).IsLockedOut);

        var enabled = await UpdateMember(client, _memberId, new UpdateFamilyMemberDto { Name = MemberName, IsDisabled = false, EnableAllFolders = true });
        Assert.False(enabled.IsLockedOut);
        Assert.Null(enabled.LockedOutUntil);
        Assert.Equal(0, enabled.Policy.InvalidLoginAttemptCount);

        using var rightAgain = await Switch(client, _memberId, MemberPin);
        Assert.Equal(HttpStatusCode.OK, rightAgain.StatusCode);

        // A member of another family cannot unlock.
        var other = await CreateOtherFamilyMemberClient();
        using var forbidden = await other.PostAsync($"Families/{_familyId}/Members/{_memberId}/Unlock", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    [Priority(6)]
    public async Task Libraries_ListWhatTheFamilyReachesByName_ToItsMembersOnly()
    {
        var serverAdmin = await CreateServerAdminClient();
        var familyAdmin = await CreateFamilyAdminClient();
        var member = await CreateMemberClient();
        var other = await CreateOtherFamilyMemberClient();

        // A real library, so that there is something to reach.
        using var created = await serverAdmin.PostAsJsonAsync(
            $"Library/VirtualFolders?name={Uri.EscapeDataString(LibraryName)}&collectionType=movies&refreshLibrary=false",
            new AddVirtualFolderDto { LibraryOptions = new LibraryOptions { Enabled = false } },
            _jsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);

        using var folders = await serverAdmin.GetAsync("Library/VirtualFolders", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, folders.StatusCode);
        var virtualFolders = await folders.Content.ReadFromJsonAsync<VirtualFolderInfo[]>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(virtualFolders);
        var library = Assert.Single(virtualFolders, folder => string.Equals(folder.Name, LibraryName, StringComparison.Ordinal));
        _libraryId = Guid.Parse(library.ItemId);

        // The family still reaches only a library that does not exist: nothing is listed.
        Assert.Empty(await GetLibraries(familyAdmin, $"Families/{_familyId}/Libraries"));

        // A family that reaches everything lists every library of the server.
        using var opened = await serverAdmin.PostAsJsonAsync($"Families/{_familyId}", new UpdateFamilyDto { EnableAllFolders = true }, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, opened.StatusCode);

        var all = await GetLibraries(familyAdmin, $"Families/{_familyId}/Libraries");
        Assert.Equal(virtualFolders.Count(folder => !string.IsNullOrEmpty(folder.ItemId)), all.Length);
        var listed = Assert.Single(all, l => l.Id.Equals(_libraryId));
        Assert.Equal(LibraryName, listed.Name);
        Assert.Equal(CollectionTypeOptions.movies, listed.CollectionType);

        // Narrowed to the library and a stray id, only the library is listed, by name, even to a
        // profile that does not reach it itself.
        using var narrowed = await serverAdmin.PostAsJsonAsync($"Families/{_familyId}", new UpdateFamilyDto { EnableAllFolders = false, EnabledFolders = [_libraryId, _libraryB] }, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, narrowed.StatusCode);
        var withoutLibraries = await UpdateMember(familyAdmin, _memberId, new UpdateFamilyMemberDto { Name = MemberName, EnabledFolders = [] });
        Assert.Empty(withoutLibraries.Policy.EnabledFolders);

        var mine = await GetLibraries(member, "Families/Mine/Libraries");
        var onlyOne = Assert.Single(mine);
        Assert.Equal(_libraryId, onlyOne.Id);
        Assert.Equal(LibraryName, onlyOne.Name);
        Assert.Equal(mine.Select(l => l.Id), (await GetLibraries(member, $"Families/{_familyId}/Libraries")).Select(l => l.Id));
        Assert.Single(await GetLibraries(serverAdmin, $"Families/{_familyId}/Libraries"));

        // A member of another family is refused, and a user outside any family has no "mine".
        using var forbidden = await other.GetAsync($"Families/{_familyId}/Libraries", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var notFound = await serverAdmin.GetAsync("Families/Mine/Libraries", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    [Priority(7)]
    public async Task MemberImage_SetAndDeletedByTheFamilyAdministratorOrTheProfile_NotByAnotherFamily()
    {
        var familyAdmin = await CreateFamilyAdminClient();
        var member = await CreateMemberClient();
        var other = await CreateOtherFamilyMemberClient();
        var imagePath = Path.Combine(
            _factory.Services.GetRequiredService<IServerApplicationPaths>().UserConfigurationDirectoryPath,
            MemberName,
            "profile.png");

        using var forbiddenPost = await PostImage(other, _memberId);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenPost.StatusCode);
        using var forbiddenDelete = await other.DeleteAsync($"Families/{_familyId}/Members/{_memberId}/Image", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenDelete.StatusCode);

        // A body that is not an image is refused before anything is written.
        using var notAnImage = new StringContent("not an image", Encoding.UTF8, "text/plain");
        using var badRequest = await familyAdmin.PostAsync($"Families/{_familyId}/Members/{_memberId}/Image", notAnImage, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        Assert.False(File.Exists(imagePath));

        using var posted = await PostImage(familyAdmin, _memberId);
        Assert.Equal(HttpStatusCode.NoContent, posted.StatusCode);
        Assert.True(File.Exists(imagePath));
        var withImage = await GetMember(familyAdmin, _memberId);
        Assert.False(string.IsNullOrEmpty(withImage.PrimaryImageTag));

        // The profile may replace its own image.
        using var replaced = await PostImage(member, _memberId);
        Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);
        Assert.True(File.Exists(imagePath));

        using var deleted = await familyAdmin.DeleteAsync($"Families/{_familyId}/Members/{_memberId}/Image", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(File.Exists(imagePath));
        var without = await GetMember(familyAdmin, _memberId);
        Assert.Null(without.PrimaryImageTag);

        // Deleting an image that is not there is not an error.
        using var deletedAgain = await member.DeleteAsync($"Families/{_familyId}/Members/{_memberId}/Image", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deletedAgain.StatusCode);
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
        client.DefaultRequestHeaders.AddAuthHeader(_familyAdminToken ??= await Authenticate(client, FamilyAdminName, FamilyAdminPin));
        return client;
    }

    private async Task<HttpClient> CreateMemberClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_memberToken ??= await Authenticate(client, MemberName, MemberPin));
        return client;
    }

    /// <summary>
    /// A client signed in as a member of a second family, created on first use.
    /// </summary>
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
            _otherMemberToken = await Authenticate(client, OtherMemberName, OtherMemberPin);
        }

        client.DefaultRequestHeaders.AddAuthHeader(_otherMemberToken);
        return client;
    }

    private async Task<string> Authenticate(HttpClient client, string username, string pw)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
        request.Content = JsonContent.Create(new AuthenticateUserByName { Username = username, Pw = pw }, options: _jsonOptions);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<SwitchResult>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.AccessToken;
    }

    private async Task<FamilyMemberDto> CreateMember(HttpClient client, Guid familyId, CreateFamilyMemberDto request)
    {
        using var response = await client.PostAsJsonAsync($"Families/{familyId}/Members", request, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<FamilyMemberDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(member);
        Assert.Equal(request.Name, member.Name);
        return member;
    }

    private async Task<FamilyMemberDto> UpdateMember(HttpClient client, Guid userId, UpdateFamilyMemberDto request)
    {
        using var response = await client.PostAsJsonAsync($"Families/{_familyId}/Members/{userId}", request, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<FamilyMemberDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(member);
        Assert.Equal(userId, member.Id);
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

    private async Task<FamilyMemberDto[]> GetMembers(HttpClient client)
    {
        using var response = await client.GetAsync($"Families/{_familyId}/Members", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var members = await response.Content.ReadFromJsonAsync<FamilyMemberDto[]>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(members);
        return members.OrderBy(member => member.Name, StringComparer.Ordinal).ToArray();
    }

    private async Task<FamilyLibraryDto[]> GetLibraries(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var libraries = await response.Content.ReadFromJsonAsync<FamilyLibraryDto[]>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(libraries);
        return libraries;
    }

    private Task<HttpResponseMessage> Switch(HttpClient client, Guid userId, string pin)
        => client.PostAsJsonAsync($"Families/{_familyId}/Members/{userId}/Switch", new SwitchFamilyProfileDto { Pw = pin }, _jsonOptions, TestContext.Current.CancellationToken);

    private async Task LockOut(HttpClient client, Guid userId)
    {
        for (var attempt = 0; attempt < Family.LoginAttemptsBeforeLockout; attempt++)
        {
            using var wrong = await Switch(client, userId, WrongPin);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
    }

    private Task<HttpResponseMessage> PostImage(HttpClient client, Guid userId)
    {
        var content = new StringContent(TinyPngBase64, Encoding.UTF8, "image/png");
        return client.PostAsync($"Families/{_familyId}/Members/{userId}/Image", content, TestContext.Current.CancellationToken);
    }

    private sealed class SwitchResult
    {
        public UserDto User { get; set; } = new();

        public string AccessToken { get; set; } = string.Empty;
    }
}
