using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Helpers;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers
{
    public static class RequestHelpersTests
    {
        [Theory]
        [MemberData(nameof(GetOrderBy_Success_TestData))]
        public static void GetOrderBy_Success(IReadOnlyList<ItemSortBy> sortBy, IReadOnlyList<SortOrder> requestedSortOrder, (ItemSortBy, SortOrder)[] expected)
        {
            Assert.Equal(expected, RequestHelpers.GetOrderBy(sortBy, requestedSortOrder));
        }

        [Fact]
        public static void GetUserId_IsAdmin()
        {
            Guid? requestUserId = Guid.NewGuid();
            Guid? authUserId = Guid.NewGuid();

            var claims = new[]
            {
                new Claim(InternalClaimTypes.UserId, authUserId.Value.ToString("N", CultureInfo.InvariantCulture)),
                new Claim(InternalClaimTypes.IsApiKey, bool.FalseString),
                new Claim(ClaimTypes.Role, UserRoles.Administrator)
            };

            var identity = new ClaimsIdentity(claims, string.Empty);
            var principal = new ClaimsPrincipal(identity);

            var userId = RequestHelpers.GetUserId(principal, requestUserId);

            Assert.Equal(requestUserId, userId);
        }

        [Fact]
        public static void GetUserId_IsApiKey_EmptyGuid()
        {
            Guid? requestUserId = Guid.Empty;

            var claims = new[]
            {
                new Claim(InternalClaimTypes.IsApiKey, bool.TrueString)
            };

            var identity = new ClaimsIdentity(claims, string.Empty);
            var principal = new ClaimsPrincipal(identity);

            var userId = RequestHelpers.GetUserId(principal, requestUserId);

            Assert.Equal(Guid.Empty, userId);
        }

        [Fact]
        public static void GetUserId_IsApiKey_Null()
        {
            Guid? requestUserId = null;

            var claims = new[]
            {
                new Claim(InternalClaimTypes.IsApiKey, bool.TrueString)
            };

            var identity = new ClaimsIdentity(claims, string.Empty);
            var principal = new ClaimsPrincipal(identity);

            var userId = RequestHelpers.GetUserId(principal, requestUserId);

            Assert.Equal(Guid.Empty, userId);
        }

        [Fact]
        public static void GetUserId_IsUser()
        {
            Guid? requestUserId = Guid.NewGuid();
            Guid? authUserId = Guid.NewGuid();

            var claims = new[]
            {
                new Claim(InternalClaimTypes.UserId, authUserId.Value.ToString("N", CultureInfo.InvariantCulture)),
                new Claim(InternalClaimTypes.IsApiKey, bool.FalseString),
                new Claim(ClaimTypes.Role, UserRoles.User)
            };

            var identity = new ClaimsIdentity(claims, string.Empty);
            var principal = new ClaimsPrincipal(identity);

            Assert.Throws<SecurityException>(() => RequestHelpers.GetUserId(principal, requestUserId));
        }

        [Fact]
        public static void CanSeeUser_Administrator_SeesEveryone()
        {
            var userManager = CreateUserManager();
            var principal = CreatePrincipal(Guid.NewGuid(), UserRoles.Administrator);

            Assert.Null(RequestHelpers.GetUserScope(principal, userManager));
            Assert.True(RequestHelpers.CanSeeUser(principal, userManager, Guid.NewGuid()));
        }

        [Fact]
        public static void CanSeeUser_FamilyMember_SeesOnlyTheirFamily()
        {
            var familyId = Guid.NewGuid();
            var caller = new User("caller", "default", "default") { FamilyId = familyId };
            var sibling = new User("sibling", "default", "default") { FamilyId = familyId };
            var stranger = new User("stranger", "default", "default") { FamilyId = Guid.NewGuid() };
            var loner = new User("loner", "default", "default");
            var userManager = CreateUserManager(caller, sibling, stranger, loner);
            var principal = CreatePrincipal(caller.Id, UserRoles.User);

            Assert.True(RequestHelpers.CanSeeUser(principal, userManager, caller.Id));
            Assert.True(RequestHelpers.CanSeeUser(principal, userManager, sibling.Id));
            Assert.False(RequestHelpers.CanSeeUser(principal, userManager, stranger.Id));
            Assert.False(RequestHelpers.CanSeeUser(principal, userManager, loner.Id));
            Assert.False(RequestHelpers.CanSeeUser(principal, userManager, Guid.NewGuid()));
        }

        [Fact]
        public static void CanSeeUser_UserWithoutFamily_SeesOnlyThemselves()
        {
            var caller = new User("caller", "default", "default");
            var other = new User("other", "default", "default");
            var member = new User("member", "default", "default") { FamilyId = Guid.NewGuid() };
            var userManager = CreateUserManager(caller, other, member);
            var principal = CreatePrincipal(caller.Id, UserRoles.User);

            Assert.True(RequestHelpers.CanSeeUser(principal, userManager, caller.Id));
            Assert.False(RequestHelpers.CanSeeUser(principal, userManager, other.Id));
            Assert.False(RequestHelpers.CanSeeUser(principal, userManager, member.Id));
        }

        private static ClaimsPrincipal CreatePrincipal(Guid userId, string role)
        {
            var claims = new[]
            {
                new Claim(InternalClaimTypes.UserId, userId.ToString("N", CultureInfo.InvariantCulture)),
                new Claim(InternalClaimTypes.IsApiKey, bool.FalseString),
                new Claim(ClaimTypes.Role, role)
            };

            return new ClaimsPrincipal(new ClaimsIdentity(claims, string.Empty));
        }

        private static IUserManager CreateUserManager(params User[] users)
        {
            var userManager = new Mock<IUserManager>();
            foreach (var user in users)
            {
                userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
            }

            return userManager.Object;
        }

        public static TheoryData<IReadOnlyList<ItemSortBy>, IReadOnlyList<SortOrder>, (ItemSortBy, SortOrder)[]> GetOrderBy_Success_TestData()
        {
            var data = new TheoryData<IReadOnlyList<ItemSortBy>, IReadOnlyList<SortOrder>, (ItemSortBy, SortOrder)[]>();

            data.Add(
                Array.Empty<ItemSortBy>(),
                Array.Empty<SortOrder>(),
                Array.Empty<(ItemSortBy, SortOrder)>());

            data.Add(
                new[]
                {
                    ItemSortBy.IsFavoriteOrLiked,
                    ItemSortBy.Random
                },
                Array.Empty<SortOrder>(),
                new (ItemSortBy, SortOrder)[]
                {
                    (ItemSortBy.IsFavoriteOrLiked, SortOrder.Ascending),
                    (ItemSortBy.Random, SortOrder.Ascending),
                });

            data.Add(
                new[]
                {
                    ItemSortBy.SortName,
                    ItemSortBy.ProductionYear
                },
                new[]
                {
                    SortOrder.Descending
                },
                new (ItemSortBy, SortOrder)[]
                {
                    (ItemSortBy.SortName, SortOrder.Descending),
                    (ItemSortBy.ProductionYear, SortOrder.Descending),
                });

            return data;
        }
    }
}
