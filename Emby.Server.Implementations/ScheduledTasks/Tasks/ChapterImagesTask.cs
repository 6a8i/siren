using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Class ChapterImagesTask.
/// </summary>
public class ChapterImagesTask : IScheduledTask
{
    private readonly ILogger<ChapterImagesTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationPaths _appPaths;
    private readonly IChapterManager _chapterManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalizationManager _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChapterImagesTask" /> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="chapterManager">Instance of the <see cref="IChapterManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    public ChapterImagesTask(
        ILogger<ChapterImagesTask> logger,
        ILibraryManager libraryManager,
        IApplicationPaths appPaths,
        IChapterManager chapterManager,
        IFileSystem fileSystem,
        ILocalizationManager localization)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _chapterManager = chapterManager;
        _fileSystem = fileSystem;
        _localization = localization;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskRefreshChapterImages");

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskRefreshChapterImagesDescription");

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public string Key => "RefreshChapterImages";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(2).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(4).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var videos = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            IsFolder = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false)
            {
                EnableImages = false
            },
            SourceTypes = [SourceType.Library],
            IsVirtualItem = false,
            IncludeOwnedItems = true
        })
        .OfType<Video>()
        .ToList();

        var numComplete = 0;

        var failHistoryPath = Path.Combine(_appPaths.CachePath, "chapter-failures.txt");

        // A set, not a list: the membership check runs once per video and the list made it quadratic.
        var previouslyFailedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(failHistoryPath))
        {
            try
            {
                previouslyFailedImages.UnionWith((await File.ReadAllTextAsync(failHistoryPath, cancellationToken).ConfigureAwait(false))
                    .Split('|', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (IOException)
            {
                // Start with an empty history.
            }
        }

        var directoryService = new DirectoryService(_fileSystem);
        var failHistoryChanged = false;

        try
        {
            foreach (var video in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var key = video.Path + video.DateModified.Ticks;

                var extract = !previouslyFailedImages.Contains(key);

                try
                {
                    var chapters = _chapterManager.GetChapters(video.Id);

                    var success = await _chapterManager.RefreshChapterImages(video, directoryService, chapters, extract, true, cancellationToken).ConfigureAwait(false);

                    if (!success && previouslyFailedImages.Add(key))
                    {
                        failHistoryChanged = true;
                    }

                    numComplete++;
                    double percent = numComplete;
                    percent /= videos.Count;

                    progress.Report(100 * percent);
                }
                catch (ObjectDisposedException ex)
                {
                    // TODO Investigate and properly fix.
                    _logger.LogError(ex, "Object Disposed");
                    break;
                }
            }
        }
        finally
        {
            // Write the failure history once at the end (also on cancellation) instead of rewriting the
            // whole file after every new failure.
            if (failHistoryChanged)
            {
                await SaveFailHistoryAsync(failHistoryPath, previouslyFailedImages).ConfigureAwait(false);
            }
        }
    }

    private async Task SaveFailHistoryAsync(string failHistoryPath, IEnumerable<string> failedImages)
    {
        try
        {
            var parentPath = Path.GetDirectoryName(failHistoryPath);
            if (parentPath is not null)
            {
                Directory.CreateDirectory(parentPath);
            }

            await File.WriteAllTextAsync(failHistoryPath, string.Join('|', failedImages), CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Unable to save the chapter image failure history to {Path}", failHistoryPath);
        }
    }
}
