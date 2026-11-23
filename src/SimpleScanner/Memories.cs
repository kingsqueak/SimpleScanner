using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleScanner.Dsp;

namespace SimpleScanner;

/// <summary>
/// A stored channel: a name, a frequency, the groups it belongs to, whether it is scanned, and
/// whatever is known about who uses it (typed in, or from a RadioReference download).
/// </summary>
public sealed class MemoryChannel
{
    /// <summary>The short name on the display (RadioReference's alpha tag).</summary>
    public string Name { get; set; } = "";
    public double FrequencyHz { get; set; }
    /// <summary>The scan groups this channel is in: none, one or several.</summary>
    public List<string> Groups { get; set; } = [];
    /// <summary>Read only from files written before a channel could be in several groups.</summary>
    public string? Group
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) && !Groups.Contains(value)) Groups.Add(value);
        }
    }
    public bool Scan { get; set; } = true;
    /// <summary>Null takes the mode and filter from the band plan.</summary>
    public Mode? Mode { get; set; }
    public double? BandwidthHz { get; set; }

    // What is known about the channel. Shown on the display; none of it changes how it is received.
    /// <summary>Who it belongs to: a town, an agency, a category of business.</summary>
    public string? Agency { get; set; }
    public string? Description { get; set; }
    /// <summary>The kind of traffic: "Fire Dispatch", "Law Tac", "Aircraft".</summary>
    public string? Tag { get; set; }
    /// <summary>What is sent, in RadioReference's words: FMN, P25, DMR, NXDN, Motorola.</summary>
    public string? Format { get; set; }
    /// <summary>Tone or digital code: "136.5 PL", "125 DPL", "293 NAC", "CC 7 TG 500 SL 1".</summary>
    public string? Tone { get; set; }
    /// <summary>The FCC licence's callsign.</summary>
    public string? Callsign { get; set; }
    /// <summary>The repeater's input frequency, when there is one.</summary>
    public double? InputHz { get; set; }
    /// <summary>FCC station class: BM base and mobile, RM repeater and mobile, M mobile, B base, F fixed.</summary>
    public string? StationClass { get; set; }
    /// <summary>For an imported channel, the row it came from, so that importing the file again does not add it twice.</summary>
    public string? ImportKey { get; set; }
}

/// <summary>The stored channels, kept as memories.json beside the settings.</summary>
public sealed class MemoryStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    public List<MemoryChannel> Channels { get; private set; } = [];
    /// <summary>Counts the saves, so a window can tell that another has changed the channels.</summary>
    public int Version { get; private set; }

    public MemoryStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleScanner", "memories.json");

    public static MemoryStore Load(string? path = null)
    {
        var store = new MemoryStore(path);
        try { store.Channels = JsonSerializer.Deserialize<List<MemoryChannel>>(File.ReadAllText(store._path), Json) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return store;
    }

    public void Save()
    {
        Version++;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Channels, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The stored channel on this frequency (within 100 Hz), if any.</summary>
    public MemoryChannel? Find(double hz) => Channels.FirstOrDefault(c => Math.Abs(c.FrequencyHz - hz) < 100);

    /// <summary>Every stored channel on this frequency: one frequency is often shared by several users.</summary>
    public List<MemoryChannel> FindAll(double hz) => Channels.Where(c => Math.Abs(c.FrequencyHz - hz) < 100).ToList();

    /// <summary>Every group name in use, in order.</summary>
    public List<string> GroupNames() =>
        Channels.SelectMany(c => c.Groups).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
}
