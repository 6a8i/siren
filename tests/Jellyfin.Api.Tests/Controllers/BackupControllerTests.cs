using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.SystemBackupService;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers
{
    public sealed class BackupControllerTests : IDisposable
    {
        private const string ScheduledArchive = "jellyfin-backup-scheduled-20260201030000.zip";

        private readonly string _backupPath;
        private readonly Mock<IBackupService> _backupService = new();
        private readonly List<ActivityLog> _activityLog = [];

        public BackupControllerTests()
        {
            _backupPath = Path.Combine(AppContext.BaseDirectory, "jellyfin-backup-controller-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_backupPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(_backupPath))
            {
                Directory.Delete(_backupPath, true);
            }
        }

        [Fact]
        public async Task Restore_ScheduledArchiveName_SchedulesTheRestore()
        {
            var archivePath = Path.Combine(_backupPath, ScheduledArchive);
            await File.WriteAllTextAsync(archivePath, "zip", TestContext.Current.CancellationToken);

            var result = CreateController().StartRestoreBackup(new BackupRestoreRequestDto { ArchiveFileName = ScheduledArchive });

            Assert.IsType<NoContentResult>(result);
            _backupService.Verify(s => s.ScheduleRestoreAndRestartServer(archivePath), Times.Once);
        }

        [Fact]
        public void Restore_MissingArchive_ReturnsNotFound()
        {
            var result = CreateController().StartRestoreBackup(new BackupRestoreRequestDto { ArchiveFileName = ScheduledArchive });

            Assert.IsType<NotFoundResult>(result);
            _backupService.Verify(s => s.ScheduleRestoreAndRestartServer(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Restore_PathOutsideTheBackupFolder_IsReducedToItsFileName()
        {
            var archivePath = Path.Combine(_backupPath, ScheduledArchive);
            await File.WriteAllTextAsync(archivePath, "zip", TestContext.Current.CancellationToken);

            var result = CreateController().StartRestoreBackup(new BackupRestoreRequestDto { ArchiveFileName = Path.Combine("..", "..", "etc", ScheduledArchive) });

            Assert.IsType<NoContentResult>(result);
            _backupService.Verify(s => s.ScheduleRestoreAndRestartServer(archivePath), Times.Once);
        }

        [Fact]
        public async Task Create_Success_LogsBackupCreated()
        {
            var archivePath = Path.Combine(_backupPath, "jellyfin-backup-20260201030000.zip");
            await File.WriteAllTextAsync(archivePath, "zip!", TestContext.Current.CancellationToken);
            _backupService
                .Setup(s => s.CreateBackupAsync(It.IsAny<BackupOptionsDto>()))
                .ReturnsAsync(new BackupManifestDto
                {
                    Path = archivePath,
                    DateCreated = DateTimeOffset.UtcNow,
                    ServerVersion = new Version(10, 11, 0),
                    BackupEngineVersion = new Version(0, 2, 0),
                    Options = new BackupOptionsDto()
                });

            var result = await CreateController().CreateBackup(new BackupOptionsDto());

            Assert.IsAssignableFrom<ObjectResult>(result.Result);
            var entry = Assert.Single(_activityLog);
            Assert.Equal(BackupActivityLog.CreatedType, entry.Type);
            Assert.Contains("jellyfin-backup-20260201030000.zip (4 B)", entry.Name, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Create_Failure_LogsBackupFailedAndRethrows()
        {
            _backupService
                .Setup(s => s.CreateBackupAsync(It.IsAny<BackupOptionsDto>()))
                .ThrowsAsync(new InvalidOperationException("scan running"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => CreateController().CreateBackup(new BackupOptionsDto()));

            var entry = Assert.Single(_activityLog);
            Assert.Equal(BackupActivityLog.FailedType, entry.Type);
            Assert.Equal(LogLevel.Error, entry.LogSeverity);
            Assert.Contains("scan running", entry.Name, StringComparison.Ordinal);
        }

        private BackupController CreateController()
        {
            var applicationPaths = new Mock<IApplicationPaths>();
            applicationPaths.SetupGet(p => p.BackupPath).Returns(_backupPath);

            var localization = new Mock<ILocalizationManager>();
            localization.Setup(l => l.GetServerLocalizedString("BackupCreatedWithNameAndSize")).Returns("Backup {0} ({1}) has been created");
            localization.Setup(l => l.GetServerLocalizedString("BackupFailedWithMessage")).Returns("Backup failed: {0}");

            var activityManager = new Mock<IActivityManager>();
            activityManager
                .Setup(a => a.CreateAsync(It.IsAny<ActivityLog>()))
                .Returns((ActivityLog entry) =>
                {
                    _activityLog.Add(entry);
                    return Task.CompletedTask;
                });

            return new BackupController(_backupService.Object, applicationPaths.Object, activityManager.Object, localization.Object);
        }
    }
}
