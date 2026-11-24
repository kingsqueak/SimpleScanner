using SimpleScanner.Radio;
using System.Globalization;
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
    private const string AllGroups = "All groups", DefaultDevice = "System default";
    private readonly DispatcherTimer _timer;
    private bool _loading, _dirty;
    private int _ticks;

    /// <summary>The radios running, the window's own first; each further one is in a helper program.</summary>
    public IReadOnlyList<RadioViewModel> RadioList { get; }
    /// <summary>The radio the display shows and the controls work.</summary>
    [ObservableProperty] private RadioViewModel _radio;

    /// <summary>How long Avoid keeps a frequency out of the scan, in minutes; 0 is until the app is closed.</summary>
    public IReadOnlyList<Choice> AvoidTimes { get; } = [new("30 min", 30), new("1 hr", 60), new("Full session", 0)];

    /// <summary>The longest a scan stays on a busy channel, in seconds; 0 (Hold) is until the signal drops.</summary>
    public IReadOnlyList<Choice> ResumeTimes { get; } =
        [new("15s", 15), new("30s", 30), new("1min", 60), new("3min", 180), new("5min", 300), new("10min", 600), new("Hold", 0)];

    /// <summary>The small window is showing: the display and one row of buttons.</summary>
    [ObservableProperty] private bool _isMini;
    [ObservableProperty] private Choice _selectedAvoidTime;
    [ObservableProperty] private Choice _selectedResumeTime;
    /// <summary>What the group selector's button says: the groups ticked, or that all are shown.</summary>
    [ObservableProperty] private string _groupsText = AllGroups;
    [ObservableProperty] private MemoryRow? _selectedMemory;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private IReadOnlyList<string> _audioDevices = [DefaultDevice];
    [ObservableProperty] private string _selectedAudioDevice = DefaultDevice;
    [ObservableProperty] private string _searchFrom = "144";
    [ObservableProperty] private string _searchTo = "148";

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
            radio.IsTaken = s => radios.Any(o => o != radio && o.Serial == s);
            radios.Add(radio);
        }
        RadioList = radios;
        _radio = radios[0];
        _loading = true;
        _selectedAvoidTime = AvoidTimes.FirstOrDefault(a => (int)a.Value == settings.AvoidMinutes) ?? AvoidTimes[0];
        _selectedResumeTime = ResumeTimes.FirstOrDefault(r => (int)r.Value == settings.ResumeSeconds) ?? ResumeTimes[^1];
        _isMini = settings.Mini;
        _loading = false;
        foreach (var r in radios)
        {
            r.SetAvoidTime(_selectedAvoidTime);
            r.SetResumeTime(_selectedResumeTime);
        }
        RefreshMemories(settings.Groups ?? (settings.Group is { } g && g != AllGroups ? [g] : []));
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
    }

    // ---- memories and scanning --------------------------------------------------------------

    /// <summary>The groups ticked in the selector; empty means all.</summary>
    private List<string> SelectedGroups => GroupRows.Where(g => g.Selected).Select(g => g.Name).ToList();

    private void RefreshMemories(IReadOnlyCollection<string>? select = null)
    {
        _loading = true;
        // The group rows are rebuilt only when the set of groups has changed, so an open selector keeps its rows.
        var wanted = select ?? SelectedGroups;
        // The user's own groups first, then the ones made from the locations and the tags. A tag's group keeps its tick
        // when its name changes spelling.
        string[] names = [.. _memories.GroupNames().Where(n => !MemoryStore.IsAutoGroup(n)), .. _memories.PlaceGroupNames(), .. _memories.TagGroupNames()];
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
        var shown = SelectedGroups;
        GroupsText = shown.Count == 0 ? AllGroups : shown.Count == 1 ? shown[0] : $"{shown.Count} groups";
        Memories.Clear();
        // A channel in several of the groups shown appears once, under the first of them.
        string First(MemoryChannel c) =>
            (shown.Count == 0 ? c.Groups : shown.Where(g => MemoryStore.InGroup(c, g))).Order().FirstOrDefault() ?? "";
        foreach (var c in _memories.Channels.Where(c => shown.Count == 0 || shown.Any(g => MemoryStore.InGroup(c, g)))
                     .OrderBy(First).ThenBy(c => c.FrequencyHz))
            Memories.Add(new MemoryRow(c, () => _memories.Save()));
        _loading = false;
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
    public void GroupRenamed(string old, string name) =>
        RefreshMemories(SelectedGroups.Select(g => g == old ? name : g).Distinct().ToList());

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
        StopScans();
        RefreshMemories();
        _dirty = true;
    }

    /// <summary>Untick every group: all of them are shown and scanned.</summary>
    [RelayCommand]
    private void ShowAllGroups()
    {
        if (SelectedGroups.Count == 0) return;
        StopScans();
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

    /// <summary>Scan the ticked channels of the groups shown, or stop scanning.</summary>
    [RelayCommand]
    private void ToggleScan()
    {
        var radio = Radio;
        if (radio.IsScanning)
        {
            radio.StopScan();
            return;
        }
        // A frequency shared by several stored channels is visited once.
        var members = Memories.Where(m => m.Scan).Select(m => m.Channel).ToList();
        var list = members.GroupBy(c => Math.Round(c.FrequencyHz / 100)).Select(g => ToScan(g.First()))
            .OrderBy(c => c.FrequencyHz).ToList();
        if (list.Count == 0)
        {
            radio.Status = "No channels are ticked for scanning in the groups shown";
            return;
        }
        radio.StartScan(members, list);
    }

    /// <summary>Takes effect at once, on the channel the scan is stopped on as well.</summary>
    partial void OnSelectedResumeTimeChanged(Choice value)
    {
        if (_loading) return;
        foreach (var r in RadioList) r.SetResumeTime(value);
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
            if (r.Serial is { Length: > 0 } serial) r.WriteTo(_settings.Entry(serial));
        }
        // What the window's own radio has goes in the one-radio fields too, for a build from before.
        var first = RadioList[0];
        var flat = _settings.Flat();
        first.WriteTo(flat);
        _settings.SetFlat(flat);
        if (first.ChosenSerial is { Length: > 0 } chosen) _settings.RadioSerial = chosen;
        _settings.AvoidMinutes = (int)SelectedAvoidTime.Value;
        _settings.ResumeSeconds = (int)SelectedResumeTime.Value;
        _settings.Groups = SelectedGroups;
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
