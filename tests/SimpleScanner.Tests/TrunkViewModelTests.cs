// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleScanner.Digital;
using SimpleScanner.Dsp;
using SimpleScanner.Radio;
using SimpleScanner.Remote;
using SimpleScanner.Trunk;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Tests;

/// <summary>
/// A receiver that does only what the window asks of it and says what the test tells it to: its trunk status, its
/// digital text and its frequency. Trunk mode and a scan end each other, and tuning or a new mode ends trunk mode,
/// as in the real one.
/// </summary>
internal sealed class FakeTrunkReceiver : IReceiver
{
    public FakeSource Fake { get; } = new();
    public IIqSource Source => Fake;
    public bool IsRunning { get; private set; }
#pragma warning disable CS0067
    public event Action<string>? Failed;
#pragma warning restore CS0067
    public void Start() => IsRunning = true;
    public void Stop()
    {
        IsRunning = false;
        Scanning = false;
        Trunk = null;
    }

    private double _hz = 162.4e6;
    public double FrequencyHz
    {
        get => _hz;
        set
        {
            if (value == _hz) return;
            _hz = value;
            Trunk = null;
            Scanning = false;
        }
    }
    /// <summary>What trunk mode reports as the receiver's frequency (the engine moves it, not the window).</summary>
    public void Report(double hz) => _hz = hz;

    public Mode Mode { get; private set; } = Mode.Nfm;
    public double BandwidthHz { get; private set; } = BandPlan.NfmNarrow;
    public void SetMode(Mode mode, double bandwidthHz)
    {
        if (mode == Mode && bandwidthHz == BandwidthHz) return;
        Mode = mode;
        BandwidthHz = bandwidthHz;
        Trunk = null;
    }

    public double LevelDb => -90;
    public double LevelOffsetDb { get; set; }
    public bool SquelchOpen => false;
    public bool Blocked => false;
    public double SquelchDb { get; set; }
    public bool SquelchEnabled { get; set; }
    public bool AutoSquelch { get; set; }
    public bool Stereo => false;
    public bool StereoEnabled { get; set; }
    public IDigitalStatus? Digital { get; set; }
    public string? AudioDevice { get; set; }
    public double Volume { get; set; }
    public double Pan { get; set; }
    public double ScanHangSeconds { get; set; }
    public double ScanSkipSeconds { get; set; }
    public double? ResumeSeconds { get; set; }
    public TimeSpan? AvoidTime { get; set; }
    public int AvoidedCount => 0;
    public bool Scanning { get; private set; }
    public bool ScanListening => false;
    public long ScanPasses => 0;
    public int ScanWindows => 0;
    public ScanChannel? ScanChannel => null;
    public void StartScan(IReadOnlyList<ScanChannel> channels)
    {
        Trunk = null;
        Scanning = IsRunning && channels.Count > 0;
    }
    public void StopScan() => Scanning = false;
    public int Skips;
    public void SkipChannel() => Skips++;
    public ScanChannel? AvoidChannel() => null;
    public void ClearAvoided() { }
    public bool MultiMonitor { get; set; }
    public double MultiHangSeconds { get; set; }
    public IReadOnlyList<SlotStatus> Slots => ReceiverStatus.EmptySlots;
    public void SkipSlot(int slot) { }
    public ScanChannel? AvoidSlot(int slot) => null;

    public TrunkSiteDef? Site;
    public TrunkFilter? Filter;
    public int FilterSends;
    /// <summary>Trunk mode does not start, the radio running (as a receiver would that cannot have it).</summary>
    public bool RefuseTrunk;
    public void StartTrunk(TrunkSiteDef site, TrunkFilter filter)
    {
        Scanning = false;
        if (!IsRunning || RefuseTrunk) return;
        Site = site;
        Filter = filter;
        Trunk = TrunkStatus.Starting(site);
        _hz = site.ControlHz[0];
    }
    public void StopTrunk() => Trunk = null;
    public void SetTrunkFilter(TrunkFilter filter)
    {
        Filter = filter;
        FilterSends++;
    }
    public TrunkStatus? Trunk { get; set; }

    public long DroppedBlocks => 0;
    public void Dispose() { }
}

