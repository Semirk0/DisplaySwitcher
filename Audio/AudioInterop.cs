using System.Runtime.InteropServices;

namespace DisplaySwitcher.Audio;

internal enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
    int GetState(out int pdwState);
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
    int GetDefaultAudioEndpoint(int dataFlow, ERole role, out IMMDevice ppEndpoint);
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
    int RegisterEndpointNotificationCallback(IntPtr pClient);
    int UnregisterEndpointNotificationCallback(IntPtr pClient);
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
    int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
    int ResetDeviceFormat(string pszDeviceName);
    int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
    int GetProcessingPeriod(string pszDeviceName, bool bDefault, out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);
    int SetProcessingPeriod(string pszDeviceName, long hnsDevicePeriod);
    int GetShareMode(string pszDeviceName, IntPtr pMode);
    int SetShareMode(string pszDeviceName, IntPtr mode);
    int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
    int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
    int SetDefaultEndpoint(string pszDeviceName, ERole role);
    int SetEndpointVisibility(string pszDeviceName, bool bVisible);
}

[ComImport]
[Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal class CPolicyConfigClient
{
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    int GetCount(out int pcDevices);
    int Item(int nDevice, out IMMDevice ppDevice);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;

    public PROPERTYKEY(Guid fmtid, uint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public IntPtr p;
    public int p2;
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    int GetCount(out int cProps);
    int GetAt(int iProp, out PROPERTYKEY pkey);
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
    int Commit();
}

internal static class AudioEndpointResolver
{
    public const int EDataFlow_eRender = 0;
    public const int DEVICE_STATE_ACTIVE = 0x00000001;
    private const int STGM_READ = 0x00000000;
    private const ushort VT_LPWSTR = 31;

    // PKEY_Device_FriendlyName
    private static readonly PROPERTYKEY PKEY_Device_FriendlyName =
        new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    /// <summary>
    /// Enumerates active render (output) endpoint IDs with their friendly name
    /// (e.g. "Speakers (AMD High Definition Audio Device)"), resolved via IPropertyStore.
    /// </summary>
    public static IEnumerable<(string Id, string FriendlyName)> EnumerateRenderEndpoints()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        var hr = enumerator.EnumAudioEndpoints(EDataFlow_eRender, DEVICE_STATE_ACTIVE, out var collectionPtr);
        if (hr != 0 || collectionPtr == IntPtr.Zero)
        {
            yield break;
        }

        var collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(collectionPtr);
        collection.GetCount(out var count);

        for (var i = 0; i < count; i++)
        {
            collection.Item(i, out var device);
            device.GetId(out var id);
            var friendlyName = GetFriendlyName(device);
            yield return (id, friendlyName);
        }
    }

    private static string GetFriendlyName(IMMDevice device)
    {
        var hr = device.OpenPropertyStore(STGM_READ, out var propertyStore);
        if (hr != 0 || propertyStore is null)
        {
            device.GetId(out var fallbackId);
            return fallbackId;
        }

        var key = PKEY_Device_FriendlyName;
        hr = propertyStore.GetValue(ref key, out var propVariant);
        if (hr != 0 || propVariant.vt != VT_LPWSTR || propVariant.p == IntPtr.Zero)
        {
            device.GetId(out var fallbackId);
            return fallbackId;
        }

        try
        {
            return Marshal.PtrToStringUni(propVariant.p) ?? string.Empty;
        }
        finally
        {
            PropVariantClear(ref propVariant);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);
}