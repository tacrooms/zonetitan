using System.Runtime.InteropServices;
using ZoneTitan.App.Interop;
using ZoneTitan.Core.Diagnostics;

namespace ZoneTitan.App.Hotkeys;

/// <summary>The outcome of trying to bind one hotkey.</summary>
internal sealed record HotkeyRegistration(string Label, string? Combination, bool Success, string? Problem);

/// <summary>
/// Registers global hotkeys against the message-only window.
/// <para>
/// Failures are reported, never swallowed: a combination already owned by another app must
/// surface at bind time rather than leaving the user pressing a dead key (spec §6).
/// </para>
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly nint _hwnd;
    private readonly Dictionary<int, Action> _actions = [];
    private readonly List<HotkeyRegistration> _registrations = [];
    private int _nextId = 1;
    private bool _disposed;

    public IReadOnlyList<HotkeyRegistration> Registrations => _registrations;

    public HotkeyManager(MessageWindow window)
    {
        _hwnd = window.Handle;
        window.HotkeyPressed += OnHotkeyPressed;
    }

    private void OnHotkeyPressed(int id)
    {
        if (_actions.TryGetValue(id, out var action))
            action();
    }

    /// <summary>Binds a combination. Returns the outcome, including why it failed.</summary>
    public HotkeyRegistration Register(string label, string? combination, Action action)
    {
        var binding = HotkeyBinding.TryParse(combination);

        if (binding is null)
        {
            var registration = new HotkeyRegistration(label, combination, false,
                string.IsNullOrWhiteSpace(combination) ? "no hotkey set" : "could not understand that combination");

            _registrations.Add(registration);
            return registration;
        }

        var id = _nextId++;

        if (!NativeMethods.RegisterHotKey(_hwnd, id, binding.Modifiers, binding.VirtualKey))
        {
            var error = Marshal.GetLastWin32Error();

            // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED. Overwhelmingly the common case.
            var problem = error == 1409
                ? "already in use by another application"
                : $"Windows refused the registration (error {error})";

            Log.Warn($"Hotkey '{combination}' for {label} failed: {problem}");

            var failed = new HotkeyRegistration(label, binding.Display, false, problem);
            _registrations.Add(failed);
            return failed;
        }

        _actions[id] = action;
        Log.Info($"Hotkey '{binding.Display}' bound to {label}.");

        var ok = new HotkeyRegistration(label, binding.Display, true, null);
        _registrations.Add(ok);
        return ok;
    }

    /// <summary>Drops every registration, ready for a fresh bind after a config reload.</summary>
    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
            NativeMethods.UnregisterHotKey(_hwnd, id);

        _actions.Clear();
        _registrations.Clear();
        _nextId = 1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        UnregisterAll();
    }
}
