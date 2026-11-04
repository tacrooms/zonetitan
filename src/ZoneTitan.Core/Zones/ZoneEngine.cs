using ZoneTitan.Core.Diagnostics;
using ZoneTitan.Core.Display;
using static ZoneTitan.Core.Native.CcdNative;

namespace ZoneTitan.Core.Zones;

public sealed class ZoneApplyResult
{
    public required bool Success { get; set; }
    public required string Operation { get; init; }
    public string? Error { get; set; }
    public List<string> Steps { get; } = [];
    public List<string> Warnings { get; } = [];

    public static ZoneApplyResult Fail(string operation, string error) =>
        new() { Success = false, Operation = operation, Error = error };
}

/// <summary>
/// Applies zones to the Windows desktop topology.
/// <para>
/// Two hard invariants, enforced independently of any UI: an apply may never leave zero
/// active displays, and a zone referencing a monitor that is not attached is refused
/// outright rather than applied partially (spec §4).
/// </para>
/// </summary>
public static class ZoneEngine
{
    private static readonly object Gate = new();
    private static DateTime _lastApplyUtc = DateTime.MinValue;
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    private const SetDisplayConfigFlags TopologyFlags =
        SetDisplayConfigFlags.SDC_APPLY |
        SetDisplayConfigFlags.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
        SetDisplayConfigFlags.SDC_ALLOW_CHANGES |
        SetDisplayConfigFlags.SDC_SAVE_TO_DATABASE;

    private const SetDisplayConfigFlags LayoutFlags =
        SetDisplayConfigFlags.SDC_APPLY |
        SetDisplayConfigFlags.SDC_USE_SUPPLIED_DISPLAY_CONFIG |
        SetDisplayConfigFlags.SDC_SAVE_TO_DATABASE;

    // ------------------------------------------------------------------
    // Failsafe — deliberately the simplest code path in the engine.
    // No config parsing, no snapshots, no aliases. Three black screens is
    // the nightmare failure mode and this is the escape hatch (spec §4).
    // ------------------------------------------------------------------

    /// <summary>Activates every attached monitor, extended, first monitor primary.</summary>
    public static ZoneApplyResult AllOn()
    {
        lock (Gate)
        {
            var result = new ZoneApplyResult { Success = true, Operation = "failsafe" };

            if (!AllOnUnlocked(result))
            {
                result.Success = false;
                result.Error = "could not restore displays — see the log";
            }

            return result;
        }
    }

