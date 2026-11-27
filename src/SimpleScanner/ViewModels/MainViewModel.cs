using SimpleScanner.Radio;
using System.Globalization;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleScanner.Dsp;

namespace SimpleScanner.ViewModels;

/// <summary>A choice in a drop-down: what it says and the value behind it.</summary>
public sealed record Choice(string Label, double Value)
{
    public override string ToString() => Label;
}

/// <summary>A stored channel as a row in the memory list.</summary>
public sealed partial class MemoryRow(MemoryChannel channel, Action changed) : ObservableObject
{
    public MemoryChannel Channel { get; } = channel;
    public string Name => Channel.Name.Length > 0 ? Channel.Name : Channel.Description ?? Channel.Agency ?? "";
    /// <summary>The frequency, then what the channel is for if that is known, otherwise its groups.</summary>
    public string Detail => $"{Channel.FrequencyHz / 1e6:0.0000}  " + (Channel.Tag is { Length: > 0 } tag
        ? string.Join(" · ", new[] { Channel.Agency, tag }.Where(t => !string.IsNullOrEmpty(t)))
        : string.Join(", ", Channel.Groups));

    /// <summary>The Channels table's columns.</summary>
    public string FrequencyText => (Channel.FrequencyHz / 1e6).ToString("0.0000", CultureInfo.InvariantCulture);
    public string ModeText => Channel.Mode?.ToString().ToUpperInvariant() ?? "Auto";
    /// <summary>The channel's own groups, then its location (the group the program makes from it).</summary>
    public string GroupsText => string.Join(", ", Channel.Groups.Append(Channel.Location ?? "").Where(g => g.Length > 0));
    public string TagText => Channel.Tag ?? "";

    public bool Scan
    {
        get => Channel.Scan;
        set
        {
            if (Channel.Scan == value) return;
            Channel.Scan = value;
            OnPropertyChanged();
            changed();
        }
    }
}

/// <summary>A stretch of band the Search tab can fill in with one click.</summary>
public sealed record SearchRange(string Label, string From, string To);

/// <summary>
/// A frequency of the selected radio's groups, as a line under the user groups on Operate. Its tick is
/// the Scan tick of the stored channels on that frequency.
/// </summary>
public sealed class ActiveFrequency(double place, string frequency, string name, bool scan, Action<ActiveFrequency> changed) : ObservableObject
{
    /// <summary>The frequency in steps of 100 Hz: stored channels with the same one are one line.</summary>
    public double Place { get; } = place;
    public string Frequency { get; } = frequency;
    public string Name { get; } = name;

    public bool Scan
    {
        get => scan;
        set
        {
            if (scan == value) return;
            scan = value;
            OnPropertyChanged();
            changed(this);
        }
    }

    /// <summary>Show a tick that was changed elsewhere, without passing it on.</summary>
    public void Show(bool value)
    {
        if (scan == value) return;
        scan = value;
        OnPropertyChanged(nameof(Scan));
    }
}

/// <summary>A memory group as a tick box in the group selector.</summary>
public sealed partial class GroupRow(string name, bool selected, Action changed) : ObservableObject
{
    public string Name { get; } = name;
    /// <summary>Made by the program from a tag, not by the user.</summary>
    public bool IsAuto { get; } = MemoryStore.IsTagGroup(name);
    /// <summary>Made by the program from a location.</summary>
    public bool IsPlace { get; } = MemoryStore.IsPlaceGroup(name);
    /// <summary>What the selector shows: a tag's or a location's group goes by the tag or location alone, under its own heading.</summary>
    public string Label => MemoryStore.GroupLabel(Name);
    /// <summary>How many stored channels the group holds.</summary>
    [ObservableProperty] private int _count;
    /// <summary>The group matches the filter on the Groups tab.</summary>
    [ObservableProperty] private bool _isShown = true;

    public bool Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            OnPropertyChanged();
            changed();
        }
    }
}

