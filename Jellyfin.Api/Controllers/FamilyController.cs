using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Models.FamilyDtos;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Families: groups of up to six profiles that only see each other. A server administrator creates
/// them; a member with the family administrator permission manages the family's profiles without
/// any access to the server dashboard.
/// </summary>
[Route("Families")]
[Authorize]
public class FamilyController : BaseJellyfinApiController
{
    private readonly IFamilyManager _familyManager;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IAuthorizationContext _authContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyController"/> class.
    /// </summary>
    /// <param name="familyManager">Instance of the <see cref="IFamilyManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="playlistManager">Instance of the <see cref="IPlaylistManager"/> interface.</param>
    /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
    public FamilyController(
        IFamilyManager familyManager,
        IUserManager userManager,
        ISessionManager sessionManager,
        IPlaylistManager playlistManager,
        IAuthorizationContext authContext)
    {
        _familyManager = familyManager;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _playlistManager = playlistManager;
        _authContext = authContext;
    }

    private bool IsServerAdministrator => User.IsInRole(UserRoles.Administrator);

    /// <summary>
    /// Gets every family.
    /// </summary>
    /// <response code="200">Families returned.</response>
    /// <returns>The families.</returns>
    [HttpGet]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<FamilyDto>> GetFamilies()
        => Ok(_familyManager.GetFamilies().Select(ToDto));

    /// <summary>
    /// Creates a family.
    /// </summary>
    /// <param name="request">The family to create.</param>
    /// <response code="200">Family created.</response>
    /// <response code="400">The name is empty or already in use.</response>
    /// <returns>The new family.</returns>
    [HttpPost]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<FamilyDto>> CreateFamily([FromBody, Required] CreateFamilyDto request)
    {
        try
        {
            var family = await _familyManager.CreateFamilyAsync(request.Name).ConfigureAwait(false);
            return ToDto(family);
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Gets the family of the current user.
    /// </summary>
    /// <response code="200">Family returned.</response>
    /// <response code="404">The current user does not belong to a family.</response>
    /// <returns>The family.</returns>
    [HttpGet("Mine")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<FamilyDto> GetMyFamily()
    {
        var family = GetCaller()?.FamilyId is { } familyId ? _familyManager.GetFamily(familyId) : null;
        if (family is null)
        {
            return NotFound("The current user does not belong to a family.");
        }

        return ToDto(family);
    }

    /// <summary>
    /// Gets a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <response code="200">Family returned.</response>
    /// <response code="403">The current user is not a member of the family.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>The family.</returns>
    [HttpGet("{familyId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<FamilyDto> GetFamily([FromRoute, Required] Guid familyId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanView(family))
        {
            return Forbidden("Only members of the family can see it.");
        }

        return ToDto(family);
    }

    /// <summary>
    /// Renames a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="request">The new values.</param>
    /// <response code="204">Family updated.</response>
    /// <response code="400">The name is empty or already in use.</response>
    /// <response code="403">The current user does not manage the family.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("{familyId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateFamily(
        [FromRoute, Required] Guid familyId,
        [FromBody, Required] UpdateFamilyDto request)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can change the family.");
        }

        try
        {
            await _familyManager.RenameFamilyAsync(family.Id, request.Name).ConfigureAwait(false);
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }

        return NoContent();
    }

    /// <summary>
    /// Deletes a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="deleteMembers">Whether to delete the family's profiles too. Otherwise they stay as users without a family.</param>
    /// <response code="204">Family deleted.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("{familyId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteFamily(
        [FromRoute, Required] Guid familyId,
        [FromQuery] bool deleteMembers = false)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (deleteMembers)
        {
            foreach (var member in _familyManager.GetMembers(family.Id))
            {
                await DeleteUserCompletely(member.Id).ConfigureAwait(false);
            }
        }

