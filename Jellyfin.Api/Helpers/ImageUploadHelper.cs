using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Reads uploaded images. Every image upload is a base64-encoded body whose content type names the
/// image format; a user's profile image is stored here so that <c>POST /UserImage</c> and the family
/// endpoints write the same file and refresh the same user snapshot.
/// </summary>
public class ImageUploadHelper
{
    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<ImageUploadHelper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageUploadHelper"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ImageUploadHelper}"/> interface.</param>
    public ImageUploadHelper(
        IUserManager userManager,
        IProviderManager providerManager,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<ImageUploadHelper> logger)
    {
        _userManager = userManager;
        _providerManager = providerManager;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
    }

    /// <summary>
    /// Wraps a base64-encoded request body in a stream that yields the decoded bytes.
    /// </summary>
    /// <param name="inputStream">The request body.</param>
    /// <returns>The decoding stream; dispose it with the request.</returns>
    public static CryptoStream GetFromBase64Stream(Stream inputStream)
        => new CryptoStream(inputStream, new FromBase64Transform(), CryptoStreamMode.Read);

    /// <summary>
    /// Gets the file extension for the image format named by an upload's content type.
    /// </summary>
    /// <param name="contentType">The request content type, such as <c>image/png; charset=utf-8</c>.</param>
    /// <param name="extension">The extension with its dot, or <c>null</c> when the content type is not an image.</param>
    /// <returns><c>true</c> when the content type names a known image format.</returns>
    public static bool TryGetImageExtensionFromContentType(string? contentType, [NotNullWhen(true)] out string? extension)
    {
        extension = null;
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        if (MediaTypeHeaderValue.TryParse(contentType, out var parsedValue)
            && parsedValue.MediaType.HasValue
            && MimeTypes.IsImage(parsedValue.MediaType.Value))
        {
            extension = MimeTypes.ToExtension(parsedValue.MediaType.Value);
            return extension is not null;
        }

        return false;
    }

    /// <summary>
    /// Stores the base64-encoded image in the request body as the user's profile image, replacing
    /// the current one.
    /// </summary>
    /// <param name="user">The user whose image is set.</param>
    /// <param name="request">The request carrying the image.</param>
    /// <returns>204 when the image was saved, or 400 when the content type is not an image or the user name is not a safe directory name.</returns>
    public async Task<ActionResult> SaveUserImageAsync(User user, HttpRequest request)
    {
        if (!TryGetImageExtensionFromContentType(request.ContentType, out var extension))
        {
            return new BadRequestObjectResult("Incorrect ContentType.");
        }

        var stream = GetFromBase64Stream(request.Body);
        await using (stream.ConfigureAwait(false))
        {
            // Handle image/png; charset=utf-8
            var mimeType = request.ContentType?.Split(';').FirstOrDefault();
            var userConfigurationDirectoryPath = _serverConfigurationManager.ApplicationPaths.UserConfigurationDirectoryPath;
            var userDataPath = Path.Combine(userConfigurationDirectoryPath, user.Username);
            if (!PathHelper.IsContainedIn(userConfigurationDirectoryPath, userDataPath))
            {
                return new BadRequestObjectResult("Invalid user.");
            }

            if (user.ProfileImage is not null)
            {
                await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
            }

            user.ProfileImage = new ImageInfo(Path.Combine(userDataPath, "profile" + extension));

            await _providerManager
                .SaveImage(stream, mimeType, user.ProfileImage.Path)
                .ConfigureAwait(false);
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

            return new NoContentResult();
        }
    }

    /// <summary>
    /// Deletes the user's profile image, if any: the file first, then the database row.
    /// </summary>
    /// <param name="user">The user whose image is removed.</param>
    /// <returns>A task.</returns>
    public async Task DeleteUserImageAsync(User user)
    {
        if (user.ProfileImage is null)
        {
            return;
        }

        try
        {
            File.Delete(user.ProfileImage.Path);
        }
        catch (IOException e)
        {
            _logger.LogError(e, "Error deleting user profile image:");
        }

        await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
    }
}
