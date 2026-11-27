// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Text.Json;
using SimpleScanner.Dsp;

namespace SimpleScanner;

/// <summary>What is kept for one radio, by its serial number: how it is set up and where it was last tuned.</summary>
public sealed class RadioSettings
{
    /// <summary>Run this radio when it is plugged in. With several marked, each runs at once.</summary>
    public bool Use { get; set; }
    public string Antenna { get; set; } = "Antenna A";
    public bool BiasT { get; set; }
    public bool FmNotch { get; set; }
    public double Ppm { get; set; }
    public bool AutoGain { get; set; } = true;
    public int GainStep { get; set; }
    public double LevelOffsetDb { get; set; }
    public double SquelchDb { get; set; } = -100;
    public bool SquelchOn { get; set; } = true;
    public bool AutoSquelch { get; set; }
    public double Volume { get; set; } = 0.5;
    /// <summary>Where this radio's sound sits between the left (-1) and the right (1) speaker.</summary>
    public double Pan { get; set; }
    public double FrequencyHz { get; set; } = 162_400_000;
    public bool AutoMode { get; set; } = true;
    public Mode Mode { get; set; } = Mode.Nfm;
    public double BandwidthHz { get; set; } = BandPlan.NfmWide;
    public double StepHz { get; set; }
    public bool Stereo { get; set; } = true;
    /// <summary>
    /// The memory groups ticked for this radio: the ones it shows and scans. Null (never set) takes the groups
    /// that were shared. Empty is none when <see cref="NoGroups"/> is set; without it, as a build before 0.1.6
    /// wrote it, empty is all of them.
    /// </summary>
    public List<string>? Groups { get; set; }
    /// <summary>No group is ticked: the radio shows and scans nothing until one is (see <see cref="Groups"/>).</summary>
    public bool NoGroups { get; set; }
    /// <summary>Multi Monitor: a scan or search holds up to four active channels of a window at once.</summary>
    public bool MultiMonitor { get; set; }
}

/// <summary>What is kept between runs, in Application Support (macOS), ~/.local/share (Linux)
/// or %LOCALAPPDATA% (Windows).</summary>
public sealed class Settings
{
    /// <summary>Where the main window was and how big, in screen pixels and in layout units.</summary>
    public sealed record Place(int X, int Y, double Width, double Height);
    public Place? Window { get; set; }

    // The fields of one radio from here to LevelOffsetDb are from when there was one radio only.
    // What is used now is in Radios, by serial number. These are still written, with what the first
    // radio has, so that a build from before can open the file.
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
    /// <summary>Each radio's own settings, by its serial number.</summary>
    public Dictionary<string, RadioSettings> Radios { get; set; } = [];
    /// <summary>The radio a scan group is given to: group name to serial number. A group not here goes to any radio.</summary>
    public Dictionary<string, string> GroupRadio { get; set; } = [];
    /// <summary>The memory group shown, which is also the one scanned.</summary>
    public string? Group { get; set; }
    /// <summary>The memory groups shown and scanned; empty or null is all of them. Replaces <see cref="Group"/>.</summary>
    public List<string>? Groups { get; set; }
    /// <summary>Sound output device by name; null is the system's default.</summary>
    public string? AudioDevice { get; set; }
    /// <summary>The look: "System" follows the macOS appearance; "Light" or "Dark" keeps to one.</summary>
    public string Appearance { get; set; } = "System";
    /// <summary>In the Light look the display follows it (dark amber on cream); otherwise it stays dark, as a scanner's LCD.</summary>
    public bool DisplayFollowsLight { get; set; }
    /// <summary>The newer release whose note was hidden with its ✕; a later one is noted again.</summary>
    public string? UpdateDismissedVersion { get; set; }
    /// <summary>Widths of the channel editor's columns that the user has changed, by column key.</summary>
    public Dictionary<string, double>? ChannelColumns { get; set; }
    /// <summary>Minutes an avoided frequency stays out of the scan; 0 is until the app is closed.</summary>
    public int AvoidMinutes { get; set; } = 30;
    /// <summary>Seconds a scan stays on a busy channel before it carries on; 0 is until the signal drops.</summary>
    public int ResumeSeconds { get; set; }
    /// <summary>Multi Monitor: seconds a slot keeps its channel after the signal has dropped, for every radio.</summary>
    public int MultiHangSeconds { get; set; } = DefaultMultiHangSeconds;
    public const int DefaultMultiHangSeconds = 8;
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

    /// <summary>The one-radio fields as a radio's settings.</summary>
    public RadioSettings Flat() => new()
    {
        Antenna = Antenna, BiasT = BiasT, FmNotch = FmNotch, Ppm = Ppm, AutoGain = AutoGain, GainStep = GainStep,
        LevelOffsetDb = LevelOffsetDb, SquelchDb = SquelchDb, SquelchOn = SquelchOn, AutoSquelch = AutoSquelch,
        Volume = Volume, FrequencyHz = FrequencyHz, AutoMode = AutoMode, Mode = Mode, BandwidthHz = BandwidthHz,
        StepHz = StepHz, Stereo = Stereo,
    };

    /// <summary>Write a radio's settings to the one-radio fields, for a build from before.</summary>
    public void SetFlat(RadioSettings r)
    {
        Antenna = r.Antenna; BiasT = r.BiasT; FmNotch = r.FmNotch; Ppm = r.Ppm; AutoGain = r.AutoGain; GainStep = r.GainStep;
        LevelOffsetDb = r.LevelOffsetDb; SquelchDb = r.SquelchDb; SquelchOn = r.SquelchOn; AutoSquelch = r.AutoSquelch;
        Volume = r.Volume; FrequencyHz = r.FrequencyHz; AutoMode = r.AutoMode; Mode = r.Mode; BandwidthHz = r.BandwidthHz;
        StepHz = r.StepHz; Stereo = r.Stereo;
    }

    /// <summary>
    /// The settings for the radio the window itself runs: its own if it has been used before; otherwise
    /// those of the radio used last, which is what a file from before several radios holds.
    /// </summary>
    public RadioSettings ForFirstRadio(string? serial) =>
        serial is not null && Radios.TryGetValue(serial, out var r) ? r : Flat();

    /// <summary>The entry for that radio, made if there is none.</summary>
    public RadioSettings Entry(string serial)
    {
        if (!Radios.TryGetValue(serial, out var r)) Radios[serial] = r = new RadioSettings();
        return r;
    }

    // A file from before several radios: what it holds belongs to the radio last used.
    private void Lift()
    {
        Radios ??= [];
        GroupRadio ??= [];
        if (Radios.Count == 0 && RadioSerial is { Length: > 0 } serial) Radios[serial] = Flat();
    }

    private static string FilePath => Path.Combine(AppFiles.Folder, "settings.json");
    private string _path = FilePath;

    /// <param name="path">The file, when it is not the usual one (for trying things on a copy).</param>
    public static Settings Load(string? path = null)
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path ?? FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { s = new(); }
        s._path = path ?? FilePath;
        s.Lift();
        return s;
    }

    public void Save()
    {
        try
        {
            if (Path.GetDirectoryName(_path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
