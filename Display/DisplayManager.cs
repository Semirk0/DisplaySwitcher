using System.Runtime.InteropServices;
using static DisplaySwitcher.Display.NativeMethods;

namespace DisplaySwitcher.Display;

/// <summary>
/// Manages display topology using the Windows CCD (Connecting and Configuring Displays) API.
/// Applies changes by taking the full current topology from QueryDisplayConfig and mutating
/// only the active flags and source positions for the paths we care about, leaving every other
/// path/mode entry (including modeInfoIdx references) completely untouched. This avoids the
/// ERROR_INVALID_PARAMETER (87) failures caused by compacting/reindexing the arrays.
/// </summary>
public sealed class DisplayManager
{
    public List<DisplayPathInfo> GetAllPaths()
    {
        var (paths, _) = QueryAllPaths();
        var result = new List<DisplayPathInfo>();

        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            var devicePath = TryGetTargetDevicePath(path.targetInfo.adapterId, path.targetInfo.id, out var friendlyName);

            result.Add(new DisplayPathInfo(
                PathIndex: i,
                DevicePath: devicePath,
                FriendlyName: friendlyName,
                IsActive: (path.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0,
                AdapterId: path.targetInfo.adapterId,
                SourceId: path.sourceInfo.id,
                TargetId: path.targetInfo.id));
        }

        return result;
    }

