using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.ScheduledTasks;

public class ExpireInactiveSessionsTaskTests
{
    private static readonly DateTime _now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SelectExpired_PicksOnlyTokensOlderThanTheCutoff()
    {
        var fresh = CreateDevice("fresh", _now.AddDays(-1));
        var onTheEdge = CreateDevice("edge", _now.AddDays(-90).AddMinutes(1));
        var stale = CreateDevice("stale", _now.AddDays(-91));
        var ancient = CreateDevice("ancient", _now.AddYears(-2));

        var expired = ExpireInactiveSessionsTask.SelectExpired([fresh, onTheEdge, stale, ancient], 90, _now);

        Assert.Equal(["stale", "ancient"], expired.Select(d => d.DeviceId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void SelectExpired_DisabledExpiresNothing(int inactiveDays)
    {
        var expired = ExpireInactiveSessionsTask.SelectExpired([CreateDevice("ancient", _now.AddYears(-2))], inactiveDays, _now);

        Assert.Empty(expired);
    }

    [Fact]
    public async Task ExecuteAsync_LogsOutTheExpiredTokensOnly()
    {
        var fresh = CreateDevice("fresh", DateTime.UtcNow.AddDays(-1));
        var stale = CreateDevice("stale", DateTime.UtcNow.AddDays(-91));

        var deviceManager = new Mock<IDeviceManager>();
        deviceManager.Setup(d => d.GetDevices(It.IsAny<DeviceQuery>()))
            .Returns(new QueryResult<Device>([fresh, stale]));
        var sessionManager = new Mock<ISessionManager>();
        var loggedOut = new List<Device>();
        sessionManager.Setup(s => s.Logout(It.IsAny<Device>()))
            .Callback<Device>(loggedOut.Add)
            .Returns(Task.CompletedTask);

        var task = CreateTask(deviceManager.Object, sessionManager.Object, 90);
        await task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        Assert.Equal([stale], loggedOut);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledDoesNotTouchTheDevices()
    {
        var deviceManager = new Mock<IDeviceManager>(MockBehavior.Strict);
        var sessionManager = new Mock<ISessionManager>(MockBehavior.Strict);

        var task = CreateTask(deviceManager.Object, sessionManager.Object, 0);
        await task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        deviceManager.VerifyNoOtherCalls();
        sessionManager.VerifyNoOtherCalls();
    }

    [Fact]
    public void Task_IsAMaintenanceTaskRunDaily()
    {
        var task = CreateTask(Mock.Of<IDeviceManager>(), Mock.Of<ISessionManager>(), 90);

        Assert.Equal("ExpireInactiveSessions", task.Key);
        Assert.Equal("TasksMaintenanceCategory", task.Category);
        var trigger = Assert.Single(task.GetDefaultTriggers());
        Assert.Equal(TimeSpan.FromHours(24).Ticks, trigger.IntervalTicks);
    }

    private static ExpireInactiveSessionsTask CreateTask(IDeviceManager deviceManager, ISessionManager sessionManager, int inactiveDays)
    {
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.SetupGet(c => c.Configuration).Returns(new ServerConfiguration { InactiveSessionDays = inactiveDays });
        var localization = new Mock<ILocalizationManager>();
        localization.Setup(l => l.GetLocalizedString(It.IsAny<string>())).Returns<string>(key => key);

        return new ExpireInactiveSessionsTask(
            NullLogger<ExpireInactiveSessionsTask>.Instance,
            localization.Object,
            configuration.Object,
            deviceManager,
            sessionManager);
    }

    private static Device CreateDevice(string deviceId, DateTime lastActivity)
        => new(Guid.NewGuid(), "App", "1.0", "Device " + deviceId, deviceId) { DateLastActivity = lastActivity };
}
