using System.Runtime.InteropServices;
using System.Text;
using ZoneTitan.Core.Diagnostics;

namespace ZoneTitan.Core.Display;

/// <summary>
/// Pulls windows that have ended up entirely outside the visible desktop back onto it.
/// <para>
/// Spec §2 rules out window position management, and this deliberately is not that: it never
/// tidies, tiles or remembers layouts. It only rescues windows that are <b>100% invisible</b>,
/// where the only alternative is the user hunting for them with keyboard shortcuts. Apps that
/// restore their own saved position — Steam is the usual culprit — reopen on a monitor a zone
/// has since detached, and no Windows setting prevents that.
/// </para>
/// </summary>
public static class WindowRescuer
{
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>
    /// A window has to be this far outside the desktop before it counts as lost. A sliver
    /// hanging off an edge is usually deliberate, and grabbing it would be obnoxious.
    /// </summary>
    private const int VisibleMarginPx = 40;

    /// <summary>
    /// Moves fully off-screen windows onto the visible desktop. Returns how many moved.
    /// </summary>
    public static int RescueOffscreenWindows()
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (width <= 0 || height <= 0)
            return 0;

        var right = left + width;
        var bottom = top + height;
        var rescued = 0;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                try
                {
                    // Minimised windows have meaningless rects and must be left alone,
                    // otherwise restoring them later lands them somewhere arbitrary.
                    if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
                        return true;

                    var title = new StringBuilder(512);
                    if (GetWindowTextW(hWnd, title, title.Capacity) == 0)
                        return true;

                    if (!GetWindowRect(hWnd, out var rect))
                        return true;

                    var windowWidth = rect.Right - rect.Left;
                    var windowHeight = rect.Bottom - rect.Top;

                    if (windowWidth <= 0 || windowHeight <= 0)
                        return true;

                    var offscreen =
                        rect.Right < left + VisibleMarginPx ||
                        rect.Left > right - VisibleMarginPx ||
                        rect.Bottom < top + VisibleMarginPx ||
                        rect.Top > bottom - VisibleMarginPx;

                    if (!offscreen)
                        return true;

                    // Never grow a window past the desktop while rescuing it.
                    var newWidth = Math.Min(windowWidth, width);
                    var newHeight = Math.Min(windowHeight, height);
                    var x = left + (width - newWidth) / 2;
                    var y = top + (height - newHeight) / 2;

                    if (MoveWindow(hWnd, x, y, newWidth, newHeight, true))
                    {
                        rescued++;
                        Log.Info($"Rescued off-screen window '{title}' from ({rect.Left},{rect.Top}) to ({x},{y}).");
                    }
                }
                catch (Exception)
                {
                    // One bad window must not abort the sweep.
                }

                return true;
            }, nint.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn($"Window rescue failed: {ex.Message}");
        }

        return rescued;
    }
}