    /// <summary>The failsafe body. Assumes the caller already holds <see cref="Gate"/>.</summary>
    private static bool AllOnUnlocked(ZoneApplyResult result)
    {
        try
        {
            var topology = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ALL_PATHS);
            var attached = DisplayTopology.AttachedTargets(topology);
            result.Steps.Add($"found {attached.Count} attached monitor(s)");

            if (attached.Count > 0 &&
                TrySelectPaths(topology, attached.Select(a => a.DevicePath).ToList(), out var chosen, out _))
            {
                var paths = BuildTopologyOnlyPaths(topology, chosen);
                var code = SetDisplayConfig((uint)paths.Length, paths, 0, null,
                    TopologyFlags | SetDisplayConfigFlags.SDC_FORCE_MODE_ENUMERATION);

                if (code == ERROR_SUCCESS)
                {
                    result.Steps.Add("activated all attached monitors");
                    NormalisePrimary(null, result);
                    Log.Info("Failsafe AllOn succeeded via explicit path activation.");
                    return true;
                }

                result.Warnings.Add($"explicit activation failed: {DescribeError(code)}");
                Log.Warn($"Failsafe explicit activation failed: {DescribeError(code)}");
            }

            // Last resort: Windows' own built-in extend topology. Needs no arrays at all,
            // so it still works when our path building is the thing that is broken.
            var extendCode = SetDisplayConfig(
                0, null, 0, null,
                SetDisplayConfigFlags.SDC_APPLY | SetDisplayConfigFlags.SDC_TOPOLOGY_EXTEND);

            if (extendCode == ERROR_SUCCESS)
            {
                result.Steps.Add("fell back to Windows extend topology");
                Log.Info("Failsafe AllOn succeeded via SDC_TOPOLOGY_EXTEND.");
                return true;
            }

            Log.Error($"Failsafe AllOn failed entirely: {DescribeError(extendCode)}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Failsafe AllOn threw: {ex.Message}");
            result.Warnings.Add($"failsafe error: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Apply a zone
    // ------------------------------------------------------------------

    public static ZoneApplyResult ApplyZone(ZoneTitanConfig config, string zoneName)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastApplyUtc < DebounceWindow)
            {
                Log.Info($"Debounced duplicate activation of '{zoneName}'.");
                return new ZoneApplyResult { Success = true, Operation = zoneName, Steps = { "debounced" } };
            }

            _lastApplyUtc = now;

            try
            {
                return ApplyZoneCore(config, zoneName);
            }
            catch (Exception ex)
            {
                Log.Error($"ApplyZone('{zoneName}') threw: {ex}");
                return ZoneApplyResult.Fail(zoneName, ex.Message);
            }
        }
    }

    private static bool expectedContains(List<(string Alias, string DevicePath)> wanted, string devicePath) =>
        wanted.Any(w => string.Equals(w.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase));

    private static ZoneApplyResult ApplyZoneCore(ZoneTitanConfig config, string zoneName)
    {
        var zone = config.FindZone(zoneName);
        if (zone is null)
        {
            var known = string.Join(", ", config.Zones.Select(z => z.Name));
            return ZoneApplyResult.Fail(zoneName, $"no such zone. Known zones: {known}");
        }

        var result = new ZoneApplyResult { Success = true, Operation = zone.Name };

        // --- Resolve aliases to device paths -------------------------------
        var wanted = new List<(string Alias, string DevicePath)>();
        foreach (var alias in zone.Monitors)
        {
            var devicePath = config.DevicePathForAlias(alias);
            if (devicePath is null)
                return ZoneApplyResult.Fail(zone.Name, $"'{alias}' is not a monitor in your config");

            wanted.Add((alias, devicePath));
        }

        if (wanted.Count == 0)
            return ZoneApplyResult.Fail(zone.Name, "zone lists no monitors — refusing to blank every display");

        // Primary first, so it takes source 0 and anchors the layout.
        if (!string.IsNullOrWhiteSpace(zone.Primary))
        {
            var idx = wanted.FindIndex(w =>
                string.Equals(w.Alias, zone.Primary, StringComparison.OrdinalIgnoreCase));

            if (idx < 0)
                return ZoneApplyResult.Fail(zone.Name, $"primary '{zone.Primary}' is not one of the zone's monitors");

            var primary = wanted[idx];
            wanted.RemoveAt(idx);
            wanted.Insert(0, primary);
        }

        // --- Re-map device paths onto current LUIDs ------------------------
        var topology = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ALL_PATHS);

        if (!TrySelectPaths(topology, wanted.Select(w => w.DevicePath).ToList(), out var chosen, out var missingIndex))
        {
            var alias = wanted[missingIndex].Alias;
            Log.Warn($"Zone '{zone.Name}' aborted: monitor '{alias}' not detected.");
            return ZoneApplyResult.Fail(zone.Name, $"'{alias}' not detected — zone not applied");
        }

        result.Steps.Add($"matched {chosen.Count} monitor(s) by device path");

        // --- Preserve a rollback point before touching anything ------------
        var rollback = CaptureSnapshot("_last-known-good", config);
        if (rollback.Monitors.Count > 0)
            ConfigStore.SaveLastKnownGood(rollback);

        // --- Stage 1: wake anything asleep before switching to it ----------
        // A detached monitor sits in standby with its link down. Windows will refuse to
        // switch *to it alone* — that would mean handing the desktop to a display it cannot
        // confirm is alive. Bringing it up alongside the current ones first gives the panel
        // time to negotiate, and only then do we drop the monitors the zone excludes.
        var currentlyActive = ActiveDevicePaths();
        var asleep = wanted.Where(w => !currentlyActive.Contains(w.DevicePath)).ToList();

        if (asleep.Count > 0 && !currentlyActive.All(a => expectedContains(wanted, a)))
        {
            var union = wanted.Select(w => w.DevicePath)
                .Concat(currentlyActive)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (union.Count > wanted.Count &&
                TrySelectPaths(topology, union, out var unionChosen, out _))
            {
                Log.Info($"Zone '{zone.Name}': staging wake for {asleep.Count} sleeping display(s).");
                TryApplyTopology(topology, unionChosen, result, forceModeEnumeration: true);
                WaitForActiveSet(union.ToHashSet(StringComparer.OrdinalIgnoreCase), TimeSpan.FromSeconds(6));

                // The paths report active before the driver has finished settling, and asking
                // for another topology change too soon returns ERROR_GEN_FAILURE — costing a
                // failed attempt, a retry, and an extra visible flash. A short pause here is
                // cheaper than the retry it prevents.
                Thread.Sleep(500);

                result.Steps.Add($"woke {string.Join(", ", asleep.Select(a => a.Alias))} before switching");

                // Waking re-enumerates paths, so ids may have moved underneath us.
                topology = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ALL_PATHS);
                if (!TrySelectPaths(topology, wanted.Select(w => w.DevicePath).ToList(), out chosen, out var lostIndex))
                {
                    return ZoneApplyResult.Fail(zone.Name,
                        $"'{wanted[lostIndex].Alias}' disappeared while waking displays");
                }
            }
        }

        // --- Stage 2: apply the zone ---------------------------------------
        var primaryDevicePath = wanted[0].DevicePath;
        var applied = TryApplyTopology(topology, chosen, result);

        if (!applied)
        {
            Log.Error($"Zone '{zone.Name}' failed to apply; restoring last known good.");
            result.Warnings.Add("apply failed — restoring previous layout");
            RestoreFrom(rollback, result);
            return ZoneApplyResult.Fail(zone.Name, "SetDisplayConfig rejected the topology; previous layout restored");
        }

        // --- Verify --------------------------------------------------------
        var expected = wanted.Select(w => w.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A panel coming out of standby needs a moment to re-establish its link, so poll
        // rather than reading the topology once and declaring failure.
        var actual = WaitForActiveSet(expected, TimeSpan.FromSeconds(3));

        // A display that was just detached is in standby with its link down. Windows may
        // accept the config and then quietly drop that path (SDC_ALLOW_CHANGES permits it).
        // Give the panel time to wake and re-apply, forcing mode re-enumeration.
        if (!expected.SetEquals(actual))
        {
            result.Warnings.Add("first apply incomplete — waking displays and retrying");
            Log.Warn($"Zone '{zone.Name}' incomplete after first apply; retrying with forced mode enumeration.");

            Thread.Sleep(1500);

            topology = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ALL_PATHS);
            if (TrySelectPaths(topology, wanted.Select(w => w.DevicePath).ToList(), out chosen, out _))
            {
                TryApplyTopology(topology, chosen, result, forceModeEnumeration: true);
                actual = WaitForActiveSet(expected, TimeSpan.FromSeconds(6));
            }
        }

        if (!expected.SetEquals(actual))
        {
            var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase)
                .Select(config.AliasForDevicePath).ToList();
            var extra = actual.Except(expected, StringComparer.OrdinalIgnoreCase)
                .Select(config.AliasForDevicePath).ToList();

            var detail = new List<string>();
            if (missing.Count > 0) detail.Add($"missing {string.Join(", ", missing)}");
            if (extra.Count > 0) detail.Add($"unexpected {string.Join(", ", extra)}");
            var summary = string.Join("; ", detail);

            Log.Error($"Zone '{zone.Name}' verification failed: {summary}");

            // Nothing from the zone came up — the user is looking at black screens, or is
            // about to be. Get displays back before anything else.
            if (actual.Count == 0 || !expected.Overlaps(actual))
            {
                result.Warnings.Add("no zone monitor became active — running failsafe");
                Log.Error($"Zone '{zone.Name}' activated nothing; invoking failsafe.");
                AllOnUnlocked(result);
            }

            result.Success = false;
            result.Error = $"zone applied only partially ({summary})";
            return result;
        }

        result.Steps.Add("verified active set matches the zone");

        // --- Layout and modes, in ONE topology change ------------------------
        // Applying arrangement and refresh rate separately means two more black-screen
        // flashes on top of the staged wake. Combining them into a single SetDisplayConfig
        // is the difference between a clean switch and visible thrashing.
        ApplyLayoutAndModes(config, zone, primaryDevicePath, result);

        // --- HDR re-assert (must come after attach; unreadable while detached) ---
        ApplyHdr(config, zone, result);

        // --- Taskbar auto-hide (burn-in defence; Windows drops it on display changes) ---
        if (config.Options.EnforceTaskbarAutoHide && TaskbarController.EnsureAutoHide())
            result.Steps.Add("re-enabled taskbar auto-hide");

        // --- Rescue windows stranded on a monitor this zone just detached ---
        if (config.Options.RescueOffscreenWindows)
        {
            var rescued = WindowRescuer.RescueOffscreenWindows();
            if (rescued > 0)
                result.Steps.Add($"brought {rescued} off-screen window{(rescued == 1 ? "" : "s")} back into view");
        }

        // --- Persist the verified snapshot ---------------------------------
        var verified = CaptureSnapshot(zone.Name, config);
        if (verified.Monitors.Count > 0)
            ConfigStore.SaveSnapshot(verified);

        Log.Info($"Zone '{zone.Name}' applied: {string.Join("; ", result.Steps)}");
        return result;
    }

    /// <summary>Restores the rolling last-known-good topology.</summary>
    public static ZoneApplyResult RestoreLastKnownGood()
    {
        lock (Gate)
        {
            var snapshot = ConfigStore.LoadLastKnownGood();
            if (snapshot is null || snapshot.Monitors.Count == 0)
                return ZoneApplyResult.Fail("restore-last", "no last-known-good snapshot recorded yet");

            var result = new ZoneApplyResult { Success = true, Operation = "restore-last" };
            return RestoreFrom(snapshot, result)
                ? result
                : ZoneApplyResult.Fail("restore-last", "could not reapply the recorded layout");
        }
    }

    private static bool RestoreFrom(ZoneSnapshot snapshot, ZoneApplyResult result)
    {
        try
        {
            var topology = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ALL_PATHS);
            var paths = snapshot.Monitors.Select(m => m.DevicePath).ToList();

            if (!TrySelectPaths(topology, paths, out var chosen, out _))
            {
                result.Warnings.Add("recorded layout references a monitor that is no longer attached");
                return false;
            }

            if (!TryApplyTopology(topology, chosen, result))
                return false;

            NormalisePrimary(
                snapshot.Monitors.FirstOrDefault(m => m is { X: 0, Y: 0 })?.DevicePath, result);

            result.Steps.Add("restored recorded layout");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"RestoreFrom failed: {ex.Message}");
            result.Warnings.Add($"restore failed: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Topology application
    // ------------------------------------------------------------------

    /// <summary>
    /// Activates exactly the chosen paths. Mode indices are left invalid so the driver picks
    /// valid timings itself — supplying hand-built timings is the single most common cause of
    /// ERROR_INVALID_PARAMETER. Layout is corrected afterwards in a second pass, where the
    /// mode entries are driver-produced and therefore known good.
    /// </summary>
    private static bool TryApplyTopology(
        TopologySnapshot topology,
        List<int> chosen,
        ZoneApplyResult result,
        bool forceModeEnumeration = false)
    {
        if (chosen.Count == 0)
        {
            result.Warnings.Add("refusing to apply a topology with zero active displays");
            return false;
        }

        var flags = forceModeEnumeration
            ? TopologyFlags | SetDisplayConfigFlags.SDC_FORCE_MODE_ENUMERATION
            : TopologyFlags;

        var paths = BuildTopologyOnlyPaths(topology, chosen);

        var code = SetDisplayConfig((uint)paths.Length, paths, 0, null, flags);
        if (code == ERROR_SUCCESS)
        {
            result.Steps.Add(forceModeEnumeration ? "topology re-applied" : "topology applied");
            return true;
        }

        Log.Warn($"SetDisplayConfig topology pass failed: {DescribeError(code)}; retrying.");
        result.Warnings.Add($"first attempt failed: {DescribeError(code)}");

        // One retry with forced mode re-enumeration — the usual cure for a panel that has
        // just come out of standby and has not re-published its mode list yet.
        Thread.Sleep(400);
        paths = BuildTopologyOnlyPaths(topology, chosen);
        code = SetDisplayConfig((uint)paths.Length, paths, 0, null,
            TopologyFlags | SetDisplayConfigFlags.SDC_FORCE_MODE_ENUMERATION);

        if (code == ERROR_SUCCESS)
        {
            result.Steps.Add("topology applied on retry");
            return true;
        }

        Log.Error($"SetDisplayConfig topology pass failed twice: {DescribeError(code)}");
        return false;
    }

    private static DISPLAYCONFIG_PATH_INFO[] BuildTopologyOnlyPaths(TopologySnapshot topology, List<int> chosen)
    {
        var paths = new DISPLAYCONFIG_PATH_INFO[chosen.Count];

        for (var i = 0; i < chosen.Count; i++)
        {
            var path = topology.Paths[chosen[i]];
            DisplayTopology.SetPathActive(ref path, true);
            DisplayTopology.ClearModeIndices(ref path);
            paths[i] = path;
        }

        return paths;
    }

    /// <summary>
    /// Chooses one path per requested monitor, guaranteeing distinct source ids. Two active
    /// paths sharing a source id means "clone", which is not what a zone ever asks for.
    /// </summary>
    private static bool TrySelectPaths(
        TopologySnapshot topology,
        List<string> devicePaths,
        out List<int> chosen,
        out int failedIndex)
    {
        chosen = [];
        failedIndex = -1;

        var usedSources = new HashSet<uint>();

        for (var i = 0; i < devicePaths.Count; i++)
        {
            var candidates = new List<int>();

            for (var p = 0; p < topology.Paths.Length; p++)
            {
                ref readonly var path = ref topology.Paths[p];
                if (!path.targetInfo.IsTargetAvailable)
                    continue;

                if (!DisplayTopology.TryGetTargetName(in path, out var name))
                    continue;

                if (string.Equals(name.monitorDevicePath, devicePaths[i], StringComparison.OrdinalIgnoreCase))
                    candidates.Add(p);
            }

            var pick = candidates
                .Where(p => !usedSources.Contains(topology.Paths[p].sourceInfo.id))
                .OrderByDescending(p => topology.Paths[p].IsActive)
                .ThenBy(p => topology.Paths[p].sourceInfo.id)
                .Select(p => (int?)p)
                .FirstOrDefault();

            if (pick is null)
            {
                failedIndex = i;
                return false;
            }

            usedSources.Add(topology.Paths[pick.Value].sourceInfo.id);
            chosen.Add(pick.Value);
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Layout: primary at (0,0)
    // ------------------------------------------------------------------

    /// <summary>
    /// Translates every active source so the primary sits at (0,0), preserving the relative
    /// arrangement. CCD has no primary flag — primary *is* the source at the origin (spec §4).
    /// Because this only translates an arrangement Windows already accepted, it cannot
    /// introduce the gaps or overlaps that make SetDisplayConfig return ERROR_INVALID_PARAMETER.
    /// </summary>
    private static void NormalisePrimary(string? primaryDevicePath, ZoneApplyResult result)
    {
        try
        {
            var active = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS);

            var sourceIndexByDevicePath = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in active.Paths)
            {
                if (!path.IsActive || path.sourceInfo.modeInfoIdx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
                    continue;

                if (DisplayTopology.TryGetTargetName(in path, out var name) &&
                    !string.IsNullOrWhiteSpace(name.monitorDevicePath))
                {
                    sourceIndexByDevicePath[name.monitorDevicePath] = path.sourceInfo.modeInfoIdx;
                }
            }

            if (sourceIndexByDevicePath.Count == 0)
                return;

            // Fall back to the left-most, top-most source when no primary was named.
            uint anchorIdx;
            if (primaryDevicePath is not null &&
                sourceIndexByDevicePath.TryGetValue(primaryDevicePath, out var named))
            {
                anchorIdx = named;
            }
            else
            {
                anchorIdx = sourceIndexByDevicePath.Values
                    .OrderBy(i => active.Modes[i].modeInfo.sourceMode.position.x)
                    .ThenBy(i => active.Modes[i].modeInfo.sourceMode.position.y)
                    .First();
            }

            var anchor = active.Modes[anchorIdx].modeInfo.sourceMode.position;
            if (anchor is { x: 0, y: 0 })
            {
                result.Steps.Add("primary already at origin");
                return;
            }

            for (var i = 0; i < active.Modes.Length; i++)
            {
                if (active.Modes[i].infoType != DisplayConfigModeInfoType.SOURCE)
                    continue;

                active.Modes[i].modeInfo.sourceMode.position.x -= anchor.x;
                active.Modes[i].modeInfo.sourceMode.position.y -= anchor.y;
            }

            var code = SetDisplayConfig(
                (uint)active.Paths.Length, active.Paths,
                (uint)active.Modes.Length, active.Modes,
                LayoutFlags);

            if (code == ERROR_SUCCESS)
            {
                result.Steps.Add("primary moved to origin");
            }
            else
            {
                result.Warnings.Add($"could not set primary: {DescribeError(code)}");
                Log.Warn($"Primary normalisation failed: {DescribeError(code)}");
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"primary normalisation error: {ex.Message}");
            Log.Warn($"Primary normalisation threw: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Layout + modes, combined
    // ------------------------------------------------------------------

    /// <summary>
    /// Sets every monitor's position and mode in a single <c>SetDisplayConfig</c>, and does
    /// nothing at all when the current state already matches.
    /// <para>
    /// Each topology change blanks the displays briefly, so the number of calls is directly
    /// what the user perceives as smoothness. Position and refresh live in the same path/mode
    /// arrays, so there is no reason to spend two flashes on them — and skipping a no-op call
    /// entirely is better still.
    /// </para>
    /// </summary>
    private static void ApplyLayoutAndModes(
        ZoneTitanConfig config,
        ZoneDefinition zone,
        string? primaryDevicePath,
        ZoneApplyResult result)
    {
        try
        {
            var active = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS);

            // devicePath -> (pathIndex, sourceModeIndex)
            var monitors = new Dictionary<string, (int PathIndex, uint SourceIdx)>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < active.Paths.Length; i++)
            {
                if (!active.Paths[i].IsActive)
                    continue;

                var srcIdx = active.Paths[i].sourceInfo.modeInfoIdx;
                if (srcIdx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID || srcIdx >= active.Modes.Length)
                    continue;

                if (DisplayTopology.TryGetTargetName(in active.Paths[i], out var name) &&
                    !string.IsNullOrWhiteSpace(name.monitorDevicePath))
                {
                    monitors[name.monitorDevicePath] = (i, srcIdx);
                }
            }

            if (monitors.Count == 0)
                return;

            // ---- Desired positions ----
            var positions = ResolveDesiredPositions(config, zone, primaryDevicePath, monitors, active, result);

            // ---- Apply positions and modes, tracking whether anything actually differs ----
            var changed = false;
            var notes = new List<string>();

            foreach (var (devicePath, (pathIndex, srcIdx)) in monitors)
            {
                var alias = config.AliasForDevicePath(devicePath);

                // Position
                if (positions is not null && positions.TryGetValue(devicePath, out var wantedPosition))
                {
                    ref var source = ref active.Modes[srcIdx].modeInfo.sourceMode;

                    if (source.position.x != wantedPosition.X || source.position.y != wantedPosition.Y)
                    {
                        source.position.x = wantedPosition.X;
                        source.position.y = wantedPosition.Y;
                        changed = true;
                    }
                }

                // Mode
                var wantedMode = zone.Modes?.FirstOrDefault(kv =>
                    string.Equals(kv.Key, alias, StringComparison.OrdinalIgnoreCase)).Value;

                if (wantedMode is null || wantedMode.RefreshHz == 0)
                    continue;

                var tgtIdx = active.Paths[pathIndex].targetInfo.modeInfoIdx;

                var currentHz = 0d;
                if (tgtIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && tgtIdx < active.Modes.Length &&
                    active.Modes[tgtIdx].infoType == DisplayConfigModeInfoType.TARGET)
                {
                    currentHz = active.Modes[tgtIdx].modeInfo.targetMode.targetVideoSignalInfo.vSyncFreq.ToHz();
                }

                var currentWidth = active.Modes[srcIdx].modeInfo.sourceMode.width;
                var currentHeight = active.Modes[srcIdx].modeInfo.sourceMode.height;

                // Reported rates are true vsync frequencies (239.76, 164.96), so compare loosely.
                var rateMatches = Math.Abs(currentHz - wantedMode.RefreshHz) < 1.0;
                var sizeMatches = wantedMode.Width == 0 || wantedMode.Height == 0 ||
                                  (currentWidth == wantedMode.Width && currentHeight == wantedMode.Height);

                if (rateMatches && sizeMatches)
                    continue;

                if (wantedMode.Width > 0 && wantedMode.Height > 0)
                {
                    active.Modes[srcIdx].modeInfo.sourceMode.width = wantedMode.Width;
                    active.Modes[srcIdx].modeInfo.sourceMode.height = wantedMode.Height;
                }

                active.Paths[pathIndex].targetInfo.refreshRate = new DISPLAYCONFIG_RATIONAL
                {
                    Numerator = wantedMode.RefreshHz,
                    Denominator = 1,
                };

                // Invalidate the target mode so the driver derives a timing for the new rate.
                // Hand-building a video signal block is the classic route to ERROR_INVALID_PARAMETER.
                active.Paths[pathIndex].targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

                changed = true;
                notes.Add($"{alias} → {wantedMode}");
            }

            if (!changed)
            {
                result.Steps.Add("layout and modes already correct");
                return;
            }

            var code = SetDisplayConfig(
                (uint)active.Paths.Length, active.Paths,
                (uint)active.Modes.Length, active.Modes,
                LayoutFlags | SetDisplayConfigFlags.SDC_ALLOW_CHANGES);

            if (code == ERROR_SUCCESS)
            {
                result.Steps.Add(notes.Count > 0
                    ? $"arrangement and mode set ({string.Join(", ", notes)})"
                    : "arrangement set");

                Log.Info($"Zone '{zone.Name}' layout/modes applied in one pass. {string.Join(", ", notes)}");
                return;
            }

            result.Warnings.Add($"could not set arrangement/mode: {DescribeError(code)}");
            Log.Warn($"Zone '{zone.Name}' layout/mode pass failed: {DescribeError(code)}");

            // Fall back to just getting the primary right, which is the part that matters most.
            NormalisePrimary(primaryDevicePath, result);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"layout/mode error: {ex.Message}");
            Log.Warn($"ApplyLayoutAndModes threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Works out where each monitor should sit: the zone's stored arrangement when it covers
    /// every active monitor, otherwise the current arrangement translated so the primary is
    /// at the origin.
    /// </summary>
    private static Dictionary<string, (int X, int Y)>? ResolveDesiredPositions(
        ZoneTitanConfig config,
        ZoneDefinition zone,
        string? primaryDevicePath,
        Dictionary<string, (int PathIndex, uint SourceIdx)> monitors,
        TopologySnapshot active,
        ZoneApplyResult result)
    {
        var positions = new Dictionary<string, (int X, int Y)>(StringComparer.OrdinalIgnoreCase);

        if (zone.Layout is { Count: > 0 })
        {
            foreach (var (alias, placement) in zone.Layout)
            {
                var devicePath = config.DevicePathForAlias(alias);
                if (devicePath is not null && monitors.ContainsKey(devicePath))
                    positions[devicePath] = (placement.X, placement.Y);
            }

            // Every active monitor must be placed, or one is left at a stale position and the
            // result can contain a gap or overlap, which SetDisplayConfig rejects outright.
            if (positions.Count == monitors.Count)
            {
                var anchor = primaryDevicePath is not null && positions.TryGetValue(primaryDevicePath, out var p)
                    ? p
                    : (X: 0, Y: 0);

                return positions.ToDictionary(
                    kv => kv.Key,
                    kv => (kv.Value.X - anchor.X, kv.Value.Y - anchor.Y),
                    StringComparer.OrdinalIgnoreCase);
            }

            result.Warnings.Add("stored arrangement does not cover every monitor — keeping current layout");
            positions.Clear();
        }

        // No usable stored layout: keep the current arrangement, translated so the primary
        // sits at (0,0). CCD has no primary flag — the source at the origin *is* the primary.
        var anchorPosition = (X: 0, Y: 0);

        if (primaryDevicePath is not null && monitors.TryGetValue(primaryDevicePath, out var primary))
        {
            var current = active.Modes[primary.SourceIdx].modeInfo.sourceMode.position;
            anchorPosition = (current.x, current.y);
        }

        foreach (var (devicePath, (_, srcIdx)) in monitors)
        {
            var current = active.Modes[srcIdx].modeInfo.sourceMode.position;
            positions[devicePath] = (current.x - anchorPosition.X, current.y - anchorPosition.Y);
        }

        return positions;
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------

    /// <summary>
    /// Places each monitor at the position the zone stored for it, normalised so the primary
    /// lands on (0,0). Returns false when the zone has no usable layout, so the caller can
    /// fall back to simply translating whatever Windows produced.
    /// </summary>
    private static bool ApplyStoredLayout(ZoneTitanConfig config, ZoneDefinition zone, ZoneApplyResult result)
    {
        if (zone.Layout is null || zone.Layout.Count == 0)
            return false;

        try
        {
            var active = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS);

            // devicePath -> source mode index
            var sourceIndex = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in active.Paths)
            {
                if (!path.IsActive || path.sourceInfo.modeInfoIdx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
                    continue;

                if (DisplayTopology.TryGetTargetName(in path, out var name) &&
                    !string.IsNullOrWhiteSpace(name.monitorDevicePath))
                {
                    sourceIndex[name.monitorDevicePath] = path.sourceInfo.modeInfoIdx;
                }
            }

            if (sourceIndex.Count == 0)
                return false;

            // Resolve the stored layout onto live device paths.
            var desired = new Dictionary<uint, (int X, int Y)>();

            foreach (var (alias, placement) in zone.Layout)
            {
                var devicePath = config.DevicePathForAlias(alias);
                if (devicePath is null || !sourceIndex.TryGetValue(devicePath, out var idx))
                    continue;

                desired[idx] = (placement.X, placement.Y);
            }

            // Every active monitor must be placed, or we would leave one at a stale position
            // and risk a gap or overlap, which SetDisplayConfig rejects outright.
            if (desired.Count != sourceIndex.Count)
            {
                result.Warnings.Add("stored layout does not cover every monitor — using default arrangement");
                return false;
            }

            // Normalise so the primary sits at the origin. CCD has no primary flag; the
            // source at (0,0) *is* the primary (spec §4).
            var primaryPath = zone.Primary is null ? null : config.DevicePathForAlias(zone.Primary);
            var anchor = (X: 0, Y: 0);

            if (primaryPath is not null && sourceIndex.TryGetValue(primaryPath, out var primaryIdx) &&
                desired.TryGetValue(primaryIdx, out var primaryPos))
            {
                anchor = primaryPos;
            }

            for (var i = 0; i < active.Modes.Length; i++)
            {
                if (active.Modes[i].infoType != DisplayConfigModeInfoType.SOURCE)
                    continue;

                if (!desired.TryGetValue((uint)i, out var position))
                    continue;

                active.Modes[i].modeInfo.sourceMode.position.x = position.X - anchor.X;
                active.Modes[i].modeInfo.sourceMode.position.y = position.Y - anchor.Y;
            }

            var code = SetDisplayConfig(
                (uint)active.Paths.Length, active.Paths,
                (uint)active.Modes.Length, active.Modes,
                LayoutFlags);

            if (code == ERROR_SUCCESS)
            {
                result.Steps.Add("restored stored monitor arrangement");
                return true;
            }

            // A rejected layout is usually a gap or overlap. Fall back rather than leave the
            // desktop in whatever half-state the driver decided on.
            result.Warnings.Add($"stored arrangement rejected ({DescribeError(code)}) — using default");
            Log.Warn($"Zone '{zone.Name}' layout rejected: {DescribeError(code)}");
            return false;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"layout error: {ex.Message}");
            Log.Warn($"ApplyStoredLayout threw: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Modes
    // ------------------------------------------------------------------

    /// <summary>
    /// Drives each monitor to its zone's desired mode.
    /// <para>
    /// Refresh rate is requested by setting the path's <c>refreshRate</c> and invalidating its
    /// target mode index, which asks the driver to derive a matching timing. Hand-building a
    /// <c>DISPLAYCONFIG_VIDEO_SIGNAL_INFO</c> is the classic route to ERROR_INVALID_PARAMETER —
    /// pixel rate, sync frequencies and totals all have to agree, so we let the driver do it.
    /// </para>
    /// </summary>
    private static void ApplyModes(ZoneTitanConfig config, ZoneDefinition zone, ZoneApplyResult result)
    {
        if (zone.Modes is null || zone.Modes.Count == 0)
            return;

        try
        {
            var active = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS);
            var changed = false;
            var requested = new List<string>();

            for (var i = 0; i < active.Paths.Length; i++)
            {
                if (!active.Paths[i].IsActive)
                    continue;

                if (!DisplayTopology.TryGetTargetName(in active.Paths[i], out var name))
                    continue;

                var alias = config.AliasForDevicePath(name.monitorDevicePath);

                var wanted = zone.Modes.FirstOrDefault(kv =>
                    string.Equals(kv.Key, alias, StringComparison.OrdinalIgnoreCase)).Value;

                if (wanted is null || wanted.RefreshHz == 0)
                    continue;

                var srcIdx = active.Paths[i].sourceInfo.modeInfoIdx;
                var tgtIdx = active.Paths[i].targetInfo.modeInfoIdx;

                // Only touch a display whose mode actually differs. Re-applying a mode that
                // already matches costs an extra black-screen flash and, on some drivers,
                // fails outright with ERROR_GEN_FAILURE.
                var currentHz = 0d;
                if (tgtIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && tgtIdx < active.Modes.Length &&
                    active.Modes[tgtIdx].infoType == DisplayConfigModeInfoType.TARGET)
                {
                    currentHz = active.Modes[tgtIdx].modeInfo.targetMode.targetVideoSignalInfo.vSyncFreq.ToHz();
                }

                uint currentWidth = 0, currentHeight = 0;
                if (srcIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && srcIdx < active.Modes.Length &&
                    active.Modes[srcIdx].infoType == DisplayConfigModeInfoType.SOURCE)
                {
                    currentWidth = active.Modes[srcIdx].modeInfo.sourceMode.width;
                    currentHeight = active.Modes[srcIdx].modeInfo.sourceMode.height;
                }

                // Reported rates are the true vsync frequency (239.76, 164.96), so compare
                // with a tolerance rather than demanding an exact integer match.
                var rateMatches = Math.Abs(currentHz - wanted.RefreshHz) < 1.0;
                var sizeMatches = wanted.Width == 0 || wanted.Height == 0 ||
                                  (currentWidth == wanted.Width && currentHeight == wanted.Height);

                if (rateMatches && sizeMatches)
                    continue;

                if (srcIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && srcIdx < active.Modes.Length &&
                    active.Modes[srcIdx].infoType == DisplayConfigModeInfoType.SOURCE &&
                    wanted.Width > 0 && wanted.Height > 0)
                {
                    active.Modes[srcIdx].modeInfo.sourceMode.width = wanted.Width;
                    active.Modes[srcIdx].modeInfo.sourceMode.height = wanted.Height;
                }

                active.Paths[i].targetInfo.refreshRate = new DISPLAYCONFIG_RATIONAL
                {
                    Numerator = wanted.RefreshHz,
                    Denominator = 1,
                };

                // Force the driver to derive a fresh timing for the requested rate.
                active.Paths[i].targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

                changed = true;
                requested.Add($"{alias} → {wanted}");
            }

            if (!changed)
                return;

            var code = SetDisplayConfig(
                (uint)active.Paths.Length, active.Paths,
                (uint)active.Modes.Length, active.Modes,
                LayoutFlags | SetDisplayConfigFlags.SDC_ALLOW_CHANGES);

            if (code == ERROR_SUCCESS)
            {
                result.Steps.Add($"mode set: {string.Join(", ", requested)}");
                Log.Info($"Zone '{zone.Name}' modes applied: {string.Join(", ", requested)}");
            }
            else
            {
                result.Warnings.Add($"could not set requested mode(s): {DescribeError(code)}");
                Log.Warn($"Zone '{zone.Name}' mode pass failed: {DescribeError(code)}");
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"mode pass error: {ex.Message}");
            Log.Warn($"Mode pass threw: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // HDR
    // ------------------------------------------------------------------

    private static void ApplyHdr(ZoneTitanConfig config, ZoneDefinition zone, ZoneApplyResult result)
    {
        if (zone.Hdr is null || zone.Hdr.Count == 0)
            return;

        var active = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS);

        foreach (var (alias, desired) in zone.Hdr)
        {
            var devicePath = config.DevicePathForAlias(alias);
            if (devicePath is null)
            {
                result.Warnings.Add($"HDR rule names unknown monitor '{alias}'");
                continue;
            }

            var matched = false;

            foreach (var path in active.Paths)
            {
                if (!path.IsActive)
                    continue;

                if (!DisplayTopology.TryGetTargetName(in path, out var name))
                    continue;

                if (!string.Equals(name.monitorDevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                matched = true;

                if (HdrController.TrySetHdr(path.targetInfo.adapterId, path.targetInfo.id, desired, out var how))
                    result.Steps.Add($"HDR {(desired ? "on" : "off")} for {alias} ({how})");
                else
                    result.Warnings.Add($"could not set HDR for {alias}: {how}");

                break;
            }

            if (!matched)
                result.Warnings.Add($"HDR rule for '{alias}' skipped — not active in this zone");
        }
    }

    // ------------------------------------------------------------------
    // Snapshots
    // ------------------------------------------------------------------

    /// <summary>
    /// Polls the active set until it matches <paramref name="expected"/> or the timeout expires,
    /// returning whatever was last observed.
    /// </summary>
    private static HashSet<string> WaitForActiveSet(HashSet<string> expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var actual = ActiveDevicePaths();

        while (!expected.SetEquals(actual) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(400);
            actual = ActiveDevicePaths();
        }

        return actual;
    }

    private static HashSet<string> ActiveDevicePaths()
    {
        var active = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in active.Paths)
        {
            if (!path.IsActive)
                continue;

            if (DisplayTopology.TryGetTargetName(in path, out var name) &&
                !string.IsNullOrWhiteSpace(name.monitorDevicePath))
            {
                set.Add(name.monitorDevicePath);
            }
        }

        return set;
    }

    /// <summary>Captures the current active topology, keyed by device path rather than LUID.</summary>
    public static ZoneSnapshot CaptureSnapshot(string zoneName, ZoneTitanConfig? config = null)
    {
        var snapshot = new ZoneSnapshot { Zone = zoneName, CapturedUtc = DateTime.UtcNow };

        try
        {
            var active = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ONLY_ACTIVE_PATHS);

            foreach (var path in active.Paths)
            {
                if (!path.IsActive)
                    continue;

                if (!DisplayTopology.TryGetTargetName(in path, out var name) ||
                    string.IsNullOrWhiteSpace(name.monitorDevicePath))
                {
                    continue;
                }

                var entry = new MonitorSnapshot
                {
                    DevicePath = name.monitorDevicePath,
                    Alias = config?.AliasForDevicePath(name.monitorDevicePath),
                    Rotation = (uint)path.targetInfo.rotation,
                    Scaling = (uint)path.targetInfo.scaling,
                };

                var srcIdx = path.sourceInfo.modeInfoIdx;
                if (srcIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && srcIdx < active.Modes.Length &&
                    active.Modes[srcIdx].infoType == DisplayConfigModeInfoType.SOURCE)
                {
                    var source = active.Modes[srcIdx].modeInfo.sourceMode;
                    entry.Width = source.width;
                    entry.Height = source.height;
                    entry.PixelFormat = (uint)source.pixelFormat;
                    entry.X = source.position.x;
                    entry.Y = source.position.y;
                }

                var tgtIdx = path.targetInfo.modeInfoIdx;
                if (tgtIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && tgtIdx < active.Modes.Length &&
                    active.Modes[tgtIdx].infoType == DisplayConfigModeInfoType.TARGET)
                {
                    var signal = active.Modes[tgtIdx].modeInfo.targetMode.targetVideoSignalInfo;
                    entry.Signal = new VideoSignalSnapshot
                    {
                        PixelRate = signal.pixelRate,
                        HSyncNumerator = signal.hSyncFreq.Numerator,
                        HSyncDenominator = signal.hSyncFreq.Denominator,
                        VSyncNumerator = signal.vSyncFreq.Numerator,
                        VSyncDenominator = signal.vSyncFreq.Denominator,
                        ActiveCx = signal.activeSize.cx,
                        ActiveCy = signal.activeSize.cy,
                        TotalCx = signal.totalSize.cx,
                        TotalCy = signal.totalSize.cy,
                        VideoStandard = signal.videoStandard,
                        ScanLineOrdering = (uint)signal.scanLineOrdering,
                    };
                }

                snapshot.Monitors.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"CaptureSnapshot failed: {ex.Message}");
        }

        return snapshot;
    }
}
