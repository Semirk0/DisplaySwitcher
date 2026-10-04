namespace DisplaySwitcher.Display;

/// <summary>
/// Describes where an active display should be positioned within the virtual desktop,
/// its explicit resolution, and whether it should be the Windows primary display.
/// </summary>
public sealed record DisplayPlacement(string DevicePath, int X, int Y, bool IsPrimary, uint Width = 0, uint Height = 0);