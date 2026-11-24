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
    /// <summary>What the selector shows: a tag's group goes by the tag alone, under its own heading.</summary>
    public string Label => IsAuto ? Name[MemoryStore.TagPrefix.Length..] : Name;

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

/// <summary>The scanner's front panel: what the display shows and what the controls do.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const double MinHz = 100_000, MaxHz = 2_000_000_000;

    private readonly Settings _settings;
    private readonly Receiver _rx;
    private readonly MemoryStore _memories;
    private const string AllGroups = "All groups", DefaultDevice = "System default";
    private long _lastPasses;
    private double _passRate;
    private bool _searching;
    // The stored channels the scan in progress was made from, and the one last picked by hand:
    // they decide whose details are shown where several channels share a frequency.
    private List<MemoryChannel> _scanMembers = [];
    private MemoryChannel? _picked;
    private readonly DispatcherTimer _timer;
    private bool _loading, _dirty;
    private int _ticks;

    public IReadOnlyList<Choice> Steps { get; } =
    [
        new("Auto", 0), new("5 kHz", 5000), new("6.25 kHz", 6250), new("7.5 kHz", 7500), new("8.33 kHz", 25000.0 / 3),
        new("10 kHz", 10000), new("12.5 kHz", 12500), new("25 kHz", 25000), new("100 kHz", 100000), new("1 MHz", 1e6),
    ];

    /// <summary>How long Avoid keeps a frequency out of the scan, in minutes; 0 is until the app is closed.</summary>
    public IReadOnlyList<Choice> AvoidTimes { get; } = [new("30 min", 30), new("1 hr", 60), new("Full session", 0)];

    private static readonly Choice[] AmWidths = [new("8.33 kHz channel", BandPlan.AmAir833), new("25 kHz channel", BandPlan.AmAir)];
    private static readonly Choice[] NfmWidths = [new("12.5 kHz channel", BandPlan.NfmNarrow), new("25 kHz channel", BandPlan.NfmWide)];
    private static readonly Choice[] WfmWidths = [new("Broadcast", BandPlan.Wfm)];

    [ObservableProperty] private double _frequencyHz;
    [ObservableProperty] private string _bandName = "";
    /// <summary>Whose channel this is: the stored channel's agency and description.</summary>
    [ObservableProperty] private string _detailText = "";
    /// <summary>The stored channel's particulars: tag, signal type, tone, station class, input, callsign.</summary>
    [ObservableProperty] private string _infoText = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private string _entryText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _deviceText = "Off";
    [ObservableProperty] private string _gainText = "";
    [ObservableProperty] private string _levelText = "";
    [ObservableProperty] private double _level = -140;
    [ObservableProperty] private bool _squelchOpen;
    [ObservableProperty] private bool _overloaded;
    [ObservableProperty] private bool _isStereo;
    [ObservableProperty] private string _digitalText = "";
    [ObservableProperty] private bool _isEncrypted;
    [ObservableProperty] private bool _stereoOn;
    [ObservableProperty] private bool _blocked;
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _volume;
    [ObservableProperty] private double _squelchDb;
    [ObservableProperty] private bool _squelchOn;
    [ObservableProperty] private bool _autoGain;
    [ObservableProperty] private double _gainStep;
    [ObservableProperty] private double _maxGainStep = 27;
    [ObservableProperty] private bool _autoMode;
    [ObservableProperty] private Mode _mode;
    [ObservableProperty] private IReadOnlyList<Choice> _widths = NfmWidths;
    [ObservableProperty] private Choice? _selectedWidth;
    [ObservableProperty] private Choice _selectedStep;
    [ObservableProperty] private Choice _selectedAvoidTime;
    [ObservableProperty] private IReadOnlyList<string> _antennas;
    [ObservableProperty] private string _selectedAntenna;
    [ObservableProperty] private bool _isScanning;
    /// <summary>What the group selector's button says: the groups ticked, or that all are shown.</summary>
    [ObservableProperty] private string _groupsText = AllGroups;
    [ObservableProperty] private MemoryRow? _selectedMemory;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private IReadOnlyList<string> _audioDevices = [DefaultDevice];
    [ObservableProperty] private string _selectedAudioDevice = DefaultDevice;
    [ObservableProperty] private string _searchFrom = "144";
    [ObservableProperty] private string _searchTo = "148";
    [ObservableProperty] private bool _isSearching;
    public string SearchText => IsSearching ? "Stop search" : "Search";

    public System.Collections.ObjectModel.ObservableCollection<MemoryRow> Memories { get; } = [];
    /// <summary>Every memory group with its tick. None ticked means all groups are shown and scanned.</summary>
    public System.Collections.ObjectModel.ObservableCollection<GroupRow> GroupRows { get; } = [];
    /// <summary>The rows of <see cref="GroupRows"/> for the groups the user made, and for those made from the tags.</summary>
    public System.Collections.ObjectModel.ObservableCollection<GroupRow> UserGroupRows { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<GroupRow> AutoGroupRows { get; } = [];
    [ObservableProperty] private bool _hasUserGroups;
    [ObservableProperty] private bool _hasAutoGroups;
    public string ScanText => IsScanning ? "Stop" : "Scan";

    public bool IsAm => !AutoMode && Mode == Mode.Am;
    public bool IsNfm => !AutoMode && Mode == Mode.Nfm;
    public bool IsWfm => !AutoMode && Mode == Mode.Wfm;
    public string PowerText => IsOn ? "Stop" : "Start";

    public MainViewModel(Settings settings, Receiver receiver, MemoryStore memories)
    {
        _settings = settings;
        _rx = receiver;
        _memories = memories;
        _loading = true;
        _antennas = _rx.Source.Antennas;
        _selectedAntenna = _antennas.Contains(settings.Antenna) ? settings.Antenna : _antennas[0];
        _selectedStep = Steps.FirstOrDefault(s => Math.Abs(s.Value - settings.StepHz) < 0.5) ?? Steps[0];
        _selectedAvoidTime = AvoidTimes.FirstOrDefault(a => (int)a.Value == settings.AvoidMinutes) ?? AvoidTimes[0];
        _rx.AvoidTime = AvoidSpan(_selectedAvoidTime);
        Volume = settings.Volume * 100;
        SquelchDb = settings.SquelchDb;
        SquelchOn = settings.SquelchOn;
        StereoOn = settings.Stereo;
        AutoGain = settings.AutoGain;
        GainStep = settings.GainStep;
        AutoMode = settings.AutoMode;
        Mode = settings.Mode;
        FrequencyHz = Math.Clamp(settings.FrequencyHz, MinHz, MaxHz);
        _loading = false;

        _rx.Source.Antenna = _selectedAntenna;
        _rx.Source.AutoGain = AutoGain;
        _rx.Source.GainStep = (int)GainStep;
        _rx.Volume = Volume / 100;
        _rx.SquelchDb = SquelchDb;
        _rx.SquelchEnabled = SquelchOn;
        _rx.LevelOffsetDb = settings.LevelOffsetDb;
        ApplyFrequency(settings.BandwidthHz);
        RefreshMemories(settings.Groups ?? (settings.Group is { } g && g != AllGroups ? [g] : []));
        _loading = true;
        SearchFrom = settings.SearchFromMHz.ToString("0.####", CultureInfo.InvariantCulture);
        SearchTo = settings.SearchToMHz.ToString("0.####", CultureInfo.InvariantCulture);
        RefreshAudioDevices(settings.AudioDevice);
        _loading = false;
        _rx.AudioDevice = SelectedAudioDevice == DefaultDevice ? null : SelectedAudioDevice;
        _rx.Failed += why => Dispatcher.UIThread.Post(() =>
        {
            IsOn = false;
            DeviceText = "Off";
            Status = why;
        });

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    // ---- tuning ---------------------------------------------------------------------------

    partial void OnFrequencyHzChanged(double value)
    {
        double clamped = Math.Clamp(Math.Round(value / 10) * 10, MinHz, MaxHz);
        if (clamped != value)
        {
            FrequencyHz = clamped;
            return;
        }
        if (_loading) return;
        IsScanning = false;
        ApplyFrequency();
    }

    /// <summary>Tune the receiver and, in automatic mode, take the mode and width from the band plan.</summary>
    private void ApplyFrequency(double? keepWidth = null)
    {
        var band = BandPlan.Find(FrequencyHz);
        ShowLabel();
        if (AutoMode)
        {
            _loading = true;
            Mode = band.Mode;
            _loading = false;
            SetWidths(band.Mode, band.BandwidthHz);
        }
        else SetWidths(Mode, keepWidth ?? SelectedWidth?.Value ?? 0);
        _rx.FrequencyHz = FrequencyHz;
        Blocked = _rx.Blocked;
        EntryText = (FrequencyHz / 1e6).ToString("0.#####", CultureInfo.InvariantCulture);
        _dirty = true;
    }

    private void SetWidths(Mode mode, double preferred)
    {
        var list = mode switch { Mode.Am => AmWidths, Mode.Wfm => WfmWidths, _ => NfmWidths };
        var pick = list.FirstOrDefault(w => w.Value == preferred) ?? list[^1];
        _loading = true;
        Widths = list;
        SelectedWidth = pick;
        _loading = false;
        _rx.SetMode(mode, pick.Value);
        Blocked = _rx.Blocked;
        string width = mode == Mode.Wfm ? "" : pick.Value < 9000 || (mode == Mode.Nfm && pick.Value < 13000)
            ? (mode == Mode.Am ? " 8.33k" : " 12.5k") : " 25k";
        ModeText = (mode switch { Mode.Am => "AM", Mode.Wfm => "WFM", _ => "NFM" }) + width;
        OnPropertyChanged(nameof(IsAm));
        OnPropertyChanged(nameof(IsNfm));
        OnPropertyChanged(nameof(IsWfm));
        _dirty = true;
    }

    partial void OnSelectedWidthChanged(Choice? value)
    {
        if (_loading || value is null) return;
        // Choosing a width by hand is choosing the mode by hand.
        _loading = true;
        AutoMode = false;
        _loading = false;
        SetWidths(Mode, value.Value);
    }

    partial void OnAutoModeChanged(bool value)
    {
        if (_loading) return;
        ApplyFrequency();
    }

    [RelayCommand]
    private void SetAutoMode() => AutoMode = true;

    [RelayCommand]
    private void SetMode(string name)
    {
        var mode = name switch { "AM" => Mode.Am, "WFM" => Mode.Wfm, _ => Mode.Nfm };
        _loading = true;
        AutoMode = false;
        Mode = mode;
        _loading = false;
        // Keep the band's own width when the band uses this mode; otherwise the wider choice.
        var band = BandPlan.Find(FrequencyHz);
        SetWidths(mode, band.Mode == mode ? band.BandwidthHz : 0);
    }

    // ---- memories and scanning --------------------------------------------------------------

    /// <summary>The groups ticked in the selector; empty means all.</summary>
    private List<string> SelectedGroups => GroupRows.Where(g => g.Selected).Select(g => g.Name).ToList();

    private void RefreshMemories(IReadOnlyCollection<string>? select = null)
    {
        _loading = true;
        // The group rows are rebuilt only when the set of groups has changed, so an open selector keeps its rows.
        var wanted = select ?? SelectedGroups;
        // The user's own groups first, then the ones made from the tags. A tag's group keeps its tick
        // when its name changes spelling.
        string[] names = [.. _memories.GroupNames().Where(n => !MemoryStore.IsTagGroup(n)), .. _memories.TagGroupNames()];
        if (select is not null || !names.SequenceEqual(GroupRows.Select(g => g.Name)))
        {
            GroupRows.Clear();
            UserGroupRows.Clear();
            AutoGroupRows.Clear();
            foreach (string n in names)
            {
                var row = new GroupRow(n, wanted.Any(w => MemoryStore.SameGroup(w, n)), OnGroupsChanged);
                GroupRows.Add(row);
                (row.IsAuto ? AutoGroupRows : UserGroupRows).Add(row);
            }
            HasUserGroups = UserGroupRows.Count > 0;
            HasAutoGroups = AutoGroupRows.Count > 0;
        }
        var shown = SelectedGroups;
        GroupsText = shown.Count == 0 ? AllGroups : shown.Count == 1 ? shown[0] : $"{shown.Count} groups";
        Memories.Clear();
        // A channel in several of the groups shown appears once, under the first of them.
        string First(MemoryChannel c) =>
            (shown.Count == 0 ? c.Groups : shown.Where(g => MemoryStore.InGroup(c, g))).Order().FirstOrDefault() ?? "";
        foreach (var c in _memories.Channels.Where(c => shown.Count == 0 || shown.Any(g => MemoryStore.InGroup(c, g)))
                     .OrderBy(First).ThenBy(c => c.FrequencyHz))
            Memories.Add(new MemoryRow(c, _memories.Save));
        _loading = false;
    }

    /// <summary>The stored channels, for the channel editor.</summary>
    public MemoryStore Store => _memories;

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

    /// <summary>The channel editor has changed the stored channels: show them again.</summary>
    public void MemoriesEdited()
    {
        RefreshMemories();
        if (!IsScanning) ShowLabel();
    }

    private int _diskTicks;
    /// <summary>The channels were read again after something else changed the file; the channel editor shows them again too.</summary>
    public event Action? Reloaded;

    // The stored channels have been read again from the file, changed by something other than this
    // program. Every channel is a new object. A scan in progress goes on with the channels it began with.
    private void MemoriesReloaded()
    {
        if (_picked is { } was)
            _picked = _memories.FindAll(was.FrequencyHz).FirstOrDefault(c => c.Name == was.Name);
        RefreshMemories();
        if (!IsScanning) ShowLabel();
        Status = IsScanning
            ? "The stored channels were changed outside the program and read again; the scan goes on with the ones it began with"
            : "The stored channels were changed outside the program and read again";
        Reloaded?.Invoke();
    }

    /// <summary>The channel editor has renamed a group: if it was ticked here, the new name is.</summary>
    public void GroupRenamed(string old, string name) =>
        RefreshMemories(SelectedGroups.Select(g => g == old ? name : g).Distinct().ToList());

    /// <summary>Tune to a stored channel (and end a scan), as picking it in the list does.</summary>
    public void Tune(MemoryChannel c)
    {
        if (IsScanning) StopScan();
        _picked = c;
        if (c.Mode is Mode mode)
        {
            _loading = true;
            AutoMode = false;
            Mode = mode;
            _loading = false;
            SetWidths(mode, c.BandwidthHz ?? 0);
        }
        else if (!AutoMode) AutoMode = true;
        if (FrequencyHz == c.FrequencyHz) ApplyFrequency();
        else FrequencyHz = c.FrequencyHz;
    }

    /// <summary>
    /// Put on the display what is stored about the frequency it shows: the name, whose it is
    /// and the particulars. Where nothing is stored the band's name stands in.
    /// </summary>
    private void ShowLabel()
    {
        // In a scan only the channels being scanned speak for the frequency; otherwise all stored ones.
        var here = (IsScanning && !_searching ? _scanMembers : _memories.Channels)
            .Where(c => Math.Abs(c.FrequencyHz - FrequencyHz) < 100).ToList();
        if (_picked is not null && here.Remove(_picked)) here.Insert(0, _picked);
        var label = ChannelLabel.For(here);
        var band = BandPlan.Find(FrequencyHz);
        BandName = label.Title ?? (band.Name.Length > 0 ? band.Name : "—");
        DetailText = label.Detail;
        InfoText = label.Info;
    }

    // A group was ticked or unticked in the selector.
    private void OnGroupsChanged()
    {
        if (_loading) return;
        if (IsScanning) StopScan();
        RefreshMemories();
        _dirty = true;
    }

    /// <summary>Untick every group: all of them are shown and scanned.</summary>
    [RelayCommand]
    private void ShowAllGroups()
    {
        if (SelectedGroups.Count == 0) return;
        if (IsScanning) StopScan();
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
        _rx.AudioDevice = value == DefaultDevice ? null : value;
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
        if (IsScanning)
        {
            StopScan();
            return;
        }
        if (!TryMHz(SearchFrom, out double lo) || !TryMHz(SearchTo, out double hi) || hi <= lo)
        {
            Status = "Enter the search range in MHz, such as 144 to 148";
            return;
        }
        if (!IsOn)
        {
            Status = "Start the radio first";
            return;
        }
        var list = new List<ScanChannel>();
        for (double f = lo; f <= hi + 1 && list.Count < 40000;)
        {
            // Channels sit on whole multiples of the step (154.935 MHz is 20658 x 7.5 kHz), so the
            // raster is counted from zero, not from where the search happens to start.
            var band = BandPlan.Find(f);
            double step = SelectedStep.Value > 0 ? SelectedStep.Value : band.StepHz;
            double channel = Math.Ceiling(f / step - 1e-6) * step;
            if (channel > hi + 1) break;
            band = BandPlan.Find(channel);
            if (!band.Blocked) list.Add(new ScanChannel("", Math.Round(channel / 10) * 10, band.Mode, band.BandwidthHz));
            f = channel + step;
        }
        if (list.Count == 0) return;
        Status = "";
        _settings.SearchFromMHz = lo / 1e6;
        _settings.SearchToMHz = hi / 1e6;
        _dirty = true;
        SquelchOn = true;
        _searching = true;
        _rx.ScanSkipSeconds = 600;
        _rx.StartScan(list);
        IsScanning = _rx.Scanning;
        IsSearching = IsScanning;
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
        if (IsScanning)
        {
            StopScan();
            return;
        }
        // A frequency shared by several stored channels is visited once.
        var members = Memories.Where(m => m.Scan).Select(m => m.Channel).ToList();
        var list = members.GroupBy(c => Math.Round(c.FrequencyHz / 100)).Select(g => ToScan(g.First()))
            .OrderBy(c => c.FrequencyHz).ToList();
        if (list.Count == 0)
        {
            Status = "No channels are ticked for scanning in the groups shown";
            return;
        }
        if (!IsOn)
        {
            Status = "Start the radio first";
            return;
        }
        Status = "";
        SquelchOn = true;
        _searching = false;
        _scanMembers = members;
        _picked = null;
        _rx.ScanSkipSeconds = 10;
        _rx.StartScan(list);
        IsScanning = _rx.Scanning;
    }

    private void StopScan()
    {
        _rx.StopScan();
        IsScanning = false;
        ShowTuned();
    }

    /// <summary>Leave the channel the scan has stopped on.</summary>
    [RelayCommand]
    private void SkipChannel() => _rx.SkipChannel();

    /// <summary>
    /// Leave the channel the scan has stopped on and keep its frequency out of scans and searches
    /// for the Avoid Timer chosen in Settings. Nothing is stored: closing the app clears them all.
    /// </summary>
    [RelayCommand]
    private void AvoidChannel()
    {
        if (_rx.AvoidChannel() is not { } ch) return;
        string howLong = SelectedAvoidTime.Value > 0 ? $"for {SelectedAvoidTime.Label}" : "until the app is closed";
        Status = $"Avoiding {(ch.FrequencyHz / 1e6).ToString("0.0000", CultureInfo.InvariantCulture)} MHz {howLong} ({_rx.AvoidedCount} avoided)";
    }

    private static TimeSpan? AvoidSpan(Choice choice) => choice.Value > 0 ? TimeSpan.FromMinutes(choice.Value) : null;

    /// <summary>A change of timer applies to what is avoided from now on; those already avoided keep the time they had.</summary>
    partial void OnSelectedAvoidTimeChanged(Choice value)
    {
        _rx.AvoidTime = AvoidSpan(value);
        _dirty = true;
    }

    /// <summary>Show what the receiver is on now, without tuning it: the scan moves it, not us.</summary>
    private void ShowTuned()
    {
        _loading = true;
        FrequencyHz = _rx.FrequencyHz;
        Mode = _rx.Mode;
        _loading = false;
        ShowLabel();
        bool narrow = _rx.BandwidthHz < 9000 || (_rx.Mode == Mode.Nfm && _rx.BandwidthHz < 13000);
        ModeText = _rx.Mode switch { Mode.Am => narrow ? "AM 8.33k" : "AM 25k", Mode.Wfm => "WFM", _ => narrow ? "NFM 12.5k" : "NFM 25k" };
        EntryText = (FrequencyHz / 1e6).ToString("0.#####", CultureInfo.InvariantCulture);
        Blocked = _rx.Blocked;
    }

    /// <summary>Store the present frequency under the name typed, in the group shown.</summary>
    [RelayCommand]
    private void StoreChannel()
    {
        string name = NewName.Trim();
        if (name.Length == 0)
        {
            Status = "Type a name for the channel first";
            return;
        }
        // A channel already stored here is renamed and keeps what else is known about it.
        var all = _memories.FindAll(FrequencyHz);
        var channel = _picked is not null && all.Contains(_picked) ? _picked : all.FirstOrDefault();
        if (channel is null)
        {
            channel = new MemoryChannel { FrequencyHz = FrequencyHz };
            _memories.Channels.Add(channel);
        }
        channel.Name = name;
        channel.Mode = AutoMode ? null : Mode;
        channel.BandwidthHz = AutoMode ? null : SelectedWidth?.Value;
        // One group shown: it goes there (unless it is a tag's group, which nothing can be put in).
        // Otherwise it stays where it was, or goes in "My channels".
        string? group = SelectedGroups is [var only] && !MemoryStore.IsTagGroup(only) ? only : channel.Groups.Count == 0 ? "My channels" : null;
        if (group is not null && !channel.Groups.Contains(group)) channel.Groups.Add(group);
        _picked = channel;
        _memories.Save();
        NewName = "";
        Status = "";
        RefreshMemories();
        if (!IsScanning) ShowLabel();
    }

    [RelayCommand]
    private void DeleteChannel()
    {
        if (SelectedMemory is null) return;
        if (IsScanning) StopScan();
        _memories.Channels.Remove(SelectedMemory.Channel);
        _memories.Save();
        RefreshMemories();
    }

    partial void OnIsScanningChanged(bool value)
    {
        if (!value) IsSearching = false;
        OnPropertyChanged(nameof(ScanText));
    }

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(SearchText));

    private double StepHz => SelectedStep.Value > 0 ? SelectedStep.Value : BandPlan.Find(FrequencyHz).StepHz;

    [RelayCommand]
    private void StepUp() => StepBy(1);

    [RelayCommand]
    private void StepDown() => StepBy(-1);

    private void StepBy(int direction)
    {
        double step = StepHz;
        // Land on the step's own raster, so a stray frequency snaps to a channel first.
        double channel = FrequencyHz / step;
        double next = direction > 0 ? Math.Floor(channel + 1e-6) + 1 : Math.Ceiling(channel - 1e-6) - 1;
        FrequencyHz = next * step;
    }

    partial void OnSelectedStepChanged(Choice value) => _dirty = true;

    /// <summary>Direct entry in MHz ("162.55"); a number too large for MHz is taken as kHz ("146520").</summary>
    [RelayCommand]
    private void EnterFrequency()
    {
        string text = EntryText.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0)
        {
            Status = "Enter a frequency in MHz, such as 162.55";
            return;
        }
        double hz = v > 2000 ? v * 1e3 : v * 1e6;
        if (hz is < MinHz or > MaxHz)
        {
            Status = "The receiver covers 0.1 MHz to 2000 MHz";
            return;
        }
        Status = "";
        FrequencyHz = hz;
    }

    // ---- controls -------------------------------------------------------------------------

    partial void OnVolumeChanged(double value)
    {
        _rx.Volume = value / 100;
        _dirty = true;
    }

    partial void OnSquelchDbChanged(double value)
    {
        _rx.SquelchDb = value;
        _dirty = true;
    }

    partial void OnSquelchOnChanged(bool value)
    {
        _rx.SquelchEnabled = value;
        _dirty = true;
    }

    partial void OnStereoOnChanged(bool value)
    {
        _rx.StereoEnabled = value;
        _dirty = true;
    }

    partial void OnAutoGainChanged(bool value)
    {
        if (_loading) return;
        _rx.Source.AutoGain = value;
        _dirty = true;
    }

    partial void OnGainStepChanged(double value)
    {
        if (_loading) return;
        _rx.Source.GainStep = (int)Math.Round(value);
        _dirty = true;
    }

    partial void OnSelectedAntennaChanged(string value)
    {
        if (_loading || value is null) return;
        _rx.Source.Antenna = value;
        _dirty = true;
    }

    partial void OnIsOnChanged(bool value) => OnPropertyChanged(nameof(PowerText));

    [RelayCommand]
    private async Task TogglePower()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (IsOn)
            {
                await Task.Run(_rx.Stop);
                IsOn = false;
                IsScanning = false;
                DeviceText = "Off";
                Status = "";
            }
            else
            {
                Status = "Starting the radio…";
                await Task.Run(_rx.Start);
                IsOn = true;
                Status = "";
                DeviceText = _rx.Source.Description;
                var antennas = _rx.Source.Antennas;
                if (!antennas.SequenceEqual(Antennas))
                {
                    _loading = true;
                    Antennas = antennas;
                    SelectedAntenna = _rx.Source.Antenna;
                    _loading = false;
                }
            }
        }
        catch (Exception e)
        {
            IsOn = false;
            DeviceText = "Off";
            Status = e.Message;
        }
        finally { IsBusy = false; }
    }

    private void Tick()
    {
        if (++_diskTicks >= 20)     // once a second
        {
            _diskTicks = 0;
            if (_memories.ReloadIfChangedOnDisk()) MemoriesReloaded();
        }
        if (IsOn)
        {
            var src = _rx.Source;
            Level = _rx.LevelDb;
            SquelchOpen = _rx.SquelchOpen;
            // The scan ends by itself when something else tunes the receiver.
            if (IsScanning && !_rx.Scanning) IsScanning = false;
            if (IsScanning)
            {
                // Between channels there is nothing to show but that it is scanning, and how fast.
                if (_ticks % 20 == 0)
                {
                    long passes = _rx.ScanPasses;
                    _passRate = Math.Max(0, passes - _lastPasses);
                    _lastPasses = passes;
                }
                if (_rx.ScanChannel is not null)
                {
                    if (_rx.FrequencyHz != FrequencyHz || BandName.StartsWith("Scanning") || BandName.StartsWith("Searching")) ShowTuned();
                }
                else
                {
                    BandName = $"{(_searching ? "Searching" : "Scanning")} · {_passRate:F0} passes/s";
                    DetailText = InfoText = "";
                }
            }
            Overloaded = src.Overloaded;
            IsStereo = _rx.Stereo;
            var digital = _rx.Digital;
            DigitalText = digital?.ToString() ?? "";
            IsEncrypted = digital is { Encrypted: true };
            MaxGainStep = src.MaxGainStep;
            LevelText = Blocked ? "" : $"{Level:F0} dB";
            GainText = $"LNA {src.ActiveGainStep}/{src.MaxGainStep} · gain {src.GainDb:F0} dB · retune {src.LastRetuneMs:F0} ms";
        }
        else
        {
            Level = -140;
            SquelchOpen = false;
            Overloaded = false;
            IsStereo = false;
            DigitalText = "";
            IsEncrypted = false;
            LevelText = "";
            GainText = "";
        }
        if (++_ticks % 100 == 0 && _dirty) Save();
    }

    private void Save()
    {
        _dirty = false;
        _settings.FrequencyHz = FrequencyHz;
        _settings.AutoMode = AutoMode;
        _settings.Mode = Mode;
        _settings.BandwidthHz = SelectedWidth?.Value ?? _settings.BandwidthHz;
        _settings.StepHz = SelectedStep.Value;
        _settings.AvoidMinutes = (int)SelectedAvoidTime.Value;
        _settings.Volume = Volume / 100;
        _settings.SquelchDb = SquelchDb;
        _settings.SquelchOn = SquelchOn;
        _settings.Stereo = StereoOn;
        _settings.AutoGain = AutoGain;
        _settings.GainStep = (int)Math.Round(GainStep);
        _settings.Antenna = SelectedAntenna;
        _settings.Groups = SelectedGroups;
        _settings.Group = null;
        _settings.AudioDevice = SelectedAudioDevice == DefaultDevice ? null : SelectedAudioDevice;
        _settings.Save();
    }

    public void Shutdown()
    {
        _timer.Stop();
        Save();
        _rx.Dispose();
    }
}
