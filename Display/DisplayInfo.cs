namespace DisplaySwitcher.Display;

public sealed record DisplayPathInfo(
    int PathIndex,
    string DevicePath,
    string FriendlyName,
    bool IsActive,
    NativeMethods.LUID AdapterId,
    uint SourceId,
    uint TargetId);