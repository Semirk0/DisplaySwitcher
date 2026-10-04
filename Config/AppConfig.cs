using System.Text.Json;

namespace DisplaySwitcher.Config;

public sealed class AppConfig
{
    public DisplaysConfig Displays { get; set; } = new();
    public AudioConfig Audio { get; set; } = new();

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configuration file not found: {path}");
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to parse appsettings.json.");
    }
}

public sealed class DisplaysConfig
{
    public string PrimaryWorkMonitor { get; set; } = string.Empty;
    public string SecondaryMonitor { get; set; } = string.Empty;
    public string Tv { get; set; } = string.Empty;
}

public sealed class AudioConfig
{
    public string SpeakersDeviceId { get; set; } = string.Empty;
    public string TvDeviceId { get; set; } = string.Empty;
}