        await _familyManager.DeleteFamilyAsync(family.Id).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets the profiles of a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <response code="200">Profiles returned.</response>
    /// <response code="403">The current user is not a member of the family.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>The profiles. <c>HasPassword</c> tells whether a profile asks for a PIN when switching to it.</returns>
    [HttpGet("{familyId}/Members")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IEnumerable<UserDto>> GetMembers([FromRoute, Required] Guid familyId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanView(family))
        {
            return Forbidden("Only members of the family can see its profiles.");
        }

        return Ok(_familyManager.GetMembers(family.Id).Select(ToMemberDto));
    }

    /// <summary>
    /// Adds a profile to a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="request">The profile to create.</param>
    /// <response code="200">Profile created.</response>
    /// <response code="400">The family is full or the name is invalid or taken.</response>
    /// <response code="403">The current user does not manage the family.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>The new profile.</returns>
    [HttpPost("{familyId}/Members")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> CreateMember(
        [FromRoute, Required] Guid familyId,
        [FromBody, Required] CreateFamilyMemberDto request)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can add profiles.");
        }

        if (_familyManager.GetMembers(family.Id).Count >= _familyManager.MaxMembers)
        {
            return BadRequest(string.Format(
                CultureInfo.InvariantCulture,
                "The family already has {0} profiles, which is the maximum.",
                _familyManager.MaxMembers));
        }

        User newUser;
        try
        {
            newUser = await _userManager.CreateUserAsync(request.Name).ConfigureAwait(false);
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }

        if (!string.IsNullOrEmpty(request.Password))
        {
            await _userManager.ChangePassword(newUser.Id, request.Password).ConfigureAwait(false);
        }

        await _familyManager.AssignUserAsync(newUser.Id, family.Id).ConfigureAwait(false);

        // A profile starts with the plain defaults: it is listed on the family's devices, cannot
        // touch the library's content and only reaches what its creator reaches.
        var policy = _userManager.GetUserDto(_userManager.GetUserById(newUser.Id)!).Policy;
        policy.IsHidden = false;
        policy.IsFamilyAdministrator = request.IsFamilyAdministrator;
        policy.MaxParentalRating = request.MaxParentalRating;
        policy.EnableContentDeletion = false;
        policy.EnableContentDeletionFromFolders = [];
        policy.EnableLiveTvManagement = false;
        policy.EnableCollectionManagement = false;
        policy.EnableSubtitleManagement = false;
        policy.EnableLyricManagement = false;
        policy.EnableRemoteControlOfOtherUsers = false;
        ApplyLibraryAccess(policy, request.EnableAllFolders, request.EnabledFolders, GetCaller());
        await _userManager.UpdatePolicyAsync(newUser.Id, policy).ConfigureAwait(false);

