using System.Windows.Threading;
using ZoneTitan.Core.Diagnostics;
using ZoneTitan.Core.Display;

namespace ZoneTitan.App.Services;

/// <summary>
/// Keeps taskbar auto-hide switched on, conservatively.
/// <para>
/// Deliberately timid, because setting taskbar state is not a free action: Windows can
/// respond with a modal "you can have only one auto-hide toolbar per side" dialog, and a
/// tight polling loop turns that into a dialog storm. So this backs off hard — a slow
/// heartbeat, a cooldown between attempts, and it switches itself off entirely if something
/// on the system keeps fighting it.
/// </para>
/// <para>
/// The underlying problem is a Windows 11 bug where display changes and sleep/wake drop the
/// flag. ZoneTitan can paper over it; it cannot cure it.
/// </para>
/// </summary>
internal sealed class TaskbarGuard : IDisposable
{
    /// <summary>Slow heartbeat. This is a safety net, not the primary mechanism.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>Never re-assert more often than this, whatever asks.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// If we have to restore this many times in one window, something is actively fighting us
    /// and continuing would only produce more dialogs. Stand down and say so.
    /// </summary>
    private const int GiveUpAfterRestores = 3;

    private static readonly TimeSpan FightWindow = TimeSpan.FromMinutes(5);

    private readonly DispatcherTimer _timer;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private readonly List<DateTime> _recentRestores = [];
    private bool _standDown;
    private bool _disposed;

    public int RestoreCount { get; private set; }

    /// <summary>Raised when the guard gives up because something keeps undoing it.</summary>
    public event Action<string>? StoodDown;

    public bool IsEnabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value)
            {
                _standDown = false;
                _recentRestores.Clear();
                Reassert("enabled");
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    public TaskbarGuard()
    {
        _timer = new DispatcherTimer { Interval = HeartbeatInterval };
        _timer.Tick += (_, _) => Reassert("heartbeat");
    }

    /// <summary>
    /// Re-asserts auto-hide if it is off and the cooldown has elapsed. Safe to call from
    /// anywhere — it is rate limited internally.
    /// </summary>
    public void Reassert(string reason)
    {
        if (_standDown || _disposed)
            return;

        var now = DateTime.UtcNow;
        if (now - _lastAttemptUtc < Cooldown)
            return;

        _lastAttemptUtc = now;

        try
        {
            if (TaskbarController.IsAutoHideEnabled())
                return;

            if (!TaskbarController.EnsureAutoHide())
                return;

            RestoreCount++;
            _recentRestores.Add(now);
            _recentRestores.RemoveAll(t => now - t > FightWindow);

            Log.Info($"Taskbar auto-hide restored (#{RestoreCount}, {reason}).");

            if (_recentRestores.Count < GiveUpAfterRestores)
                return;

            _standDown = true;
            _timer.Stop();

            const string message =
                "Something on this system keeps switching taskbar auto-hide back off. " +
                "ZoneTitan has stopped fighting it to avoid repeated Windows dialogs.";

            Log.Warn(message);
            StoodDown?.Invoke(message);
        }
        catch (Exception ex)
        {
            Log.Warn($"Taskbar guard check failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
    }
}
