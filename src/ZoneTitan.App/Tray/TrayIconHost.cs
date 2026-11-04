using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ZoneTitan.App.Services;
using ZoneTitan.Core;
using ZoneTitan.Core.Diagnostics;
using ZoneTitan.Core.Zones;

namespace ZoneTitan.App.Tray;

/// <summary>
/// The tray icon and its menu — the daily driver alongside hotkeys (spec §10).
/// The badge shows the active zone's initial, tinted with that zone's accent.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint handle);

    private readonly NotifyIcon _notifyIcon;
    private readonly ZoneController _controller;
    private readonly Action _openWindow;
    private readonly Action _quit;
    private readonly Func<bool> _isGuardingTaskbar;
    private readonly Action<bool> _setGuardingTaskbar;

    private bool _disposed;

    public TrayIconHost(
        ZoneController controller,
        Action openWindow,
        Action quit,
        Func<bool> isGuardingTaskbar,
        Action<bool> setGuardingTaskbar)
    {
        _controller = controller;
        _openWindow = openWindow;
        _quit = quit;
        _isGuardingTaskbar = isGuardingTaskbar;
        _setGuardingTaskbar = setGuardingTaskbar;

        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "ZoneTitan",
            ContextMenuStrip = new ContextMenuStrip(),
        };

        _notifyIcon.DoubleClick += (_, _) => _openWindow();
        _notifyIcon.ContextMenuStrip.Opening += (_, _) => RebuildMenu();

        _controller.Changed += Refresh;

        Refresh();
    }

    public void Refresh()
    {
        try
        {
            var zone = _controller.ActiveZone;
            var accent = ZoneController.ColorFor(_controller.Config.FindZone(zone ?? "")?.Accent);

            UpdateIcon(
                zone is { Length: > 0 } ? char.ToUpperInvariant(zone[0]) : '·',
                Color.FromArgb(accent.R, accent.G, accent.B));

            _notifyIcon.Text = zone is null
                ? "ZoneTitan — no zone active"
                : $"ZoneTitan — {zone}";
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray refresh failed: {ex.Message}");
        }
    }

    /// <summary>Draws the badge: a rounded accent square with the zone's initial knocked out.</summary>
    private void UpdateIcon(char initial, Color accent)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);

            using var path = RoundedRect(new Rectangle(1, 1, 30, 30), 8);
            using var fill = new SolidBrush(accent);
            g.FillPath(fill, path);

            using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.FromArgb(10, 12, 18));
            var size = g.MeasureString(initial.ToString(), font);
            g.DrawString(initial.ToString(), font, text,
                (32 - size.Width) / 2, (32 - size.Height) / 2);
        }

        var handle = bitmap.GetHicon();

        try
        {
            using var icon = Icon.FromHandle(handle);
            _notifyIcon.Icon = (Icon)icon.Clone();
        }
        finally
        {
            // GetHicon allocates; without this the app leaks a GDI handle per zone change.
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    private void RebuildMenu()
    {
        var menu = _notifyIcon.ContextMenuStrip!;
        menu.Items.Clear();

        foreach (var zone in _controller.Config.Zones)
        {
            var name = zone.Name;
            var item = new ToolStripMenuItem(BuildZoneLabel(zone.Name, zone.Hotkey))
            {
                Checked = string.Equals(_controller.ActiveZone, name, StringComparison.OrdinalIgnoreCase),
                CheckOnClick = false,
            };

            item.Click += (_, _) => _controller.ApplyZone(name);
            menu.Items.Add(item);
        }

        if (_controller.Config.Zones.Count > 0)
            menu.Items.Add(new ToolStripSeparator());

        var routing = new ToolStripMenuItem("Wake routing")
        {
            Checked = _controller.Config.InputRouting?.Enabled ?? false,
            CheckOnClick = true,
        };
        routing.Click += (_, _) =>
        {
            _controller.Config.InputRouting ??= new InputRoutingOptions();
            _controller.Config.InputRouting.Enabled = routing.Checked;
            _controller.SaveConfig();
        };
        menu.Items.Add(routing);

        var guard = new ToolStripMenuItem("Keep taskbar hidden")
        {
            Checked = _isGuardingTaskbar(),
            CheckOnClick = true,
        };
        guard.Click += (_, _) => _setGuardingTaskbar(guard.Checked);
        menu.Items.Add(guard);

        menu.Items.Add(new ToolStripSeparator());

        var failsafe = new ToolStripMenuItem("All monitors on   (failsafe)");
        failsafe.Click += (_, _) => _controller.RunFailsafe();
        menu.Items.Add(failsafe);

        var identify = new ToolStripMenuItem("Identify displays");
        identify.Click += (_, _) => IdentifyRequested?.Invoke();
        menu.Items.Add(identify);

        // Apps that restore their own saved window position reopen on monitors a zone has
        // detached, so this is needed on demand, not only when a zone is applied.
        var rescue = new ToolStripMenuItem("Bring lost windows back");
        rescue.Click += (_, _) =>
        {
            var count = ZoneTitan.Core.Display.WindowRescuer.RescueOffscreenWindows();
            Notify("ZoneTitan", count == 0
                ? "No off-screen windows found."
                : $"Brought {count} window{(count == 1 ? "" : "s")} back into view.");
        };
        menu.Items.Add(rescue);

        menu.Items.Add(new ToolStripSeparator());

        var open = new ToolStripMenuItem("Open ZoneTitan");
        open.Click += (_, _) => _openWindow();
        menu.Items.Add(open);

        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => _quit();
        menu.Items.Add(quit);
    }

    public event Action? IdentifyRequested;

    private string BuildZoneLabel(string name, string? hotkey)
    {
        var missing = _controller.Config.FindZone(name)?.Monitors
            .Any(alias => _controller.Config.DevicePathForAlias(alias) is null) ?? false;

        var label = missing ? $"{name}  (monitor missing)" : name;
        return string.IsNullOrWhiteSpace(hotkey) ? label : $"{label}\t{hotkey}";
    }

    /// <summary>Balloon notification, used for problems the user must see (spec §10).</summary>
    public void Notify(string title, string message, bool warning = false)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            Log.Warn($"Balloon notification failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _controller.Changed -= Refresh;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
