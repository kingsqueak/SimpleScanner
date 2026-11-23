using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleScanner.Dsp;

namespace SimpleScanner;

/// <summary>A stored channel: a name, a frequency, the group it belongs to, and whether it is scanned.</summary>
public sealed class MemoryChannel
{
    public string Name { get; set; } = "";
    public double FrequencyHz { get; set; }
    public string Group { get; set; } = "My channels";
    public bool Scan { get; set; } = true;
    /// <summary>Null takes the mode and filter from the band plan.</summary>
    public Mode? Mode { get; set; }
    public double? BandwidthHz { get; set; }
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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Channels, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The stored channel on this frequency (within 100 Hz), if any.</summary>
    public MemoryChannel? Find(double hz) => Channels.FirstOrDefault(c => Math.Abs(c.FrequencyHz - hz) < 100);
}
