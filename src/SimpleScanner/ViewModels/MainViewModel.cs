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
    public string Name => Channel.Name;
    public string Detail => $"{Channel.FrequencyHz / 1e6:0.0000}  {Channel.Group}";

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

/// <summary>The scanner's front panel: what the display shows and what the controls do.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const double MinHz = 100_000, MaxHz = 2_000_000_000;

    private readonly Settings _settings;
    private readonly Receiver _rx;
    private readonly MemoryStore _memories;
    private const string AllGroups = "All groups";
    private readonly DispatcherTimer _timer;
    private bool _loading, _dirty;
    private int _ticks;

    public IReadOnlyList<Choice> Steps { get; } =
    [
        new("Auto", 0), new("5 kHz", 5000), new("6.25 kHz", 6250), new("7.5 kHz", 7500), new("8.33 kHz", 25000.0 / 3),
        new("10 kHz", 10000), new("12.5 kHz", 12500), new("25 kHz", 25000), new("100 kHz", 100000), new("1 MHz", 1e6),
    ];

    private static readonly Choice[] AmWidths = [new("8.33 kHz channel", BandPlan.AmAir833), new("25 kHz channel", BandPlan.AmAir)];
    private static readonly Choice[] NfmWidths = [new("12.5 kHz channel", BandPlan.NfmNarrow), new("25 kHz channel", BandPlan.NfmWide)];
    private static readonly Choice[] WfmWidths = [new("Broadcast", BandPlan.Wfm)];

    [ObservableProperty] private double _frequencyHz;
    [ObservableProperty] private string _bandName = "";
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
    [ObservableProperty] private IReadOnlyList<string> _antennas;
    [ObservableProperty] private string _selectedAntenna;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private IReadOnlyList<string> _groups = [AllGroups];
    [ObservableProperty] private string _selectedGroup = AllGroups;
    [ObservableProperty] private MemoryRow? _selectedMemory;
    [ObservableProperty] private string _newName = "";

    public System.Collections.ObjectModel.ObservableCollection<MemoryRow> Memories { get; } = [];
    public string ScanText => IsScanning ? "Stop scan" : "Scan";

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
        RefreshMemories(settings.Group);
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
        // A stored channel's own name comes before the band's.
        BandName = _memories.Find(FrequencyHz)?.Name ?? (band.Name.Length > 0 ? band.Name : "—");
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

    private void RefreshMemories(string? group = null)
    {
        _loading = true;
        Groups = [AllGroups, .. _memories.Channels.Select(c => c.Group).Distinct().Order()];
        SelectedGroup = Groups.Contains(group ?? SelectedGroup) ? group ?? SelectedGroup : AllGroups;
        Memories.Clear();
        foreach (var c in _memories.Channels.Where(c => SelectedGroup == AllGroups || c.Group == SelectedGroup)
                     .OrderBy(c => c.Group).ThenBy(c => c.FrequencyHz))
            Memories.Add(new MemoryRow(c, _memories.Save));
        _loading = false;
    }

    partial void OnSelectedGroupChanged(string value)
    {
        if (_loading || value is null) return;
        if (IsScanning) StopScan();
        RefreshMemories();
        _dirty = true;
    }

    /// <summary>Picking a stored channel tunes to it (and ends a scan).</summary>
    partial void OnSelectedMemoryChanged(MemoryRow? value)
    {
        if (_loading || value is null) return;
        if (IsScanning) StopScan();
        var c = value.Channel;
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

    private static ScanChannel ToScan(MemoryChannel c)
    {
        var band = BandPlan.Find(c.FrequencyHz);
        var mode = c.Mode ?? band.Mode;
        double width = c.BandwidthHz ?? (mode == band.Mode ? band.BandwidthHz
            : mode switch { Mode.Am => BandPlan.AmAir, Mode.Wfm => BandPlan.Wfm, _ => BandPlan.NfmWide });
        return new ScanChannel(c.Name, c.FrequencyHz, mode, width);
    }

    /// <summary>Scan the ticked channels of the group shown, or stop scanning.</summary>
    [RelayCommand]
    private void ToggleScan()
    {
        if (IsScanning)
        {
            StopScan();
            return;
        }
        var list = Memories.Where(m => m.Scan).Select(m => ToScan(m.Channel)).OrderBy(c => c.FrequencyHz).ToList();
        if (list.Count == 0)
        {
            Status = "No channels are ticked for scanning in this group";
            return;
        }
        if (!IsOn)
        {
            Status = "Start the radio first";
            return;
        }
        Status = "";
        SquelchOn = true;
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

    /// <summary>Show what the receiver is on now, without tuning it: the scan moves it, not us.</summary>
    private void ShowTuned()
    {
        _loading = true;
        FrequencyHz = _rx.FrequencyHz;
        Mode = _rx.Mode;
        _loading = false;
        var ch = _rx.ScanChannel;
        var band = BandPlan.Find(FrequencyHz);
        BandName = ch?.Name ?? _memories.Find(FrequencyHz)?.Name ?? (band.Name.Length > 0 ? band.Name : "—");
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
        var existing = _memories.Find(FrequencyHz);
        if (existing is not null) _memories.Channels.Remove(existing);
        _memories.Channels.Add(new MemoryChannel
        {
            Name = name,
            FrequencyHz = FrequencyHz,
            Group = SelectedGroup == AllGroups ? existing?.Group ?? "My channels" : SelectedGroup,
            Mode = AutoMode ? null : Mode,
            BandwidthHz = AutoMode ? null : SelectedWidth?.Value,
        });
        _memories.Save();
        NewName = "";
        Status = "";
        RefreshMemories();
        if (!IsScanning) BandName = name;
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

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(ScanText));

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
        if (IsOn)
        {
            var src = _rx.Source;
            Level = _rx.LevelDb;
            SquelchOpen = _rx.SquelchOpen;
            // The scan ends by itself when something else tunes the receiver.
            if (IsScanning && !_rx.Scanning) IsScanning = false;
            if (IsScanning && (_rx.ScanListening || _ticks % 4 == 0) && _rx.FrequencyHz != FrequencyHz) ShowTuned();
            Overloaded = src.Overloaded;
            IsStereo = _rx.Stereo;
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
        _settings.Volume = Volume / 100;
        _settings.SquelchDb = SquelchDb;
        _settings.SquelchOn = SquelchOn;
        _settings.Stereo = StereoOn;
        _settings.AutoGain = AutoGain;
        _settings.GainStep = (int)Math.Round(GainStep);
        _settings.Antenna = SelectedAntenna;
        _settings.Group = SelectedGroup;
        _settings.Save();
    }

    public void Shutdown()
    {
        _timer.Stop();
        Save();
        _rx.Dispose();
    }
}
