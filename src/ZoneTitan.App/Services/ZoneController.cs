using System.IO;
using System.Windows;
using System.Windows.Media;
using ZoneTitan.App.Osd;
using ZoneTitan.Core.Diagnostics;
using ZoneTitan.Core.Display;
using ZoneTitan.Core.Zones;

namespace ZoneTitan.App.Services;

/// <summary>
/// The single place zone changes flow through, whatever triggered them — hotkey, tray, wake
/// routing, CLI, or the config window. Owns the active-zone state, the accent swap, and the
/// toast, so every path produces identical behaviour.
/// </summary>
internal sealed class ZoneController : IDisposable
{
    private readonly object _gate = new();
    private FileSystemWatcher? _configWatcher;
    private DateTime _lastConfigEventUtc = DateTime.MinValue;
    private DateTime _suppressReloadUntilUtc = DateTime.MinValue;
    private bool _disposed;

    public ZoneTitanConfig Config { get; private set; } = new();

    /// <summary>Name of the zone currently believed active, or null if the layout matches none.</summary>
    public string? ActiveZone { get; private set; }

    public string? ConfigError { get; private set; }

    public event Action? Changed;

    public ZoneController()
    {
        LoadConfig();
        DetectActiveZone();
        StartWatchingConfig();
    }

    // ------------------------------------------------------------------
    // Config
    // ------------------------------------------------------------------

    public void LoadConfig()
    {
        lock (_gate)
        {
            try
            {
                if (!ConfigStore.ConfigExists)
                {
                    // First run: derive a config from whatever is plugged in right now, so the
                    // app is usable immediately rather than presenting an empty shell.
                    var generated = ConfigBootstrap.Generate(MonitorEnumerator.Enumerate());
                    ConfigStore.SaveConfig(generated);
                    Log.Info("No config found; generated one from current hardware.");
                }

                Config = ConfigStore.LoadConfig();
                ConfigError = null;
            }
            catch (Exception ex)
            {
                ConfigError = ex.Message;
                Log.Error($"Config load failed: {ex.Message}");
            }
        }
    }

    public void SaveConfig()
    {
        lock (_gate)
        {
            try
            {
                // Ignore the file-changed event our own write is about to raise. Without this
                // the watcher reloads config.json into a *new* object moments after every
                // edit, and any UI control still holding the old one writes into an orphan —
                // which looks exactly like "saving a second rule deletes the first".
                _suppressReloadUntilUtc = DateTime.UtcNow.AddSeconds(2);

                ConfigStore.SaveConfig(Config);
                Log.Info("Config saved.");
            }
            catch (Exception ex)
            {
                Log.Error($"Config save failed: {ex.Message}");
            }
        }
    }

    /// <summary>Config is human-editable and hot-reloaded, which re-registers hotkeys (spec §8).</summary>
    private void StartWatchingConfig()
    {
        try
        {
            ConfigStore.EnsureDirectories();

            _configWatcher = new FileSystemWatcher(ConfigStore.RootDirectory, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            _configWatcher.Changed += OnConfigFileChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not watch config for changes: {ex.Message}");
        }
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        // Our own save, not a human editing the file.
        if (DateTime.UtcNow < _suppressReloadUntilUtc)
            return;

        // Editors routinely emit several write events for one save.
        var now = DateTime.UtcNow;
        if (now - _lastConfigEventUtc < TimeSpan.FromMilliseconds(700))
            return;

        _lastConfigEventUtc = now;
        Thread.Sleep(200);

        LoadConfig();
        ConfigReloaded?.Invoke();
        RaiseChanged();
    }

    public event Action? ConfigReloaded;

    // ------------------------------------------------------------------
    // Applying zones
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies a zone off the UI thread and reports the outcome through the OSD.
    /// Never throws — a failed apply is a toast, not a crash.
    /// </summary>
    public void ApplyZone(string zoneName, bool showToast = true)
    {
        Task.Run(() =>
        {
            ZoneApplyResult result;

            try
            {
                result = ZoneEngine.ApplyZone(Config, zoneName);
            }
            catch (Exception ex)
            {
                Log.Error($"ApplyZone('{zoneName}') threw: {ex}");
                result = ZoneApplyResult.Fail(zoneName, ex.Message);
            }

            if (result.Success)
            {
                ActiveZone = zoneName;
                ApplyAccent(zoneName);

                if (showToast && Config.Options.OsdToast)
                    ShowZoneToast(zoneName);
            }
            else
            {
                Log.Warn($"Zone '{zoneName}' failed: {result.Error}");

                if (showToast)
                {
                    ToastWindow.Show(zoneName, result.Error, ColorFor("#FF5470"), "⚠", 3200);
                }
            }

            RaiseChanged();
        });
    }

    public void RunFailsafe()
    {
        Task.Run(() =>
        {
            var result = ZoneEngine.AllOn();
            ActiveZone = null;
            DetectActiveZone();

            ToastWindow.Show(
                result.Success ? "all monitors on" : "failsafe failed",
                result.Success ? null : result.Error,
                ColorFor(result.Success ? "#DCE6FF" : "#FF5470"),
                result.Success ? "🖥" : "⚠",
                result.Success ? 1200 : 3200);

            RaiseChanged();
        });
    }

    private void ShowZoneToast(string zoneName)
    {
        var zone = Config.FindZone(zoneName);
        var glyph = zoneName.ToLowerInvariant() switch
        {
            "game" => "🎮",
            "studio" => "🎛",
            "all" => "🖥",
            _ => "⬛",
        };

        ToastWindow.Show(zoneName, null, ColorFor(zone?.Accent), glyph);
    }

    // ------------------------------------------------------------------
    // Accent — one resource swap, and the whole app takes the cue (spec §9)
    // ------------------------------------------------------------------

    public void ApplyAccent(string? zoneName)
    {
        var accent = ColorFor(Config.FindZone(zoneName ?? "")?.Accent);

        Application.Current?.Dispatcher.Invoke(() =>
        {
            Application.Current.Resources["AccentBrush"] = new SolidColorBrush(accent);
            Application.Current.Resources["AccentColor"] = accent;
        });
    }

    public static Color ColorFor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return (Color)ColorConverter.ConvertFromString("#DCE6FF")!;

        try
        {
            return (Color)ColorConverter.ConvertFromString(hex)!;
        }
        catch (FormatException)
        {
            return (Color)ColorConverter.ConvertFromString("#DCE6FF")!;
        }
    }

    // ------------------------------------------------------------------
    // Active-zone detection
    // ------------------------------------------------------------------

    /// <summary>
    /// Works out which zone the current layout corresponds to, so the tray shows the right
    /// state at startup rather than claiming nothing is active.
    /// </summary>
    public void DetectActiveZone()
    {
        try
        {
            var active = MonitorEnumerator.Enumerate()
                .Where(m => m.IsActive)
                .Select(m => m.DevicePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var zone in Config.Zones)
            {
                var paths = zone.Monitors
                    .Select(Config.DevicePathForAlias)
                    .Where(p => p is not null)
                    .Select(p => p!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (paths.Count > 0 && paths.SetEquals(active))
                {
                    ActiveZone = zone.Name;
                    ApplyAccent(zone.Name);
                    return;
                }
            }

            ActiveZone = null;
            ApplyAccent(null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Active-zone detection failed: {ex.Message}");
        }
    }

    private void RaiseChanged() =>
        Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_configWatcher is not null)
        {
            _configWatcher.Changed -= OnConfigFileChanged;
            _configWatcher.Dispose();
        }
    }
}
