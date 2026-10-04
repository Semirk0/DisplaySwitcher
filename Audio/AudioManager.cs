namespace DisplaySwitcher.Audio;

/// <summary>
/// Sets the default audio render (playback) endpoint using the undocumented IPolicyConfig
/// COM interface (the same mechanism tools like SoundVolumeView use internally), avoiding
/// a dependency on an external executable.
/// </summary>
public sealed class AudioManager
{
    /// <summary>
    /// Sets the default playback device for Console, Multimedia, and Communications roles.
    /// </summary>
    /// <param name="deviceId">The audio endpoint ID (GUID string, e.g. "{0.0.0.00000000}.{guid}")
    /// or a partial/full friendly device path to match against enumerated endpoints.</param>
    public void SetDefaultPlaybackDevice(string deviceId)
    {
        var resolvedId = ResolveDeviceId(deviceId);

        var policyConfig = (IPolicyConfig)new CPolicyConfigClient();

        SetForAllRoles(policyConfig, resolvedId);
    }

    /// <summary>
    /// Lists active render (playback) endpoints with their raw endpoint IDs, useful for
    /// populating configuration since friendly names aren't resolved here.
    /// </summary>
    public IReadOnlyList<(string Id, string FriendlyName)> ListPlaybackDevices()
        => AudioEndpointResolver.EnumerateRenderEndpoints().ToList();

    private static void SetForAllRoles(IPolicyConfig policyConfig, string deviceId)
    {
        foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
        {
            var hr = policyConfig.SetDefaultEndpoint(deviceId, role);
            if (hr != 0)
            {
                throw new InvalidOperationException($"SetDefaultEndpoint failed for role {role} with HRESULT 0x{hr:X8}.");
            }
        }
    }

    private static string ResolveDeviceId(string deviceId)
    {
        // If it already looks like an endpoint ID (GUID-like), use as-is.
        if (deviceId.Contains('{'))
        {
            return deviceId;
        }

        // Otherwise, try to match against enumerated devices by friendly/interface name.
        var match = AudioEndpointResolver.EnumerateRenderEndpoints()
            .FirstOrDefault(d => d.FriendlyName.Contains(deviceId, StringComparison.OrdinalIgnoreCase)
                               || d.Id.Contains(deviceId, StringComparison.OrdinalIgnoreCase));

        if (match.Id is null)
        {
            throw new InvalidOperationException($"Could not resolve audio device '{deviceId}'. Run the list-audio command to see available endpoint IDs.");
        }

        return match.Id;
    }
}