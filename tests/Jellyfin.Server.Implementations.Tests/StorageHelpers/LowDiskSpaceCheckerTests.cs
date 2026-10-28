using System;
using System.Linq;
using Jellyfin.Server.Implementations.StorageHelpers;
using MediaBrowser.Model.System;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.StorageHelpers;

public class LowDiskSpaceCheckerTests
{
    private static readonly DateTime _now = new(2026, 2, 1, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_DriveBelowThreshold_WarnsOnceNamingEveryPathOnIt()
    {
        var checker = new LowDiskSpaceChecker();

        var warnings = checker.Evaluate(
            [
                Folder("/data", "/dev/sda1", 2 * LowDiskSpaceChecker.BytesPerGb),
                Folder("/cache", "/dev/sda1", 2 * LowDiskSpaceChecker.BytesPerGb),
                Folder("/logs", "/dev/sdb1", 50 * LowDiskSpaceChecker.BytesPerGb)
            ],
            5,
            _now);

        var warning = Assert.Single(warnings);
        Assert.Equal("/dev/sda1", warning.DeviceId);
        Assert.Equal(["/data", "/cache"], warning.Paths);
        Assert.Equal(2.0, warning.FreeGb);
        Assert.Equal(5, warning.ThresholdGb);
    }

    [Fact]
    public void Evaluate_AboveThreshold_IsQuiet()
    {
        var checker = new LowDiskSpaceChecker();

        Assert.Empty(checker.Evaluate([Folder("/data", "/dev/sda1", 5 * LowDiskSpaceChecker.BytesPerGb)], 5, _now));
    }

    [Fact]
    public void Evaluate_ThresholdZero_IsDisabled()
    {
        var checker = new LowDiskSpaceChecker();

        Assert.Empty(checker.Evaluate([Folder("/data", "/dev/sda1", 0)], 0, _now));
    }

    [Fact]
    public void Evaluate_UnmeasuredFolder_IsIgnored()
    {
        var checker = new LowDiskSpaceChecker();

        Assert.Empty(checker.Evaluate(
            [
                Folder("/data", null, -1),
                Folder("/cache", "/dev/sda1", -1)
            ],
            5,
            _now));
    }

    [Fact]
    public void Evaluate_SameDriveWithinADay_WarnsOnlyOnce()
    {
        var checker = new LowDiskSpaceChecker();
        var low = new[] { Folder("/data", "/dev/sda1", LowDiskSpaceChecker.BytesPerGb) };

        Assert.Single(checker.Evaluate(low, 5, _now));
        Assert.Empty(checker.Evaluate(low, 5, _now.AddHours(1)));
        Assert.Empty(checker.Evaluate(low, 5, _now.AddHours(23)));
        Assert.Single(checker.Evaluate(low, 5, _now.AddDays(1)));
    }

    [Fact]
    public void Evaluate_DriveRecoversThenDropsAgain_WarnsRightAway()
    {
        var checker = new LowDiskSpaceChecker();
        var low = new[] { Folder("/data", "/dev/sda1", LowDiskSpaceChecker.BytesPerGb) };
        var fine = new[] { Folder("/data", "/dev/sda1", 20 * LowDiskSpaceChecker.BytesPerGb) };

        Assert.Single(checker.Evaluate(low, 5, _now));
        Assert.Empty(checker.Evaluate(fine, 5, _now.AddHours(1)));
        Assert.Single(checker.Evaluate(low, 5, _now.AddHours(2)));
    }

    [Fact]
    public void Evaluate_EachDriveHasItsOwnClock()
    {
        var checker = new LowDiskSpaceChecker();

        Assert.Single(checker.Evaluate([Folder("/data", "/dev/sda1", 0)], 5, _now));

        var second = checker.Evaluate(
            [
                Folder("/data", "/dev/sda1", 0),
                Folder("/logs", "/dev/sdb1", 0)
            ],
            5,
            _now.AddHours(1));

        Assert.Equal("/dev/sdb1", Assert.Single(second).DeviceId);
    }

    private static FolderStorageInfo Folder(string path, string? deviceId, long freeSpace)
        => new()
        {
            Path = path,
            ResolvedPath = path,
            FreeSpace = freeSpace,
            UsedSpace = 0,
            StorageType = "Fixed",
            DeviceId = deviceId
        };
}
