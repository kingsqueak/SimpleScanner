// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SimpleScanner.Trunk;

/// <summary>
/// The stored trunked systems, their sites and talkgroups, kept in the program's database beside the
/// stored channels (tables "trunk_systems", "trunk_sites", "trunk_site_freqs" and "trunk_talkgroups").
/// All of it is held in memory too, and read again when something else has changed it.
/// </summary>
public sealed class TrunkStore
{
    private readonly string _path;
    private Dictionary<long, List<TrunkSite>> _sites = [];
    private Dictionary<long, List<TrunkTalkgroup>> _talkgroups = [];
    // The count of changes to the tables as it was when last read or written here.
    private long _stamp;

    public List<TrunkSystem> Systems { get; private set; } = [];
    /// <summary>Counts the changes, made here or read in, so a window can tell that the systems are not as it last showed them.</summary>
    public int Version { get; private set; }
    /// <summary>Counts the times the systems were read again after something else had changed them.</summary>
    public int Reloads { get; private set; }

    public TrunkStore(string? path = null) => _path = path ?? AppFiles.Database;

    public static TrunkStore Load(string? path = null)
    {
        var store = new TrunkStore(path);
        try
        {
            if (!File.Exists(store._path)) return store;
            using var db = store.Open();
            store.Read(db);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { }
        return store;
    }

    public TrunkSystem? Find(long id) => Systems.FirstOrDefault(s => s.Id == id);
    public TrunkSystem? ByRadioReference(int rrSid) => Systems.FirstOrDefault(s => s.RrSid == rrSid);
    public IReadOnlyList<TrunkSite> Sites(long systemId) => _sites.TryGetValue(systemId, out var list) ? list : [];
    public IReadOnlyList<TrunkTalkgroup> Talkgroups(long systemId) => _talkgroups.TryGetValue(systemId, out var list) ? list : [];

    // ---- changes ------------------------------------------------------------------------------

    /// <summary>
    /// Store a system loaded from RadioReference, all in one go. A system already stored (the same
    /// RadioReference number) is brought up to date: its sites and talkgroups take the new names and
    /// frequencies, those no longer listed go, and a talkgroup stored before keeps its tick. A new
    /// talkgroup takes the tick it comes with. The ids of what is passed in are not used.
    /// </summary>
    /// <returns>The system's id here, or null when the database could not be written.</returns>
    public long? Upsert(TrunkSystem system, IEnumerable<TrunkSite> sites, IEnumerable<TrunkTalkgroup> talkgroups)
    {
        try
        {
            using var db = Open();
            using var all = db.BeginTransaction();
            long id;
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO trunk_systems (rr_sid, name, type, flavor, voice, sysid, wacn, location, imported)
                        VALUES ($rr, $name, $type, $flavor, $voice, $sysid, $wacn, $location, $imported)
                    ON CONFLICT(rr_sid) DO UPDATE SET name = excluded.name, type = excluded.type, flavor = excluded.flavor,
                        voice = excluded.voice, sysid = excluded.sysid, wacn = excluded.wacn, location = excluded.location,
                        imported = excluded.imported;
                    SELECT id FROM trunk_systems WHERE rr_sid = $rr;
                    """;
                cmd.Parameters.AddWithValue("$rr", system.RrSid);
                cmd.Parameters.AddWithValue("$name", system.Name);
                cmd.Parameters.AddWithValue("$type", system.Type);
                cmd.Parameters.AddWithValue("$flavor", system.Flavor);
                cmd.Parameters.AddWithValue("$voice", system.Voice);
                cmd.Parameters.AddWithValue("$sysid", (object?)system.SysId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$wacn", (object?)system.Wacn ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$location", system.Location);
                cmd.Parameters.AddWithValue("$imported", new DateTimeOffset(DateTime.SpecifyKind(system.Imported, DateTimeKind.Utc)).ToUnixTimeSeconds());
                id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            var keptSites = new HashSet<long>();
            using (var site = db.CreateCommand())
            using (var clear = db.CreateCommand())
            using (var freq = db.CreateCommand())
            {
                site.CommandText = """
                    INSERT INTO trunk_sites (system_id, rr_site_id, number, rfss, name, county, nac, modulation, tdma_cc, lat, lon)
                        VALUES ($system, $rr, $number, $rfss, $name, $county, $nac, $modulation, $tdma, $lat, $lon)
                    ON CONFLICT(system_id, rr_site_id) DO UPDATE SET number = excluded.number, rfss = excluded.rfss, name = excluded.name,
                        county = excluded.county, nac = excluded.nac, modulation = excluded.modulation, tdma_cc = excluded.tdma_cc,
                        lat = excluded.lat, lon = excluded.lon;
                    SELECT id FROM trunk_sites WHERE system_id = $system AND rr_site_id = $rr;
                    """;
                string[] names = ["$system", "$rr", "$number", "$rfss", "$name", "$county", "$nac", "$modulation", "$tdma", "$lat", "$lon"];
                var p = names.Select(n => site.Parameters.Add(n, SqliteType.Text)).ToArray();
                clear.CommandText = "DELETE FROM trunk_site_freqs WHERE site_id = $site";
                var clearSite = clear.Parameters.Add("$site", SqliteType.Integer);
                freq.CommandText = "INSERT OR REPLACE INTO trunk_site_freqs (site_id, hz, lcn, use) VALUES ($site, $hz, $lcn, $use)";
                var fSite = freq.Parameters.Add("$site", SqliteType.Integer);
                var fHz = freq.Parameters.Add("$hz", SqliteType.Real);
                var fLcn = freq.Parameters.Add("$lcn", SqliteType.Integer);
                var fUse = freq.Parameters.Add("$use", SqliteType.Text);
                foreach (var s in sites)
                {
                    object?[] row = [id, s.RrSiteId, s.Number, s.Rfss, s.Name, s.County, s.Nac, s.Modulation, s.TdmaControl ? 1 : 0, s.Lat, s.Lon];
                    for (int i = 0; i < row.Length; i++)
                    {
                        p[i].ResetSqliteType();
                        p[i].Value = row[i] ?? DBNull.Value;
                    }
                    long siteId = Convert.ToInt64(site.ExecuteScalar(), CultureInfo.InvariantCulture);
                    keptSites.Add(siteId);
                    // A site's frequencies carry nothing of the user's: they are put in afresh.
                    clearSite.Value = siteId;
                    clear.ExecuteNonQuery();
                    foreach (var f in s.Freqs)
                    {
                        fSite.Value = siteId;
                        fHz.Value = f.Hz;
                        fLcn.Value = (object?)f.Lcn ?? DBNull.Value;
                        fUse.Value = f.Use.ToString();
                        freq.ExecuteNonQuery();
                    }
                }
            }
            // The sites no longer listed, and their frequencies.
            foreach (long gone in Ids(db, "SELECT id FROM trunk_sites WHERE system_id = $system", id).Where(s => !keptSites.Contains(s)))
                Run(db, "DELETE FROM trunk_site_freqs WHERE site_id = $id; DELETE FROM trunk_sites WHERE id = $id", gone);

            var keptGroups = new HashSet<long>();
            using (var tg = db.CreateCommand())
            {
                // The tick ("listen") is set only when the talkgroup is new.
                tg.CommandText = """
                    INSERT INTO trunk_talkgroups (system_id, dec, alpha, descr, mode, enc, tag, category, listen)
                        VALUES ($system, $dec, $alpha, $descr, $mode, $enc, $tag, $category, $listen)
                    ON CONFLICT(system_id, dec) DO UPDATE SET alpha = excluded.alpha, descr = excluded.descr, mode = excluded.mode,
                        enc = excluded.enc, tag = excluded.tag, category = excluded.category
                    """;
                string[] names = ["$system", "$dec", "$alpha", "$descr", "$mode", "$enc", "$tag", "$category", "$listen"];
                var p = names.Select(n => tg.Parameters.Add(n, SqliteType.Text)).ToArray();
                foreach (var t in talkgroups)
                {
                    object?[] row = [id, t.Dec, t.Alpha, t.Descr, t.Mode, t.Enc, t.Tag, t.Category, t.Listen ? 1 : 0];
                    for (int i = 0; i < row.Length; i++)
                    {
                        p[i].ResetSqliteType();
                        p[i].Value = row[i] ?? DBNull.Value;
                    }
                    tg.ExecuteNonQuery();
                    keptGroups.Add(t.Dec);
                }
            }
            foreach (long gone in Ids(db, "SELECT dec FROM trunk_talkgroups WHERE system_id = $system", id).Where(d => !keptGroups.Contains(d)))
                Run(db, "DELETE FROM trunk_talkgroups WHERE system_id = $system AND dec = $id", gone, id);

            all.Commit();
            Read(db);
            Version++;
            return id;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Tick or untick one talkgroup.</summary>
    /// <returns>True when it is in the database.</returns>
    public bool SetListen(long systemId, int dec, bool listen) =>
        Change("UPDATE trunk_talkgroups SET listen = $on WHERE system_id = $system AND dec = $dec AND listen != $on",
            systemId, listen, dec, list =>
            {
                int i = list.FindIndex(t => t.Dec == dec);
                if (i >= 0) list[i] = list[i] with { Listen = listen };
            });

    /// <summary>Tick or untick every talkgroup of a system.</summary>
    public bool SetListenAll(long systemId, bool listen) =>
        Change("UPDATE trunk_talkgroups SET listen = $on WHERE system_id = $system AND listen != $on", systemId, listen, 0, list =>
        {
            for (int i = 0; i < list.Count; i++) list[i] = list[i] with { Listen = listen };
        });

    private bool Change(string sql, long systemId, bool listen, int dec, Action<List<TrunkTalkgroup>> here)
    {
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$on", listen ? 1 : 0);
            cmd.Parameters.AddWithValue("$system", systemId);
            cmd.Parameters.AddWithValue("$dec", dec);
            // Changed elsewhere since it was read: all of it is read again, this change and all.
            bool behind = Changes(db) != _stamp;
            cmd.ExecuteNonQuery();
            if (behind) Read(db);
            else
            {
                if (_talkgroups.TryGetValue(systemId, out var list)) here(list);
                _stamp = Changes(db);
            }
            Version++;
            return true;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Remove a system with its sites, their frequencies and its talkgroups.</summary>
    public bool Delete(long systemId)
    {
        try
        {
            using var db = Open();
            using var all = db.BeginTransaction();
            Run(db, """
                DELETE FROM trunk_site_freqs WHERE site_id IN (SELECT id FROM trunk_sites WHERE system_id = $id);
                DELETE FROM trunk_sites WHERE system_id = $id;
                DELETE FROM trunk_talkgroups WHERE system_id = $id;
                DELETE FROM trunk_systems WHERE id = $id;
                """, systemId);
            all.Commit();
            Read(db);
            Version++;
            return true;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Read the systems again if the tables are not as this program left them: changed with another tool,
    /// or by another copy of the program. Cheap enough to ask every second.
    /// </summary>
    /// <returns>True when the systems have been replaced.</returns>
    public bool ReloadIfChangedOnDisk()
    {
        try
        {
            if (!File.Exists(_path)) return false;
            using var db = Open();
            if (Changes(db) == _stamp) return false;
            Read(db);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { return false; }
        Version++;
        Reloads++;
        return true;
    }

    // ---- the database -------------------------------------------------------------------------

    // Every change to the four tables, by this program or by anything else, counts up "trunk_changes"
    // in meta (the table the stored channels keep their own count in).
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS trunk_systems (
            id INTEGER PRIMARY KEY, rr_sid INTEGER UNIQUE, name TEXT NOT NULL, type TEXT, flavor TEXT, voice TEXT,
            sysid INTEGER, wacn INTEGER, location TEXT, imported INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS trunk_sites (
            id INTEGER PRIMARY KEY, system_id INTEGER NOT NULL, rr_site_id INTEGER, number INTEGER, rfss INTEGER, name TEXT,
            county TEXT, nac INTEGER, modulation TEXT, tdma_cc INTEGER NOT NULL DEFAULT 0, lat REAL, lon REAL,
            UNIQUE(system_id, rr_site_id));
        CREATE TABLE IF NOT EXISTS trunk_site_freqs (
            site_id INTEGER NOT NULL, hz REAL NOT NULL, lcn INTEGER, use TEXT, PRIMARY KEY(site_id, hz));
        CREATE TABLE IF NOT EXISTS trunk_talkgroups (
            system_id INTEGER NOT NULL, dec INTEGER NOT NULL, alpha TEXT, descr TEXT, mode TEXT, enc INTEGER, tag TEXT,
            category TEXT, listen INTEGER NOT NULL DEFAULT 1, PRIMARY KEY(system_id, dec));
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value INTEGER NOT NULL);
        INSERT OR IGNORE INTO meta (key, value) VALUES ('trunk_changes', 0);
        """;

    private static readonly string Triggers = string.Concat(
        from table in new[] { "trunk_systems", "trunk_sites", "trunk_site_freqs", "trunk_talkgroups" }
        from change in new[] { ("added", "INSERT"), ("edited", "UPDATE"), ("removed", "DELETE") }
        select $"CREATE TRIGGER IF NOT EXISTS {table}_{change.Item1} AFTER {change.Item2} ON {table} " +
               "BEGIN UPDATE meta SET value = value + 1 WHERE key = 'trunk_changes'; END;\n");

    // Opened for each piece of work and closed after it: the stored channels and the RadioReference browser have the same file open.
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        try
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = Schema + Triggers;
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
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'trunk_changes'";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static List<long> Ids(SqliteConnection db, string sql, long system)
    {
        var list = new List<long>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$system", system);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    private static void Run(SqliteConnection db, string sql, long id, long system = 0)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$system", system);
        cmd.ExecuteNonQuery();
    }

    private void Read(SqliteConnection db)
    {
        var systems = new List<TrunkSystem>();
        var sites = new Dictionary<long, List<TrunkSite>>();
        var talkgroups = new Dictionary<long, List<TrunkTalkgroup>>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, rr_sid, name, type, flavor, voice, sysid, wacn, location, imported FROM trunk_systems ORDER BY name, id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                systems.Add(new TrunkSystem(r.GetInt64(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.GetString(2), Text(r, 3), Text(r, 4), Text(r, 5),
                    r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetInt32(7), Text(r, 8),
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(9)).UtcDateTime));
        }
        var freqs = new Dictionary<long, List<TrunkSiteFreq>>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT site_id, hz, lcn, use FROM trunk_site_freqs ORDER BY site_id, hz";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long site = r.GetInt64(0);
                if (!freqs.TryGetValue(site, out var list)) freqs[site] = list = [];
                list.Add(new TrunkSiteFreq(r.GetDouble(1), r.IsDBNull(2) ? null : r.GetInt32(2),
                    Enum.TryParse(Text(r, 3), ignoreCase: true, out TrunkFreqUse use) ? use : TrunkFreqUse.Voice));
            }
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, system_id, rr_site_id, number, rfss, name, county, nac, modulation, tdma_cc, lat, lon FROM trunk_sites ORDER BY system_id, number, id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long id = r.GetInt64(0), system = r.GetInt64(1);
                if (!sites.TryGetValue(system, out var list)) sites[system] = list = [];
                list.Add(new TrunkSite(id, system, Int(r, 2), Int(r, 3), Int(r, 4), Text(r, 5), Text(r, 6), r.IsDBNull(7) ? null : r.GetInt32(7),
                    Text(r, 8), r.GetInt64(9) != 0, r.IsDBNull(10) ? null : r.GetDouble(10), r.IsDBNull(11) ? null : r.GetDouble(11),
                    freqs.TryGetValue(id, out var f) ? f : []));
            }
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT system_id, dec, alpha, descr, mode, enc, tag, category, listen FROM trunk_talkgroups ORDER BY system_id, dec";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long system = r.GetInt64(0);
                if (!talkgroups.TryGetValue(system, out var list)) talkgroups[system] = list = [];
                list.Add(new TrunkTalkgroup(system, r.GetInt32(1), Text(r, 2), Text(r, 3), Text(r, 4), Int(r, 5), Text(r, 6), Text(r, 7), r.GetInt64(8) != 0));
            }
        }
        Systems = systems;
        _sites = sites;
        _talkgroups = talkgroups;
        _stamp = Changes(db);
    }

    private static string Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static int Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt32(i);
}
