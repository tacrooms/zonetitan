using System.Runtime.InteropServices;
using ZoneTitan.Core.Diagnostics;

namespace ZoneTitan.Core.Display;

/// <summary>
/// Keeps the Windows taskbar auto-hidden.
/// <para>
/// Not a display-engine concern on paper, but on an OLED it is burn-in defence: a taskbar
/// pinned to the same row of pixels for hours is exactly the static element that damages the
/// panel. Windows 11 drops the auto-hide flag on sleep/wake and display changes, so a zone
/// switch is a natural moment to re-assert it.
/// </para>
/// <para>
/// Uses <c>SHAppBarMessage</c> rather than editing <c>StuckRects3</c>, because the registry
/// route needs an Explorer restart before it takes effect. This applies immediately.
/// </para>
/// </summary>
public static class TaskbarController
{
    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABM_SETSTATE = 0x0000000A;

    private const int ABS_AUTOHIDE = 0x01;
    private const int ABS_ALWAYSONTOP = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern nint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    /// <summary>True when the taskbar is currently set to auto-hide.</summary>
    public static bool IsAutoHideEnabled()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        return ((int)SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0;
    }

    /// <summary>
    /// Turns taskbar auto-hide on if it is off. Returns true when a change was made.
    /// </summary>
    public static bool EnsureAutoHide()
    {
        if (IsAutoHideEnabled())
            return false;

        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == nint.Zero)
        {
            Log.Warn("Could not find the taskbar window; auto-hide not re-asserted.");
            return false;
        }

        var data = new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = taskbar,
            lParam = ABS_AUTOHIDE | ABS_ALWAYSONTOP,
        };

        SHAppBarMessage(ABM_SETSTATE, ref data);

        var nowHidden = IsAutoHideEnabled();
        if (nowHidden)
            Log.Info("Taskbar auto-hide was off; re-enabled it.");
        else
            Log.Warn("Attempted to re-enable taskbar auto-hide but it did not stick.");

        return nowHidden;
    }
}
