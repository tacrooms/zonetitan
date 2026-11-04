using System.Text.Json.Serialization;

namespace ZoneTitan.Core.Zones;

/// <summary>A monitor's user-facing identity, keyed in config by its device path.</summary>
public sealed class MonitorAlias
{
    [JsonPropertyName("alias")] public string Alias { get; set; } = "";
    [JsonPropertyName("friendly")] public string Friendly { get; set; } = "";
}

/// <summary>A named subset of monitors, with a designated primary.</summary>
public sealed class ZoneDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("hotkey")] public string? Hotkey { get; set; }

    /// <summary>Monitor aliases belonging to this zone.</summary>
    [JsonPropertyName("monitors")] public List<string> Monitors { get; set; } = [];

    /// <summary>Alias of the monitor to place at (0,0). CCD has no primary flag (spec §4).</summary>
    [JsonPropertyName("primary")] public string? Primary { get; set; }

    [JsonPropertyName("accent")] public string? Accent { get; set; }

    /// <summary>Desired HDR state per monitor alias. Re-asserted after apply (spec §5).</summary>
    [JsonPropertyName("hdr")] public Dictionary<string, bool>? Hdr { get; set; }

    /// <summary>
    /// Desired mode per monitor alias. Without this Windows picks for itself when a display
    /// is re-attached, and it habitually picks 60 Hz — the OLED must come back at 240 Hz,
    /// not 60 (spec §4, step 5).
    /// </summary>
    [JsonPropertyName("modes")] public Dictionary<string, DesiredMode>? Modes { get; set; }

    /// <summary>
    /// Desired desktop arrangement per monitor alias, relative to the primary at (0,0).
    /// <para>
    /// Without this, Windows arranges re-attached displays in source-id order and your
    /// left-hand monitor can end up on the right. Spec §4 step 4 requires the stored relative
    /// layout to be preserved, not merely translated.
    /// </para>
    /// </summary>
    [JsonPropertyName("layout")] public Dictionary<string, MonitorPlacement>? Layout { get; set; }
}

/// <summary>A monitor's top-left corner on the virtual desktop, relative to the primary.</summary>
public sealed class MonitorPlacement
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }

    public override string ToString() => $"({X}, {Y})";
}

/// <summary>A resolution and refresh rate a zone wants a monitor to run at.</summary>
public sealed class DesiredMode
{
    [JsonPropertyName("width")] public uint Width { get; set; }
    [JsonPropertyName("height")] public uint Height { get; set; }
    [JsonPropertyName("refreshHz")] public uint RefreshHz { get; set; }

    public override string ToString() => $"{Width}x{Height} @ {RefreshHz} Hz";
}

public sealed class ZoneOptions
{
    [JsonPropertyName("startWithWindows")] public bool StartWithWindows { get; set; } = true;
    [JsonPropertyName("osdToast")] public bool OsdToast { get; set; } = true;
    [JsonPropertyName("failsafeHotkey")] public string FailsafeHotkey { get; set; } = "Ctrl+Alt+F12";

    /// <summary>
    /// Re-assert taskbar auto-hide after each zone apply. Windows 11 drops the flag on
    /// display changes, and a permanently visible taskbar is a burn-in risk on an OLED.
    /// <para>
    /// Off by default. Setting taskbar state can make Windows raise a modal "you can have
    /// only one auto-hide toolbar per side" dialog, so this stays opt-in until the user has
    /// confirmed it behaves on their machine.
    /// </para>
    /// </summary>
    [JsonPropertyName("enforceTaskbarAutoHide")] public bool EnforceTaskbarAutoHide { get; set; }

    /// <summary>
    /// After a zone applies, drag any window that is now entirely off-screen back into view.
    /// <para>
    /// On by default: it only ever touches windows that are 100% invisible, so the outcome is
    /// strictly better than leaving them lost. Apps that restore their own saved position
    /// (Steam being the usual offender) reopen on a monitor the zone has detached.
    /// </para>
    /// </summary>
    [JsonPropertyName("rescueOffscreenWindows")] public bool RescueOffscreenWindows { get; set; } = true;
}

public sealed class ZoneTitanConfig
{
    /// <summary>Keyed by monitor device path — the stable identity (spec §4).</summary>
    [JsonPropertyName("monitors")]
    public Dictionary<string, MonitorAlias> Monitors { get; set; } = [];

    [JsonPropertyName("zones")]
    public List<ZoneDefinition> Zones { get; set; } = [];

    [JsonPropertyName("options")]
    public ZoneOptions Options { get; set; } = new();

    [JsonPropertyName("inputRouting")]
    public InputRoutingOptions? InputRouting { get; set; }

