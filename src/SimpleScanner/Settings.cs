using System.Text.Json;
using SimpleScanner.Dsp;

namespace SimpleScanner;

/// <summary>What is kept between runs, in Application Support (macOS), ~/.local/share (Linux)
/// or %LOCALAPPDATA% (Windows).</summary>
public sealed class Settings
{
    /// <summary>Where the main window was and how big, in screen pixels and in layout units.</summary>
    public sealed record Place(int X, int Y, double Width, double Height);
    public Place? Window { get; set; }
    /// <summary>The same for the small window, which has a size of its own.</summary>
    public Place? MiniWindow { get; set; }
    /// <summary>The small window was showing when the app was closed.</summary>
    public bool Mini { get; set; }

    public double FrequencyHz { get; set; } = 162_400_000;
    /// <summary>Take the mode, filter width and step from the band plan.</summary>
    public bool AutoMode { get; set; } = true;
    public Mode Mode { get; set; } = Mode.Nfm;
    public double BandwidthHz { get; set; } = BandPlan.NfmWide;
    /// <summary>Tuning step in Hz; 0 follows the band plan.</summary>
    public double StepHz { get; set; }
    public double Volume { get; set; } = 0.5;
    public double SquelchDb { get; set; } = -100;
    public bool SquelchOn { get; set; } = true;
    /// <summary>Keep the squelch shut on noise that is above its level (the Auto button).</summary>
    public bool AutoSquelch { get; set; }
    /// <summary>Decode stereo on broadcast FM stations that send it.</summary>
    public bool Stereo { get; set; } = true;
    public bool AutoGain { get; set; } = true;
    public int GainStep { get; set; }
    /// <summary>Serial number of the radio last chosen, offered first when more than one is plugged in.</summary>
    public string? RadioSerial { get; set; }
    public string Antenna { get; set; } = "Antenna A";
    /// <summary>DC on the antenna socket for a preamplifier there (Antenna B on an RSPdx).</summary>
    public bool BiasT { get; set; }
    /// <summary>The radio's notch filter for the FM broadcast band.</summary>
    public bool FmNotch { get; set; }
    /// <summary>Correction for the radio's reference oscillator, in parts per million.</summary>
    public double Ppm { get; set; }
    public double LevelOffsetDb { get; set; }
    /// <summary>The memory group shown, which is also the one scanned.</summary>
    public string? Group { get; set; }
    /// <summary>The memory groups shown and scanned; empty or null is all of them. Replaces <see cref="Group"/>.</summary>
    public List<string>? Groups { get; set; }
    /// <summary>Sound output device by name; null is the system's default.</summary>
    public string? AudioDevice { get; set; }
    /// <summary>Widths of the channel editor's columns that the user has changed, by column key.</summary>
    public Dictionary<string, double>? ChannelColumns { get; set; }
    /// <summary>Minutes an avoided frequency stays out of the scan; 0 is until the app is closed.</summary>
    public int AvoidMinutes { get; set; } = 30;
    /// <summary>Seconds a scan stays on a busy channel before it carries on; 0 is until the signal drops.</summary>
    public int ResumeSeconds { get; set; }
    /// <summary>The RadioReference browser's account name and the place last looked at (numbers are RadioReference's own).
    /// The password is not kept here: see <see cref="RadioReference.RrPassword"/>.</summary>
    public sealed class RrPlace
    {
        public string? Username { get; set; }
        public string? Zip { get; set; }
        public double? HomeLat { get; set; }
        public double? HomeLon { get; set; }
        public int Country { get; set; }
        public int State { get; set; }
        public int County { get; set; }
    }
    public RrPlace? RadioReference { get; set; }
    public double SearchFromMHz { get; set; } = 144;
    public double SearchToMHz { get; set; } = 148;

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleScanner");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
