using System.Windows.Interop;

namespace ZoneTitan.App.Interop;

/// <summary>
/// A message-only window (child of <c>HWND_MESSAGE</c>) that receives <c>WM_HOTKEY</c> and
/// <c>WM_INPUT</c>. It has no presence on screen, in the taskbar, or in Alt-Tab.
/// <para>
/// Note it deliberately does not handle <c>WM_POWERBROADCAST</c>: message-only windows are
/// excluded from broadcast messages, so resume detection uses
/// <c>SystemEvents.PowerModeChanged</c> instead.
/// </para>
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private const int HWND_MESSAGE = -3;

    private readonly HwndSource _source;
    private bool _disposed;

    public nint Handle => _source.Handle;

    /// <summary>Raised on WM_HOTKEY with the hotkey id.</summary>
    public event Action<int>? HotkeyPressed;

    /// <summary>Raised on WM_INPUT with the raw input handle.</summary>
    public event Action<nint>? RawInputReceived;

    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("ZoneTitanMessageWindow")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_HOTKEY:
                HotkeyPressed?.Invoke((int)wParam);
                handled = true;
                break;

            case NativeMethods.WM_INPUT:
                RawInputReceived?.Invoke(lParam);
                break;
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
