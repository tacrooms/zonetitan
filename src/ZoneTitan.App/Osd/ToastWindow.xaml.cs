using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ZoneTitan.Core.Display;

namespace ZoneTitan.App.Osd;

/// <summary>
/// The zone-change toast: a small translucent panel on the new primary display, bordered in
/// the zone's accent, gone in under a second (spec §10).
/// </summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _dismissTimer;

    private ToastWindow()
    {
        InitializeComponent();

        _dismissTimer = new DispatcherTimer();
        _dismissTimer.Tick += (_, _) => BeginFadeOut();
    }

    /// <summary>
    /// Shows a toast on the current primary display. Any toast already on screen is replaced,
    /// so rapid zone switching never stacks panels.
    /// </summary>
    public static void Show(string title, string? detail, Color accent, string glyph = "🎮",
        int visibleMilliseconds = 900)
    {
        if (Application.Current is null)
            return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            _current?.CloseImmediately();

            var toast = new ToastWindow();
            toast.TitleText.Text = title;
            toast.GlyphText.Text = glyph;

            var brush = new SolidColorBrush(accent);
            toast.Root.BorderBrush = brush;
            toast.TitleText.Foreground = brush;

            if (!string.IsNullOrWhiteSpace(detail))
            {
                toast.DetailText.Text = detail;
                toast.DetailText.Visibility = Visibility.Visible;
            }

            _current = toast;
            toast.Show();
            toast.PositionOnPrimary();
            toast.BeginFadeIn();

            toast._dismissTimer.Interval = TimeSpan.FromMilliseconds(visibleMilliseconds);
            toast._dismissTimer.Start();
        });
    }

    /// <summary>
    /// Places the toast near the bottom of whichever monitor is currently primary. Uses the
    /// engine's own view of the topology rather than WPF's, which can lag a topology change.
    /// </summary>
    private void PositionOnPrimary()
    {
        try
        {
            var primary = MonitorEnumerator.Enumerate().FirstOrDefault(m => m.IsPrimary);

            double left, top;

            if (primary?.CurrentMode is { } mode && primary.Position is { } position)
            {
                // Device pixels → WPF DIPs.
                var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                var widthDip = mode.Width / scale;
                var heightDip = mode.Height / scale;

                left = position.X / scale + (widthDip - ActualWidth) / 2;
                top = position.Y / scale + heightDip * 0.82;
            }
            else
            {
                left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
                top = SystemParameters.PrimaryScreenHeight * 0.82;
            }

            Left = left;
            Top = top;
        }
        catch (Exception)
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = SystemParameters.PrimaryScreenHeight * 0.82;
        }
    }

    private void BeginFadeIn()
    {
        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    private void BeginFadeOut()
    {
        _dismissTimer.Stop();

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) => CloseImmediately();
        BeginAnimation(OpacityProperty, fade);
    }

    private void CloseImmediately()
    {
        _dismissTimer.Stop();

        if (ReferenceEquals(_current, this))
            _current = null;

        try { Close(); }
        catch (InvalidOperationException) { /* already closing */ }
    }
}
