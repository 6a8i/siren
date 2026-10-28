using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.SystemBackupService;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.ScheduledTasks;

public sealed class BackupTaskTests : IDisposable
{
    private const string ManualBackup = "jellyfin-backup-20260101120000.zip";
    private const string StrayFile = BackupTask.ScheduledBackupPrefix + "notabackup.zip";

    private readonly string _backupPath;
    private readonly Mock<IBackupService> _backupService = new();
    private readonly Mock<IServerApplicationHost> _applicationHost = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();

    public BackupTaskTests()
    {
        // Under the test output rather than the system temp directory, like BackupServiceTests.
        _backupPath = Path.Combine(AppContext.BaseDirectory, "jellyfin-backup-task-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupPath);

        _applicationHost.SetupGet(h => h.RestoreBackupPath).Returns((string)null!);
        _libraryManager.SetupGet(l => l.IsScanRunning).Returns(false);

        _backupService
            .Setup(s => s.CreateBackupAsync(It.IsAny<BackupOptionsDto>()))
            .Returns(async (BackupOptionsDto options) =>
            {
                var created = new DateTimeOffset(new DateTime(2026, 2, 1, 3, 0, 0, DateTimeKind.Local));
                var path = Path.Combine(_backupPath, "jellyfin-backup-20260201030000.zip");
                await File.WriteAllTextAsync(path, "new", TestContext.Current.CancellationToken);
                return CreateManifest(path, created, options);
            });

        // The manifest of an archive is derived from the timestamp in its name; anything else is not a backup.
        _backupService
            .Setup(s => s.GetBackupManifest(It.IsAny<string>()))
            .Returns((string path) => Task.FromResult(ManifestFromFileName(path)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_backupPath))
        {
            Directory.Delete(_backupPath, true);
        }
    }

    [Fact]
    public void DefaultTrigger_IsWeeklyOnSundayAtThree()
    {
        var trigger = Assert.Single(CreateTask().GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.WeeklyTrigger, trigger.Type);
        Assert.Equal(DayOfWeek.Sunday, trigger.DayOfWeek);
        Assert.Equal(TimeSpan.FromHours(3).Ticks, trigger.TimeOfDayTicks);
    }

    [Fact]
    public async Task Execute_RestorePending_DoesNotBackUp()
    {
        _applicationHost.SetupGet(h => h.RestoreBackupPath).Returns(Path.Combine(_backupPath, ManualBackup));

        await CreateTask().ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        _backupService.Verify(s => s.CreateBackupAsync(It.IsAny<BackupOptionsDto>()), Times.Never);
    }

    [Fact]
    public async Task Execute_ScanRunning_DoesNotBackUp()
    {
        _libraryManager.SetupGet(l => l.IsScanRunning).Returns(true);

        await CreateTask().ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        _backupService.Verify(s => s.CreateBackupAsync(It.IsAny<BackupOptionsDto>()), Times.Never);
    }

    [Fact]
    public async Task Execute_BacksUpDatabaseAndConfigurationOnly()
    {
        await CreateTask().ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        _backupService.Verify(
            s => s.CreateBackupAsync(It.Is<BackupOptionsDto>(o => o.Database && !o.Metadata && !o.Trickplay && !o.Subtitles)),
            Times.Once);
    }

    [Fact]
    public async Task Execute_MarksTheArchiveAsScheduledAndKeepsOnlyTheNewest()
    {
        // Six older scheduled backups, one manual backup and a stray file carrying the prefix.
        var cancellationToken = TestContext.Current.CancellationToken;
        for (var day = 1; day <= 6; day++)
        {
            await File.WriteAllTextAsync(Path.Combine(_backupPath, ScheduledName(day)), "old", cancellationToken);
        }

        await File.WriteAllTextAsync(Path.Combine(_backupPath, ManualBackup), "manual", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_backupPath, StrayFile), "stray", cancellationToken);

        var progress = new Mock<IProgress<double>>();
        await CreateTask().ExecuteAsync(progress.Object, TestContext.Current.CancellationToken);

        var remaining = Directory.GetFiles(_backupPath).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                ManualBackup,
                ScheduledName(3),
                ScheduledName(4),
                ScheduledName(5),
                ScheduledName(6),
                BackupTask.ScheduledBackupPrefix + "20260201030000.zip",
                StrayFile
            }.Order(StringComparer.Ordinal).ToArray(),
            remaining);

        Assert.Equal(BackupTask.BackupsToKeep, remaining.Count(f => f!.StartsWith(BackupTask.ScheduledBackupPrefix, StringComparison.Ordinal) && !string.Equals(f, StrayFile, StringComparison.Ordinal)));
        progress.Verify(p => p.Report(100), Times.Once);
    }

    private static string ScheduledName(int day)
        => string.Create(CultureInfo.InvariantCulture, $"{BackupTask.ScheduledBackupPrefix}202601{day:00}030000.zip");

    private static BackupManifestDto? ManifestFromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var timestamp = name[(name.LastIndexOf('-') + 1)..];
        if (!DateTime.TryParseExact(timestamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var created))
        {
            return null;
        }

        return CreateManifest(path, new DateTimeOffset(created), new BackupOptionsDto());
    }

    private static BackupManifestDto CreateManifest(string path, DateTimeOffset created, BackupOptionsDto options)
    {
        return new BackupManifestDto
        {
            Path = path,
            DateCreated = created,
            ServerVersion = new Version(10, 11, 0),
            BackupEngineVersion = new Version(0, 2, 0),
            Options = options
        };
    }

    private BackupTask CreateTask()
    {
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.SetupGet(p => p.BackupPath).Returns(_backupPath);

        var localization = new Mock<ILocalizationManager>();
        localization.Setup(l => l.GetLocalizedString(It.IsAny<string>())).Returns((string key) => key);

        return new BackupTask(
            NullLogger<BackupTask>.Instance,
            localization.Object,
            _backupService.Object,
            _applicationHost.Object,
            applicationPaths.Object,
            _libraryManager.Object);
    }
}