/// <summary>
/// The scanner's front panel: the radios running, which of them the controls work, and what they
/// share: the stored channels and their groups, the timers, the sound device, the window.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const double MinHz = RadioViewModel.MinHz, MaxHz = RadioViewModel.MaxHz;

    private readonly Settings _settings;
    private readonly MemoryStore _memories;
    private const string AllGroups = "All groups", NoGroups = "No groups", DefaultDevice = "System default";
    private readonly DispatcherTimer _timer;
    private bool _loading, _dirty;
    private int _ticks;
    // The groups ticked for each radio. Those of the radio the controls work are the rows' ticks; this holds them between whiles.
    private readonly Dictionary<RadioViewModel, List<string>> _groups = [];

    /// <summary>The radios running, the window's own first; each further one is in a helper program.</summary>
    public IReadOnlyList<RadioViewModel> RadioList { get; }
    /// <summary>The radio the display shows and the controls work.</summary>
    [ObservableProperty] private RadioViewModel _radio;
    /// <summary>Several radios are running: each has a strip of its own in place of the one display.</summary>
    public bool IsMulti => RadioList.Count > 1;
    public bool IsSingle => RadioList.Count == 1;

    /// <summary>The radios in the Receiver box: those running here and those plugged in and free.</summary>
    [ObservableProperty] private IReadOnlyList<RadioInfo> _receiverChoices = [];
    /// <summary>The radio chosen in the Receiver box, whose settings the Radio part shows; null when the one radio has not been chosen or opened yet.</summary>
    [ObservableProperty] private RadioInfo? _shownReceiver;

    /// <summary>How long Avoid keeps a frequency out of the scan, in minutes; 0 is until the app is closed.</summary>
    public IReadOnlyList<Choice> AvoidTimes { get; } = [new("30 min", 30), new("1 hr", 60), new("Full session", 0)];

    /// <summary>The longest a scan stays on a busy channel, in seconds; 0 (Hold) is until the signal drops.</summary>
    public IReadOnlyList<Choice> ResumeTimes { get; } =
        [new("15s", 15), new("30s", 30), new("1min", 60), new("3min", 180), new("5min", 300), new("10min", 600), new("Hold", 0)];

    /// <summary>Multi Monitor: how long a slot keeps its channel after the signal has dropped, in seconds; one for every radio.</summary>
    public IReadOnlyList<Choice> MultiHangTimes { get; } = [new("2s", 2), new("5s", 5), new("8s", 8), new("10s", 10), new("15s", 15)];

    /// <summary>The small window is showing: the display and one row of buttons.</summary>
    [ObservableProperty] private bool _isMini;
    [ObservableProperty] private Choice _selectedAvoidTime;
    [ObservableProperty] private Choice _selectedResumeTime;
    [ObservableProperty] private Choice _selectedMultiHangTime;
    /// <summary>What the group selector's button says: the groups ticked, or that all or none are.</summary>
    [ObservableProperty] private string _groupsText = AllGroups;
    [ObservableProperty] private MemoryRow? _selectedMemory;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private IReadOnlyList<string> _audioDevices = [DefaultDevice];
    [ObservableProperty] private string _selectedAudioDevice = DefaultDevice;
    [ObservableProperty] private string _searchFrom = "144";
    [ObservableProperty] private string _searchTo = "148";

    // ---- the look ----------------------------------------------------------------------------

    public const string StaysDark = "Stays dark", Follows = "Follows";
    /// <summary>Settings, Appearance: follow the Mac, or keep to Light or Dark.</summary>
    public IReadOnlyList<string> Appearances { get; } = ["System", "Light", "Dark"];
    [ObservableProperty] private string _selectedAppearance = "System";
    /// <summary>Settings, Display in Light: the display stays dark like a scanner's LCD, or follows the Light look.</summary>
    public IReadOnlyList<string> DisplayLooks { get; } = [StaysDark, Follows];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayVariant))]
    private string _selectedDisplayLook = StaysDark;
    /// <summary>The look the radios' displays are drawn in: always Dark, or the window's own.</summary>
    public ThemeVariant DisplayVariant => SelectedDisplayLook == Follows ? ThemeVariant.Default : ThemeVariant.Dark;

    partial void OnSelectedAppearanceChanged(string value)
    {
        ApplyAppearance(value);
        if (_loading) return;
        _settings.Appearance = value;
        _dirty = true;
    }

    partial void OnSelectedDisplayLookChanged(string value)
    {
        if (_loading) return;
        _settings.DisplayFollowsLight = value == Follows;
        _dirty = true;
    }

    /// <summary>Give the whole app the look chosen ("System" follows the macOS appearance).</summary>
    public static void ApplyAppearance(string look)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = look switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }

    // ---- the deck and the Groups tab ----------------------------------------------------------

    /// <summary>The deck's line about what is scanned: the groups ticked for the radio selected.</summary>
    [ObservableProperty] private string _groupsSummary = "";
    /// <summary>How many frequencies of those groups are ticked to be scanned.</summary>
    [ObservableProperty] private int _activeCount;
    /// <summary>The Groups tab's filter: only the groups and frequencies whose names hold this are shown.</summary>
    [ObservableProperty] private string _groupFilter = "";
    /// <summary>The frequency list as shown: all of it, or those matching the filter.</summary>
    [ObservableProperty] private IReadOnlyList<ActiveFrequency> _shownActive = [];

    partial void OnGroupFilterChanged(string value) => ApplyFilter();
    partial void OnActiveFrequenciesChanged(IReadOnlyList<ActiveFrequency> value) => ApplyFilter();

    private void ApplyFilter()
    {
        string f = GroupFilter.Trim();
        bool Has(string text) => text.Contains(f, StringComparison.OrdinalIgnoreCase);
        foreach (var g in GroupRows) g.IsShown = f.Length == 0 || Has(g.Label);
        ShownActive = f.Length == 0 ? ActiveFrequencies : ActiveFrequencies.Where(a => Has(a.Name) || a.Frequency.StartsWith(f, StringComparison.Ordinal)).ToList();
    }

    public IReadOnlyList<SearchRange> SearchRanges { get; } =
    [
        new("Airband 118–137", "118", "137"), new("2 m 144–148", "144", "148"), new("VHF high 148–174", "148", "174"),
        new("Marine 156–162", "156", "162.025"), new("NOAA 162.40–162.55", "162.4", "162.55"),
        new("70 cm 420–450", "420", "450"), new("UHF 450–470", "450", "470"),
    ];

    [RelayCommand]
    private void SetSearchRange(SearchRange range)
    {
        SearchFrom = range.From;
        SearchTo = range.To;
    }

    /// <summary>The deck's "Controls for" buttons: make this radio the one the controls work, as clicking its display does.</summary>
    [RelayCommand]
    private void SelectRadio(RadioViewModel radio) => Select(radio);

    public System.Collections.ObjectModel.ObservableCollection<MemoryRow> Memories { get; } = [];
    /// <summary>Every memory group with its tick. None ticked means all groups are shown and scanned.</summary>
    public System.Collections.ObjectModel.ObservableCollection<GroupRow> GroupRows { get; } = [];
    /// <summary>The rows of <see cref="GroupRows"/> for the groups the user made, and for those made from the tags.</summary>
    public System.Collections.ObjectModel.ObservableCollection<GroupRow> UserGroupRows { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<GroupRow> AutoGroupRows { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<GroupRow> PlaceGroupRows { get; } = [];
    [ObservableProperty] private bool _hasUserGroups;
    [ObservableProperty] private bool _hasAutoGroups;
    [ObservableProperty] private bool _hasPlaceGroups;

    /// <param name="receivers">One for each radio to run, the window's own first. The first takes the settings of
    /// the radio it will open, or those last used; each other takes the settings kept under its serial number.</param>
    public MainViewModel(Settings settings, IReadOnlyList<IReceiver> receivers, MemoryStore memories)
    {
        _settings = settings;
        _memories = memories;
        var radios = new List<RadioViewModel>();
        foreach (var rx in receivers)
        {
            var kept = radios.Count == 0 ? settings.ForFirstRadio(rx.Source.Serial)
                : rx.Source.Serial is { } serial ? settings.Entry(serial) : new RadioSettings();
            var radio = new RadioViewModel(rx, kept, memories);
            // A radio that has no groups of its own yet starts with the ones that were shared. No group ticked was
            // every group before 0.1.6, and is none now: a list kept empty without NoGroups is every group.
            List<string> picked = [.. kept.Groups ?? settings.Groups ?? (settings.Group is { } g && g != AllGroups ? [g] : [])];
            _groups[radio] = picked.Count == 0 && !kept.NoGroups ? AllGroupNames() : picked;
            radio.IsTaken = s => radios.Any(o => o != radio && o.Serial == s);
            radios.Add(radio);
        }
        RadioList = radios;
        _radio = radios[0];
        _radio.IsSelected = true;
        foreach (var r in radios)
        {
            r.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(RadioViewModel.IsScanning) or nameof(RadioViewModel.IsSearching)) ScanStateChanged();
                else if (e.PropertyName is nameof(RadioViewModel.Name)) OnPropertyChanged(nameof(GroupsTitle));
                else if (e.PropertyName is nameof(RadioViewModel.IsOn)) MarkInUse();
                else if (e.PropertyName is nameof(RadioViewModel.IsBusy) && !r.IsBusy) ListReceivers();     // switched on or off, or changed over
            };
        }
        _loading = true;
        _selectedAvoidTime = AvoidTimes.FirstOrDefault(a => (int)a.Value == settings.AvoidMinutes) ?? AvoidTimes[0];
        _selectedResumeTime = ResumeTimes.FirstOrDefault(r => (int)r.Value == settings.ResumeSeconds) ?? ResumeTimes[^1];
        _selectedMultiHangTime = MultiHangTimes.FirstOrDefault(h => (int)h.Value == settings.MultiHangSeconds)
            ?? MultiHangTimes.First(h => (int)h.Value == Settings.DefaultMultiHangSeconds);
        _isMini = settings.Mini;
        _selectedAppearance = Appearances.Contains(settings.Appearance) ? settings.Appearance : "System";
        _selectedDisplayLook = settings.DisplayFollowsLight ? Follows : StaysDark;
        ApplyAppearance(_selectedAppearance);
        _loading = false;
        foreach (var r in radios)
        {
            r.SetAvoidTime(_selectedAvoidTime);
            r.SetResumeTime(_selectedResumeTime);
            r.SetMultiHangTime(_selectedMultiHangTime);
        }
        RefreshMemories(_groups[_radio]);
        _loading = true;
        SearchFrom = settings.SearchFromMHz.ToString("0.####", CultureInfo.InvariantCulture);
        SearchTo = settings.SearchToMHz.ToString("0.####", CultureInfo.InvariantCulture);
        RefreshAudioDevices(settings.AudioDevice);
        _loading = false;
        foreach (var r in radios) r.SetAudioDevice(SelectedAudioDevice == DefaultDevice ? null : SelectedAudioDevice);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    /// <summary>Switch on every radio that is off, one after another.</summary>
    public async Task StartAll()
    {
        foreach (var r in RadioList)
            if (!r.IsOn) await r.TogglePowerCommand.ExecuteAsync(null);
    }

    /// <summary>Look again for radios, for the Receiver box.</summary>
    public void RefreshRadios()
    {
        foreach (var r in RadioList) r.RefreshRadios();
        ListReceivers();
    }

    // The Receiver box's list, from what each radio last found.
    private void ListReceivers()
    {
        var list = new List<RadioInfo>();
        foreach (var r in RadioList)
            if (InfoOf(r) is { } here && !list.Contains(here)) list.Add(here);
        foreach (var free in RadioList[0].Radios)
            if (!list.Any(r => r.Serial == free.Serial)) list.Add(free);
        var shown = ShownReceiver;
        _loading = true;
        ReceiverChoices = list;
        // The one chosen stays chosen while it is there; otherwise the radio the controls work.
        ShownReceiver = shown is not null && list.Contains(shown) && !IsHere(shown.Serial) ? shown : InfoOf(Radio);
        _loading = false;
        ShownChanged();
    }

    // ---- several radios ----------------------------------------------------------------------

    private static RadioInfo? InfoOf(RadioViewModel r) =>
        r.Serial is { Length: > 0 } serial ? new RadioInfo(r.Model ?? "Radio", serial) : null;

    private bool IsHere(string serial) => RadioList.Any(r => r.Serial == serial);

    partial void OnRadioChanged(RadioViewModel? oldValue, RadioViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
            _groups[oldValue] = SelectedGroups;
        }
        newValue.IsSelected = true;
        // The ticks are the selected radio's own.
        RefreshMemories(_groups[newValue]);
        OnPropertyChanged(nameof(GroupsTitle));
        _loading = true;
        ShownReceiver = InfoOf(newValue);
        _loading = false;
        ShownChanged();
        ScanStateChanged();
    }

    /// <summary>Make this radio the one the controls work, as clicking its strip does.</summary>
    public void Select(RadioViewModel radio)
    {
        if (RadioList.Contains(radio)) Radio = radio;
    }

    /// <summary>Choosing a radio that is running here in the Receiver box selects it, as clicking its strip does.</summary>
    partial void OnShownReceiverChanged(RadioInfo? value)
    {
        if (_loading) return;
        if (value is not null && RadioList.FirstOrDefault(r => r.Serial == value.Serial) is { } here) Radio = here;
        ShownChanged();
    }

    private void ShownChanged()
    {
        OnPropertyChanged(nameof(ShownIsHere));
        OnPropertyChanged(nameof(ShownIsElsewhere));
        OnPropertyChanged(nameof(ShownUse));
        OnPropertyChanged(nameof(CanSetUse));
        OnPropertyChanged(nameof(CanChangeOver));
    }

    /// <summary>The radio chosen in the Receiver box is one running here, so the Radio part shows and sets it.</summary>
    public bool ShownIsHere => ShownReceiver is null || IsHere(ShownReceiver.Serial);
    /// <summary>The radio chosen in the Receiver box is plugged in but not run by this program now.</summary>
    public bool ShownIsElsewhere => !ShownIsHere;
    private string? ShownSerial => ShownReceiver?.Serial ?? Radio.Serial;
    public bool CanSetUse => ShownSerial is { Length: > 0 };

    /// <summary>The radio chosen in the Receiver box is run whenever it is plugged in. Takes effect when the app is next started.</summary>
    public bool ShownUse
    {
        get => ShownSerial is { } serial && _settings.Radios.TryGetValue(serial, out var kept) && kept.Use;
        set
        {
            if (ShownSerial is not { Length: > 0 } serial || value == ShownUse) return;
            _settings.Entry(serial).Use = value;
            _settings.Save();
            OnPropertyChanged();
            Radio.Status = !value && !_settings.Radios.Values.Any(r => r.Use)
                ? "With no radio marked for use, the one used last is used"
                : value == IsHere(serial) ? "" : "Takes effect when the app is next started";
        }
    }

    /// <summary>With one radio running, it can be changed over to the free one chosen in the Receiver box.</summary>
    public bool CanChangeOver => IsSingle && ShownIsElsewhere;

    /// <summary>Stop using the radio in use and use the one chosen in the Receiver box instead, now.</summary>
    [RelayCommand]
    private void ChangeOver()
    {
        if (!CanChangeOver || ShownReceiver is not { } to) return;
        var radio = RadioList[0];
        if (radio.Serial is { Length: > 0 } from && _settings.Radios.TryGetValue(from, out var old)) old.Use = false;
        _settings.Entry(to.Serial).Use = true;
        radio.ChangeTo(to);
        _dirty = true;
        ListReceivers();
    }

    // The radio that is run when none is marked is the one in use: mark it, so the Use box says what is so.
    private void MarkInUse()
    {
        if (_settings.Radios.Values.Any(r => r.Use)) return;
        if (RadioList[0] is not { IsOn: true, Serial: { Length: > 0 } serial }) return;
        _settings.Entry(serial).Use = true;
        _dirty = true;
        ShownChanged();
    }

    private void ScanStateChanged()
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(ScanText));
        RefreshActive();
    }

    /// <summary>
    /// The frequencies of the selected radio's groups, for the list under the user groups on Operate, each
    /// with the Scan tick of its stored channels: the ticked ones are what that radio scans, whatever the
    /// other radios scan.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<ActiveFrequency> _activeFrequencies = [];
    [ObservableProperty] private string _activeTitle = "Active Frequencies";

    private static double Place(double hz) => Math.Round(hz / 100);

    private void RefreshActive()
    {
        // A frequency shared by several stored channels is one line, as it is visited once: ticked if any of them is.
        var lines = Memories.GroupBy(m => Place(m.Channel.FrequencyHz))
            .Select(g => (Place: g.Key, Hz: g.First().Channel.FrequencyHz, (g.FirstOrDefault(m => m.Scan) ?? g.First()).Name, Scan: g.Any(m => m.Scan)))
            .OrderBy(l => l.Hz).ToList();
        // The same lines as before: only the ticks are brought up to date, so the list stays where it was scrolled to.
        if (lines.Select(l => (l.Place, l.Name)).SequenceEqual(ActiveFrequencies.Select(a => (a.Place, a.Name))))
            for (int i = 0; i < lines.Count; i++) ActiveFrequencies[i].Show(lines[i].Scan);
        else
            ActiveFrequencies = lines.Select(l => new ActiveFrequency(l.Place, (l.Hz / 1e6).ToString("0.0000", CultureInfo.InvariantCulture),
                l.Name, l.Scan, ActiveTicked)).ToList();
        ActiveTitle = $"Active Frequencies ({lines.Count(l => l.Scan)})";
        ActiveCount = lines.Count(l => l.Scan);
    }

    private bool _ticking;

    // A frequency was ticked or unticked in the list on Operate: every stored channel shown on it takes the tick.
    private void ActiveTicked(ActiveFrequency line)
    {
        _ticking = true;
        foreach (var m in Memories.Where(m => Place(m.Channel.FrequencyHz) == line.Place)) m.Scan = line.Scan;
        _ticking = false;
        ScanTicksChanged();
    }

    // A channel's Scan tick has changed, here or in a list on Groups or Channels: the radios that are scanning take it up at once.
    private void ScanTicksChanged()
    {
        if (_ticking) return;
        _memories.Save();
        Rescan();
        RefreshActive();
    }

    /// <summary>The radio the controls work is scanning or searching: the Scan button stops it.</summary>
    public bool IsScanning => Radio.IsScanning;
    public string ScanText => IsScanning ? "Stop" : "Scan";

    // ---- memories and scanning --------------------------------------------------------------

    /// <summary>The groups ticked in the selector, which are those of the radio the controls work; empty means none.</summary>
    private List<string> SelectedGroups => GroupRows.Where(g => g.Selected).Select(g => g.Name).ToList();

    /// <summary>The groups ticked for a radio, each radio having its own; empty means none. Groups that are no longer there are left out.</summary>
    private List<string> GroupsOf(RadioViewModel radio) => radio == Radio
        ? SelectedGroups
        : GroupRows.Where(g => _groups[radio].Any(w => MemoryStore.SameGroup(w, g.Name))).Select(g => g.Name).ToList();

    /// <summary>The heading over the user groups on Operate: with several radios it says whose ticks these are.</summary>
    public string GroupsTitle => IsMulti ? $"User Groups · {Radio.Name}" : "User Groups";

    /// <summary>Every group, in the order the selector lists them: the user's own, then those made from the locations and the tags.</summary>
    private List<string> AllGroupNames() =>
        [.. _memories.GroupNames().Where(n => !MemoryStore.IsAutoGroup(n)), .. _memories.PlaceGroupNames(), .. _memories.TagGroupNames()];

    private void RefreshMemories(IReadOnlyCollection<string>? select = null)
    {
        _loading = true;
        // The group rows are rebuilt only when the set of groups has changed, so an open selector keeps its rows.
        var wanted = select ?? SelectedGroups;
        // The user's own groups first, then the ones made from the locations and the tags. A tag's group keeps its tick
        // when its name changes spelling.
        var names = AllGroupNames();
        if (select is not null || !names.SequenceEqual(GroupRows.Select(g => g.Name)))
        {
            GroupRows.Clear();
            UserGroupRows.Clear();
            AutoGroupRows.Clear();
            PlaceGroupRows.Clear();
            foreach (string n in names)
            {
                var row = new GroupRow(n, wanted.Any(w => MemoryStore.SameGroup(w, n)), OnGroupsChanged);
                GroupRows.Add(row);
                (row.IsPlace ? PlaceGroupRows : row.IsAuto ? AutoGroupRows : UserGroupRows).Add(row);
            }
            HasUserGroups = UserGroupRows.Count > 0;
            HasAutoGroups = AutoGroupRows.Count > 0;
            HasPlaceGroups = PlaceGroupRows.Count > 0;
        }
        foreach (var row in GroupRows) row.Count = _memories.Channels.Count(c => MemoryStore.InGroup(c, row.Name));
        var shown = SelectedGroups;
        var labels = shown.Select(MemoryStore.GroupLabel).ToList();
        GroupsSummary = labels.Count == 0 ? "No groups ticked" : labels.Count <= 2 ? string.Join(", ", labels)
            : $"{labels[0]}, {labels[1]}  +{labels.Count - 2}";
        GroupsText = shown.Count == 0 ? NoGroups : shown.Count == GroupRows.Count ? AllGroups : shown.Count == 1 ? shown[0] : $"{shown.Count} groups";
        Memories.Clear();
        // A channel in several of the groups shown appears once, under the first of them.
        string First(MemoryChannel c) =>
            shown.Where(g => MemoryStore.InGroup(c, g)).Order().FirstOrDefault() ?? "";
        foreach (var c in _memories.Channels.Where(c => shown.Any(g => MemoryStore.InGroup(c, g)))
                     .OrderBy(First).ThenBy(c => c.FrequencyHz))
            Memories.Add(new MemoryRow(c, ScanTicksChanged));
        _loading = false;
        RefreshActive();
        ApplyFilter();
    }

    /// <summary>The stored channels, for the channel editor.</summary>
    public MemoryStore Store => _memories;

    /// <summary>Where the main window was when it was last closed, kept with the settings.</summary>
    public Settings.Place? WindowPlace
    {
        get => _settings.Window;
        set
        {
            if (value == _settings.Window) return;
            _settings.Window = value;
            _settings.Save();
        }
    }

    /// <summary>Where the small window was and how big, kept with the settings.</summary>
    public Settings.Place? MiniWindowPlace
    {
        get => _settings.MiniWindow;
        set
        {
            if (value == _settings.MiniWindow) return;
            _settings.MiniWindow = value;
            _settings.Save();
        }
    }

    /// <summary>The channel editor's column widths, kept with the settings.</summary>
    public Dictionary<string, double>? ChannelColumns
    {
        get => _settings.ChannelColumns;
        set
        {
            _settings.ChannelColumns = value;
            _settings.Save();
        }
    }

    /// <summary>The RadioReference browser's account name and place, kept with the settings.</summary>
    public Settings.RrPlace RadioReferencePlace => _settings.RadioReference ??= new Settings.RrPlace();

    public void SaveSettings() => _settings.Save();

    /// <summary>The channel editor has changed the stored channels: show them again.</summary>
    public void MemoriesEdited()
    {
        RefreshMemories();
        foreach (var r in RadioList) r.StoreChanged();
    }

    private int _diskTicks;
    /// <summary>The channels were read again after something else changed the file; the channel editor shows them again too.</summary>
    public event Action? Reloaded;

    // The stored channels have been read again from the file, changed by something other than this
    // program. Every channel is a new object. A scan in progress goes on with the channels it began with.
    private void MemoriesReloaded()
    {
        RefreshMemories();
        foreach (var r in RadioList) r.StoreReloaded();
        Radio.Status = RadioList.Any(r => r.IsScanning)
            ? "The stored channels were changed outside the program and read again; the scan goes on with the ones it began with"
            : "The stored channels were changed outside the program and read again";
        Reloaded?.Invoke();
    }

    /// <summary>The channel editor has renamed a group: if it was ticked here, the new name is.</summary>
    public void GroupRenamed(string old, string name)
    {
        foreach (var r in RadioList) _groups[r] = _groups[r].Select(g => g == old ? name : g).Distinct().ToList();
        RefreshMemories(SelectedGroups.Select(g => g == old ? name : g).Distinct().ToList());
    }

    /// <summary>Tune the radio the controls work to a stored channel (and end its scan), as picking it in the list does.</summary>
    public void Tune(MemoryChannel c) => Radio.Tune(c);

    private void StopScans()
    {
        foreach (var r in RadioList)
            if (r.IsScanning) r.StopScan();
    }

    // A group was ticked or unticked in the selector.
    private void OnGroupsChanged()
    {
        if (_loading) return;
        // The ticks are the selected radio's: its scan ends; the others' are not touched.
        if (Radio.IsScanning) Radio.StopScan();
        RefreshMemories();
        _dirty = true;
    }

    /// <summary>Tick every group: all of them are shown and scanned by the radio selected.</summary>
    [RelayCommand]
    private void ShowAllGroups()
    {
        if (GroupRows.All(g => g.Selected)) return;
        if (Radio.IsScanning) Radio.StopScan();
        RefreshMemories(AllGroupNames());
        _dirty = true;
    }

    /// <summary>Untick every group: the radio selected shows and scans nothing until one is ticked again.</summary>
    [RelayCommand]
    private void ClearGroups()
    {
        if (SelectedGroups.Count == 0) return;
        if (Radio.IsScanning) Radio.StopScan();
        RefreshMemories([]);
        _dirty = true;
    }

    /// <summary>Picking a stored channel tunes to it (and ends a scan).</summary>
    partial void OnSelectedMemoryChanged(MemoryRow? value)
    {
        if (_loading || value is null) return;
        Tune(value.Channel);
    }

    private static ScanChannel ToScan(MemoryChannel c)
    {
        var band = BandPlan.Find(c.FrequencyHz);
        var mode = c.Mode ?? band.Mode;
        double width = c.BandwidthHz ?? (mode == band.Mode ? band.BandwidthHz
            : mode switch { Mode.Am => BandPlan.AmAir, Mode.Wfm => BandPlan.Wfm, _ => BandPlan.NfmWide });
        return new ScanChannel(c.Name, c.FrequencyHz, mode, width);
    }

    /// <summary>The output devices present now, keeping the choice if it is still there.</summary>
    private void RefreshAudioDevices(string? wanted)
    {
        var names = Audio.AudioOut.Devices();
        AudioDevices = [DefaultDevice, .. names];
        SelectedAudioDevice = wanted is not null && names.Contains(wanted) ? wanted : DefaultDevice;
    }

    partial void OnSelectedAudioDeviceChanged(string value)
    {
        if (_loading || value is null) return;
        foreach (var r in RadioList) r.SetAudioDevice(value == DefaultDevice ? null : value);
        _dirty = true;
    }

    /// <summary>Look again for output devices (one plugged in since the list was made).</summary>
    [RelayCommand]
    private void RescanAudioDevices()
    {
        _loading = true;
        RefreshAudioDevices(SelectedAudioDevice);
        _loading = false;
    }

    /// <summary>
    /// Search a range of frequencies for anything active, on the band plan's channel raster (or
    /// the step chosen), or stop searching. A frequency skipped is left out for ten minutes.
    /// </summary>
    [RelayCommand]
    private void ToggleSearch()
    {
        var radio = Radio;
        if (radio.IsScanning)
        {
            radio.StopScan();
            return;
        }
        if (!TryMHz(SearchFrom, out double lo) || !TryMHz(SearchTo, out double hi) || hi <= lo)
        {
            radio.Status = "Enter the search range in MHz, such as 144 to 148";
            return;
        }
        if (!radio.IsOn)
        {
            radio.Status = "Start the radio first";
            return;
        }
        var list = new List<ScanChannel>();
        for (double f = lo; f <= hi + 1 && list.Count < 40000;)
        {
            // Channels sit on whole multiples of the step (154.935 MHz is 20658 x 7.5 kHz), so the
            // raster is counted from zero, not from where the search happens to start.
            var band = BandPlan.Find(f);
            double step = radio.SelectedStep.Value > 0 ? radio.SelectedStep.Value : band.StepHz;
            double channel = Math.Ceiling(f / step - 1e-6) * step;
            if (channel > hi + 1) break;
            band = BandPlan.Find(channel);
            if (!band.Blocked) list.Add(new ScanChannel("", Math.Round(channel / 10) * 10, band.Mode, band.BandwidthHz));
            f = channel + step;
        }
        if (list.Count == 0) return;
        _settings.SearchFromMHz = lo / 1e6;
        _settings.SearchToMHz = hi / 1e6;
        _dirty = true;
        radio.StartSearch(list);
    }

    private static bool TryMHz(string text, out double hz)
    {
        bool ok = double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
        hz = v * 1e6;
        return ok && hz is >= MinHz and <= MaxHz;
    }

    // What each radio that is scanning from the stored channels was last given, so that a change of ticks
    // starts again only the scans whose list it changes.
    private readonly Dictionary<RadioViewModel, List<ScanChannel>> _scanLists = [];

    private IEnumerable<RadioViewModel> Scanners => RadioList.Where(r => r.IsOn && r.IsScanning && !r.IsSearching);

    /// <summary>
    /// Scan the ticked channels of the selected radio's groups with that radio, or stop its scan (or its
    /// search). Each radio scans its own groups in full and the others are left as they are: two radios
    /// whose groups have channels in common both scan them.
    /// </summary>
    [RelayCommand]
    private void ToggleScan()
    {
        var radio = Radio;
        if (radio.IsScanning)
        {
            radio.StopScan();
            return;
        }
        if (!radio.IsOn)
        {
            radio.Status = "Start the radio first";
            return;
        }
        if (ScanListOf(radio) is not (var members, var list))
        {
            radio.Status = SelectedGroups.Count == 0 ? "No groups are ticked: tick one or more on Groups"
                : "No channels are ticked for scanning in the groups shown";
            return;
        }
        radio.StartScan(members, list);
        _scanLists[radio] = list;
        RefreshActive();
    }

    /// <summary>The Scan button on a radio's own display: select that radio, then start or stop its scan as the Scan button on Operate does.</summary>
    [RelayCommand]
    private void ToggleScanOf(RadioViewModel radio)
    {
        Select(radio);
        if (Radio == radio) ToggleScan();
    }

    // A channel's Scan tick has changed: each radio scanning takes up its own list again, if the change touched it.
    private void Rescan()
    {
        foreach (var radio in Scanners.ToList())
        {
            if (ScanListOf(radio) is not (var members, var list))
            {
                radio.StopScan();
                radio.Status = "No channels are ticked for scanning in this radio's groups";
                _scanLists.Remove(radio);
                continue;
            }
            if (_scanLists.TryGetValue(radio, out var had) && had.Select(c => c.FrequencyHz).SequenceEqual(list.Select(c => c.FrequencyHz))) continue;
            _scanLists[radio] = list;
            radio.StartScan(members, list);
        }
    }

    /// <summary>
    /// What a radio scans: the ticked stored channels of its own groups, and the scan list made from them
    /// (a frequency several stored channels have is visited once). Null when none is ticked.
    /// </summary>
    private (List<MemoryChannel> Members, List<ScanChannel> List)? ScanListOf(RadioViewModel radio)
    {
        var groups = GroupsOf(radio);
        var members = _memories.Channels.Where(c => c.Scan && groups.Any(g => MemoryStore.InGroup(c, g))).ToList();
        if (members.Count == 0) return null;
        var list = members.GroupBy(c => Math.Round(c.FrequencyHz / 100)).OrderBy(g => g.First().FrequencyHz).Select(g => ToScan(g.First())).ToList();
        return (members, list);
    }

    /// <summary>Takes effect at once, on the channel the scan is stopped on as well.</summary>
    partial void OnSelectedResumeTimeChanged(Choice value)
    {
        if (_loading) return;
        foreach (var r in RadioList) r.SetResumeTime(value);
        _dirty = true;
    }

    /// <summary>Takes effect at once, on every radio, on the slots holding a channel as well.</summary>
    partial void OnSelectedMultiHangTimeChanged(Choice value)
    {
        if (_loading) return;
        foreach (var r in RadioList) r.SetMultiHangTime(value);
        _dirty = true;
    }

    /// <summary>A change of timer applies to what is avoided from now on; those already avoided keep the time they had.</summary>
    partial void OnSelectedAvoidTimeChanged(Choice value)
    {
        if (_loading) return;
        foreach (var r in RadioList) r.SetAvoidTime(value);
        _dirty = true;
    }

    /// <summary>Store the present frequency under the name typed, in the group shown.</summary>
    [RelayCommand]
    private void StoreChannel()
    {
        var radio = Radio;
        string name = NewName.Trim();
        if (name.Length == 0)
        {
            radio.Status = "Type a name for the channel first";
            return;
        }
        // A channel already stored here is renamed and keeps what else is known about it.
        var all = _memories.FindAll(radio.FrequencyHz);
        var channel = radio.Picked is not null && all.Contains(radio.Picked) ? radio.Picked : all.FirstOrDefault();
        if (channel is null)
        {
            channel = new MemoryChannel { FrequencyHz = radio.FrequencyHz };
            _memories.Channels.Add(channel);
        }
        channel.Name = name;
        channel.Mode = radio.AutoMode ? null : radio.Mode;
        channel.BandwidthHz = radio.AutoMode ? null : radio.SelectedWidth?.Value;
        // One group shown: it goes there (unless it is a tag's group, which nothing can be put in).
        // Otherwise it stays where it was, or goes in "My channels".
        string? group = SelectedGroups is [var only] && !MemoryStore.IsAutoGroup(only) ? only : channel.Groups.Count == 0 ? "My channels" : null;
        if (group is not null && !channel.Groups.Contains(group)) channel.Groups.Add(group);
        radio.Picked = channel;
        _memories.Save();
        NewName = "";
        radio.Status = "";
        RefreshMemories();
        foreach (var r in RadioList) r.StoreChanged();
    }

    [RelayCommand]
    private void DeleteChannel()
    {
        if (SelectedMemory is null) return;
        StopScans();
        _memories.Channels.Remove(SelectedMemory.Channel);
        _memories.Save();
        RefreshMemories();
    }

    // ---- the window -------------------------------------------------------------------------

    /// <summary>Go between the full window and the small one. The window follows <see cref="IsMini"/>.</summary>
    [RelayCommand]
    private void ToggleMini() => IsMini = !IsMini;

    partial void OnIsMiniChanged(bool value)
    {
        _settings.Mini = value;
        _dirty = true;
    }

    private void Tick()
    {
        if (++_diskTicks >= 20)     // once a second
        {
            _diskTicks = 0;
            if (_memories.ReloadIfChangedOnDisk()) MemoriesReloaded();
        }
        foreach (var r in RadioList) r.Tick();
        if (++_ticks % 100 == 0 && (_dirty || RadioList.Any(r => r.Dirty))) Save();
    }

    private void Save()
    {
        _dirty = false;
        foreach (var r in RadioList)
        {
            r.Dirty = false;
            if (r.Serial is not { Length: > 0 } serial) continue;
            var entry = _settings.Entry(serial);
            r.WriteTo(entry);
            entry.Groups = GroupsOf(r);
            entry.NoGroups = entry.Groups.Count == 0;
        }
        // What the window's own radio has goes in the one-radio fields too, for a build from before.
        var first = RadioList[0];
        var flat = _settings.Flat();
        first.WriteTo(flat);
        _settings.SetFlat(flat);
        if (first.ChosenSerial is { Length: > 0 } chosen) _settings.RadioSerial = chosen;
        _settings.AvoidMinutes = (int)SelectedAvoidTime.Value;
        _settings.ResumeSeconds = (int)SelectedResumeTime.Value;
        _settings.MultiHangSeconds = (int)SelectedMultiHangTime.Value;
        _settings.Groups = GroupsOf(first);       // the shared ones of before, for a build from before
        _settings.Group = null;
        _settings.AudioDevice = SelectedAudioDevice == DefaultDevice ? null : SelectedAudioDevice;
        _settings.Save();
    }

    public void Shutdown()
    {
        _timer.Stop();
        Save();
        foreach (var r in RadioList) r.Dispose();
    }
}