/// <summary>Trunk mode as the window has it: the Trunk tab's choosers, ticks and calls, the deck's Trunk, and the display.</summary>
public class TrunkViewModelTests : IDisposable
{
    private const int Rr = 6643, RrSite = 21001;
    private const double Control = 859.4875e6, Voice = 851.0125e6;
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"ss-trunkvm-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (string f in new[] { _db, _db + "-wal", _db + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static TrunkTalkgroup Tg(int dec, string alpha, string descr = "", string category = "", bool listen = true, string tag = "") =>
        new(0, dec, alpha, descr, "D", 0, tag, category, listen);

    private static readonly TrunkTalkgroup[] Groups =
    [
        Tg(101, "FD Disp", "Fire dispatch", "Fire", tag: "Fire Dispatch"), Tg(102, "FD Ops 2", "Fire operations", "Fire"),
        Tg(201, "PD Main", "Police dispatch", "Police"), Tg(202, "PD Tac", "Police tactical", "Police"),
        Tg(301, "DPW", "Public works", "Town"),
    ];

    private static TrunkSystem System(string name = "NH Statewide", int rr = Rr) =>
        new(0, rr, name, "Project 25 Phase I", "", "", 0x3A1, 0xBEE00, "New Hampshire", DateTime.UtcNow);

    private static TrunkSite[] Sites(string name = "Manchester") =>
    [
        new(0, 0, RrSite, 12, 1, name, "Hillsborough", 0x293, "", false, null, null,
            [new TrunkSiteFreq(Control, 1, TrunkFreqUse.Control), new TrunkSiteFreq(Voice, 2, TrunkFreqUse.Voice)]),
        new(0, 0, RrSite + 1, 13, 1, "Nashua", "Hillsborough", 0x294, "", false, null, null,
            [new TrunkSiteFreq(858.9875e6, 1, TrunkFreqUse.Control)]),
    ];

    private TrunkStore Store(IEnumerable<TrunkTalkgroup>? groups = null)
    {
        var store = new TrunkStore(_db);
        Assert.NotNull(store.Upsert(System(), Sites(), groups ?? Groups));
        return store;
    }

    private RadioViewModel Radio(TrunkStore store, FakeTrunkReceiver rx, RadioSettings? settings = null) =>
        new(rx, settings ?? new RadioSettings(), new MemoryStore(Path.ChangeExtension(_db, ".memories.db")), post: a => a(), trunks: store);

    private static async Task On(RadioViewModel radio) => await radio.TogglePowerCommand.ExecuteAsync(null);

    private static void Choose(RadioViewModel radio, string site = "Manchester")
    {
        radio.Trunk.SelectedSystem = radio.Trunk.Systems.Single(s => s.System.RrSid == Rr);
        radio.Trunk.SelectedSite = radio.Trunk.Sites.Single(s => s.Site.Name == site);
    }

    private static TrunkCall Call(long seq, int tg, int src = 4001, TrunkOutcome outcome = TrunkOutcome.Heard, TrunkCallFlags flags = TrunkCallFlags.Voice,
        double seconds = 4) =>
        new(seq, tg, src, Voice, 0, 1, flags, new DateTime(2026, 11, 28, 15, 0, 0, DateTimeKind.Utc).AddSeconds(seq), seconds, outcome);

    private static TrunkStatus Status(TrunkState state, TrunkCall? active = null, params TrunkCall[] calls) =>
        new(state, 1, 1, Control, -70, 0x293, 0x3A1, 0xBEE00, 1, 12, true, 38, 1000, 0, active, calls);

