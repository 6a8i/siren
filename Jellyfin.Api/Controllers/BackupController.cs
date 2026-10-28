using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.SystemBackupService;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The backup controller.
/// </summary>
[Authorize(Policy = Policies.RequiresElevation)]
public class BackupController : BaseJellyfinApiController
{
    private readonly IBackupService _backupService;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IActivityManager _activityManager;
    private readonly ILocalizationManager _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupController"/> class.
    /// </summary>
    /// <param name="backupService">Instance of the <see cref="IBackupService"/> interface.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="activityManager">Instance of the <see cref="IActivityManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    public BackupController(
        IBackupService backupService,
        IApplicationPaths applicationPaths,
        IActivityManager activityManager,
        ILocalizationManager localization)
    {
        _backupService = backupService;
        _applicationPaths = applicationPaths;
        _activityManager = activityManager;
        _localization = localization;
    }

    /// <summary>
    /// Creates a new Backup.
    /// </summary>
    /// <param name="backupOptions">The backup options.</param>
    /// <response code="200">Backup created.</response>
    /// <response code="403">User does not have permission to retrieve information.</response>
    /// <returns>The created backup manifest.</returns>
    [HttpPost("Create")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BackupManifestDto>> CreateBackup([FromBody] BackupOptionsDto backupOptions)
    {
        BackupManifestDto manifest;
        try
        {
            manifest = await _backupService.CreateBackupAsync(backupOptions ?? new()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same trace as the scheduled task, so the activity log tells the whole story of the backups.
            await BackupActivityLog.LogFailedAsync(_activityManager, _localization, ex).ConfigureAwait(false);
            throw;
        }

        await BackupActivityLog.LogCreatedAsync(_activityManager, _localization, manifest.Path).ConfigureAwait(false);
        return Ok(manifest);
    }

    /// <summary>
    /// Restores to a backup by restarting the server and applying the backup.
    /// </summary>
    /// <param name="archiveRestoreDto">The data to start a restore process.</param>
    /// <response code="204">Backup restore started.</response>
    /// <response code="403">User does not have permission to retrieve information.</response>
    /// <returns>No-Content.</returns>
    [HttpPost("Restore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult StartRestoreBackup([FromBody, BindRequired] BackupRestoreRequestDto archiveRestoreDto)
    {
        var archivePath = SanitizePath(archiveRestoreDto.ArchiveFileName);
        if (!System.IO.File.Exists(archivePath))
        {
            return NotFound();
        }

        _backupService.ScheduleRestoreAndRestartServer(archivePath);
        return NoContent();
    }

    /// <summary>
    /// Gets a list of all currently present backups in the backup directory.
    /// </summary>
    /// <response code="200">Backups available.</response>
    /// <response code="403">User does not have permission to retrieve information.</response>
    /// <returns>The list of backups.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BackupManifestDto[]>> ListBackups()
    {
        return Ok(await _backupService.EnumerateBackups().ConfigureAwait(false));
    }

    /// <summary>
    /// Gets the descriptor from an existing archive is present.
    /// </summary>
    /// <param name="path">The data to start a restore process.</param>
    /// <response code="200">Backup archive manifest.</response>
    /// <response code="204">Not a valid jellyfin Archive.</response>
    /// <response code="404">Not a valid path.</response>
    /// <response code="403">User does not have permission to retrieve information.</response>
    /// <returns>The backup manifest.</returns>
    [HttpGet("Manifest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BackupManifestDto>> GetBackup([BindRequired] string path)
    {
        var backupPath = SanitizePath(path);

        if (!System.IO.File.Exists(backupPath))
        {
            return NotFound();
        }

        var manifest = await _backupService.GetBackupManifest(backupPath).ConfigureAwait(false);
        if (manifest is null)
        {
            return NoContent();
        }

        return Ok(manifest);
    }

    [NonAction]
    private string SanitizePath(string path)
    {
        // sanitize path
        var archiveRestorePath = Path.GetFileName(Path.GetFullPath(path));
        var archivePath = Path.Combine(_applicationPaths.BackupPath, archiveRestorePath);
        return archivePath;
    }
}
