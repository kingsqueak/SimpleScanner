// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using Microsoft.Data.Sqlite;
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
    /// <summary>Read only from memories.json files written before a channel could be in several groups.</summary>
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

/// <summary>
/// The stored channels, kept in the program's database (SQLite, table "channels") beside the
/// settings: the same file that holds what was fetched from RadioReference.
/// </summary>
public sealed class MemoryStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    public List<MemoryChannel> Channels { get; private set; } = [];
    /// <summary>
    /// Groups made by name in the channel editor's Groups editor. Each stays, with or without channels in
    /// it, until it is deleted there; other groups exist only as long as a channel is in them.
    /// </summary>
    public List<string> KeptGroups { get; private set; } = [];
    /// <summary>Counts the saves, so a window can tell that another has changed the channels.</summary>
    public int Version { get; private set; }

    public MemoryStore(string? path = null) => _path = path ?? AppFiles.Database;

    public static MemoryStore Load(string? path = null)
    {
        var store = new MemoryStore(path);
        try
        {
            if (!File.Exists(store._path)) return store;
            using var db = store.Open();
            store.Channels = Read(db);
            store.KeptGroups = ReadGroups(db);
            store._stamp = Changes(db);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { }
        return store;
    }

    /// <returns>True when the channels are in the database.</returns>
    public bool Save()
    {
        Version++;
        try
        {
            using var db = Open();
            using var all = db.BeginTransaction();
            using (var clear = db.CreateCommand())
            {
                clear.CommandText = "DELETE FROM channels";
                clear.ExecuteNonQuery();
            }
            using (var add = db.CreateCommand())
            {
                string[] names = Columns.Split(", ");
                add.CommandText = $"INSERT INTO channels (id, {Columns}) VALUES ($id, {string.Join(", ", names.Select(n => "$" + n))})";
                var id = add.Parameters.Add("$id", SqliteType.Integer);
                var values = names.Select(n => add.Parameters.Add("$" + n, SqliteType.Text)).ToArray();
                int n = 0;
                foreach (var c in Channels)
                {
                    // The row number keeps the channels in the order they are held in.
                    id.Value = ++n;
                    object?[] row =
                    [
                        c.Name, c.FrequencyHz, c.Scan ? 1 : 0, JsonSerializer.Serialize(c.Groups, Json), c.Mode?.ToString(), c.BandwidthHz,
                        c.Agency, c.Description, c.Tag, c.Format, c.Tone, c.Callsign, c.InputHz, c.StationClass, c.Location, c.ImportKey,
                    ];
                    for (int i = 0; i < row.Length; i++)
                    {
                        values[i].ResetSqliteType();
                        values[i].Value = row[i] ?? DBNull.Value;
                    }
                    add.ExecuteNonQuery();
                }
            }
            using (var clear = db.CreateCommand())
            {
                clear.CommandText = "DELETE FROM groups";
                clear.ExecuteNonQuery();
            }
            using (var add = db.CreateCommand())
            {
                add.CommandText = "INSERT OR IGNORE INTO groups (name) VALUES ($name)";
                var name = add.Parameters.Add("$name", SqliteType.Text);
                foreach (string g in KeptGroups)
                {
                    name.Value = g;
                    add.ExecuteNonQuery();
                }
            }
            long stamp = Changes(db);
            all.Commit();
            _stamp = stamp;
            return true;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return false; }
    }

    // ---- the database -------------------------------------------------------------------------

    private const string Columns =
        "name, frequency_hz, scan, groups, mode, bandwidth_hz, agency, description, tag, format, tone, callsign, input_hz, station_class, location, import_key";

    // One row per channel; its groups are a JSON list of names. Every change to the table, by this
    // program or by anything else (the sqlite3 tool), counts up "channels_changes" in meta, which
    // is how a change made elsewhere is noticed.
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS channels (
            id INTEGER PRIMARY KEY, name TEXT NOT NULL, frequency_hz REAL NOT NULL, scan INTEGER NOT NULL DEFAULT 1,
            groups TEXT NOT NULL DEFAULT '[]', mode TEXT, bandwidth_hz REAL, agency TEXT, description TEXT, tag TEXT,
            format TEXT, tone TEXT, callsign TEXT, input_hz REAL, station_class TEXT, location TEXT, import_key TEXT);
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value INTEGER NOT NULL);
        INSERT OR IGNORE INTO meta (key, value) VALUES ('channels_changes', 0);
        CREATE TRIGGER IF NOT EXISTS channels_added AFTER INSERT ON channels
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'channels_changes'; END;
        CREATE TRIGGER IF NOT EXISTS channels_edited AFTER UPDATE ON channels
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'channels_changes'; END;
        CREATE TRIGGER IF NOT EXISTS channels_removed AFTER DELETE ON channels
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'channels_changes'; END;
        CREATE TABLE IF NOT EXISTS groups (name TEXT PRIMARY KEY);
        CREATE TRIGGER IF NOT EXISTS groups_added AFTER INSERT ON groups
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'channels_changes'; END;
        CREATE TRIGGER IF NOT EXISTS groups_removed AFTER DELETE ON groups
            BEGIN UPDATE meta SET value = value + 1 WHERE key = 'channels_changes'; END;
        """;

    // Opened for each piece of work and closed after it: the RadioReference browser has the same file open.
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        try
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static long Changes(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'channels_changes'";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<string> ReadGroups(SqliteConnection db)
    {
        var list = new List<string>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM groups ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static List<MemoryChannel> Read(SqliteConnection db)
    {
        var list = new List<MemoryChannel>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM channels ORDER BY id";
        using var r = cmd.ExecuteReader();
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        double? Number(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        while (r.Read())
        {
            List<string> groups;
            try { groups = JsonSerializer.Deserialize<List<string>>(Text(3) ?? "[]", Json) ?? []; }
            catch (JsonException) { groups = []; }
            list.Add(new MemoryChannel
            {
                Name = Text(0) ?? "", FrequencyHz = r.GetDouble(1), Scan = r.GetInt64(2) != 0, Groups = groups,
                Mode = Enum.TryParse(Text(4), ignoreCase: true, out Mode mode) ? mode : null, BandwidthHz = Number(5),
                Agency = Text(6), Description = Text(7), Tag = Text(8), Format = Text(9), Tone = Text(10), Callsign = Text(11),
                InputHz = Number(12), StationClass = Text(13), Location = Text(14), ImportKey = Text(15),
            });
        }
        return list;
    }

    // ---- channels from a memories.json of an earlier version ----------------------------------

    /// <summary>
    /// Add the channels of a memories.json file, as earlier versions kept them. One already held is
    /// left as it is: the same imported row, or the same frequency, name and description. Not saved here.
    /// </summary>
    /// <returns>How many were added.</returns>
    public int AddFromJson(string file)
    {
        List<MemoryChannel>? read;
        try { read = JsonSerializer.Deserialize<List<MemoryChannel>>(File.ReadAllText(file), Json); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return 0; }
        if (read is null) return 0;
        // Into an empty store they go as they are, twins and all.
        if (Channels.Count > 0)
        {
            var keys = Channels.Where(c => c.ImportKey is not null).Select(c => c.ImportKey!).ToHashSet();
            var rows = Channels.Select(RadioReference.RrImport.Row).ToHashSet();
            read = read.Where(c => !(c.ImportKey is not null && keys.Contains(c.ImportKey)) && !rows.Contains(RadioReference.RrImport.Row(c))).ToList();
        }
        Channels.AddRange(read);
        return read.Count;
    }

    // ---- the channels changed by something else -----------------------------------------------

    // The count of changes to the table as it was when last read or written here.
    private long _stamp;
    /// <summary>Counts the times the channels were read again after something else had changed them.
    /// Every channel is then a new object: whatever holds the old ones must let them go.</summary>
    public int Reloads { get; private set; }

    /// <summary>
    /// Read the channels again if the table is not as this program left it: edited with another tool,
    /// or by another copy of the program. Cheap enough to ask every second. A database that cannot be
    /// read just now is left for the next time; one that has gone changes nothing.
    /// </summary>
    /// <returns>True when the channels have been replaced.</returns>
    public bool ReloadIfChangedOnDisk()
    {
        try
        {
            if (!File.Exists(_path)) return false;
            using var db = Open();
            long now = Changes(db);
            if (now == _stamp) return false;
            Channels = Read(db);
            KeptGroups = ReadGroups(db);
            _stamp = now;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return false; }
        Version++;
        Reloads++;
        return true;
    }

    /// <summary>The stored channel on this frequency (within 100 Hz), if any.</summary>
    public MemoryChannel? Find(double hz) => Channels.FirstOrDefault(c => Math.Abs(c.FrequencyHz - hz) < 100);

    /// <summary>Every stored channel on this frequency: one frequency is often shared by several users.</summary>
    public List<MemoryChannel> FindAll(double hz) => Channels.Where(c => Math.Abs(c.FrequencyHz - hz) < 100).ToList();

    /// <summary>Every group name in use, and those kept with no channel in them, in order.</summary>
    public List<string> GroupNames() =>
        Channels.SelectMany(c => c.Groups).Concat(KeptGroups).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();

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