    [Fact]
    public async Task TicksReachTheStoreAndTheRadiosFilterAtOnce()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx);
        await On(radio);
        Choose(radio);
        radio.ToggleTrunkCommand.Execute(null);
        Assert.True(radio.IsTrunking);
        Assert.Equal("", radio.Status);
        Assert.Equal([101, 102, 201, 202, 301], rx.Filter!.Listen);
        Assert.Empty(rx.Filter.Off);
        Assert.True(rx.Filter.ListenUnknown);
        Assert.Equal("TALKGROUPS · 5 of 5", radio.Trunk.TalkgroupsTitle);

        // Unticked: kept, and the radio follows it no more.
        radio.Trunk.Talkgroups.Single(r => r.Dec == 202).Listen = false;
        long id = store.Systems.Single().Id;
        Assert.False(TrunkStore.Load(_db).Talkgroups(id).Single(t => t.Dec == 202).Listen);
        Assert.Equal([202], rx.Filter.Off);
        Assert.DoesNotContain(202, rx.Filter.Listen);
        Assert.Equal("TALKGROUPS · 4 of 5", radio.Trunk.TalkgroupsTitle);

        // The switch for talkgroups not in the list goes to the radio too.
        radio.Trunk.ListenUnknown = false;
        Assert.False(rx.Filter.ListenUnknown);

        // Nothing changed: nothing is sent again.
        int sends = rx.FilterSends;
        radio.Trunk.Refresh();
        radio.Tick();
        Assert.Equal(sends, rx.FilterSends);
    }

    [Fact]
    public async Task TwoRadiosOnOneSystemShareItsTicks()
    {
        var store = Store();
        FakeTrunkReceiver rxA = new(), rxB = new();
        var a = Radio(store, rxA);
        var b = Radio(store, rxB);
        await On(a);
        await On(b);
        Choose(a);
        Choose(b, "Nashua");
        a.ToggleTrunkCommand.Execute(null);
        b.ToggleTrunkCommand.Execute(null);

        a.Trunk.Talkgroups.Single(r => r.Dec == 101).Listen = false;
        Assert.Equal([101], rxA.Filter!.Off);
        // The other radio takes it up on its next tick: its row and its radio's filter.
        b.Tick();
        Assert.False(b.Trunk.Talkgroups.Single(r => r.Dec == 101).Listen);
        Assert.Equal([101], rxB.Filter!.Off);
    }

    [Fact]
    public async Task AllAndNoneApplyToTheRowsShownOnly()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx);
        await On(radio);
        Choose(radio);
        var trunk = radio.Trunk;

        trunk.TalkgroupFilter = "fire";
        Assert.Equal([101, 102], trunk.ShownTalkgroups.Select(r => r.Dec));
        trunk.TickNoneCommand.Execute(null);
        Assert.Equal([201, 202, 301], trunk.Talkgroups.Where(r => r.Listen).Select(r => r.Dec));

        // A number filters from its start.
        trunk.TalkgroupFilter = "20";
        Assert.Equal([201, 202], trunk.ShownTalkgroups.Select(r => r.Dec));
        trunk.TickNoneCommand.Execute(null);
        Assert.Equal([301], trunk.Talkgroups.Where(r => r.Listen).Select(r => r.Dec));

        // With no filter, every one.
        trunk.TalkgroupFilter = "";
        Assert.Equal(5, trunk.ShownTalkgroups.Count);
        trunk.TickAllCommand.Execute(null);
        Assert.All(trunk.Talkgroups, r => Assert.True(r.Listen));
        Assert.All(TrunkStore.Load(_db).Talkgroups(store.Systems.Single().Id), t => Assert.True(t.Listen));
    }

    [Fact]
    public async Task RecentCallsComeNewestFirstOnceEachAndAreCapped()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx);
        await On(radio);
        Choose(radio);
        radio.ToggleTrunkCommand.Execute(null);
        var trunk = radio.Trunk;

        rx.Trunk = Status(TrunkState.Control, null, Call(1, 101), Call(2, 999, outcome: TrunkOutcome.NotTicked, seconds: 0));
        radio.Tick();
        radio.Tick();
        Assert.Equal([2L, 1L], trunk.Calls.Select(c => c.Seq));
        rx.Trunk = Status(TrunkState.Control, null, Call(1, 101), Call(2, 999), Call(3, 201, outcome: TrunkOutcome.Encrypted, seconds: 0));
        radio.Tick();
        Assert.Equal([3L, 2L, 1L], trunk.Calls.Select(c => c.Seq));

        var heard = trunk.Calls[2];
        Assert.Equal("101", heard.Talkgroup);
        Assert.Equal("FD Disp", heard.Name);
        Assert.Equal("4001", heard.Source);
        Assert.Equal("851.0125", heard.Frequency);
        Assert.Equal("4 s", heard.Length);
        Assert.Equal("heard", heard.Outcome);
        Assert.Equal(new DateTime(2026, 11, 28, 15, 0, 1, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss"), heard.Time);
        // Not in the list: by its number. Not followed: no length.
        Assert.Equal("TG 999", trunk.Calls[1].Name);
        Assert.Equal("not ticked", trunk.Calls[1].Outcome);
        Assert.Equal("", trunk.Calls[1].Length);
        Assert.Equal("encrypted", trunk.Calls[0].Outcome);

        // The engine keeps its last 24; polled often enough, every call is listed, and only the newest 200 are kept.
        for (long seq = 4; seq <= 260; seq += 6)
        {
            rx.Trunk = Status(TrunkState.Control, null, [.. Enumerable.Range(0, 24).Select(k => Call(seq + k - 18, 202)).Where(c => c.Seq > 0)]);
            radio.Tick();
        }
        Assert.Equal(TrunkViewModel.MostCalls, trunk.Calls.Count);
        var seqs = trunk.Calls.Select(c => c.Seq).ToList();
        Assert.Equal(seqs.OrderByDescending(s => s), seqs);
        Assert.Equal(seqs.Count, seqs.Distinct().Count());
        Assert.Equal(261L, seqs[0]);

        // An engine started again numbers from 1: its calls are listed too.
        rx.Trunk = Status(TrunkState.Control, null, Call(1, 301));
        radio.Tick();
        Assert.Equal("DPW", trunk.Calls[0].Name);
    }

    [Fact]
    public async Task ScanAndTrunkStopEachOther()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx);
        await On(radio);
        Choose(radio);
        radio.ToggleTrunkCommand.Execute(null);
        Assert.True(radio.IsTrunking);
        Assert.True(radio.CanSkip);
        radio.SkipChannelCommand.Execute(null);
        Assert.Equal(1, rx.Skips);

        var ch = new MemoryChannel { Name = "Tower", FrequencyHz = 120e6 };
        radio.StartScan([ch], [new ScanChannel("Tower", 120e6, Mode.Am, BandPlan.AmAir)]);
        Assert.True(radio.IsScanning);
        Assert.False(radio.IsTrunking);
        Assert.Null(rx.Trunk);

        radio.ToggleTrunkCommand.Execute(null);
        Assert.True(radio.IsTrunking);
        Assert.False(radio.IsScanning);
        Assert.False(rx.Scanning);

        // Tuning by hand ends it, and the display keeps the frequency typed.
        radio.EntryText = "162.55";
        radio.EnterFrequencyCommand.Execute(null);
        Assert.False(radio.IsTrunking);
        Assert.Equal(162.55e6, radio.FrequencyHz);
        Assert.Equal(162.55e6, rx.FrequencyHz);

        // Ended by the receiver (as when the radio stops): the next tick says so.
        radio.ToggleTrunkCommand.Execute(null);
        rx.Trunk = null;
        radio.Tick();
        Assert.False(radio.IsTrunking);
        Assert.Equal("Trunk", radio.Trunk.ToggleText);
    }

    [Fact]
    public async Task TheDecksTrunkSaysWhatIsMissing()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx);
        radio.ToggleTrunkCommand.Execute(null);
        Assert.Equal("Start the radio first", radio.Status);
        await On(radio);
        radio.ToggleTrunkCommand.Execute(null);
        Assert.False(radio.IsTrunking);
        Assert.Equal("Choose a system and a site on Trunk first", radio.Status);
        // A system with one site chosen: the site is not chosen for the user when there are several.
        radio.Trunk.SelectedSystem = radio.Trunk.Systems.Single();
        Assert.Null(radio.Trunk.SelectedSite);
        Assert.Equal(2, radio.Trunk.Sites.Count);
        Assert.Equal("12 · Manchester", radio.Trunk.Sites[0].Label);
    }

    [Fact]
    public async Task LoadingTheSystemAgainKeepsWhatIsChosenAndTheTicks()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx);
        await On(radio);
        Choose(radio);
        radio.Trunk.Talkgroups.Single(r => r.Dec == 301).Listen = false;
        radio.ToggleTrunkCommand.Execute(null);

        // Loaded again from RadioReference with a new name and a new talkgroup, and another system beside it.
        Assert.NotNull(store.Upsert(System("NH State Radio"), Sites("Manchester Simulcast"), [.. Groups, Tg(401, "Schools")]));
        Assert.NotNull(store.Upsert(System("Nashua City", 9999), [], []));
        radio.Tick();
        var trunk = radio.Trunk;
        Assert.Equal(["NH State Radio", "Nashua City"], trunk.Systems.Select(s => s.Label).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("NH State Radio", trunk.SelectedSystem!.Label);
        Assert.Equal(RrSite, trunk.SelectedSite!.Site.RrSiteId);
        Assert.Equal("12 · Manchester Simulcast", trunk.SelectedSite.Label);
        Assert.Equal(6, trunk.Talkgroups.Count);
        Assert.False(trunk.Talkgroups.Single(r => r.Dec == 301).Listen);
        Assert.Contains(401, rx.Filter!.Listen);
        Assert.True(radio.IsTrunking);

        // Kept with the radio's settings by RadioReference's numbers, and chosen again from them.
        var kept = new RadioSettings();
        radio.WriteTo(kept);
        Assert.Equal(Rr, kept.TrunkSystem);
        Assert.Equal(RrSite, kept.TrunkSite);
        Assert.True(kept.Trunking);
        var next = Radio(TrunkStore.Load(_db), new FakeTrunkReceiver(), kept);
        Assert.Equal(Rr, next.Trunk.SelectedSystem!.System.RrSid);
        Assert.Equal(RrSite, next.Trunk.SelectedSite!.Site.RrSiteId);
        Assert.False(next.IsTrunking);       // resuming it at start is for later

        // Chosen before any system is stored: kept until one is.
        var empty = new TrunkStore(Path.ChangeExtension(_db, ".empty.db"));
        try
        {
            var none = Radio(empty, new FakeTrunkReceiver(), kept);
            Assert.True(none.Trunk.NoSystems);
            var again = new RadioSettings();
            none.WriteTo(again);
            Assert.Equal(Rr, again.TrunkSystem);
            Assert.Equal(RrSite, again.TrunkSite);
        }
        finally { File.Delete(Path.ChangeExtension(_db, ".empty.db")); }
    }

    [Fact]
    public async Task TheDisplaySaysTheSiteTheStateAndTheCall()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx);
        await On(radio);
        Choose(radio);
        radio.ToggleTrunkCommand.Execute(null);
        var trunk = radio.Trunk;

        // Looking for the control channel.
        radio.Tick();
        Assert.Equal("NH Statewide · Manchester", radio.BandName);
        Assert.Equal("Looking for the control channel", radio.DetailText);
        Assert.Equal("Looking for the control channel", trunk.StateText);
        Assert.Equal("Trying 859.4875 MHz", trunk.SiteText);
        Assert.Equal("P25", radio.ModeText);
        Assert.Equal(Control, radio.FrequencyHz);

        // On it: the control channel's own text on the digital line, the site's numbers on the tab.
        var cc = new P25Status { ControlChannel = true, Nac = 0x293, SystemId = 0x3A1, Wacn = 0xBEE00, Site = 12 };
        rx.Digital = cc;
        rx.Trunk = Status(TrunkState.Control);
        radio.Tick();
        Assert.Equal("On the control channel", radio.DetailText);
        Assert.Equal("", radio.InfoText);
        Assert.Equal(cc.ToString(), radio.DigitalText);
        Assert.Equal("859.4875 MHz · NAC 293 · WACN BEE00 · system 3A1 · site 1-12 · 38 messages/s", trunk.SiteText);
        Assert.Equal("", trunk.WarningText);
        Assert.False(radio.IsEncrypted);

        // A call: the voice frequency, the talkgroup's names, the caller.
        rx.Report(Voice);
        rx.Digital = new P25Status { InCall = true, Nac = 0x293, Talkgroup = 101, Source = 4001 };
        rx.Trunk = Status(TrunkState.Call, Call(0, 101, outcome: TrunkOutcome.Following));
        radio.Tick();
        Assert.Equal(Voice, radio.FrequencyHz);
        Assert.Equal("NH Statewide · Manchester", radio.BandName);
        Assert.Equal("Fire dispatch · Fire", radio.DetailText);
        Assert.Equal("Fire Dispatch", radio.InfoText);
        Assert.Equal("TG 101 FD Disp · ID 4001", radio.DigitalText);
        Assert.Equal("Following a call", trunk.StateText);
        Assert.True(radio.IsTrunking);       // shown, not tuned to

        // Encrypted, an emergency, and a talkgroup not in the list.
        rx.Trunk = Status(TrunkState.Call, Call(0, 999, src: 0, outcome: TrunkOutcome.Following,
            flags: TrunkCallFlags.Emergency | TrunkCallFlags.Encrypted));
        radio.Tick();
        Assert.Equal("TG 999 · EMERGENCY · encrypted", radio.DigitalText);
        Assert.Equal("Talkgroup not in the list", radio.DetailText);
        Assert.True(radio.IsEncrypted);

        // Hang: the call is still shown until it is over.
        rx.Trunk = Status(TrunkState.Hang, Call(0, 201, outcome: TrunkOutcome.Following));
        radio.Tick();
        Assert.Equal("TG 201 PD Main · ID 4001", radio.DigitalText);
        Assert.Equal("Waiting for a reply", trunk.StateText);

        // Back on the control channel; then a site that is not the one chosen.
        rx.Report(Control);
        rx.Digital = cc;
        rx.Trunk = Status(TrunkState.Control) with { MatchesSite = false, Site = 5 };
        radio.Tick();
        Assert.Equal(Control, radio.FrequencyHz);
        Assert.Equal("On the control channel", radio.DetailText);
        Assert.Equal("Not the site chosen", trunk.WarningText);

        // Lost: the state in words.
        rx.Trunk = Status(TrunkState.Lost);
        radio.Tick();
        Assert.Equal("Control channel lost, looking again", radio.DetailText);

        // Stopped: the display is the receiver's again.
        radio.ToggleTrunkCommand.Execute(null);
        Assert.False(radio.IsTrunking);
        Assert.Equal("Not running", trunk.StateText);
        Assert.NotEqual("P25", radio.ModeText);
        Assert.Equal(Control, radio.FrequencyHz);
    }

    [Fact]
    public void TheSwitchForTalkgroupsNotInTheListIsOneForEveryRadioAndKept()
    {
        var store = Store();
        string path = Path.ChangeExtension(_db, ".settings.json");
        try
        {
            var settings = Settings.Load(path);
            FakeTrunkReceiver rxA = new(), rxB = new();
            rxA.Fake.Serial = "A";
            rxB.Fake.Serial = "B";
            var main = new MainViewModel(settings, [rxA, rxB], new MemoryStore(Path.ChangeExtension(_db, ".memories.db")), store);
            Assert.All(main.RadioList, r => Assert.True(r.Trunk.ListenUnknown));
            main.RadioList[0].Trunk.ListenUnknown = false;
            Assert.False(main.RadioList[1].Trunk.ListenUnknown);
            Assert.False(settings.TrunkListenUnknown);
            main.Shutdown();
            Assert.False(Settings.Load(path).TrunkListenUnknown);
            var next = new MainViewModel(Settings.Load(path), [new FakeTrunkReceiver()], new MemoryStore(Path.ChangeExtension(_db, ".memories.db")), store);
            Assert.False(next.Radio.Trunk.ListenUnknown);
        }
        finally { File.Delete(path); }
    }

    // ---- Stage 8 ----

    private static RadioSettings WasTrunking(int system = Rr, int site = RrSite) =>
        new() { TrunkSystem = system, TrunkSite = site, Trunking = true };

    [Fact]
    public async Task ARadioLeftInTrunkModeTakesItUpWhenStarted()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx, WasTrunking());
        Assert.False(radio.IsTrunking);
        // Not started yet: what is saved still says trunk mode.
        var kept = new RadioSettings();
        radio.WriteTo(kept);
        Assert.True(kept.Trunking);

        await On(radio);
        Assert.True(radio.IsTrunking);
        Assert.Equal("Manchester", rx.Site?.SiteName);
        Assert.Equal("", radio.Status);

        // Stopped by its power button in trunk mode: kept so, and taken up again at the next start.
        await On(radio);
        Assert.False(radio.IsOn);
        Assert.False(radio.IsTrunking);
        kept = new RadioSettings();
        radio.WriteTo(kept);
        Assert.True(kept.Trunking);
        await On(radio);
        Assert.True(radio.IsTrunking);

        // Trunk mode stopped by hand, the radio running: not taken up again.
        radio.ToggleTrunkCommand.Execute(null);
        Assert.False(radio.IsTrunking);
        await On(radio);
        await On(radio);
        Assert.False(radio.IsTrunking);
        kept = new RadioSettings();
        radio.WriteTo(kept);
        Assert.False(kept.Trunking);
    }

    [Fact]
    public async Task ARadioLeftInTrunkModeOnASiteNoLongerStoredStartsAsUsual()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx, WasTrunking(site: 99999));
        await On(radio);
        Assert.True(radio.IsOn);
        Assert.False(radio.IsTrunking);
        Assert.Null(rx.Site);
        Assert.Equal("", radio.Status);         // quietly
        var gone = Radio(store, new FakeTrunkReceiver(), WasTrunking(system: 4242));
        await On(gone);
        Assert.False(gone.IsTrunking);
        Assert.Equal("", gone.Status);
    }

    [Fact]
    public async Task TrunkModeThatCannotStartSaysWhy()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver { RefuseTrunk = true };
        var radio = Radio(store, rx);
        Choose(radio);
        radio.ToggleTrunkCommand.Execute(null);
        Assert.Equal("Start the radio first", radio.Status);
        await On(radio);
        radio.ToggleTrunkCommand.Execute(null);
        Assert.False(radio.IsTrunking);
        Assert.Equal("Trunk mode could not start on this radio", radio.Status);
    }

    [Fact]
    public async Task InTrunkModeTheDeckShowsNarrowFmAndMultiOff()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx, new RadioSettings { MultiMonitor = true, AutoMode = false, Mode = Mode.Am, BandwidthHz = BandPlan.AmAir });
        await On(radio);
        Assert.True(radio.IsAm);
        Assert.True(radio.MultiLit);
        Choose(radio);
        radio.ToggleTrunkCommand.Execute(null);
        Assert.True(radio.IsTrunking);
        Assert.False(radio.MultiLit);
        Assert.True(radio.MultiMonitor);            // the setting is kept
        Assert.False(radio.IsAm);
        Assert.True(radio.IsNfm);
        Assert.False(radio.AutoLit);
        Assert.Equal(BandPlan.NfmNarrow, radio.SelectedWidth?.Value);
        Assert.True(radio.IsTrunking);              // showing the width tuned nothing
        radio.Trunk.Stop();
        Assert.True(radio.MultiLit);
    }

    [Fact]
    public async Task HoldFollowsOnlyTheTalkgroupOfTheCallThenTheTicksAgain()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx);
        await On(radio);
        Choose(radio);
        var trunk = radio.Trunk;
        Assert.False(trunk.CanHold);
        radio.ToggleTrunkCommand.Execute(null);
        rx.Trunk = Status(TrunkState.Control);
        radio.Tick();
        Assert.False(trunk.CanHold);               // nothing followed yet

        rx.Trunk = Status(TrunkState.Call, Call(0, 201, outcome: TrunkOutcome.Following));
        radio.Tick();
        Assert.True(trunk.CanHold);
        trunk.ToggleHoldCommand.Execute(null);
        Assert.True(trunk.IsHolding);
        Assert.Equal([201], rx.Filter!.Listen);
        Assert.False(rx.Filter.ListenUnknown);
        Assert.Equal("HOLDING TG 201", trunk.TalkgroupsTitle);
        // A tick changed meanwhile is kept, and the hold stands.
        trunk.Talkgroups.Single(r => r.Dec == 101).Listen = false;
        Assert.Equal([201], rx.Filter.Listen);
        Assert.False(TrunkStore.Load(_db).Talkgroups(store.Systems.Single().Id).Single(t => t.Dec == 101).Listen);

        trunk.ToggleHoldCommand.Execute(null);
        Assert.False(trunk.IsHolding);
        Assert.Equal([102, 201, 202, 301], rx.Filter.Listen);
        Assert.True(rx.Filter.ListenUnknown);

        // With no call followed now, the last one followed is held; trunk mode stopping lets it go.
        rx.Trunk = Status(TrunkState.Control, null, Call(1, 301), Call(2, 202, outcome: TrunkOutcome.NotTicked));
        radio.Tick();
        trunk.ToggleHoldCommand.Execute(null);
        Assert.Equal([301], rx.Filter.Listen);
        radio.ToggleTrunkCommand.Execute(null);
        Assert.False(trunk.IsHolding);
        Assert.False(trunk.CanHold);
        Assert.StartsWith("TALKGROUPS", trunk.TalkgroupsTitle);
    }

    [Fact]
    public void TalkgroupsAreListedUnderTheirCategoriesEachWithATick()
    {
        var store = Store();
        var radio = Radio(store, new FakeTrunkReceiver());
        Choose(radio);
        var trunk = radio.Trunk;
        string Lines() => string.Join(" ", trunk.TalkgroupLines.Select(l => l switch
        {
            TalkgroupCategoryRow c => $"[{c.Name} {(c.Listen is { } on ? on ? "x" : "-" : "~")}]",
            TalkgroupRow r => $"{r.Dec}",
            _ => "?",
        }));
        Assert.Equal("[Fire x] 101 102 [Police x] 201 202 [Town x] 301", Lines());

        trunk.Talkgroups.Single(r => r.Dec == 101).Listen = false;
        Assert.Equal("[Fire ~] 101 102 [Police x] 201 202 [Town x] 301", Lines());
        var fire = trunk.TalkgroupLines.OfType<TalkgroupCategoryRow>().First();
        Assert.Equal("1 of 2", fire.CountText);

        // The heading's tick: all of the category, kept.
        var police = trunk.TalkgroupLines.OfType<TalkgroupCategoryRow>().Single(c => c.Name == "Police");
        police.Listen = false;
        Assert.Equal("[Fire ~] 101 102 [Police -] 201 202 [Town x] 301", Lines());
        long id = store.Systems.Single().Id;
        Assert.Equal([102, 301], TrunkStore.Load(_db).Talkgroups(id).Where(t => t.Listen).Select(t => t.Dec));
        trunk.TalkgroupLines.OfType<TalkgroupCategoryRow>().First().Listen = true;
        Assert.Equal("[Fire x] 101 102 [Police -] 201 202 [Town x] 301", Lines());

        // With a filter, only the categories it shows, and only their rows shown.
        trunk.TalkgroupFilter = "PD";
        Assert.Equal("[Police -] 201 202", Lines());
        trunk.TalkgroupFilter = "";
        Assert.Equal(8, trunk.TalkgroupLines.Count);

        // Talkgroups with no category are last, under Other.
        var other = Store([.. Groups, Tg(401, "Spare")]);
        var radio2 = Radio(other, new FakeTrunkReceiver());
        Choose(radio2);
        Assert.Equal("Other", ((TalkgroupCategoryRow)radio2.Trunk.TalkgroupLines[^2]).Name);
    }

    [Fact]
    public async Task TheStateShownChangesAtMostOnceASecondAndPhase2CallsSayTheirSlot()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx);
        await On(radio);
        Choose(radio);
        radio.ToggleTrunkCommand.Execute(null);
        var trunk = radio.Trunk;
        var call = Call(0, 101, outcome: TrunkOutcome.Following);
        rx.Trunk = Status(TrunkState.Call, call);
        radio.Tick();
        rx.Trunk = Status(TrunkState.Hang, call);
        radio.Tick();
        Assert.Equal("Waiting for a reply", trunk.StateText);
        rx.Trunk = Status(TrunkState.Call, call);
        radio.Tick();
        Assert.Equal("Following a call", trunk.StateText);
        // A moment later its voice drops again: still shown as the call.
        rx.Trunk = Status(TrunkState.Hang, call);
        radio.Tick();
        Assert.Equal("Following a call", trunk.StateText);
        rx.Trunk = Status(TrunkState.Control);
        radio.Tick();
        Assert.Equal("On the control channel", trunk.StateText);

        // A Phase 2 call: its phase and slot on the display's third line and in its row's tip.
        var p2 = call with { Phase = 2, Slot = 1, Talkgroup = 201 };
        rx.Trunk = Status(TrunkState.Call, p2);
        radio.Tick();
        Assert.Equal("P25 Phase 2 · slot 2", radio.InfoText);
        Assert.Equal("P25", radio.ModeText);
        rx.Trunk = Status(TrunkState.Control, null, p2 with { Seq = 1, Outcome = TrunkOutcome.Heard });
        radio.Tick();
        Assert.Contains("P25 Phase 2 · slot 2", trunk.Calls[0].Tip);
        Assert.Equal("P25 Phase 2 · slot 1", TrunkViewModel.PhaseText(p2 with { Slot = 0 }));
        Assert.Equal("", TrunkViewModel.PhaseText(call));
    }

    [Fact]
    public async Task TheNeighbouringSitesAreInTheStatusLinesTip()
    {
        var store = Store();
        var rx = new FakeTrunkReceiver();
        var radio = Radio(store, rx);
        await On(radio);
        Choose(radio);
        radio.ToggleTrunkCommand.Execute(null);
        rx.Trunk = Status(TrunkState.Control);
        radio.Tick();
        Assert.DoesNotContain("Neighbouring", radio.Trunk.SiteTip);
        rx.Trunk = Status(TrunkState.Control) with
        {
            Neighbours = [new TrunkAdjacentSite(0x3A1, 1, 3, 0x1001, 851_237_500, 0), new TrunkAdjacentSite(0x3A1, 1, 5, 0x2001, 0, 0)],
        };
        radio.Tick();
        Assert.EndsWith("Neighbouring sites it names: site 1-3 on 851.2375 MHz, site 1-5", radio.Trunk.SiteTip);
    }
}
