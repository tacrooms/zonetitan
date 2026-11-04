using System.Windows;
using ZoneTitan.App.Hotkeys;
using ZoneTitan.App.Input;
using ZoneTitan.App.Interop;
using ZoneTitan.App.Services;
using ZoneTitan.App.Tray;
using ZoneTitan.App.Views;
using ZoneTitan.Core.Diagnostics;

namespace ZoneTitan.App;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private ZoneController? _controller;
    private MessageWindow? _messageWindow;
    private HotkeyManager? _hotkeys;
    private TrayIconHost? _tray;
    private TaskbarGuard? _taskbarGuard;
    private WakeRouter? _wakeRouter;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new SingleInstance();

        // A second invocation is a remote control, not a new app.
        if (!_singleInstance.IsFirstInstance)
        {
            SingleInstance.TryForward(e.Args.Length > 0 ? e.Args : ["--show"]);
            Shutdown();
            return;
        }

        try
        {
            Start(e.Args);
        }
        catch (Exception ex)
        {
            Log.Error($"Startup failed: {ex}");
            MessageBox.Show(
                $"ZoneTitan could not start:\n\n{ex.Message}",
                "ZoneTitan", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void Start(string[] args)
    {
        Log.Info("ZoneTitan starting.");

        _controller = new ZoneController();
        _messageWindow = new MessageWindow();
        _hotkeys = new HotkeyManager(_messageWindow);
        _taskbarGuard = new TaskbarGuard();
        _wakeRouter = new WakeRouter(_controller, _messageWindow);

        _tray = new TrayIconHost(
            _controller,
            ShowMainWindow,
            QuitApplication,
            () => _taskbarGuard.IsEnabled,
            enabled => _taskbarGuard.IsEnabled = enabled);

        _tray.IdentifyRequested += ShowIdentifyOverlays;

        _singleInstance!.CommandReceived += OnCommandReceived;
        _singleInstance.StartServer();

        _controller.ConfigReloaded += BindHotkeys;
        _wakeRouter.Routed += (device, zone) =>
            Log.Info($"Wake routing applied '{zone}' after input from {device}.");

        _taskbarGuard.StoodDown += message =>
            Dispatcher.BeginInvoke(() => _tray?.Notify("Taskbar auto-hide", message, warning: true));

        _taskbarGuard.IsEnabled = _controller.Config.Options.EnforceTaskbarAutoHide;
        SyncAutoStart();

        BindHotkeys();

        if (_controller.ConfigError is { } error)
            _tray.Notify("ZoneTitan config problem", error, warning: true);

        // Launching without --tray means the user ran the exe directly; show the window.
        if (!args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
            HandleCommand(args.Length > 0 ? args : ["--show"]);
    }

    /// <summary>
    /// Make the Run key agree with <c>options.startWithWindows</c>.
    /// </summary>
    /// <remarks>
    /// Config is the source of truth here, exactly as it is for the taskbar guard
    /// above. Without this the option was stored and never applied: the settings
    /// checkbox was populated *from* the registry, and the registry was only ever
    /// written when that checkbox was clicked. So a config carrying
    /// <c>startWithWindows: true</c> - which is the default - registered nothing,
    /// ZoneTitan never started with Windows, and since the global hotkeys are
    /// registered by this process, none of them fired either.
    /// </remarks>
    private void SyncAutoStart()
    {
        var wanted = _controller!.Config.Options.StartWithWindows;
        if (AutoStart.IsEnabled() == wanted)
            return;

        if (AutoStart.SetEnabled(wanted))
            Log.Info($"Auto-start reconciled to config: startWithWindows={wanted}.");
        else
            Log.Warn($"Could not reconcile auto-start to startWithWindows={wanted}.");
    }

    // ------------------------------------------------------------------
    // Hotkeys
    // ------------------------------------------------------------------

    private void BindHotkeys()
    {
        if (_hotkeys is null || _controller is null)
            return;

        Dispatcher.Invoke(() =>
        {
            _hotkeys.UnregisterAll();

            foreach (var zone in _controller.Config.Zones)
            {
                var name = zone.Name;
                _hotkeys.Register(name, zone.Hotkey, () => _controller.ApplyZone(name));
            }

            _hotkeys.Register("failsafe", _controller.Config.Options.FailsafeHotkey,
                () => _controller.RunFailsafe());

            // A combination another app already owns must be reported, never silently dead.
            var failures = _hotkeys.Registrations
                .Where(r => !r.Success && r.Problem != "no hotkey set")
                .ToList();

            if (failures.Count > 0 && _tray is not null)
            {
                var detail = string.Join("\n",
                    failures.Select(f => $"{f.Combination ?? "(unset)"} for {f.Label}: {f.Problem}"));

                _tray.Notify("Some hotkeys could not be registered", detail, warning: true);
            }
        });
    }

    // ------------------------------------------------------------------
    // Commands, local or forwarded from another invocation
    // ------------------------------------------------------------------

    private void OnCommandReceived(string[] args) =>
        Dispatcher.BeginInvoke(() => HandleCommand(args));

    private void HandleCommand(string[] args)
    {
        if (_controller is null)
            return;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i].ToLowerInvariant();

            switch (arg)
            {
                case "--zone" when i + 1 < args.Length:
                    _controller.ApplyZone(args[++i]);
                    break;

                case "--failsafe":
                    _controller.RunFailsafe();
                    break;

                case "--restore-last":
                    Task.Run(() => Core.Zones.ZoneEngine.RestoreLastKnownGood());
                    break;

                case "--identify":
                    ShowIdentifyOverlays();
                    break;

                case "--show":
                    ShowMainWindow();
                    break;

                case "--quit":
                    QuitApplication();
                    break;

                case "--tray":
                    break;

                default:
                    Log.Warn($"Ignoring unrecognised argument '{args[i]}'.");
                    break;
            }
        }
    }

    // ------------------------------------------------------------------
    // Windows
    // ------------------------------------------------------------------

    private void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_controller is null)
                return;

            if (_mainWindow is null || !_mainWindow.IsLoaded)
            {
                _mainWindow = new MainWindow(_controller, _taskbarGuard!, _wakeRouter);
                _mainWindow.Closed += (_, _) => _mainWindow = null;
            }

            _mainWindow.Show();

            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;

            _mainWindow.Activate();
        });
    }

    private void ShowIdentifyOverlays()
    {
        Dispatcher.Invoke(() =>
        {
            if (_controller is not null)
                IdentifyOverlay.ShowForAllMonitors(_controller.Config);
        });
    }

    private void QuitApplication()
    {
        Log.Info("ZoneTitan shutting down.");
        Dispatcher.Invoke(Shutdown);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wakeRouter?.Dispose();
        _tray?.Dispose();
        _taskbarGuard?.Dispose();
        _hotkeys?.Dispose();
        _messageWindow?.Dispose();
        _controller?.Dispose();
        _singleInstance?.Dispose();

        base.OnExit(e);
    }
}
