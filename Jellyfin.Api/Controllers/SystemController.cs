using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Attributes;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.SystemInfoDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The system controller.
/// </summary>
public class SystemController : BaseJellyfinApiController
{
    private readonly ILogger<SystemController> _logger;
    private readonly IServerApplicationHost _appHost;
    private readonly IApplicationPaths _appPaths;
    private readonly IFileSystem _fileSystem;
    private readonly INetworkManager _networkManager;
    private readonly ISystemManager _systemManager;
    private readonly CspReportThrottle _cspReportThrottle;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemController"/> class.
    /// </summary>
    /// <param name="logger">Instance of <see cref="ILogger{SystemController}"/> interface.</param>
    /// <param name="appPaths">Instance of <see cref="IServerApplicationPaths"/> interface.</param>
    /// <param name="appHost">Instance of <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="fileSystem">Instance of <see cref="IFileSystem"/> interface.</param>
    /// <param name="networkManager">Instance of <see cref="INetworkManager"/> interface.</param>
    /// <param name="systemManager">Instance of <see cref="ISystemManager"/> interface.</param>
    /// <param name="cspReportThrottle">The throttle deciding which CSP violation reports are logged.</param>
    public SystemController(
        ILogger<SystemController> logger,
        IServerApplicationHost appHost,
        IServerApplicationPaths appPaths,
        IFileSystem fileSystem,
        INetworkManager networkManager,
        ISystemManager systemManager,
        CspReportThrottle cspReportThrottle)
    {
        _logger = logger;
        _appHost = appHost;
        _appPaths = appPaths;
        _fileSystem = fileSystem;
        _networkManager = networkManager;
        _systemManager = systemManager;
        _cspReportThrottle = cspReportThrottle;
    }