    /// <summary>
    /// Applies a topology where only the given displays are active, each at its specified
    /// position, with exactly one marked primary. Every other known path in the full topology
    /// is deactivated. Only active flags and source mode positions are mutated; everything else
    /// (including modeInfoIdx and unrelated paths/modes) is passed through unchanged.
    /// </summary>
    public void ApplyTopology(IReadOnlyList<DisplayPlacement> displays)
    {
        if (displays.Count(d => d.IsPrimary) != 1)
        {
            throw new ArgumentException("Exactly one display must be marked as primary.", nameof(displays));
        }

        var (pathArray, modeArray) = QueryAllPaths(QDC_ALL_PATHS);
        var paths = pathArray.ToList();
        var modes = modeArray.ToList();

        var placementByIndex = new Dictionary<int, DisplayPlacement>();
        var usedIndices = new HashSet<int>();
        var usedSourceIds = new HashSet<(uint LowPart, int HighPart, uint SourceId)>();

        foreach (var placement in displays)
        {
            var normalized = placement.DevicePath.Trim();
            var found = false;

            for (var i = 0; i < paths.Count; i++)
            {
                if (usedIndices.Contains(i))
                {
                    continue;
                }

                var dp = TryGetTargetDevicePath(paths[i].targetInfo.adapterId, paths[i].targetInfo.id, out _);
                if (string.IsNullOrEmpty(dp) || !dp.Trim().Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sourceKey = (paths[i].sourceInfo.adapterId.LowPart, paths[i].sourceInfo.adapterId.HighPart, paths[i].sourceInfo.id);
                if (usedSourceIds.Contains(sourceKey))
                {
                    // This candidate path's source id is already claimed by another display
                    // we're activating simultaneously; keep searching for a different candidate
                    // path for the same monitor that uses a distinct source id.
                    continue;
                }

                placementByIndex[i] = placement;
                usedIndices.Add(i);
                usedSourceIds.Add(sourceKey);
                found = true;
                break;
            }

            if (!found)
            {
                throw new InvalidOperationException($"Could not find a display path matching '{placement.DevicePath}' with an available (non-conflicting) source id. Run list-displays to see available paths.");
            }
        }

        // Reorder FIRST, before any other mutation, so all subsequent index-based bookkeeping
        // operates on the final array positions. Swapping paths[0] and paths[primaryIndex] after
        // other mutations were already keyed by the old indices would silently scramble which
        // struct each mutation actually landed on.
        var primaryIndex = placementByIndex.First(kvp => kvp.Value.IsPrimary).Key;
        if (primaryIndex != 0)
        {
            (paths[0], paths[primaryIndex]) = (paths[primaryIndex], paths[0]);

            // Rebuild placementByIndex to reflect the new positions after the swap.
            var updated = new Dictionary<int, DisplayPlacement>();
            foreach (var (oldIndex, placement) in placementByIndex)
            {
                var newIndex = oldIndex == 0 ? primaryIndex : oldIndex == primaryIndex ? 0 : oldIndex;
                updated[newIndex] = placement;
            }
            placementByIndex = updated;
        }

        // Set active flags: active for requested displays, inactive for everything else.
        for (var i = 0; i < paths.Count; i++)
        {
            var isRequested = placementByIndex.ContainsKey(i);
            var wasActive = (paths[i].flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;

            if (isRequested)
            {
                var p = paths[i];
                p.flags |= DISPLAYCONFIG_PATH_ACTIVE;
                paths[i] = p;
            }
            else if (wasActive)
            {
                var p = paths[i];
                p.flags &= unchecked((uint)~DISPLAYCONFIG_PATH_ACTIVE);
                paths[i] = p;
            }
        }

        // Repair invalid source/target mode indices by synthesizing fresh mode entries.
        foreach (var (pathIndex, placement) in placementByIndex)
        {
            var path = paths[pathIndex];

            if (path.targetInfo.modeInfoIdx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
            {
                var targetModeIdx = EnsureTargetMode(modes, path.targetInfo.adapterId, path.targetInfo.id);
                path.targetInfo.modeInfoIdx = targetModeIdx;
            }

            if (path.sourceInfo.modeInfoIdx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID)
            {
                var width = modes[(int)path.targetInfo.modeInfoIdx].modeInfo.targetMode.targetVideoSignalInfo.activeSize.cx;
                var height = modes[(int)path.targetInfo.modeInfoIdx].modeInfo.targetMode.targetVideoSignalInfo.activeSize.cy;

                var sourceModeIdx = EnsureSourceMode(modes, path.sourceInfo.adapterId, path.sourceInfo.id, width, height);
                path.sourceInfo.modeInfoIdx = sourceModeIdx;
            }

            paths[pathIndex] = path;
        }

        // Apply explicit position (and resolution, if specified) to each requested display's source mode.
        foreach (var (pathIndex, placement) in placementByIndex)
        {
            var sourceId = paths[pathIndex].sourceInfo.id;
            var adapterId = paths[pathIndex].sourceInfo.adapterId;

            for (var i = 0; i < modes.Count; i++)
            {
                var m = modes[i];
                if (m.infoType == DISPLAYCONFIG_MODE_INFO_TYPE.SOURCE &&
                    m.id == sourceId &&
                    m.adapterId.Equals(adapterId))
                {
                    m.modeInfo.sourceMode.position = new POINTL { x = placement.X, y = placement.Y };

                    if (placement.Width > 0 && placement.Height > 0)
                    {
                        m.modeInfo.sourceMode.width = placement.Width;
                        m.modeInfo.sourceMode.height = placement.Height;
                    }

                    modes[i] = m;
                }
            }
        }

        Console.WriteLine("=== Pre-submit state for requested displays ===");
        foreach (var (pathIndex, placement) in placementByIndex)
        {
            var p = paths[pathIndex];
            Console.WriteLine($"Path {pathIndex} ({placement.DevicePath}): active={(p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0} source.id={p.sourceInfo.id} source.modeIdx={p.sourceInfo.modeInfoIdx} target.modeIdx={p.targetInfo.modeInfoIdx} modes.Count={modes.Count}");
        }
        Console.WriteLine("================================================");

        TryValidateAndApply(paths.ToArray(), modes.ToArray(), "ApplyTopology");
    }

    /// <summary>
    /// Disables exactly one target display, leaving every other path/mode entry unmodified.
    /// Retained from diagnostic testing; useful for quick manual checks.
    /// </summary>
    public void DisableSingleDisplay(string devicePath)
    {
        var (paths, modes) = QueryAllPaths(QDC_ALL_PATHS);
        var targetIndex = FindFirstPathIndex(paths, devicePath);

        paths[targetIndex].flags &= unchecked((uint)~DISPLAYCONFIG_PATH_ACTIVE);

        TryValidateAndApply(paths, modes, "disable-only");
    }

    /// <summary>
    /// Enables exactly one target display and sets its source mode position explicitly, leaving
    /// every other path/mode entry unmodified. Retained from diagnostic testing.
    /// </summary>
    public void EnableSingleDisplayAt(string devicePath, int x, int y)
    {
        var (paths, modes) = QueryAllPaths(QDC_ALL_PATHS);
        var targetIndex = FindFirstPathIndex(paths, devicePath);

        paths[targetIndex].flags |= DISPLAYCONFIG_PATH_ACTIVE;

        var sourceId = paths[targetIndex].sourceInfo.id;
        var adapterId = paths[targetIndex].sourceInfo.adapterId;

        for (var i = 0; i < modes.Length; i++)
        {
            if (modes[i].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.SOURCE &&
                modes[i].id == sourceId &&
                modes[i].adapterId.Equals(adapterId))
            {
                modes[i].modeInfo.sourceMode.position = new POINTL { x = x, y = y };
            }
        }

        TryValidateAndApply(paths, modes, $"enable + position ({x},{y})");
    }

    /// <summary>
    /// Prints the active flag, source id, and modeInfoIdx for every known real display path
    /// (those with a resolvable device path), to inspect state right before a failing
    /// ApplyTopology call.
    /// </summary>
    public void DumpRawTopology()
    {
        var (paths, modes) = QueryAllPaths(QDC_ALL_PATHS);

        for (var i = 0; i < paths.Length; i++)
        {
            var p = paths[i];
            var devicePath = TryGetTargetDevicePath(p.targetInfo.adapterId, p.targetInfo.id, out var friendlyName);

            if (string.IsNullOrEmpty(devicePath))
            {
                continue;
            }

            Console.WriteLine($"[Path {i}] {friendlyName}  active={(p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0}");
            Console.WriteLine($"  source.id={p.sourceInfo.id} source.modeIdx={p.sourceInfo.modeInfoIdx}");
            Console.WriteLine($"  target.id={p.targetInfo.id} target.modeIdx={p.targetInfo.modeInfoIdx}");

            var srcValid = p.sourceInfo.modeInfoIdx < modes.Length &&
                           modes[p.sourceInfo.modeInfoIdx].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.SOURCE;
            var tgtValid = p.targetInfo.modeInfoIdx < modes.Length &&
                           modes[p.targetInfo.modeInfoIdx].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.TARGET;

            Console.WriteLine($"  source.modeIdx valid={srcValid}  target.modeIdx valid={tgtValid}");

            if (srcValid)
            {
                var sm = modes[p.sourceInfo.modeInfoIdx].modeInfo.sourceMode;
                Console.WriteLine($"  -> {sm.width}x{sm.height} @ ({sm.position.x},{sm.position.y})");
            }

            Console.WriteLine();
        }
    }

    private static int FindFirstPathIndex(DISPLAYCONFIG_PATH_INFO[] paths, string devicePath)
    {
        var normalized = devicePath.Trim();
        for (var i = 0; i < paths.Length; i++)
        {
            var dp = TryGetTargetDevicePath(paths[i].targetInfo.adapterId, paths[i].targetInfo.id, out _);
            if (!string.IsNullOrEmpty(dp) && dp.Trim().Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Could not find a display path matching '{devicePath}'.");
    }

    private static void TryValidateAndApply(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, string label)
    {
        var baseFlags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES;

        var validateResult = SetDisplayConfig(
            (uint)paths.Length, paths, (uint)modes.Length, modes,
            baseFlags | SDC_VALIDATE);

        Console.WriteLine($"[{label}] VALIDATE -> {validateResult} ({(validateResult == ERROR_SUCCESS ? "OK" : "FAILED")})");

        if (validateResult != ERROR_SUCCESS)
        {
            throw new InvalidOperationException($"Topology validation failed with code {validateResult}.");
        }

        var applyResult = SetDisplayConfig(
            (uint)paths.Length, paths, (uint)modes.Length, modes,
            baseFlags | SDC_APPLY | SDC_SAVE_TO_DATABASE);

        Console.WriteLine($"[{label}] APPLY -> {applyResult} ({(applyResult == ERROR_SUCCESS ? "OK" : "FAILED")})");

        if (applyResult != ERROR_SUCCESS)
        {
            throw new InvalidOperationException($"Topology apply failed with code {applyResult}.");
        }
    }

    private static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) QueryAllPaths(
        uint flags = QDC_ALL_PATHS)
    {
        int result;
        uint pathCount, modeCount;
        DISPLAYCONFIG_PATH_INFO[] paths;
        DISPLAYCONFIG_MODE_INFO[] modes;

        do
        {
            result = GetDisplayConfigBufferSizes(flags, out pathCount, out modeCount);
            if (result != ERROR_SUCCESS)
            {
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed with error code {result}.");
            }

            paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            result = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        } while (result == ERROR_INSUFFICIENT_BUFFER);

        if (result != ERROR_SUCCESS)
        {
            throw new InvalidOperationException($"QueryDisplayConfig failed with error code {result}.");
        }

        if (paths.Length != pathCount)
        {
            Array.Resize(ref paths, (int)pathCount);
        }
        if (modes.Length != modeCount)
        {
            Array.Resize(ref modes, (int)modeCount);
        }

        return (paths, modes);
    }

    private static string TryGetTargetDevicePath(LUID adapterId, uint targetId, out string friendlyName)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = targetId
            }
        };

        var result = DisplayConfigGetDeviceInfo(ref request);
        if (result != ERROR_SUCCESS)
        {
            friendlyName = string.Empty;
            return string.Empty;
        }

        friendlyName = request.monitorFriendlyDeviceName;
        return request.monitorDevicePath;
    }

    private static uint EnsureTargetMode(List<DISPLAYCONFIG_MODE_INFO> modes, LUID adapterId, uint targetId)
    {
        // Reuse an existing valid target mode for this target id/adapter if one exists anywhere
        // in the array (e.g. an inactive duplicate path may still carry a usable entry).
        for (var i = 0; i < modes.Count; i++)
        {
            if (modes[i].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.TARGET &&
                modes[i].id == targetId &&
                modes[i].adapterId.Equals(adapterId))
            {
                return (uint)i;
            }
        }

        // Fall back to querying the monitor's preferred mode directly from the driver.
        var request = new DISPLAYCONFIG_TARGET_PREFERRED_MODE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_TARGET_PREFERRED_MODE,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_PREFERRED_MODE>(),
                adapterId = adapterId,
                id = targetId
            }
        };

