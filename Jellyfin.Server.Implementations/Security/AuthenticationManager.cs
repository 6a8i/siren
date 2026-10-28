using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Controller.Security;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Security
{
    /// <inheritdoc />
    public class AuthenticationManager : IAuthenticationManager
    {
        private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;

        // API keys change only through this class, so the whole (tiny) table is kept in memory and
        // dropped on every write. A token that is absent is a miss, so unknown tokens cost nothing.
        private volatile Dictionary<string, AuthenticationInfo>? _apiKeysByToken;

        /// <summary>
        /// Initializes a new instance of the <see cref="AuthenticationManager"/> class.
        /// </summary>
        /// <param name="dbProvider">The database provider.</param>
        public AuthenticationManager(IDbContextFactory<JellyfinDbContext> dbProvider)
        {
            _dbProvider = dbProvider;
        }

        /// <inheritdoc />
        public async Task<AuthenticationInfo> CreateApiKey(string name, bool isReadOnly = false)
        {
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                var apiKey = new ApiKey(name) { IsReadOnly = isReadOnly };
                dbContext.ApiKeys.Add(apiKey);

                await dbContext.SaveChangesAsync().ConfigureAwait(false);
                _apiKeysByToken = null;

                return ToAuthenticationInfo(apiKey);
            }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AuthenticationInfo>> GetApiKeys()
        {
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                return await dbContext.ApiKeys
                    .Select(key => new AuthenticationInfo
                    {
                        AppName = key.Name,
                        AccessToken = key.AccessToken,
                        DateCreated = key.DateCreated,
                        DeviceId = string.Empty,
                        DeviceName = string.Empty,
                        AppVersion = string.Empty,
                        IsReadOnly = key.IsReadOnly
                    }).ToListAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public AuthenticationInfo? GetApiKeyByToken(string accessToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(accessToken);

            var keys = _apiKeysByToken;
            if (keys is null)
            {
                using var dbContext = _dbProvider.CreateDbContext();
                keys = dbContext.ApiKeys
                    .AsNoTracking()
                    .AsEnumerable()
                    .Select(ToAuthenticationInfo)
                    .ToDictionary(key => key.AccessToken, StringComparer.Ordinal);
                _apiKeysByToken = keys;
            }

            return keys.GetValueOrDefault(accessToken);
        }

        /// <inheritdoc />
        public async Task<bool> SetApiKeyReadOnly(string accessToken, bool isReadOnly)
        {
            ArgumentException.ThrowIfNullOrEmpty(accessToken);

            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                var updated = await dbContext.ApiKeys
                    .Where(apiKey => apiKey.AccessToken == accessToken)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(apiKey => apiKey.IsReadOnly, isReadOnly))
                    .ConfigureAwait(false);

                _apiKeysByToken = null;
                return updated > 0;
            }
        }

        /// <inheritdoc />
        public async Task DeleteApiKey(string accessToken)
        {
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                await dbContext.ApiKeys
                    .Where(apiKey => apiKey.AccessToken == accessToken)
                    .ExecuteDeleteAsync()
                    .ConfigureAwait(false);
            }

            _apiKeysByToken = null;
        }

        private static AuthenticationInfo ToAuthenticationInfo(ApiKey apiKey)
        {
            return new AuthenticationInfo
            {
                AppName = apiKey.Name,
                AccessToken = apiKey.AccessToken,
                DateCreated = apiKey.DateCreated,
                DeviceId = string.Empty,
                DeviceName = string.Empty,
                AppVersion = string.Empty,
                IsReadOnly = apiKey.IsReadOnly
            };
        }
    }
}
