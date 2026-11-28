// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using Microsoft.Data.Sqlite;
using SimpleScanner.Trunk;

namespace SimpleScanner.Tests;

/// <summary>The stored trunked systems, in a database file of their own for each test.</summary>
public class TrunkStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ss-trunk-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private static readonly DateTime When = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);

    private static TrunkSystem Trs(int rrSid, string name = "Souhegan Valley") =>
        new(0, rrSid, name, "Project 25 Phase I", "Standard", "Analog and Digital", 0x3A1, 0xBEE00, "Hillsborough County, NH", When);

    private static TrunkSite Site(int rrId, int number, string name, params TrunkSiteFreq[] freqs) =>
        new(0, 0, rrId, number, 1, name, "Hillsborough", 0x293, "C4FM", false, 42.8, -71.6, freqs);

    private static TrunkTalkgroup Group(int dec, string alpha, bool listen = true, int enc = 0) =>
        new(0, dec, alpha, alpha + " talk", enc >= 2 ? "DE" : "D", enc, "Law Dispatch", "Police", listen);

    private static TrunkSite[] Sites() =>
    [
        Site(61, 1, "Milford", new(851_012_500, 1, TrunkFreqUse.Control), new(851_262_500, 2, TrunkFreqUse.Alternate), new(852_537_500, 3, TrunkFreqUse.Voice)),
        Site(62, 2, "Amherst", new TrunkSiteFreq(853_112_500, null, TrunkFreqUse.Control)),
    ];

    private long Count(string sql)
    {
        using var db = new SqliteConnection($"Data Source={_path};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private void Sql(string sql)
    {
        using var db = new SqliteConnection($"Data Source={_path};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void A_system_is_stored_and_read_back_whole()
    {
        var store = new TrunkStore(_path);
        long? id = store.Upsert(Trs(6000), Sites(), [Group(101, "Milford PD"), Group(103, "PD Tac", listen: false, enc: 2)]);
        Assert.NotNull(id);

        var again = TrunkStore.Load(_path);
        var system = again.Systems.Single();
        Assert.Equal(Trs(6000) with { Id = id.Value }, system);
        var sites = again.Sites(system.Id);
        Assert.Equal(["Milford", "Amherst"], sites.Select(s => s.Name));
        Assert.Equal((61, 1, 1, 0x293, "C4FM", false, 42.8, -71.6), (sites[0].RrSiteId, sites[0].Number, sites[0].Rfss, sites[0].Nac!.Value,
            sites[0].Modulation, sites[0].TdmaControl, sites[0].Lat!.Value, sites[0].Lon!.Value));
        Assert.Equal(Sites()[0].Freqs, sites[0].Freqs);
        Assert.Equal(Sites()[1].Freqs, sites[1].Freqs);                               // with no channel number
        Assert.Equal([Group(101, "Milford PD") with { SystemId = id.Value }, Group(103, "PD Tac", false, 2) with { SystemId = id.Value }],
            again.Talkgroups(system.Id));
        Assert.Equal(again.Talkgroups(system.Id), store.Talkgroups(system.Id));       // what this one holds is what is on disk
        Assert.Empty(again.Talkgroups(id.Value + 1));
    }

    [Fact]
    public void Storing_a_system_again_updates_it_and_keeps_the_ticks()
    {
        var store = new TrunkStore(_path);
        long id = store.Upsert(Trs(6000), Sites(), [Group(101, "Milford PD"), Group(102, "Milford FD"), Group(103, "PD Tac", false, 2)])!.Value;
        Assert.True(store.SetListen(id, 101, false));
        Assert.True(store.SetListen(id, 103, true));

        // Renamed, one site and one talkgroup gone, one new; the new list says nothing of ticks the user has made.
        long again = store.Upsert(Trs(6000, "Souhegan Valley P25"),
            [Site(61, 1, "Milford Hill", new(851_012_500, 1, TrunkFreqUse.Control), new(851_512_500, 4, TrunkFreqUse.Voice))],
            [Group(101, "Milford PD 1"), Group(103, "PD Tac", false, 2), Group(104, "Hwy", listen: false)])!.Value;

        Assert.Equal(id, again);
        Assert.Equal("Souhegan Valley P25", store.Systems.Single().Name);
        var site = store.Sites(id).Single();
        Assert.Equal("Milford Hill", site.Name);
        Assert.Equal([851_012_500, 851_512_500], site.Freqs.Select(f => f.Hz));
        Assert.Equal([(101, "Milford PD 1", false), (103, "PD Tac", true), (104, "Hwy", false)],
            store.Talkgroups(id).Select(t => (t.Dec, t.Alpha, t.Listen)));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM trunk_site_freqs WHERE site_id NOT IN (SELECT id FROM trunk_sites)"));
    }

    [Fact]
    public void Every_talkgroup_can_be_ticked_or_unticked_at_once()
    {
        var store = new TrunkStore(_path);
        long id = store.Upsert(Trs(6000), Sites(), [Group(101, "A"), Group(102, "B", listen: false)])!.Value;
        long other = store.Upsert(Trs(7000, "Other"), [], [Group(101, "A")])!.Value;
        Assert.True(store.SetListenAll(id, false));
        Assert.All(store.Talkgroups(id), t => Assert.False(t.Listen));
        Assert.True(store.Talkgroups(other).Single().Listen);                        // another system is not touched
        Assert.True(store.SetListenAll(id, true));
        Assert.All(TrunkStore.Load(_path).Talkgroups(id), t => Assert.True(t.Listen));
    }

    [Fact]
    public void Deleting_a_system_takes_its_sites_frequencies_and_talkgroups_with_it()
    {
        var store = new TrunkStore(_path);
        long id = store.Upsert(Trs(6000), Sites(), [Group(101, "A"), Group(102, "B")])!.Value;
        long other = store.Upsert(Trs(7000, "Other"), [Site(71, 1, "Elsewhere", new TrunkSiteFreq(854_000_000, 1, TrunkFreqUse.Control))], [Group(101, "A")])!.Value;

        Assert.True(store.Delete(id));
        Assert.Equal(["Other"], store.Systems.Select(s => s.Name));
        Assert.Empty(store.Sites(id));
        Assert.Empty(store.Talkgroups(id));
        Assert.Equal((1L, 1L, 1L, 1L), (Count("SELECT COUNT(*) FROM trunk_systems"), Count("SELECT COUNT(*) FROM trunk_sites"),
            Count("SELECT COUNT(*) FROM trunk_site_freqs"), Count("SELECT COUNT(*) FROM trunk_talkgroups")));
        Assert.Single(store.Talkgroups(other));
    }

    [Fact]
    public void A_change_made_elsewhere_is_noticed_and_one_made_here_is_not()
    {
        var store = new TrunkStore(_path);
        Assert.False(store.ReloadIfChangedOnDisk());                                  // no file yet
        long id = store.Upsert(Trs(6000), Sites(), [Group(101, "A")])!.Value;
        Assert.False(store.ReloadIfChangedOnDisk());
        store.SetListen(id, 101, false);
        Assert.False(store.ReloadIfChangedOnDisk());

        // Another copy of the program, and the sqlite3 tool.
        var other = TrunkStore.Load(_path);
        other.SetListen(id, 101, true);
        Assert.True(store.ReloadIfChangedOnDisk());
        Assert.True(store.Talkgroups(id).Single().Listen);
        Assert.Equal(1, store.Reloads);

        Sql("UPDATE trunk_talkgroups SET alpha = 'Renamed'");
        Assert.True(store.ReloadIfChangedOnDisk());
        Assert.Equal("Renamed", store.Talkgroups(id).Single().Alpha);
        Assert.False(store.ReloadIfChangedOnDisk());
    }

    [Fact]
    public void The_trunked_systems_live_beside_the_stored_channels()
    {
        var channels = new MemoryStore(_path);
        channels.Channels.Add(new MemoryChannel { Name = "Mine", FrequencyHz = 146.52e6 });
        Assert.True(channels.Save());
        var store = new TrunkStore(channels.FilePath);
        store.Upsert(Trs(6000), Sites(), [Group(101, "A")]);
        Assert.False(channels.ReloadIfChangedOnDisk());                               // the channels' count is their own
        Assert.Equal("Mine", MemoryStore.Load(_path).Channels.Single().Name);
        Assert.Single(TrunkStore.Load(_path).Systems);
    }
}
