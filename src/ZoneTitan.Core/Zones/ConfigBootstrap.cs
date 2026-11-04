using ZoneTitan.Core.Display;

namespace ZoneTitan.Core.Zones;

/// <summary>Generates a starting config from the monitors actually attached right now.</summary>
public static class ConfigBootstrap
{
    /// <summary>
    /// Builds a config from live hardware. Aliases and primaries are heuristics — the caller
    /// is expected to show what was chosen so the user can correct it.
    /// </summary>
    public static ZoneTitanConfig Generate(IReadOnlyList<MonitorInfo> monitors)
    {
        var config = new ZoneTitanConfig();

        // An OLED gaming panel is the one thing worth detecting by name: it drives the
        // game zone and it is the display the whole burn-in rationale exists for.
        var oled = monitors.FirstOrDefault(m =>
            m.FriendlyName.Contains("AW", StringComparison.OrdinalIgnoreCase) ||
            m.FriendlyName.Contains("OLED", StringComparison.OrdinalIgnoreCase));

        var others = monitors.Where(m => !ReferenceEquals(m, oled))
            .OrderBy(m => m.Position?.X ?? int.MaxValue)
            .ToList();

        string[] positional = ["Left", "Right", "Third", "Fourth"];

        foreach (var monitor in monitors)
        {
            string alias;
            if (ReferenceEquals(monitor, oled))
            {
                alias = "OLED";
            }
            else
            {
                var index = others.IndexOf(monitor);
                alias = index >= 0 && index < positional.Length
                    ? positional[index]
                    : monitor.FriendlyName.Replace(" ", "");
            }

            config.Monitors[monitor.DevicePath] = new MonitorAlias
            {
                Alias = alias,
                Friendly = monitor.FriendlyName,
            };
        }

        var oledAlias = oled is null ? null : "OLED";

        // Best mode per alias: native resolution at the highest refresh the panel offers.
        // This is what stops a re-attached display coming back at 60 Hz.
        var bestModes = new Dictionary<string, DesiredMode>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in monitors)
            if (BestMode(monitor) is { } best)
                bestModes[config.Monitors[monitor.DevicePath].Alias] = best;

        Dictionary<string, DesiredMode>? ModesFor(IEnumerable<string> aliases)
        {
            var subset = aliases
                .Where(bestModes.ContainsKey)
                .ToDictionary(a => a, a => bestModes[a], StringComparer.OrdinalIgnoreCase);

            return subset.Count > 0 ? subset : null;
        }

        // Capture the arrangement as it stands, so a zone reproduces the desk rather than
        // letting Windows re-order displays by source id when it re-attaches them.
        Dictionary<string, MonitorPlacement>? LayoutFor(IReadOnlyList<string> aliases)
        {
            if (aliases.Count == 0)
                return null;

            var placements = new Dictionary<string, MonitorPlacement>(StringComparer.OrdinalIgnoreCase);

            // Active monitors keep their real positions; detached ones have none, so they get
            // appended to the right of everything else, edge-adjacent.
            var ordered = aliases
                .Select(a => (Alias: a, Monitor: monitors.FirstOrDefault(m =>
                    string.Equals(config.Monitors[m.DevicePath].Alias, a, StringComparison.OrdinalIgnoreCase))))
                .Where(t => t.Monitor is not null)
                .OrderBy(t => t.Monitor!.Position?.X ?? int.MaxValue)
                .ToList();

            var appendCursor = ordered
                .Where(t => t.Monitor!.Position is not null)
                .Select(t => t.Monitor!.Position!.X + (int)(t.Monitor.CurrentMode?.Width ?? 0))
                .DefaultIfEmpty(0)
                .Max();

            foreach (var (alias, monitor) in ordered)
            {
                if (monitor!.Position is { } position)
                {
                    placements[alias] = new MonitorPlacement { X = position.X, Y = position.Y };
                }
                else
                {
                    var width = (int)(monitor.PreferredMode?.Width ?? 1920);
                    placements[alias] = new MonitorPlacement { X = appendCursor, Y = 0 };
                    appendCursor += width;
                }
            }

            return placements.Count > 0 ? placements : null;
        }

        // Studio primary: the highest-resolution side monitor. On a browsing/production
        // pair that is reliably the better panel, and it is where you want the desktop.
        var studioAliases = others.Select(m => config.Monitors[m.DevicePath].Alias).ToList();
        var studioPrimary = others
            .OrderByDescending(m => (long)(m.CurrentMode?.Width ?? m.PreferredMode?.Width ?? 0)
                                    * (m.CurrentMode?.Height ?? m.PreferredMode?.Height ?? 0))
            .Select(m => config.Monitors[m.DevicePath].Alias)
            .FirstOrDefault();

        if (oledAlias is not null)
        {
            config.Zones.Add(new ZoneDefinition
            {
                Name = "game",
                Hotkey = "Ctrl+Alt+G",
                Monitors = [oledAlias],
                Primary = oledAlias,
                Accent = "#FF2E97",
                Hdr = new Dictionary<string, bool> { [oledAlias] = true },
                Modes = ModesFor([oledAlias]),
                Layout = LayoutFor([oledAlias]),
            });
        }

        if (studioAliases.Count > 0)
        {
            config.Zones.Add(new ZoneDefinition
            {
                Name = "studio",
                Hotkey = "Ctrl+Alt+S",
                Monitors = studioAliases,
                Primary = studioPrimary,
                Accent = "#38D4FF",
                Modes = ModesFor(studioAliases),
                Layout = LayoutFor(studioAliases),
            });
        }

        var allAliases = config.Monitors.Values.Select(m => m.Alias).ToList();

        config.Zones.Add(new ZoneDefinition
        {
            Name = "all",
            Hotkey = "Ctrl+Alt+A",
            Monitors = allAliases,
            Primary = oledAlias ?? studioPrimary,
            Accent = "#DCE6FF",
            Modes = ModesFor(allAliases),
            Layout = LayoutFor(allAliases),
        });

        return config;
    }

    /// <summary>
    /// The mode to pin for a monitor: whatever it is running now, or its preferred mode when
    /// detached.
    /// <para>
    /// Deliberately *not* the maximum rate the panel advertises. Highest-available would push
    /// a display into an overclocked mode the user never chose — on this desk that is the
    /// difference between a monitor's rated 165 Hz and its 180 Hz OC bin. Capturing the
    /// current mode records a decision the user already made.
    /// </para>
    /// </summary>
    private static DesiredMode? BestMode(MonitorInfo monitor)
    {
        var mode = monitor.CurrentMode ?? monitor.PreferredMode;

        if (mode is null && monitor is { IsActive: true, GdiName: not null })
        {
            var current = ModeEnumerator.Current(monitor.GdiName);
            if (current is not null)
                return new DesiredMode
                {
                    Width = current.Width,
                    Height = current.Height,
                    RefreshHz = current.RefreshHz,
                };
        }

        if (mode is null)
            return null;

        return new DesiredMode
        {
            Width = mode.Width,
            Height = mode.Height,
            RefreshHz = (uint)Math.Round(mode.RefreshHz),
        };
    }
}
