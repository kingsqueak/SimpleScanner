// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleScanner.Trunk;

namespace SimpleScanner.ViewModels;

/// <summary>A stored trunked system in the Trunk tab's System box.</summary>
public sealed class TrunkSystemChoice(TrunkSystem system)
{
    public TrunkSystem System { get; internal set; } = system;
    public string Label => System.Name;
    public override string ToString() => Label;
}

/// <summary>A site of the chosen system in the Trunk tab's Site box: its number and name.</summary>
public sealed class TrunkSiteChoice(TrunkSite site)
{
    public TrunkSite Site { get; internal set; } = site;
    public string Label => Site.Number > 0 ? $"{Site.Number} · {Site.Name}" : Site.Name;
    public override string ToString() => Label;
}

/// <summary>A talkgroup as a row of the Trunk tab, with its tick (calls to it are followed).</summary>
public sealed class TalkgroupRow(TrunkTalkgroup talkgroup, Action<TalkgroupRow> changed) : ObservableObject
{
    private bool _listen = talkgroup.Listen;

    public TrunkTalkgroup Talkgroup { get; internal set; } = talkgroup;
    public int Dec => Talkgroup.Dec;
    public string DecText => Talkgroup.Dec.ToString(CultureInfo.InvariantCulture);
    public string Alpha => Talkgroup.Alpha;
    public string Descr => Talkgroup.Descr;
    public string Mode => Talkgroup.Mode;
    public string Category => Talkgroup.Category;
    /// <summary>Everything about it, for the row's tool tip.</summary>
    public string Tip => string.Join(" · ", new[] { DecText, Alpha, Descr, Talkgroup.Tag, Category }.Where(t => t.Length > 0));

    public bool Listen
    {
        get => _listen;
        set
        {
            if (_listen == value) return;
            _listen = value;
            OnPropertyChanged();
            changed(this);
        }
    }

    /// <summary>Show a tick changed elsewhere, without passing it on.</summary>
    public void Show(bool value)
    {
        if (_listen == value) return;
        _listen = value;
        OnPropertyChanged(nameof(Listen));
    }
}

/// <summary>
/// A category's heading in the Trunk tab's talkgroup list, with a tick of its own for the talkgroups of it shown: ticked when
/// all are, empty when none are, between when some are. Ticking or unticking it does that to all of them.
/// </summary>
public sealed class TalkgroupCategoryRow(string name, IReadOnlyList<TalkgroupRow> rows, Action<TalkgroupCategoryRow, bool> changed)
    : ObservableObject
{
    private bool? _listen = Of(rows);

    /// <summary>The category, as RadioReference names it; "Other" for talkgroups without one.</summary>
    public string Name { get; } = name.Length > 0 ? name : "Other";
    public IReadOnlyList<TalkgroupRow> Rows { get; } = rows;
    public string CountText => $"{Rows.Count(r => r.Listen)} of {Rows.Count}";
    public string Tip => $"Tick or untick every talkgroup of {Name} shown ({CountText} ticked)";

    public bool? Listen
    {
        get => _listen;
        set
        {
            if (_listen == value || value is null) return;
            _listen = value;
            OnPropertyChanged();
            changed(this, value.Value);
        }
    }

    /// <summary>Its rows' ticks have changed: show what they are now.</summary>
    public void Update()
    {
        var now = Of(Rows);
        if (now != _listen)
        {
            _listen = now;
            OnPropertyChanged(nameof(Listen));
        }
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(Tip));
    }

    private static bool? Of(IReadOnlyList<TalkgroupRow> rows) =>
        rows.All(r => r.Listen) ? true : rows.Any(r => r.Listen) ? null : false;
}

/// <summary>A finished call as a row of the Trunk tab's recent calls. Made once; never changes.</summary>
public sealed class TrunkCallRow
{
    public long Seq { get; init; }
    public string Time { get; init; } = "";
    public string Talkgroup { get; init; } = "";
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public string Frequency { get; init; } = "";
    public string Length { get; init; } = "";
    public string Outcome { get; init; } = "";
    /// <summary>The call was heard (followed and speech played): the row is shown brighter.</summary>
    public bool Heard { get; init; }
    public bool Emergency { get; init; }
    public string Tip { get; init; } = "";
}