    public ZoneDefinition? FindZone(string name) =>
        Zones.FirstOrDefault(z => string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves an alias to its device path. Aliases are case-insensitive.</summary>
    public string? DevicePathForAlias(string alias) =>
        Monitors.FirstOrDefault(kv =>
            string.Equals(kv.Value.Alias, alias, StringComparison.OrdinalIgnoreCase)).Key;

    public string AliasForDevicePath(string devicePath) =>
        Monitors.TryGetValue(devicePath, out var m) && !string.IsNullOrWhiteSpace(m.Alias)
            ? m.Alias
            : devicePath;
}

// ---------------------------------------------------------------------------
// Wake routing by input device
// ---------------------------------------------------------------------------

/// <summary>
/// One "this device means this zone" rule. Matched against the device instance path of the
/// first input received after the machine resumes.
/// </summary>
public sealed class InputRule
{
    /// <summary>Device instance path, or a VID/PID fragment when <see cref="Match"/> is vidPid.</summary>
    [JsonPropertyName("device")] public string Device { get; set; } = "";

    /// <summary><c>exact</c> for a full instance path, <c>vidPid</c> for a substring match.</summary>
    [JsonPropertyName("match")] public string Match { get; set; } = "vidPid";

    [JsonPropertyName("zone")] public string Zone { get; set; } = "";

    /// <summary>Human label, for the rules list in the UI.</summary>
    [JsonPropertyName("label")] public string? Label { get; set; }

    public bool Matches(string deviceInstancePath)
    {
        if (string.IsNullOrWhiteSpace(Device) || string.IsNullOrWhiteSpace(deviceInstancePath))
            return false;

        return string.Equals(Match, "exact", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(Device, deviceInstancePath, StringComparison.OrdinalIgnoreCase)
            : deviceInstancePath.Contains(Device, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class InputRoutingOptions
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long after a resume ZoneTitan listens for the first input. Expires quietly:
    /// no input means no zone change.
    /// </summary>
    [JsonPropertyName("routingWindowSeconds")] public int RoutingWindowSeconds { get; set; } = 10;

    [JsonPropertyName("rules")] public List<InputRule> Rules { get; set; } = [];
}

// ---------------------------------------------------------------------------
// Verified snapshots (spec §4, step 7)
// ---------------------------------------------------------------------------

/// <summary>
/// A CCD video signal timing, stored verbatim. Refresh rate cannot be changed by editing
/// vSyncFreq alone — the whole timing block must be internally consistent, so ZoneTitan
/// only ever replays blocks the driver itself produced.
/// </summary>
public sealed class VideoSignalSnapshot
{
    [JsonPropertyName("pixelRate")] public ulong PixelRate { get; set; }
    [JsonPropertyName("hSyncNum")] public uint HSyncNumerator { get; set; }
    [JsonPropertyName("hSyncDen")] public uint HSyncDenominator { get; set; }
    [JsonPropertyName("vSyncNum")] public uint VSyncNumerator { get; set; }
    [JsonPropertyName("vSyncDen")] public uint VSyncDenominator { get; set; }
    [JsonPropertyName("activeCx")] public uint ActiveCx { get; set; }
    [JsonPropertyName("activeCy")] public uint ActiveCy { get; set; }
    [JsonPropertyName("totalCx")] public uint TotalCx { get; set; }
    [JsonPropertyName("totalCy")] public uint TotalCy { get; set; }
    [JsonPropertyName("videoStandard")] public uint VideoStandard { get; set; }
    [JsonPropertyName("scanLineOrdering")] public uint ScanLineOrdering { get; set; }

    [JsonIgnore]
    public double RefreshHz => VSyncDenominator == 0 ? 0 : (double)VSyncNumerator / VSyncDenominator;
}

/// <summary>One monitor's verified state within a zone snapshot.</summary>
public sealed class MonitorSnapshot
{
    [JsonPropertyName("devicePath")] public string DevicePath { get; set; } = "";
    [JsonPropertyName("alias")] public string? Alias { get; set; }

    [JsonPropertyName("width")] public uint Width { get; set; }
    [JsonPropertyName("height")] public uint Height { get; set; }
    [JsonPropertyName("pixelFormat")] public uint PixelFormat { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("rotation")] public uint Rotation { get; set; }
    [JsonPropertyName("scaling")] public uint Scaling { get; set; }

    [JsonPropertyName("signal")] public VideoSignalSnapshot? Signal { get; set; }
}

/// <summary>
/// A zone's last verified-good topology. Replayed on later activations after re-mapping
/// device paths to current LUIDs, which is both faster and more deterministic than
/// rebuilding a layout from scratch.
/// </summary>
public sealed class ZoneSnapshot
{
    [JsonPropertyName("zone")] public string Zone { get; set; } = "";
    [JsonPropertyName("capturedUtc")] public DateTime CapturedUtc { get; set; }
    [JsonPropertyName("monitors")] public List<MonitorSnapshot> Monitors { get; set; } = [];
}
