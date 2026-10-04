using DisplaySwitcher.Audio;
using DisplaySwitcher.Config;
using DisplaySwitcher.Display;

var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();

try
{
    switch (command)
    {
        case "list-displays":
            ListDisplays();
            break;

        case "list-audio":
            ListAudioDevices();
            break;

        case "set-audio":
            new AudioManager().SetDefaultPlaybackDevice(args[1]);
            Console.WriteLine("Audio switched.");
            break;

        case "disable-tv":
            new DisplayManager().DisableSingleDisplay(args[1]);
            break;

        case "enable-tv-at":
            new DisplayManager().EnableSingleDisplayAt(args[1], int.Parse(args[2]), int.Parse(args[3]));
            break;

        case "gaming":
            RunGamingMode();
            break;

        case "work":
            RunWorkMode();
            break;

        case "dump-config":
            new DisplayManager().DumpRawTopology();
            break;

        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

return 0;

void ListDisplays()
{
    var manager = new DisplayManager();
    var paths = manager.GetAllPaths();

    Console.WriteLine($"{"Active",-8} {"DevicePath",-70} FriendlyName");
    foreach (var path in paths)
    {
        if (string.IsNullOrEmpty(path.DevicePath)) continue;
        Console.WriteLine($"{(path.IsActive ? "yes" : "no"),-8} {path.DevicePath,-70} {path.FriendlyName}");
    }
}

void ListAudioDevices()
{
    var manager = new AudioManager();
    foreach (var (id, friendlyName) in manager.ListPlaybackDevices())
    {
        Console.WriteLine($"{id}  {friendlyName}");
    }
}

void RunGamingMode()
{
    var config = AppConfig.Load(configPath);

    Console.WriteLine("Switching to gaming mode: TV only, primary...");
    var displayManager = new DisplayManager();
    displayManager.ApplyTopology(new[]
    {
        new DisplayPlacement(config.Displays.Tv, X: 0, Y: 0, IsPrimary: true, Width: 3840, Height: 2160)
    });

    if (!string.IsNullOrWhiteSpace(config.Audio.TvDeviceId))
    {
        Console.WriteLine("Switching audio output to TV...");
        new AudioManager().SetDefaultPlaybackDevice(config.Audio.TvDeviceId);
    }

    Console.WriteLine("Gaming mode applied.");
}

void RunWorkMode()
{
    var config = AppConfig.Load(configPath);

    Console.WriteLine("Switching to work mode: both monitors enabled, TV disabled...");
    var displayManager = new DisplayManager();
    displayManager.ApplyTopology(new[]
    {
        new DisplayPlacement(config.Displays.PrimaryWorkMonitor, X: 0, Y: 0, IsPrimary: true),
        new DisplayPlacement(config.Displays.SecondaryMonitor, X: 1920, Y: 0, IsPrimary: false)
    });

    Console.WriteLine("Switching audio output to speakers...");
    new AudioManager().SetDefaultPlaybackDevice(config.Audio.SpeakersDeviceId);

    Console.WriteLine("Work mode applied.");
}

void PrintUsage()
{
    Console.WriteLine("""
        DisplaySwitcher - switch display topology and default audio output.

        Usage:
          DisplaySwitcher.exe list-displays
          DisplaySwitcher.exe list-audio
          DisplaySwitcher.exe set-audio <deviceId>
          DisplaySwitcher.exe gaming
          DisplaySwitcher.exe work
        """);
}
