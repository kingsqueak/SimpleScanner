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
    /// <summary>Where it is used: a town ("Manchester, NH"), a county, or "New Hampshire statewide". Every
    /// location in use is also a scan group, made by the program.</summary>
    public string? Location { get; set; }
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
        store._stamp = Stamp(store._path);
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
            // Written beside the file and then put in its place, so that nothing reading it finds half a file.
            string part = _path + ".part";
            File.WriteAllText(part, JsonSerializer.Serialize(Channels, Json));
            File.Move(part, _path, overwrite: true);
            _stamp = Stamp(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---- the file changed by something else ---------------------------------------------------

    // The file as it was when last read or written here: when it was changed, and its length.
    private (DateTime Changed, long Length) _stamp;
    /// <summary>Counts the times the channels were read again from a file something else had changed.
    /// Every channel is then a new object: whatever holds the old ones must let them go.</summary>
    public int Reloads { get; private set; }

    private static (DateTime, long) Stamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.LastWriteTimeUtc, file.Length) : default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>
    /// Read the channels again if the file is not as this program left it: edited by hand, written by
    /// another tool, or by another copy of the program. Cheap enough to ask every second. A file
    /// that cannot be read yet (half written) is left for the next time; one that has gone changes nothing.
    /// </summary>
    /// <returns>True when the channels have been replaced.</returns>
    public bool ReloadIfChangedOnDisk()
    {
        var now = Stamp(_path);
        if (now == _stamp) return false;
        if (now == default)
        {
            _stamp = now;
            return false;
        }
        List<MemoryChannel>? read;
        try { read = JsonSerializer.Deserialize<List<MemoryChannel>>(File.ReadAllText(_path), Json); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return false; }
        if (read is null) return false;
        Channels = read;
        _stamp = now;
        Version++;
        Reloads++;
        return true;
    }

    /// <summary>The stored channel on this frequency (within 100 Hz), if any.</summary>
    public MemoryChannel? Find(double hz) => Channels.FirstOrDefault(c => Math.Abs(c.FrequencyHz - hz) < 100);

    /// <summary>Every stored channel on this frequency: one frequency is often shared by several users.</summary>
    public List<MemoryChannel> FindAll(double hz) => Channels.Where(c => Math.Abs(c.FrequencyHz - hz) < 100).ToList();

    /// <summary>Every group name in use, in order.</summary>
    public List<string> GroupNames() =>
        Channels.SelectMany(c => c.Groups).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    // ---- groups made by the program -----------------------------------------------------------
    // Every tag in use is also a group, without anyone making it: its members are the channels
    // with that tag. The same goes for every location. Nothing is stored for these groups;
    // they follow the tags and locations as they are edited.

    /// <summary>What the name of a group made from a tag starts with.</summary>
    public const string TagPrefix = "Tag: ";
    /// <summary>What the name of a group made from a location starts with.</summary>
    public const string PlacePrefix = "Location: ";

    public static bool IsTagGroup(string group) => group.StartsWith(TagPrefix, StringComparison.Ordinal);
    public static bool IsPlaceGroup(string group) => group.StartsWith(PlacePrefix, StringComparison.Ordinal);
    /// <summary>A group the program makes (from a tag or a location), which cannot be renamed, deleted or ticked by hand.</summary>
    public static bool IsAutoGroup(string group) => IsTagGroup(group) || IsPlaceGroup(group);
    /// <summary>A group's name as it is shown under its heading: without the prefix of a group the program makes.</summary>
    public static string GroupLabel(string group) =>
        IsTagGroup(group) ? group[TagPrefix.Length..] : IsPlaceGroup(group) ? group[PlacePrefix.Length..] : group;

    /// <summary>
    /// What two tags (or two locations) must share to count as the same: their letters and digits, whatever the case.
    /// "Fire Dispatch", "FIRE DISPATCH", "fire-dispatch" and " Fire  Dispatch " are one tag.
    /// </summary>
    public static string TagKey(string? tag) =>
        tag is null ? "" : string.Concat(tag.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

    /// <summary>The groups made from the tags in use, in order. Each is named for its tag's most usual spelling.</summary>
    public List<string> TagGroupNames() => MadeGroups(TagPrefix, c => c.Tag);

    /// <summary>The groups made from the locations in use, in order.</summary>
    public List<string> PlaceGroupNames() => MadeGroups(PlacePrefix, c => c.Location);

    private List<string> MadeGroups(string prefix, Func<MemoryChannel, string?> field) =>
        Channels.Where(c => TagKey(field(c)).Length > 0)
            .GroupBy(c => TagKey(field(c)))
            .Select(g => prefix + g.GroupBy(c => field(c)!.Trim()).OrderByDescending(s => s.Count()).ThenBy(s => s.Key, StringComparer.Ordinal).First().Key)
            .Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Whether two group names mean the same group: the same name, or groups of the same tag or location.</summary>
    public static bool SameGroup(string a, string b) =>
        a == b
        || (IsTagGroup(a) && IsTagGroup(b) && TagKey(a[TagPrefix.Length..]) == TagKey(b[TagPrefix.Length..]))
        || (IsPlaceGroup(a) && IsPlaceGroup(b) && TagKey(a[PlacePrefix.Length..]) == TagKey(b[PlacePrefix.Length..]));

    /// <summary>Whether the channel is in the group: one it was put in, or the group of its tag or of its location.</summary>
    public static bool InGroup(MemoryChannel c, string group)
    {
        if (c.Groups.Contains(group)) return true;
        string? value = IsTagGroup(group) ? c.Tag : IsPlaceGroup(group) ? c.Location : null;
        string key = TagKey(value);
        return key.Length > 0 && key == TagKey(GroupLabel(group));
    }
}
