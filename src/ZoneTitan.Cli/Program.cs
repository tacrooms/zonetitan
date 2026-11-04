using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZoneTitan.Core.Diagnostics;
using ZoneTitan.Core.Display;
using ZoneTitan.Core.Zones;

namespace ZoneTitan.Cli;

/// <summary>Console harness for the ZoneTitan engine (M1/M2).</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch (IOException) { /* redirected output; harmless */ }

        if (args.Any(a => a is "-h" or "--help" or "/?"))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return Dispatch(args);
        }
        catch (DisplayQueryException ex)
        {
            Console.Error.WriteLine($"Display query failed: {ex.Message}");
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unexpected failure: {ex}");
            return 1;
        }
    }

    private static int Dispatch(string[] args)
    {
        if (Flag(args, "--failsafe"))
            return RunEngine("Restoring all monitors", ZoneEngine.AllOn());

        if (Flag(args, "--restore-last"))
            return RunEngine("Restoring last known good layout", ZoneEngine.RestoreLastKnownGood());

        if (Flag(args, "--init"))
            return InitConfig(Flag(args, "--force"));

        if (Flag(args, "--zones"))
            return ListZones();

        if (Flag(args, "--modes"))
            return ListModes();

        if (Flag(args, "--rescue-windows"))
        {
            var count = WindowRescuer.RescueOffscreenWindows();
            Console.WriteLine(count == 0
                ? "  No off-screen windows found."
                : $"  Brought {count} window{(count == 1 ? "" : "s")} back into view.");
            return 0;
        }

        if (Flag(args, "--hide-taskbar"))
        {
            var changed = TaskbarController.EnsureAutoHide();
            Console.WriteLine(changed
                ? "  Taskbar auto-hide was off — turned it back on."
                : "  Taskbar auto-hide is already on.");
            return 0;
        }

        if (Flag(args, "--guard-taskbar"))
            return GuardTaskbar();

        var zoneName = Value(args, "--zone");
        if (zoneName is not null)
        {
            var config = ConfigStore.LoadConfig();
            return RunEngine($"Activating zone '{zoneName}'", ZoneEngine.ApplyZone(config, zoneName));
        }

        var monitors = MonitorEnumerator.Enumerate();

        if (Flag(args, "--json"))
            PrintJson(monitors);
        else
            PrintReport(monitors);

        return 0;
    }

    private static bool Flag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                return args[i + 1];

            Console.Error.WriteLine($"{name} needs a value, e.g. {name} game");
            Environment.Exit(2);
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Engine commands
    // ------------------------------------------------------------------

    private static int RunEngine(string headline, ZoneApplyResult result)
    {
        Log.EchoToConsole = false;

        Console.WriteLine();
        Console.WriteLine($"  {headline}");
        Console.WriteLine();

        foreach (var step in result.Steps)
            Console.WriteLine($"    ✓ {step}");

        foreach (var warning in result.Warnings)
            Console.WriteLine($"    ! {warning}");

        Console.WriteLine();

        if (result.Success)
        {
            Console.WriteLine("  Done.");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine($"  FAILED: {result.Error}");
        Console.WriteLine();
        Console.WriteLine("  Recover with:  zonetitan --failsafe");
        Console.WriteLine();
        return 1;
    }

    /// <summary>
    /// Watches for Windows dropping taskbar auto-hide and turns it straight back on.
    /// A stopgap until the tray app (M3) can do this without a console window.
    /// </summary>
    private static int GuardTaskbar()
    {
        Console.WriteLine();
        Console.WriteLine("  Guarding taskbar auto-hide. Press Ctrl+C to stop.");
        Console.WriteLine();

        var stopping = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping = true; };

        var restores = 0;

        // Deliberately slow. Setting taskbar state can make Windows raise a modal
        // "only one auto-hide toolbar per side" dialog, so a tight loop risks a dialog storm.
        while (!stopping)
        {
            if (TaskbarController.EnsureAutoHide())
            {
                restores++;
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] auto-hide had been turned off — restored (#{restores})");

                if (restores >= 3)
                {
                    Console.WriteLine();
                    Console.WriteLine("  Something keeps switching it back off. Stopping rather than");
                    Console.WriteLine("  triggering repeated Windows dialogs.");
                    break;
                }
            }

            for (var i = 0; i < 60 && !stopping; i++)
                Thread.Sleep(500);
        }

        Console.WriteLine();
        Console.WriteLine($"  Stopped. Restored auto-hide {restores} time(s).");
        return 0;
    }

    private static int ListZones()
    {
        if (!ConfigStore.ConfigExists)
        {
            Console.Error.WriteLine($"No config at {ConfigStore.ConfigPath}. Run 'zonetitan --init' first.");
            return 1;
        }

        var config = ConfigStore.LoadConfig();
        var attached = MonitorEnumerator.Enumerate()
            .ToDictionary(m => m.DevicePath, m => m, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine();
        Console.WriteLine($"  Zones defined in {ConfigStore.ConfigPath}");
        Console.WriteLine();

        foreach (var zone in config.Zones)
        {
            var monitors = zone.Monitors.Select(alias =>
            {
                var devicePath = config.DevicePathForAlias(alias);
                var present = devicePath is not null && attached.ContainsKey(devicePath);
                var isPrimary = string.Equals(alias, zone.Primary, StringComparison.OrdinalIgnoreCase);
                return $"{alias}{(isPrimary ? "*" : "")}{(present ? "" : " (MISSING)")}";
            });

            Console.WriteLine($"    {zone.Name,-10} {zone.Hotkey,-14} {string.Join(", ", monitors)}");
        }

        Console.WriteLine();
        Console.WriteLine("    * = primary (placed at 0,0)");
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Shows the top refresh rates each attached monitor offers at its native resolution.
    /// Answers "is this panel capable of the rate I expect, on this cable" — which the CCD
    /// current/preferred mode alone cannot.
    /// </summary>
    private static int ListModes()
    {
        var monitors = MonitorEnumerator.Enumerate();

        Console.WriteLine();
        Console.WriteLine("  Available display modes");
        Console.WriteLine();

        foreach (var monitor in monitors)
        {
            Console.WriteLine($"  {monitor.FriendlyName}  ({monitor.ConnectorType})");

            if (!monitor.IsActive || monitor.GdiName is null)
            {
                Console.WriteLine("      detached — Windows only lists modes for attached displays");
                Console.WriteLine();
                continue;
            }

            var modes = ModeEnumerator.Enumerate(monitor.GdiName);
            if (modes.Count == 0)
            {
                Console.WriteLine("      no modes reported");
                Console.WriteLine();
                continue;
            }

            var current = ModeEnumerator.Current(monitor.GdiName);
            var native = modes.First();

            var atNative = modes
                .Where(m => m.Width == native.Width && m.Height == native.Height)
                .Select(m => m.RefreshHz)
                .Distinct()
                .OrderByDescending(hz => hz)
                .ToList();

            Console.WriteLine($"      native        {native.Width}x{native.Height}");
            Console.WriteLine($"      current       {current?.Width}x{current?.Height} @ {current?.RefreshHz} Hz");
            Console.WriteLine($"      rates at native   {string.Join(", ", atNative.Select(hz => $"{hz} Hz"))}");
            Console.WriteLine($"      max available     {atNative.Max()} Hz");
            Console.WriteLine();
        }

        return 0;
    }

    private static int InitConfig(bool force)
    {
        if (ConfigStore.ConfigExists && !force)
        {
            Console.Error.WriteLine(
                $"Config already exists at {ConfigStore.ConfigPath}.\n" +
                "Pass --force to overwrite it.");
            return 1;
        }

        var monitors = MonitorEnumerator.Enumerate();
        if (monitors.Count == 0)
        {
            Console.Error.WriteLine("No monitors detected — refusing to write a config.");
            return 1;
        }

        var config = ConfigBootstrap.Generate(monitors);
        ConfigStore.SaveConfig(config);

        Console.WriteLine();
        Console.WriteLine($"  Wrote {ConfigStore.ConfigPath}");
        Console.WriteLine();
        Console.WriteLine("  Monitors");
        foreach (var (devicePath, alias) in config.Monitors)
        {
            var detached = monitors.First(m => m.DevicePath == devicePath).IsActive ? "" : "  (detached)";
            Console.WriteLine($"    {alias.Alias,-8} {alias.Friendly}{detached}");
        }

        Console.WriteLine();
        Console.WriteLine("  Zones");
        foreach (var zone in config.Zones)
        {
            Console.WriteLine($"    {zone.Name,-10} {string.Join(", ", zone.Monitors)}   primary: {zone.Primary}");

            foreach (var (alias, mode) in zone.Modes ?? [])
                Console.WriteLine($"               {alias} pinned to {mode}");
        }

        Console.WriteLine();
        Console.WriteLine("  Aliases and primaries are guesses — edit the file if they are wrong.");
        Console.WriteLine();
        return 0;
    }

    // ------------------------------------------------------------------
    // Enumeration report (M1)
    // ------------------------------------------------------------------

    private static void PrintUsage()
    {
        Console.WriteLine("""
            ZoneTitan CLI

              zonetitan                    Print the attached-monitor report
              zonetitan --json             Emit the monitor report as JSON

              zonetitan --init [--force]   Generate config.json from current hardware
              zonetitan --zones            List configured zones
              zonetitan --zone <name>      Activate a zone

              zonetitan --failsafe         Turn every attached monitor back on
              zonetitan --restore-last     Reapply the last known good layout

              zonetitan --rescue-windows   Bring fully off-screen windows back into view

              zonetitan --hide-taskbar     Re-enable taskbar auto-hide once
              zonetitan --guard-taskbar    Keep re-enabling it whenever Windows drops it

              zonetitan --help             Show this message
            """);
    }

    private static void PrintReport(IReadOnlyList<MonitorInfo> monitors)
    {
        Console.WriteLine();
        Console.WriteLine("  ZoneTitan — monitor enumeration");
        Console.WriteLine($"  {DateTime.Now:yyyy-MM-dd HH:mm:ss}   ·   {monitors.Count} attached, "
                          + $"{monitors.Count(m => m.IsActive)} active");
        Console.WriteLine();

        if (monitors.Count == 0)
        {
            Console.WriteLine("  No attached monitors reported. That should be impossible on a running desktop —");
            Console.WriteLine("  treat this as a bug in the enumerator, not a hardware finding.");
            Console.WriteLine();
            return;
        }

        PrintSummaryTable(monitors);
        Console.WriteLine();

        foreach (var (monitor, index) in monitors.Select((m, i) => (m, i + 1)))
            PrintDetail(monitor, index);

        PrintIdentityNote();
    }

    private static void PrintSummaryTable(IReadOnlyList<MonitorInfo> monitors)
    {
        var rows = monitors.Select((m, i) => new[]
        {
            (i + 1).ToString(),
            m.FriendlyName,
            m.ConnectorType,
            m.IsActive ? "active" : "detached",
            m.CurrentMode?.ToString() ?? "—",
            m.Position?.ToString() ?? "—",
            m.IsPrimary ? "yes" : "",
            DescribeHdrShort(m.Hdr),
        }).ToList();

        string[] headers = ["#", "Monitor", "Connector", "State", "Mode", "Position", "Primary", "HDR"];

        var widths = headers
            .Select((h, col) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[col].Length)))
            .ToArray();

        static string Rule(IReadOnlyList<int> w, char left, char mid, char right) =>
            "  " + left + string.Join(mid, w.Select(x => new string('─', x + 2))) + right;

        string Row(IReadOnlyList<string> cells) =>
            "  │ " + string.Join(" │ ", cells.Select((c, i) => c.PadRight(widths[i]))) + " │";

        Console.WriteLine(Rule(widths, '┌', '┬', '┐'));
        Console.WriteLine(Row(headers));
        Console.WriteLine(Rule(widths, '├', '┼', '┤'));
        foreach (var row in rows)
            Console.WriteLine(Row(row));
        Console.WriteLine(Rule(widths, '└', '┴', '┘'));
    }

    private static void PrintDetail(MonitorInfo m, int index)
    {
        var vendor = m.VendorCode is null ? "" : $"   [{m.VendorCode} {m.ProductCode:X4}]";
        Console.WriteLine($"  [{index}] {m.FriendlyName}{vendor}");
        Console.WriteLine($"      {(m.IsActive ? "ACTIVE" : "DETACHED")}"
                          + $"{(m.IsPrimary ? "  ·  PRIMARY (source at origin)" : "")}");
        Console.WriteLine();

        Console.WriteLine("      identity (persisted)");
        Console.WriteLine($"        device path    {m.DevicePath}");
        Console.WriteLine($"        friendly name  {m.FriendlyName}");
        Console.WriteLine($"        connector      {m.ConnectorType}");
        Console.WriteLine();

        Console.WriteLine("      mode");
        Console.WriteLine($"        current        {m.CurrentMode?.ToString() ?? "— (detached)"}");
        Console.WriteLine($"        preferred      {m.PreferredMode?.ToString() ?? "— (unavailable)"}");
        Console.WriteLine($"        position       {m.Position?.ToString() ?? "— (detached)"}");
        Console.WriteLine($"        rotation       {m.Rotation ?? "— (detached)"}");
        Console.WriteLine();

        Console.WriteLine("      hdr");
        Console.WriteLine($"        supported      {Yn(m.Hdr.Supported)}");
        Console.WriteLine($"        enabled        {Yn(m.Hdr.Enabled)}"
                          + (m.Hdr.LimitedByPolicy ? "   (limited by system policy)" : ""));
        Console.WriteLine($"        encoding       {m.Hdr.ColorEncoding} @ {m.Hdr.BitsPerColorChannel} bpc");
        Console.WriteLine($"        via            {m.Hdr.Source}");
        Console.WriteLine();

        Console.WriteLine("      volatile — never persisted as identity");
        Console.WriteLine($"        gdi name       {m.GdiName ?? "—"}");
        Console.WriteLine($"        adapter luid   {m.AdapterLuid}");
        Console.WriteLine($"        source / target ids   {m.SourceId} / {m.TargetId}");
        Console.WriteLine();
    }

    private static void PrintIdentityNote()
    {
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────");
        Console.WriteLine("  Verification: run this again after a reboot. Device paths must be");
        Console.WriteLine("  identical; adapter LUIDs, GDI names and source/target ids may all");
        Console.WriteLine("  change. That difference is exactly why zones key on device paths.");
        Console.WriteLine();
    }

    private static string Yn(bool value) => value ? "yes" : "no";

    private static string DescribeHdrShort(HdrState hdr) =>
        !hdr.Supported ? "—" : hdr.Enabled ? "on" : "off";

    private static void PrintJson(IReadOnlyList<MonitorInfo> monitors)
    {
        var payload = monitors.Select(m => new
        {
            devicePath = m.DevicePath,
            friendlyName = m.FriendlyName,
            vendorCode = m.VendorCode,
            productCode = m.ProductCode,
            isActive = m.IsActive,
            isPrimary = m.IsPrimary,
            connectorType = m.ConnectorType,
            currentMode = Describe(m.CurrentMode),
            preferredMode = Describe(m.PreferredMode),
            position = m.Position is null ? null : new { x = m.Position.X, y = m.Position.Y },
            rotation = m.Rotation,
            hdr = new
            {
                supported = m.Hdr.Supported,
                enabled = m.Hdr.Enabled,
                limitedByPolicy = m.Hdr.LimitedByPolicy,
                colorEncoding = m.Hdr.ColorEncoding,
                bitsPerColorChannel = m.Hdr.BitsPerColorChannel,
                source = m.Hdr.Source,
            },
            volatileDiagnostics = new
            {
                gdiName = m.GdiName,
                adapterLuid = m.AdapterLuid,
                adapterDevicePath = m.AdapterDevicePath,
                sourceId = m.SourceId,
                targetId = m.TargetId,
            },
        });

        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));

        static object? Describe(DisplayMode? mode) => mode is null
            ? null
            : new { width = mode.Width, height = mode.Height, refreshHz = Math.Round(mode.RefreshHz, 3) };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