/// <summary>
/// One radio's trunk mode as the window shows and works it: the stored system and site chosen for it, the
/// talkgroups of that system with their ticks, what the trunk engine says of the site, and the calls it has
/// logged. Each radio has its own; the ticks belong to the system, so two radios on one system share them.
/// The helper running a radio knows numbers only: names are found here, in the store.
/// </summary>
public sealed partial class TrunkViewModel : ObservableObject
{
    /// <summary>The most recent calls kept for the list.</summary>
    public const int MostCalls = 200;

    private readonly IReceiver _rx;
    private readonly TrunkStore _store;
    private readonly Action _dirty;
    private int _seenVersion = -1;
    private bool _loading;
    // What was kept in the settings, until the systems are there to choose it from.
    private int? _wantSystem, _wantSite;
    // The site trunk mode was started on, and its system's talkgroups by number.
    private TrunkSiteDef? _running;
    private Dictionary<int, TrunkTalkgroup> _names = [];
    private TrunkFilter? _sent;
    private long _lastSeq;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSystems))]
    [NotifyPropertyChangedFor(nameof(NoSystems))]
    private IReadOnlyList<TrunkSystemChoice> _systems = [];
    [ObservableProperty] private TrunkSystemChoice? _selectedSystem;
    [ObservableProperty] private IReadOnlyList<TrunkSiteChoice> _sites = [];
    [ObservableProperty] private TrunkSiteChoice? _selectedSite;
    /// <summary>Every talkgroup of the chosen system.</summary>
    [ObservableProperty] private IReadOnlyList<TalkgroupRow> _talkgroups = [];
    /// <summary>Those matching the filter.</summary>
    [ObservableProperty] private IReadOnlyList<TalkgroupRow> _shownTalkgroups = [];
    /// <summary>What the list shows: those matching the filter by category, each category under its heading (<see cref="TalkgroupCategoryRow"/>).</summary>
    [ObservableProperty] private IReadOnlyList<object> _talkgroupLines = [];
    [ObservableProperty] private string _talkgroupFilter = "";
    [ObservableProperty] private string _talkgroupsTitle = "TALKGROUPS";
    /// <summary>Calls to talkgroups not in the list are followed (and shown as "TG 12345"). The same for every radio.</summary>
    [ObservableProperty] private bool _listenUnknown = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    [NotifyPropertyChangedFor(nameof(CanHold))]
    private bool _isTrunking;
    /// <summary>The status line: what the engine is doing, in words.</summary>
    [ObservableProperty] private string _stateText = "Not running";
    /// <summary>The status line: the control frequency, what the site says of itself, and the messages a second.</summary>
    [ObservableProperty] private string _siteText = "";
    /// <summary>The status line: said when the site heard is not the one chosen.</summary>
    [ObservableProperty] private string _warningText = "";
    /// <summary>The status line's tip: what it shows, and the neighbouring sites the control channel names.</summary>
    [ObservableProperty] private string _siteTip = SiteTipText;
    /// <summary>
    /// Hold: only the talkgroup of the call followed now (or last) is followed until Hold is pressed again. It is done by the
    /// filter sent to the radio (that one talkgroup ticked, no others), so the ticks kept are not touched.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanHold))]
    [NotifyPropertyChangedFor(nameof(IsHolding))]
    private int? _heldTalkgroup;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanHold))]
    private int? _holdCandidate;

    private const string SiteTipText = "The control channel, then what it says of itself (NAC, WACN and system in hex, the RFSS and site in decimal, as RadioReference lists them), and the trunking messages heard a second";

    /// <summary>The finished calls, newest first.</summary>
    public ObservableCollection<TrunkCallRow> Calls { get; } = [];

    public bool HasSystems => Systems.Count > 0;
    public bool NoSystems => Systems.Count == 0;
    public string ToggleText => IsTrunking ? "Stop" : "Trunk";
    public bool IsHolding => HeldTalkgroup is not null;
    /// <summary>Hold has something to do: it is on, or in trunk mode a call has been followed whose talkgroup it can hold.</summary>
    public bool CanHold => IsHolding || (IsTrunking && HoldCandidate is not null);
    /// <summary>The trunk engine as last read; null outside trunk mode.</summary>
    public TrunkStatus? Status { get; private set; }

    /// <summary>The switch for talkgroups not in the list was changed here: the window passes it to the other radios.</summary>
    public event Action<bool>? ListenUnknownChanged;

    /// <param name="dirty">Called when something kept in the settings has changed.</param>
    public TrunkViewModel(IReceiver receiver, TrunkStore store, RadioSettings settings, bool listenUnknown, Action dirty)
    {
        _rx = receiver;
        _store = store;
        _dirty = dirty;
        _wantSystem = settings.TrunkSystem;
        _wantSite = settings.TrunkSite;
        _listenUnknown = listenUnknown;
        Refresh();
    }

    /// <summary>Put what is kept between runs into this radio's settings.</summary>
    public void WriteTo(RadioSettings s)
    {
        s.TrunkSystem = SelectedSystem?.System.RrSid ?? _wantSystem;
        s.TrunkSite = SelectedSite?.Site.RrSiteId ?? (SelectedSystem is null ? _wantSite : null);
        s.Trunking = IsTrunking;
    }

    // ---- the store ----------------------------------------------------------------------------

    /// <summary>
    /// Show the store as it is: the systems, the chosen one's sites and talkgroups. What is chosen stays chosen
    /// (found by its RadioReference numbers, so a system loaded again keeps it); a list that has not changed is
    /// not made again, only its ticks are brought up to date, so it stays where it was scrolled to. In trunk mode
    /// the radio is sent the ticks if they have changed.
    /// </summary>
    public void Refresh()
    {
        _seenVersion = _store.Version;
        _loading = true;
        try
        {
            var systems = _store.Systems;
            int? want = SelectedSystem?.System.RrSid ?? _wantSystem;
            if (!systems.Select(s => s.Id).SequenceEqual(Systems.Select(c => c.System.Id)))
            {
                Systems = systems.Select(s => new TrunkSystemChoice(s)).ToList();
                SelectedSystem = Systems.FirstOrDefault(c => c.System.RrSid == want);
            }
            else
            {
                // The same systems: each takes its new particulars, and is shown again if its name changed.
                bool renamed = false;
                for (int i = 0; i < systems.Count; i++)
                {
                    renamed |= Systems[i].System.Name != systems[i].Name;
                    Systems[i].System = systems[i];
                }
                if (renamed)
                {
                    var chosen = SelectedSystem;
                    Systems = Systems.ToList();
                    SelectedSystem = chosen;
                }
                if (SelectedSystem is null && want is not null) SelectedSystem = Systems.FirstOrDefault(c => c.System.RrSid == want);
            }
            if (SelectedSystem is not null) _wantSystem = null;
            ShowSites();
            ShowTalkgroups();
            if (_running is not null) Names();
        }
        finally { _loading = false; }
        SendFilter();
    }

    private void ShowSites()
    {
        var sites = SelectedSystem is { } sys ? _store.Sites(sys.System.Id) : [];
        int? want = SelectedSite?.Site.RrSiteId ?? _wantSite;
        if (!sites.Select(s => (s.Id, s.Number, s.Name)).SequenceEqual(Sites.Select(c => (c.Site.Id, c.Site.Number, c.Site.Name))))
        {
            Sites = sites.Select(s => new TrunkSiteChoice(s)).ToList();
            SelectedSite = Sites.FirstOrDefault(c => c.Site.RrSiteId == want) ?? (Sites.Count == 1 ? Sites[0] : null);
        }
        else
        {
            for (int i = 0; i < sites.Count; i++) Sites[i].Site = sites[i];
            if (SelectedSite is null && want is not null) SelectedSite = Sites.FirstOrDefault(c => c.Site.RrSiteId == want);
        }
        if (SelectedSite is not null || SelectedSystem is not null) _wantSite = null;
    }

    private void ShowTalkgroups()
    {
        var list = SelectedSystem is { } sys ? _store.Talkgroups(sys.System.Id) : [];
        if (list.Count == Talkgroups.Count && list.Select(t => t with { Listen = false }).SequenceEqual(Talkgroups.Select(r => r.Talkgroup with { Listen = false })))
        {
            for (int i = 0; i < list.Count; i++)
            {
                Talkgroups[i].Talkgroup = list[i];
                Talkgroups[i].Show(list[i].Listen);
            }
        }
        else
        {
            Talkgroups = list.Select(t => new TalkgroupRow(t, Ticked)).ToList();
            ApplyFilter();
        }
        foreach (var line in TalkgroupLines)
            if (line is TalkgroupCategoryRow c) c.Update();
        ShowTitle();
    }

    // Holding, it says what is held (the list's own ticks are not what is followed then).
    private void ShowTitle() => TalkgroupsTitle = HeldTalkgroup is int held ? $"HOLDING TG {held}"
        : Talkgroups.Count == 0 ? "TALKGROUPS" : $"TALKGROUPS · {Talkgroups.Count(r => r.Listen)} of {Talkgroups.Count}";

    partial void OnTalkgroupFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        string f = TalkgroupFilter.Trim();
        bool Has(string text) => text.Contains(f, StringComparison.OrdinalIgnoreCase);
        ShownTalkgroups = f.Length == 0 ? Talkgroups
            : Talkgroups.Where(r => r.DecText.StartsWith(f, StringComparison.Ordinal) || Has(r.Alpha) || Has(r.Descr) || Has(r.Category)
                                    || Has(r.Talkgroup.Tag)).ToList();
        // By category, in the order of their names, those with none last; in each, the talkgroups in the list's order.
        var lines = new List<object>();
        foreach (var g in ShownTalkgroups.GroupBy(r => r.Category).OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var rows = g.ToList();
            lines.Add(new TalkgroupCategoryRow(g.Key, rows, TickCategory));
            lines.AddRange(rows);
        }
        TalkgroupLines = lines;
    }

    // A category's tick: every talkgroup of it shown, ticked or unticked.
    private void TickCategory(TalkgroupCategoryRow category, bool listen)
    {
        if (_loading || SelectedSystem is not { } sys) return;
        foreach (var row in category.Rows.Where(r => r.Listen != listen).ToList())
            _store.SetListen(sys.System.Id, row.Dec, listen);
        Refresh();
    }

    partial void OnSelectedSystemChanged(TrunkSystemChoice? value)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            _wantSystem = null;
            _wantSite = null;
            ShowSites();
            ShowTalkgroups();
        }
        finally { _loading = false; }
        _dirty();
        Release();
        Restart();
    }

    partial void OnSelectedSiteChanged(TrunkSiteChoice? value)
    {
        if (_loading) return;
        _wantSite = null;
        _dirty();
        Restart();
    }

    // Another site chosen in trunk mode: trunk mode carries on there (or ends, with none chosen).
    private void Restart()
    {
        if (!IsTrunking) return;
        if (SelectedSite is null) Stop();
        else if (Start() is not null) Stop();
    }

    // A row's tick: kept in the store, and sent to the radio (and so to every radio on the system).
    private void Ticked(TalkgroupRow row)
    {
        if (_loading || SelectedSystem is not { } sys) return;
        _store.SetListen(sys.System.Id, row.Dec, row.Listen);
        Refresh();
    }

    /// <summary>Tick every talkgroup shown: with a filter typed, only those it shows.</summary>
    [RelayCommand]
    private void TickAll() => TickShown(true);

    /// <summary>Untick every talkgroup shown: with a filter typed, only those it shows.</summary>
    [RelayCommand]
    private void TickNone() => TickShown(false);

    private void TickShown(bool listen)
    {
        if (SelectedSystem is not { } sys) return;
        if (TalkgroupFilter.Trim().Length == 0) _store.SetListenAll(sys.System.Id, listen);
        else
            foreach (var row in ShownTalkgroups.Where(r => r.Listen != listen).ToList())
                _store.SetListen(sys.System.Id, row.Dec, listen);
        Refresh();
    }

    partial void OnListenUnknownChanged(bool value)
    {
        if (_loading) return;
        ListenUnknownChanged?.Invoke(value);
        SendFilter();
    }

    /// <summary>The switch was changed on another radio: take it up here without passing it back.</summary>
    public void SetListenUnknown(bool value)
    {
        _loading = true;
        ListenUnknown = value;
        _loading = false;
        SendFilter();
    }

    /// <summary>Which calls trunk mode follows on the site it runs on: the talkgroups ticked, those not, and the switch.</summary>
    private TrunkFilter FilterFor(long systemId)
    {
        if (HeldTalkgroup is int held) return new TrunkFilter([held], [], ListenUnknown: false);
        var list = _store.Talkgroups(systemId);
        return new TrunkFilter(list.Where(t => t.Listen).Select(t => t.Dec).ToArray(), list.Where(t => !t.Listen).Select(t => t.Dec).ToArray(),
            ListenUnknown);
    }

    private void SendFilter()
    {
        if (_running is null || !IsTrunking) return;
        var filter = FilterFor(_running.SystemId);
        if (_sent is { } had && had.ListenUnknown == filter.ListenUnknown && had.SkipEncrypted == filter.SkipEncrypted
            && had.Listen.AsSpan().SequenceEqual(filter.Listen) && had.Off.AsSpan().SequenceEqual(filter.Off)) return;
        _sent = filter;
        _rx.SetTrunkFilter(filter);
    }

    /// <summary>Hold the talkgroup of the call followed now (or last), or let it go.</summary>
    [RelayCommand]
    private void ToggleHold()
    {
        if (HeldTalkgroup is not null) Release();
        else if (IsTrunking && HoldCandidate is int tg)
        {
            HeldTalkgroup = tg;
            ShowTitle();
            SendFilter();
        }
    }

    // Hold off: the ticks are followed again.
    private void Release()
    {
        if (HeldTalkgroup is null) return;
        HeldTalkgroup = null;
        ShowTitle();
        SendFilter();
    }

    private void Names() => _names = _running is null ? [] : _store.Talkgroups(_running.SystemId).ToDictionary(t => t.Dec);

    /// <summary>The stored talkgroup of the system trunk mode runs on, or null when it is not in the list.</summary>
    public TrunkTalkgroup? Find(int talkgroup) => _names.GetValueOrDefault(talkgroup);

    // ---- trunk mode ---------------------------------------------------------------------------

    /// <summary>Start trunk mode on the site chosen. The radio must be running.</summary>
    /// <returns>Null when it started; otherwise why not, in words for the status line.</returns>
    internal string? Start()
    {
        if (SelectedSystem is not { } sys || SelectedSite is not { } site) return "Choose a system and a site on Trunk first";
        var def = TrunkSiteDef.From(sys.System, site.Site);
        if (def.ControlHz.Count == 0) return "RadioReference lists no control channel for this site";
        _running = def;
        Names();
        _lastSeq = 0;
        HoldCandidate = null;
        _sent = FilterFor(def.SystemId);
        _rx.StartTrunk(def, _sent);
        Status = _rx.Trunk;
        if (Status is null)
        {
            _running = null;
            _sent = null;
            Ended();
            return !_rx.IsRunning ? "Start the radio first"
                : _rx is Receiver { DigitalAvailable: false } ? "Trunk mode could not start: the digital voice library is missing"
                : "Trunk mode could not start on this radio";
        }
        IsTrunking = true;
        ShowStatus(Status);
        _dirty();
        return null;
    }

    /// <summary>Leave trunk mode.</summary>
    internal void Stop()
    {
        _rx.StopTrunk();
        Ended();
    }

    /// <summary>Trunk mode has ended, here or by something else (a scan, a tune by hand, the radio stopped).</summary>
    internal void Ended()
    {
        _running = null;
        _sent = null;
        Status = null;
        if (IsTrunking) _dirty();
        IsTrunking = false;
        HoldCandidate = null;
        if (HeldTalkgroup is not null)
        {
            HeldTalkgroup = null;
            ShowTitle();
        }
        StateText = "Not running";
        SiteText = "";
        WarningText = "";
    }

    /// <summary>The system and site as the display's top line says them, in trunk mode.</summary>
    public string BandText => _running is { } d ? $"{d.SystemName} · {d.SiteName}" : "";

    /// <summary>Bring the tab up to date with the store and the radio. Called twenty times a second.</summary>
    public void Tick()
    {
        if (_store.Version != _seenVersion) Refresh();
        if (!IsTrunking) return;
        var s = _rx.Trunk;
        if (s is null)
        {
            Ended();
            return;
        }
        Status = s;
        ShowStatus(s);
        TakeCalls(s.Calls);
        if (s.Active is { } a && !a.Flags.HasFlag(TrunkCallFlags.Unit)) HoldCandidate = a.Talkgroup;
    }

    private void ShowStatus(TrunkStatus s)
    {
        StateText = StateWords(Shown(s.State));
        string tip = s.Neighbours is { Length: > 0 } n
            ? SiteTipText + ".\nNeighbouring sites it names: " + string.Join(", ", n.Select(a =>
                $"site {a.Rfss}-{a.Site}" + (a.Hz > 0 ? $" on {(a.Hz / 1e6).ToString("0.0000#", CultureInfo.InvariantCulture)} MHz" : "")))
            : SiteTipText;
        if (tip != SiteTip) SiteTip = tip;
        string mhz = (s.ControlHz / 1e6).ToString("0.0000#", CultureInfo.InvariantCulture);
        if (s.State is TrunkState.Hunt or TrunkState.Lost && s.Nac is null)
        {
            SiteText = $"Trying {mhz} MHz";
            WarningText = "";
            return;
        }
        var t = new List<string> { $"{mhz} MHz" };
        if (s.Nac is { } nac) t.Add($"NAC {nac:X3}");
        if (s.Wacn is { } wacn) t.Add($"WACN {wacn:X5}");
        if (s.SysId is { } sysId) t.Add($"system {sysId:X3}");
        if (s.Site is { } site) t.Add(s.Rfss is { } rfss ? $"site {rfss}-{site}" : $"site {site}");
        t.Add($"{s.BlocksPerSecond:F0} messages/s");
        SiteText = string.Join(" · ", t);
        WarningText = s.MatchesSite == false ? "Not the site chosen" : "";
    }

    // The state shown. A reply that comes within a moment of a hang time is shown as the call going on, so the line changes
    // between "Following a call" and "Waiting for a reply" at most once a second, however the voice comes and goes.
    private TrunkState Shown(TrunkState state)
    {
        var now = DateTime.UtcNow;
        if (state == TrunkState.Hang && _shown == TrunkState.Call && now < _hangShownFrom) state = TrunkState.Call;
        if (_shown == TrunkState.Hang && state == TrunkState.Call) _hangShownFrom = now + HangShownAgainAfter;
        _shown = state;
        return state;
    }

    private TrunkState _shown;
    private DateTime _hangShownFrom;
    /// <summary>After a hang time has given way to the call again, how long before another is shown.</summary>
    public static readonly TimeSpan HangShownAgainAfter = TimeSpan.FromSeconds(1);

    /// <summary>What the trunk engine is doing, in words.</summary>
    public static string StateWords(TrunkState state) => state switch
    {
        TrunkState.Hunt => "Looking for the control channel",
        TrunkState.Control => "On the control channel",
        TrunkState.Call => "Following a call",
        TrunkState.Hang => "Waiting for a reply",
        TrunkState.Lost => "Control channel lost, looking again",
        _ => "Not running",
    };

    /// <summary>What became of a call, in words.</summary>
    public static string OutcomeWords(TrunkCall c) => c.Outcome switch
    {
        TrunkOutcome.Following => "following",
        TrunkOutcome.Heard => c.Flags.HasFlag(TrunkCallFlags.Skipped) ? "skipped" : "heard",
        TrunkOutcome.Lost => "lost",
        TrunkOutcome.Encrypted => "encrypted",
        TrunkOutcome.NotTicked => "not ticked",
        TrunkOutcome.Busy => "busy",
        TrunkOutcome.Phase2 => "no voice codec",
        _ => "",
    };

    /// <summary>A talkgroup's name for a list: its alpha tag, or "TG 12345" when it is not in the list.</summary>
    public string NameOf(TrunkCall c)
    {
        if (c.Flags.HasFlag(TrunkCallFlags.Unit)) return $"Call to radio {c.Talkgroup}";
        return Find(c.Talkgroup) is { Alpha.Length: > 0 } tg ? tg.Alpha : $"TG {c.Talkgroup}";
    }

    /// <summary>The display's digital line during a call: "TG 1234 FD Dispatch · ID 5678", with what is unusual about it.</summary>
    public string CallText(TrunkCall c)
    {
        string who = c.Flags.HasFlag(TrunkCallFlags.Unit) ? $"to {c.Talkgroup}"
            : Find(c.Talkgroup) is { Alpha.Length: > 0 } tg ? $"TG {c.Talkgroup} {tg.Alpha}" : $"TG {c.Talkgroup}";
        return who + (c.Source != 0 ? $" · ID {c.Source}" : "") + (c.Flags.HasFlag(TrunkCallFlags.Emergency) ? " · EMERGENCY" : "")
            + (c.Flags.HasFlag(TrunkCallFlags.Encrypted) ? " · encrypted" : "");
    }

    /// <summary>The display's second line during a call: the talkgroup's description and category.</summary>
    public string CallDetail(TrunkCall c)
    {
        if (c.Flags.HasFlag(TrunkCallFlags.Unit)) return "Call to one radio";
        if (Find(c.Talkgroup) is not { } tg) return "Talkgroup not in the list";
        return string.Join(" · ", new[] { tg.Descr, tg.Category }.Where(t => t.Length > 0));
    }

    /// <summary>The display's third line during a call: the talkgroup's tag, and for a Phase 2 call its phase and slot.</summary>
    public string CallInfo(TrunkCall c)
    {
        string tag = c.Flags.HasFlag(TrunkCallFlags.Unit) ? "" : Find(c.Talkgroup)?.Tag ?? "";
        return c.Phase == 2 ? string.Join(" · ", new[] { tag, PhaseText(c) }.Where(t => t.Length > 0)) : tag;
    }

    /// <summary>"P25 Phase 2 · slot 1" for a Phase 2 call (its slots counted from 1, as DMR's are); empty for Phase 1.</summary>
    public static string PhaseText(TrunkCall c) => c.Phase == 2 ? $"P25 Phase 2 · slot {c.Slot + 1}" : "";

    // The finished calls not yet in the list, newest put first. The engine numbers them from 1 when it starts again.
    private void TakeCalls(TrunkCall[] calls)
    {
        if (calls.Length == 0) return;
        if (calls[^1].Seq < _lastSeq) _lastSeq = 0;
        foreach (var c in calls)
        {
            if (c.Seq <= _lastSeq) continue;
            _lastSeq = c.Seq;
            Calls.Insert(0, Row(c));
            // A call followed (heard or not) is what Hold takes when no call is being followed
            if (c.Outcome is TrunkOutcome.Heard or TrunkOutcome.Lost or TrunkOutcome.Phase2 && !c.Flags.HasFlag(TrunkCallFlags.Unit))
                HoldCandidate = c.Talkgroup;
        }
        while (Calls.Count > MostCalls) Calls.RemoveAt(Calls.Count - 1);
    }

    private TrunkCallRow Row(TrunkCall c)
    {
        var start = c.Start.Kind == DateTimeKind.Local ? c.Start : DateTime.SpecifyKind(c.Start, DateTimeKind.Utc).ToLocalTime();
        string outcome = OutcomeWords(c);
        string name = NameOf(c);
        string mhz = (c.Hz / 1e6).ToString("0.0000", CultureInfo.InvariantCulture);
        return new TrunkCallRow
        {
            Seq = c.Seq,
            Time = start.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            Talkgroup = c.Talkgroup.ToString(CultureInfo.InvariantCulture),
            Name = name,
            Source = c.Source != 0 ? c.Source.ToString(CultureInfo.InvariantCulture) : "",
            Frequency = mhz,
            Length = c.Seconds >= 0.5 ? $"{c.Seconds:F0} s" : "",
            Outcome = outcome,
            Heard = c.Outcome == TrunkOutcome.Heard,
            Emergency = c.Flags.HasFlag(TrunkCallFlags.Emergency),
            Tip = $"{name} · {CallDetail(c)} · {mhz} MHz" + (c.Phase == 2 ? $" · {PhaseText(c)}" : "") + (c.Source != 0 ? $" · from {c.Source}" : "") + $" · {outcome}"
                  + (c.Flags.HasFlag(TrunkCallFlags.Emergency) ? " · emergency" : "") + (c.Flags.HasFlag(TrunkCallFlags.Hop) ? " · retuned to reach it" : ""),
        };
    }
}
