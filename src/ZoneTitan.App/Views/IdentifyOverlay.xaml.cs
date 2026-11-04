using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ZoneTitan.Core.Display;
using ZoneTitan.Core.Zones;

namespace ZoneTitan.App.Views;

/// <summary>
/// Flashes each monitor's alias full-screen so the user can tell which physical panel is
/// which (spec §10). Borderless, topmost, one per display, gone in about 1.5 seconds.
/// </summary>
public partial class IdentifyOverlay : Window
{
    private IdentifyOverlay()
    {
        InitializeComponent();
    }

    public static void ShowForAllMonitors(ZoneTitanConfig config, int milliseconds = 1500)
    {
        var monitors = MonitorEnumerator.Enumerate().Where(m => m.IsActive).ToList();
        var overlays = new List<IdentifyOverlay>();

        foreach (var monitor in monitors)
        {
            if (monitor.Position is not { } position || monitor.CurrentMode is not { } mode)
                continue;

            var overlay = new IdentifyOverlay
            {
                AliasText = { Text = config.AliasForDevicePath(monitor.DevicePath) },
                DetailText = { Text = $"{monitor.FriendlyName}   ·   {mode}" },
            };

            // Device pixels → DIPs. Without this the overlay lands in the wrong place on a
            // desk with mixed scaling.
            var scale = GetScaleFor(overlay);

            overlay.Left = position.X / scale;
            overlay.Top = position.Y / scale;
            overlay.Width = mode.Width / scale;
            overlay.Height = mode.Height / scale;

            // Primary gets the live accent; the rest stay quiet so the primary is obvious.
            if (!monitor.IsPrimary)
            {
                var dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA8));
                overlay.AliasText.Foreground = dim;
            }

            overlay.Show();
            overlays.Add(overlay);
        }

        if (overlays.Count == 0)
            return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            foreach (var overlay in overlays)
            {
                try { overlay.Close(); }
                catch (InvalidOperationException) { /* already gone */ }
            }
        };
        timer.Start();
    }

    private static double GetScaleFor(Window window)
    {
        try
        {
            return VisualTreeHelper.GetDpi(window).DpiScaleX;
        }
        catch (Exception)
        {
            return 1.0;
        }
    }
}