        return ToMemberDto(_userManager.GetUserById(newUser.Id)!);
    }

    /// <summary>
    /// Moves an existing user into a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The user id.</param>
    /// <response code="204">User moved into the family.</response>
    /// <response code="400">The family is full or the user is a server administrator.</response>
    /// <response code="404">Family or user not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPut("{familyId}/Members/{userId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AddExistingMember(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId)
    {
        if (_familyManager.GetFamily(familyId) is null)
        {
            return NotFound("Family not found.");
        }

        if (_userManager.GetUserById(userId) is null)
        {
            return NotFound("User not found.");
        }

        try
        {
            await _familyManager.AssignUserAsync(userId, familyId).ConfigureAwait(false);
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }

        return NoContent();
    }

    /// <summary>
    /// Updates a family profile.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The profile's user id.</param>
    /// <param name="request">The new values.</param>
    /// <response code="204">Profile updated.</response>
    /// <response code="400">The name is taken, or the change would leave the family without an administrator.</response>
    /// <response code="403">The current user does not manage the family.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("{familyId}/Members/{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateMember(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId,
        [FromBody, Required] UpdateFamilyMemberDto request)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can change profiles.");
        }

        var member = GetMember(family, userId);
        if (member is null)
        {
            return NotFound("Profile not found in this family.");
        }

        var caller = GetCaller();
        if (!IsServerAdministrator)
        {
            if (!request.IsFamilyAdministrator
                && member.HasPermission(PermissionKind.IsFamilyAdministrator)
                && !_familyManager.GetMembers(family.Id).Any(m => !m.Id.Equals(member.Id) && m.HasPermission(PermissionKind.IsFamilyAdministrator)))
            {
                return BadRequest("The family needs at least one family administrator.");
            }

            if (request.IsDisabled && caller is not null && caller.Id.Equals(member.Id))
            {
                return BadRequest("You cannot disable your own profile.");
            }
        }

        if (!string.Equals(member.Username, request.Name, StringComparison.Ordinal))
        {
            try
            {
                await _userManager.RenameUser(member.Id, member.Username, request.Name).ConfigureAwait(false);
            }
            catch (ArgumentException e)
            {
                return BadRequest(e.Message);
            }
        }

        var policy = _userManager.GetUserDto(_userManager.GetUserById(member.Id)!).Policy;
        policy.IsFamilyAdministrator = request.IsFamilyAdministrator;
        policy.IsDisabled = request.IsDisabled;
        policy.MaxParentalRating = request.MaxParentalRating;
        ApplyLibraryAccess(policy, request.EnableAllFolders, request.EnabledFolders ?? [], caller);
        await _userManager.UpdatePolicyAsync(member.Id, policy).ConfigureAwait(false);

        if (request.IsDisabled)
        {
            await _sessionManager.RevokeUserTokens(member.Id, null).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Sets or clears the PIN of a family profile.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The profile's user id.</param>
    /// <param name="request">The new PIN, or a reset.</param>
    /// <response code="204">PIN updated; the profile's other sessions are signed out.</response>
    /// <response code="403">The current user does not manage the family.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("{familyId}/Members/{userId}/Password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateMemberPassword(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId,
        [FromBody, Required] UpdateFamilyMemberPasswordDto request)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can change a profile's PIN.");
        }

        var member = GetMember(family, userId);
        if (member is null)
        {
            return NotFound("Profile not found in this family.");
        }

        if (request.ResetPassword)
        {
            await _userManager.ResetPassword(member.Id).ConfigureAwait(false);
        }
        else
        {
            await _userManager.ChangePassword(member.Id, request.NewPassword ?? string.Empty).ConfigureAwait(false);
        }

        await _sessionManager.RevokeUserTokens(member.Id, User.GetToken()).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Removes a profile from a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The profile's user id.</param>
    /// <param name="deleteUser">Whether to delete the user. A server administrator may pass false to keep it as a user without a family.</param>
    /// <response code="204">Profile removed.</response>
    /// <response code="400">A family administrator cannot remove their own profile or keep the user.</response>
    /// <response code="403">The current user does not manage the family.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("{familyId}/Members/{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RemoveMember(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId,
        [FromQuery] bool deleteUser = true)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can remove profiles.");
        }

        var member = GetMember(family, userId);
        if (member is null)
        {
            return NotFound("Profile not found in this family.");
        }

        if (!IsServerAdministrator)
        {
            if (!deleteUser)
            {
                return BadRequest("Only a server administrator can keep the user outside the family.");
            }

            var caller = GetCaller();
            if (caller is not null && caller.Id.Equals(member.Id))
            {
                return BadRequest("You cannot remove your own profile.");
            }
        }

        if (deleteUser)
        {
            await DeleteUserCompletely(member.Id).ConfigureAwait(false);
        }
        else
        {
            await _familyManager.AssignUserAsync(member.Id, null).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Switches the current session to another profile of the same family, like picking a profile
    /// on a shared screen. A wrong PIN counts as a failed login for that profile.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The user id of the profile to switch to.</param>
    /// <param name="request">The profile's PIN.</param>
    /// <response code="200">Switched; the result carries the new access token.</response>
    /// <response code="400">The profile is the current one.</response>
    /// <response code="401">Wrong PIN.</response>
    /// <response code="403">The current user is not a member of the family, or the profile is disabled.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>The new session.</returns>
    [HttpPost("{familyId}/Members/{userId}/Switch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthenticationResult>> SwitchProfile(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId,
        [FromBody, Required] SwitchFamilyProfileDto request)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        var caller = GetCaller();
        if (caller is null || !caller.FamilyId.Equals(family.Id))
        {
            return Forbidden("Only members of the family can switch between its profiles.");
        }

        var target = GetMember(family, userId);
        if (target is null)
        {
            return NotFound("Profile not found in this family.");
        }

        if (target.Id.Equals(caller.Id))
        {
            return BadRequest("This is already the current profile.");
        }

        if (target.HasPermission(PermissionKind.IsDisabled))
        {
            return Forbidden("This profile is disabled.");
        }

        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var result = await _sessionManager.AuthenticateNewSession(new AuthenticationRequest
        {
            App = auth.Client,
            AppVersion = auth.Version,
            DeviceId = auth.DeviceId,
            DeviceName = auth.Device,
            Password = request.Pw ?? string.Empty,
            RemoteEndPoint = HttpContext.GetNormalizedRemoteIP().ToString(),
            Username = target.Username
        }).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Limits a profile's library access to what the managing user can reach. A family administrator
    /// cannot hand out libraries they do not have; a server administrator (or an API key) can.
    /// </summary>
    private static void ApplyLibraryAccess(UserPolicy policy, bool? enableAllFolders, Guid[]? enabledFolders, User? manager)
    {
        // What the manager can reach: null means everything.
        Guid[]? reach = manager is null
            || manager.HasPermission(PermissionKind.IsAdministrator)
            || manager.HasPermission(PermissionKind.EnableAllFolders)
            ? null
            : manager.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);

        bool all;
        Guid[] folders;
        if (enableAllFolders is null && enabledFolders is null)
        {
            // Nothing requested: the profile gets the manager's own access.
            all = reach is null;
            folders = reach ?? [];
        }
        else
        {
            all = enableAllFolders ?? false;
            folders = enabledFolders ?? [];
        }

        if (reach is not null)
        {
            if (all)
            {
                all = false;
                folders = reach;
            }
            else
            {
                folders = folders.Intersect(reach).ToArray();
            }
        }

        policy.EnableAllFolders = all;
        policy.EnabledFolders = folders;
    }

    private User? GetCaller()
    {
        var userId = User.GetUserId();
        return userId.IsEmpty() ? null : _userManager.GetUserById(userId);
    }

    private bool CanView(Family family)
        => IsServerAdministrator || (GetCaller() is { } caller && caller.FamilyId.Equals(family.Id));

    private bool CanManage(Family family)
        => IsServerAdministrator
            || (GetCaller() is { } caller
                && caller.FamilyId.Equals(family.Id)
                && caller.HasPermission(PermissionKind.IsFamilyAdministrator));

    private User? GetMember(Family family, Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        return user is not null && user.FamilyId.Equals(family.Id) ? user : null;
    }

    private ObjectResult Forbidden(string message)
        => StatusCode(StatusCodes.Status403Forbidden, message);

    private FamilyDto ToDto(Family family)
        => new()
        {
            Id = family.Id,
            Name = family.Name,
            DateCreated = family.DateCreated,
            MemberCount = _familyManager.GetMembers(family.Id).Count,
            MaxMembers = _familyManager.MaxMembers
        };

    private UserDto ToMemberDto(User user)
    {
        var dto = _userManager.GetUserDto(user, HttpContext.GetNormalizedRemoteIP().ToString());
        var hasPassword = !string.IsNullOrEmpty(user.Password);
#pragma warning disable CS0618 // Family members may know which of their own profiles ask for a PIN.
        dto.HasPassword = hasPassword;
        dto.HasConfiguredPassword = hasPassword;
#pragma warning restore CS0618
        return dto;
    }

    private async Task DeleteUserCompletely(Guid userId)
    {
        await _sessionManager.RevokeUserTokens(userId, null).ConfigureAwait(false);
        await _playlistManager.RemovePlaylistsAsync(userId).ConfigureAwait(false);
        await _userManager.DeleteUserAsync(userId).ConfigureAwait(false);
    }
}
