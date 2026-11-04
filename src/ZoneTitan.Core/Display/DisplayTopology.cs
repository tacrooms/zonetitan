// -----------------------------------------------------------------------------
// The CCD query/marshal sequence here follows the approach in DisplayProfileManager
// (https://github.com/zac15987/DisplayProfileManager), src/Helpers/DisplayConfigHelper.cs.
//
//     MIT License
//     Copyright (c) 2025 zac15987
//
// Full license text: THIRD-PARTY-LICENSES.md at the repository root.
//
// Modified for ZoneTitan: shared query/lookup helpers extracted so the enumerator and
// the zone engine work from one implementation; device-path lookup added.
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;
using static ZoneTitan.Core.Native.CcdNative;

namespace ZoneTitan.Core.Display;

/// <summary>A queried topology: the raw path and mode arrays straight from CCD.</summary>
internal sealed class TopologySnapshot
{
    public required DISPLAYCONFIG_PATH_INFO[] Paths { get; init; }
    public required DISPLAYCONFIG_MODE_INFO[] Modes { get; init; }
}

/// <summary>Shared CCD query plumbing used by both the enumerator and the zone engine.</summary>
internal static class DisplayTopology
{
    /// <summary>
    /// Queries paths and modes, retrying when the topology shifts underneath us
    /// (hotplug or driver reset between sizing and querying).
    /// </summary>
    public static TopologySnapshot Query(QueryDisplayConfigFlags flags)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            var sizeResult = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (sizeResult != ERROR_SUCCESS)
            {
                throw new DisplayQueryException(
                    $"GetDisplayConfigBufferSizes failed: {DescribeError(sizeResult)}");
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            var queryResult = QueryDisplayConfig(
                flags, ref pathCount, paths, ref modeCount, modes, nint.Zero);

            if (queryResult == ERROR_SUCCESS)
            {
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
                return new TopologySnapshot { Paths = paths, Modes = modes };
            }

            if (queryResult == ERROR_INSUFFICIENT_BUFFER && attempt < maxAttempts)
                continue;

            throw new DisplayQueryException($"QueryDisplayConfig failed: {DescribeError(queryResult)}");
        }
    }

    /// <summary>Reads the monitor device path and friendly name for a path's target.</summary>
    public static bool TryGetTargetName(
        in DISPLAYCONFIG_PATH_INFO path,
        out DISPLAYCONFIG_TARGET_DEVICE_NAME targetName)
    {
        targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };

        return DisplayConfigGetDeviceInfo(ref targetName) == ERROR_SUCCESS;
    }

    /// <summary>
    /// Finds the index of the best path for a monitor device path, preferring an already
    /// active path. Returns -1 when the monitor is not attached.
    /// <para>
    /// This is the LUID re-mapping step from spec §4: the caller supplies a persisted device
    /// path, and gets back a path carrying whatever adapter LUID and target id the system
    /// happens to be using right now.
    /// </para>
    /// </summary>
    public static int FindPathIndex(TopologySnapshot topology, string devicePath)
    {
        var best = -1;

        for (var i = 0; i < topology.Paths.Length; i++)
        {
            ref readonly var path = ref topology.Paths[i];

            if (!path.targetInfo.IsTargetAvailable)
                continue;

            if (!TryGetTargetName(in path, out var name))
                continue;

            if (!string.Equals(name.monitorDevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
                continue;

            // An active path already carries valid mode indices, so prefer it.
            if (path.IsActive)
                return i;

            if (best < 0)
                best = i;
        }

        return best;
    }

    /// <summary>Every distinct attached target, as (pathIndex, devicePath) pairs.</summary>
    public static List<(int PathIndex, string DevicePath)> AttachedTargets(TopologySnapshot topology)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < topology.Paths.Length; i++)
        {
            ref readonly var path = ref topology.Paths[i];

            if (!path.targetInfo.IsTargetAvailable)
                continue;

            if (!TryGetTargetName(in path, out var name))
                continue;

            if (string.IsNullOrWhiteSpace(name.monitorDevicePath))
                continue;

            // Prefer active paths; otherwise first one wins.
            if (seen.TryGetValue(name.monitorDevicePath, out var existing) &&
                (topology.Paths[existing].IsActive || !path.IsActive))
            {
                continue;
            }

            seen[name.monitorDevicePath] = i;
        }

        return seen.Select(kv => (kv.Value, kv.Key)).OrderBy(t => t.Value).ToList();
    }

    public static void SetPathActive(ref DISPLAYCONFIG_PATH_INFO path, bool active)
    {
        if (active)
            path.flags |= (uint)DisplayConfigPathFlags.DISPLAYCONFIG_PATH_ACTIVE;
        else
            path.flags &= ~(uint)DisplayConfigPathFlags.DISPLAYCONFIG_PATH_ACTIVE;
    }

    /// <summary>Clears both mode indices so Windows selects modes itself.</summary>
    public static void ClearModeIndices(ref DISPLAYCONFIG_PATH_INFO path)
    {
        path.sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        path.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
    }
}
