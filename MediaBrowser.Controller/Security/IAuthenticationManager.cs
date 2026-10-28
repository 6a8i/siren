using System.Collections.Generic;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Security
{
    /// <summary>
    /// Handles the retrieval and storage of API keys.
    /// </summary>
    public interface IAuthenticationManager
    {
        /// <summary>
        /// Creates an API key.
        /// </summary>
        /// <param name="name">The name of the key.</param>
        /// <param name="isReadOnly">Whether the key may only make read requests (GET, HEAD, OPTIONS).</param>
        /// <returns>A task representing the creation of the key, containing the created key.</returns>
        Task<AuthenticationInfo> CreateApiKey(string name, bool isReadOnly = false);

        /// <summary>
        /// Marks an API key as read-only or read/write.
        /// </summary>
        /// <param name="accessToken">The access token.</param>
        /// <param name="isReadOnly">Whether the key may only make read requests (GET, HEAD, OPTIONS).</param>
        /// <returns>A task containing <c>true</c> when a key holds the token, <c>false</c> otherwise.</returns>
        Task<bool> SetApiKeyReadOnly(string accessToken, bool isReadOnly);

        /// <summary>
        /// Gets the API keys.
        /// </summary>
        /// <returns>A task representing the retrieval of the API keys.</returns>
        Task<IReadOnlyList<AuthenticationInfo>> GetApiKeys();

        /// <summary>
        /// Gets the API key that holds an access token.
        /// </summary>
        /// <param name="accessToken">The access token.</param>
        /// <returns>The key, or <c>null</c> when no API key holds the token.</returns>
        AuthenticationInfo? GetApiKeyByToken(string accessToken);

        /// <summary>
        /// Deletes an API key with the provided access token.
        /// </summary>
        /// <param name="accessToken">The access token.</param>
        /// <returns>A task representing the deletion of the API key.</returns>
        Task DeleteApiKey(string accessToken);
    }
}
