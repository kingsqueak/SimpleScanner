using System.Text.Json;
using SimpleScanner.Dsp;

namespace SimpleScanner;

/// <summary>What is kept between runs, in Application Support (macOS), ~/.local/share (Linux)
/// or %LOCALAPPDATA% (Windows).</summary>
public sealed class Settings
{
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
    /// <summary>Decode stereo on broadcast FM stations that send it.</summary>
    public bool Stereo { get; set; } = true;
    public bool AutoGain { get; set; } = true;
    public int GainStep { get; set; }
    public string Antenna { get; set; } = "Antenna A";
    public double LevelOffsetDb { get; set; }
    /// <summary>The memory group shown, which is also the one scanned.</summary>
    public string? Group { get; set; }
    /// <summary>The memory groups shown and scanned; empty or null is all of them. Replaces <see cref="Group"/>.</summary>
    public List<string>? Groups { get; set; }
    /// <summary>Sound output device by name; null is the system's default.</summary>
    public string? AudioDevice { get; set; }
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