    /// <summary>
    /// Gets information about the server.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <response code="403">User does not have permission to retrieve information.</response>
    /// <returns>A <see cref="SystemInfo"/> with info about the system.</returns>
    [HttpGet("Info")]
    [Authorize(Policy = Policies.FirstTimeSetupOrIgnoreParentalControl)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<SystemInfo> GetSystemInfo()
        => _systemManager.GetSystemInfo(Request);

    /// <summary>
    /// Gets information about the server.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <response code="403">User does not have permission to retrieve information.</response>
    /// <returns>A <see cref="SystemInfo"/> with info about the system.</returns>
    [HttpGet("Info/Storage")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<SystemStorageDto> GetSystemStorage()
        => Ok(SystemStorageDto.FromSystemStorageInfo(_systemManager.GetSystemStorageInfo()));

    /// <summary>
    /// Gets public information about the server.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <returns>A <see cref="PublicSystemInfo"/> with public info about the system.</returns>
    [HttpGet("Info/Public")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PublicSystemInfo> GetPublicSystemInfo()
        => _systemManager.GetPublicSystemInfo(Request);

    /// <summary>
    /// Pings the system.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <returns>The server name.</returns>
    [HttpGet("Ping", Name = "GetPingSystem")]
    [HttpPost("Ping", Name = "PostPingSystem")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<string> PingSystem()
        => _appHost.Name;

    /// <summary>
    /// Receives the Content-Security-Policy violation reports browsers send to the <c>report-uri</c> of
    /// the policy, and logs them. Anonymous, because the browser sends them without credentials; not
    /// rate limited as a login, because a page with one broken resource sends one per load.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the browser gives up on the request.</param>
    /// <response code="204">Report received.</response>
    /// <response code="413">Report larger than a browser would send.</response>
    /// <response code="415">Not a CSP report content type.</response>
    /// <returns>No content.</returns>
    [HttpPost("CspReport")]
    [AllowAnonymous]
    [Consumes("application/csp-report", "application/reports+json")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<ActionResult> PostCspReport(CancellationToken cancellationToken)
    {
        if (Request.ContentLength > CspViolationReport.MaxBodyBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // One byte more than allowed tells a body without Content-Length apart from one exactly at the limit.
        var buffer = ArrayPool<byte>.Shared.Rent(CspViolationReport.MaxBodyBytes + 1);
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (length > CspViolationReport.MaxBodyBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            foreach (var report in CspViolationReport.Parse(buffer.AsMemory(0, length)))
            {
                LogCspReport(report);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return NoContent();
    }

    /// <summary>
    /// Restarts the application.
    /// </summary>
    /// <response code="204">Server restarted.</response>
    /// <response code="403">User does not have permission to restart server.</response>
    /// <returns>No content. Server restarted.</returns>
    [HttpPost("Restart")]
    [Authorize(Policy = Policies.LocalAccessOrRequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult RestartApplication()
    {
        _systemManager.Restart();
        return NoContent();
    }

    /// <summary>
    /// Shuts down the application.
    /// </summary>
    /// <response code="204">Server shut down.</response>
    /// <response code="403">User does not have permission to shutdown server.</response>
    /// <returns>No content. Server shut down.</returns>
    [HttpPost("Shutdown")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult ShutdownApplication()
    {
        _systemManager.Shutdown();
        return NoContent();
    }

    /// <summary>
    /// Gets a list of available server log files.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <response code="403">User does not have permission to get server logs.</response>
    /// <returns>An array of <see cref="LogFile"/> with the available log files.</returns>
    [HttpGet("Logs")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<LogFile[]> GetServerLogs()
    {
        IEnumerable<FileSystemMetadata> files;

        try
        {
            files = _fileSystem.GetFiles(_appPaths.LogDirectoryPath, new[] { ".txt", ".log" }, true, false);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error getting logs");
            files = Enumerable.Empty<FileSystemMetadata>();
        }

        var result = files.Select(i => new LogFile
        {
            DateCreated = _fileSystem.GetCreationTimeUtc(i),
            DateModified = _fileSystem.GetLastWriteTimeUtc(i),
            Name = i.Name,
            Size = i.Length
        })
            .OrderByDescending(i => i.DateModified)
            .ThenByDescending(i => i.DateCreated)
            .ThenBy(i => i.Name)
            .ToArray();

        return result;
    }

    /// <summary>
    /// Gets information about the request endpoint.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <response code="403">User does not have permission to get endpoint information.</response>
    /// <returns><see cref="EndPointInfo"/> with information about the endpoint.</returns>
    [HttpGet("Endpoint")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<EndPointInfo> GetEndpointInfo()
    {
        return new EndPointInfo
        {
            IsLocal = HttpContext.IsLocal(),
            IsInNetwork = _networkManager.IsInLocalNetwork(HttpContext.GetNormalizedRemoteIP())
        };
    }

    /// <summary>
    /// Gets a log file.
    /// </summary>
    /// <param name="name">The name of the log file to get.</param>
    /// <response code="200">Log file retrieved.</response>
    /// <response code="403">User does not have permission to get log files.</response>
    /// <response code="404">Could not find a log file with the name.</response>
    /// <returns>The log file.</returns>
    [HttpGet("Logs/Log")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesFile(MediaTypeNames.Text.Plain)]
    public ActionResult GetLogFile([FromQuery, Required] string name)
    {
        var file = _fileSystem
            .GetFiles(_appPaths.LogDirectoryPath)
            .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            return NotFound("Log file not found.");
        }

        // For older files, assume fully static
        var fileShare = file.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1) ? FileShare.Read : FileShare.ReadWrite;
        FileStream stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, fileShare, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);
        return File(stream, "text/plain; charset=utf-8");
    }

    private void LogCspReport(CspViolationReport report)
    {
        var verdict = _cspReportThrottle.Register(report.ThrottleKey, out var droppedInPreviousWindow);
        if (droppedInPreviousWindow > 0)
        {
            _logger.LogWarning(
                "{Count} further CSP violation reports in the previous window were repeats or over the limit and were not logged",
                droppedInPreviousWindow);
        }

        switch (verdict)
        {
            case CspReportVerdict.Log:
                // The fields are already bounded and stripped of control characters by the parser.
                _logger.LogWarning(
                    "CSP violation ({Disposition}) on {DocumentUri}: {ViolatedDirective} blocked {BlockedUri}, source {SourceFile}:{LineNumber}, client {RemoteIp}",
                    report.Disposition,
                    report.DocumentUri,
                    report.ViolatedDirective,
                    report.BlockedUri,
                    report.SourceFile,
                    report.LineNumber,
                    HttpContext.GetNormalizedRemoteIP());
                break;
            case CspReportVerdict.LimitReached:
                _logger.LogWarning(
                    "CSP report limit reached: {Limit} distinct violations logged in {Window}s; further reports are counted but not logged until the window ends",
                    _cspReportThrottle.Limit,
                    (int)_cspReportThrottle.Window.TotalSeconds);
                break;
        }
    }
}
