namespace ZoneTitan.Core.Display;

/// <summary>A display mode: resolution plus refresh rate.</summary>
public sealed record DisplayMode(uint Width, uint Height, double RefreshHz)
{
    public override string ToString() =>
        RefreshHz > 0
            ? $"{Width}x{Height} @ {RefreshHz:0.##} Hz"
            : $"{Width}x{Height}";
}

/// <summary>Where a monitor's source sits on the virtual desktop.</summary>
public sealed record DisplayPosition(int X, int Y)
{
    /// <summary>CCD has no primary flag — the primary display is definitionally at (0,0) (spec §4).</summary>
    public bool IsOrigin => X == 0 && Y == 0;

    public override string ToString() => $"({X}, {Y})";
}

/// <summary>HDR / advanced-colour state for a monitor.</summary>
public sealed record HdrState(
    bool Supported,
    bool Enabled,
    bool LimitedByPolicy,
    string ColorEncoding,
    uint BitsPerColorChannel,
    string Source)
{
    public static HdrState Unknown { get; } =
        new(false, false, false, "unknown", 0, "unavailable");
}

/// <summary>
/// A physically attached monitor, as ZoneTitan understands it.
/// <para>
/// <see cref="DevicePath"/> is the identity. Adapter LUIDs and GDI names
/// (<c>\\.\DISPLAY1</c>) change across reboots and driver restarts and are captured
/// for diagnostics only — never persisted as identity (spec §4).
/// </para>
/// </summary>
public sealed class MonitorInfo
{
    /// <summary>Stable identity, e.g. <c>\\?\DISPLAY#DELA1FB#5&amp;2f4a...</c>.</summary>
    public required string DevicePath { get; init; }

    /// <summary>EDID friendly name, e.g. <c>AW3225QF</c>. Fallback matcher, not identity.</summary>
    public required string FriendlyName { get; init; }

    /// <summary>Three-letter PNP vendor code decoded from EDID, e.g. <c>DEL</c>.</summary>
    public string? VendorCode { get; init; }

    /// <summary>EDID product code.</summary>
    public ushort ProductCode { get; init; }

    /// <summary>True when this monitor is part of the current desktop topology.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Connector type, e.g. DisplayPort or HDMI. Secondary fallback matcher.</summary>
    public required string ConnectorType { get; init; }

    /// <summary>Current mode when active; null when detached.</summary>
    public DisplayMode? CurrentMode { get; init; }

    /// <summary>Monitor's preferred (native) mode, queried via GET_TARGET_PREFERRED_MODE.</summary>
    public DisplayMode? PreferredMode { get; init; }

    /// <summary>Source position on the virtual desktop when active.</summary>
    public DisplayPosition? Position { get; init; }

    /// <summary>True when this monitor's source is at (0,0) — i.e. it is the primary.</summary>
    public bool IsPrimary => IsActive && Position is { IsOrigin: true };

    public string? Rotation { get; init; }

    public HdrState Hdr { get; init; } = HdrState.Unknown;

    // ---- Volatile diagnostics. Never persist these as identity. ----

    /// <summary>GDI name such as <c>\\.\DISPLAY1</c>. Unstable across reboots.</summary>
    public string? GdiName { get; init; }

    /// <summary>Adapter LUID as text. Changes on every reboot and driver restart.</summary>
    public required string AdapterLuid { get; init; }

    public string? AdapterDevicePath { get; init; }

    public uint SourceId { get; init; }

    public uint TargetId { get; init; }
}
