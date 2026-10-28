using System;
using System.Linq;
using System.Threading;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Triggers;

/// <summary>
/// Represents a task trigger that runs repeatedly on an interval.
/// </summary>
public sealed class IntervalTrigger : ITaskTrigger, IDisposable
{
    private readonly TimeSpan _interval;
    private DateTime _lastStartDate;
    private Timer? _timer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntervalTrigger"/> class.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <param name="taskOptions">The options of this task.</param>
    public IntervalTrigger(TimeSpan interval, TaskOptions taskOptions)
    {
        _interval = interval;
        TaskOptions = taskOptions;
    }

    /// <inheritdoc />
    public event EventHandler<EventArgs>? Triggered;

    /// <inheritdoc />
    public TaskOptions TaskOptions { get; }

    /// <inheritdoc />
    public void Start(TaskResult? lastResult, ILogger logger, string taskName, bool isApplicationStartup)
    {
        DisposeTimer();

        DateTime now = DateTime.UtcNow;
        DateTime triggerDate;

        if (lastResult is null)
        {
            // Task has never been completed before. Spread the first runs over the following hour with a
            // per-task offset, otherwise every interval task on a fresh install (library scan, database
            // optimization, media segment extraction, loudness scan, ...) fires at the same minute.
            triggerDate = now.AddHours(1).AddMinutes(GetFirstRunJitterMinutes(taskName));
        }
        else
        {
            triggerDate = new[] { lastResult.EndTimeUtc, _lastStartDate, now.AddMinutes(1) }.Max().Add(_interval);
        }

        var dueTime = triggerDate - now;
        var maxDueTime = TimeSpan.FromDays(7);

        if (dueTime > maxDueTime)
        {
            dueTime = maxDueTime;
        }

        _timer = new Timer(_ => OnTriggered(), null, dueTime, TimeSpan.FromMilliseconds(-1));
    }

    /// <inheritdoc />
    public void Stop()
    {
        DisposeTimer();
    }

    /// <summary>
    /// Deterministic 0-59 minute offset derived from the task name, so the same task always lands on the same
    /// minute and different tasks spread out. string.GetHashCode is randomized per process, hence the manual hash.
    /// </summary>
    private static int GetFirstRunJitterMinutes(string taskName)
    {
        var hash = 17;
        foreach (var c in taskName)
        {
            hash = unchecked((hash * 31) + c);
        }

        return (int)((uint)hash % 60);
    }

    /// <summary>
    /// Disposes the timer.
    /// </summary>
    private void DisposeTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Called when [triggered].
    /// </summary>
    private void OnTriggered()
    {
        DisposeTimer();

        if (Triggered is not null)
        {
            _lastStartDate = DateTime.UtcNow;
            Triggered(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DisposeTimer();

        _disposed = true;
    }
}