        var result = DisplayConfigGetDeviceInfo(ref request);
        if (result != ERROR_SUCCESS)
        {
            throw new InvalidOperationException($"Could not query preferred mode for target {targetId} (error {result}).");
        }

        var newMode = new DISPLAYCONFIG_MODE_INFO
        {
            infoType = DISPLAYCONFIG_MODE_INFO_TYPE.TARGET,
            id = targetId,
            adapterId = adapterId,
            modeInfo = new DISPLAYCONFIG_MODE_INFO_UNION
            {
                targetMode = request.targetMode
            }
        };

        modes.Add(newMode);
        return (uint)(modes.Count - 1);
    }

    private static uint EnsureSourceMode(List<DISPLAYCONFIG_MODE_INFO> modes, LUID adapterId, uint sourceId, uint width, uint height)
    {
        for (var i = 0; i < modes.Count; i++)
        {
            if (modes[i].infoType == DISPLAYCONFIG_MODE_INFO_TYPE.SOURCE &&
                modes[i].id == sourceId &&
                modes[i].adapterId.Equals(adapterId))
            {
                return (uint)i;
            }
        }

        const uint DISPLAYCONFIG_PIXELFORMAT_32BPP = 4;

        var newMode = new DISPLAYCONFIG_MODE_INFO
        {
            infoType = DISPLAYCONFIG_MODE_INFO_TYPE.SOURCE,
            id = sourceId,
            adapterId = adapterId,
            modeInfo = new DISPLAYCONFIG_MODE_INFO_UNION
            {
                sourceMode = new DISPLAYCONFIG_SOURCE_MODE
                {
                    width = width,
                    height = height,
                    pixelFormat = DISPLAYCONFIG_PIXELFORMAT_32BPP,
                    position = new POINTL { x = 0, y = 0 }
                }
            }
        };

        modes.Add(newMode);
        return (uint)(modes.Count - 1);
    }
}