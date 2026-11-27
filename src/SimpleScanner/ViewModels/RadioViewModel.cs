// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleScanner.Dsp;
using SimpleScanner.Radio;

namespace SimpleScanner.ViewModels;

/// <summary>
/// One radio as the window shows and works it: its power, its settings, where it is tuned, its
/// squelch and volume, its scan and what its display says. <see cref="MainViewModel"/> holds one of
/// these for each radio running and what they share (the stored channels, the groups, the timers).
/// </summary>
public sealed partial class RadioViewModel : ObservableObject
{
    public const double MinHz = 100_000, MaxHz = 2_000_000_000;

    private readonly IReceiver _rx;
    private readonly MemoryStore _memories;
    private readonly Action<Action> _post;
    private readonly double _levelOffsetDb;
    private long _lastPasses;
    private double _passRate;
    private bool _searching;
    // The stored channels the scan in progress was made from, and the one last picked by hand:
    // they decide whose details are shown where several channels share a frequency.
    private List<MemoryChannel> _scanMembers = [];
    private bool _loading;
    private int _ticks;
    // Multi Monitor: the rows are showing a scan's slots (otherwise they are empty); what they say about a
    // frequency is to be worked out again; and what the line above them was last made from.
    private bool _rowsShown, _relabel;
    private int _headerHeld = -1;
    private string? _headerBand;
    private double _headerRate = -1;

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
    /// <summary>Whose channel this is: the stored channel's agency and description.</summary>
    [ObservableProperty] private string _detailText = "";
    /// <summary>The stored channel's particulars: tag, signal type, tone, station class, input, callsign.</summary>
    [ObservableProperty] private string _infoText = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private string _entryText = "";
    /// <summary>What went wrong or what is going on, for the line under the controls.</summary>
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSlots))]
    private bool _isOn;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _volume;
    /// <summary>The speaker is silenced; the volume setting is left as it is. Not kept between runs.</summary>
    [ObservableProperty] private bool _isMuted;
    /// <summary>Where this radio's sound sits between the left speaker (-1) and the right (1).</summary>
    [ObservableProperty] private double _pan;
    [ObservableProperty] private double _squelchDb;
    [ObservableProperty] private bool _squelchOn;
    /// <summary>The squelch also stays shut on noise above its level.</summary>
    [ObservableProperty] private bool _autoSquelch;
    [ObservableProperty] private bool _autoGain;
    [ObservableProperty] private double _gainStep;
    [ObservableProperty] private double _maxGainStep = 27;
    [ObservableProperty] private bool _autoMode;
    [ObservableProperty] private Mode _mode;
    [ObservableProperty] private IReadOnlyList<Choice> _widths = NfmWidths;
    [ObservableProperty] private Choice? _selectedWidth;
    [ObservableProperty] private Choice _selectedStep;
    /// <summary>The radios that can be chosen for this receiver: the free ones and the one in use.</summary>
    [ObservableProperty] private IReadOnlyList<RadioInfo> _radios = [];
    [ObservableProperty] private RadioInfo? _selectedRadio;
    [ObservableProperty] private IReadOnlyList<string> _antennas;
    [ObservableProperty] private string _selectedAntenna;
    [ObservableProperty] private bool _biasT;
    [ObservableProperty] private bool _fmNotch;
    /// <summary>Frequency correction in parts per million; null while the box is empty.</summary>
    [ObservableProperty] private decimal? _ppm = 0;
    /// <summary>The radio in use has a bias T (not known until it has been started once).</summary>
    [ObservableProperty] private bool _hasBiasT = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSlots))]
    [NotifyPropertyChangedFor(nameof(CanAvoidChannel))]
    [NotifyCanExecuteChangedFor(nameof(AvoidChannelCommand))]
    private bool _isScanning;
    /// <summary>Multi Monitor: a scan or search holds up to four active channels of a window at once. Kept with this radio's settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSlots))]
    [NotifyPropertyChangedFor(nameof(CanAvoidChannel))]
    [NotifyCanExecuteChangedFor(nameof(AvoidChannelCommand))]
    private bool _multiMonitor;
    /// <summary>Multi Monitor: the line above the four rows, how many are held and the band ("MULTI · 2 of 4 · Airband").</summary>
    [ObservableProperty] private string _slotsHeader = "";
    [ObservableProperty] private bool _isSearching;
    /// <summary>This is the radio the controls work: its strip is marked among the others.</summary>
    [ObservableProperty] private bool _isSelected;
    /// <summary>What the radio is called where several are shown: its model, once that is known.</summary>
    [ObservableProperty] private string _name = "Radio";
    public string SearchText => IsSearching ? "Stop search" : "Search";
    public string ScanText => IsScanning ? "Stop" : "Scan";

    /// <summary>Multi Monitor: the four rows, one for each seat in seat order (33% left, 33% right, hard left, hard right); the same four objects for the life of the radio.</summary>
    public IReadOnlyList<SlotViewModel> Slots { get; }
    /// <summary>The display shows the four rows in place of the big frequency: the mode is on and this radio is on and scanning or searching.</summary>
    public bool ShowSlots => MultiMonitor && IsScanning && IsOn;
    /// <summary>The main Avoid has something to do: a scan is stopped on a channel, and not in Multi Monitor, where avoiding is done from a row.</summary>
    public bool CanAvoidChannel => IsScanning && !MultiMonitor;

    public bool IsAm => !AutoMode && Mode == Mode.Am;
    public bool IsNfm => !AutoMode && Mode == Mode.Nfm;
    public bool IsWfm => !AutoMode && Mode == Mode.Wfm;
    public string PowerText => IsOn ? "Stop" : "Start";

    /// <summary>Something here has changed that is kept between runs, and has not been saved yet.</summary>
    public bool Dirty { get; set; }

    /// <summary>This radio runs in a helper program that keeps the one radio it was started for (one for a further
    /// radio; the window's own receiver can be changed to another radio, in a helper or not).</summary>
    public bool IsHelper => _rx is Remote.RemoteReceiver { CanChangeRadio: false };

    /// <summary>Says whether another receiver of this program has the radio with that serial number.</summary>
    public Func<string, bool>? IsTaken { get; set; }

    /// <summary>The serial number of the radio this is running on, or will open; null when none has been chosen or opened yet.</summary>
    public string? Serial => IsOn ? _rx.Source.Radio?.Serial ?? _rx.Source.Serial : _rx.Source.Serial ?? _rx.Source.Radio?.Serial;

    /// <summary>The radio's model, if it has been seen (it is listed, or has been opened).</summary>
    public string? Model => _rx.Source.Radio?.Model ?? Radios.FirstOrDefault(r => r.Serial == Serial)?.Model;

    /// <summary>The width the radio can watch from one tuning, for sharing a scan out; the usual one until it has been started.</summary>
    public double UsableHz => _rx.Source.UsableBandwidthHz is > 0 and var hz ? hz : Receiver.DefaultSampleRate * 0.8;

    /// <summary>The serial number chosen for this receiver (null: it finds a radio itself).</summary>
    public string? ChosenSerial => _rx.Source.Serial;

    /// <summary>The stored channel last picked by hand; where several share a frequency, its details are shown.</summary>
    public MemoryChannel? Picked { get; set; }

    /// <param name="post">Runs something on the window's thread (the default); a test runs it where it is.</param>
    public RadioViewModel(IReceiver receiver, RadioSettings settings, MemoryStore memories, Action<Action>? post = null)
    {
        _rx = receiver;
        _memories = memories;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _levelOffsetDb = settings.LevelOffsetDb;
        Slots = Enumerable.Range(0, SlotMixer.Seats).Select(k => new SlotViewModel(k, SkipSlot, AvoidSlot, LabelAt)).ToArray();
        _loading = true;
        MultiMonitor = settings.MultiMonitor;
        Pan = double.IsFinite(settings.Pan) ? Math.Clamp(settings.Pan, -1, 1) : 0;
        _antennas = _rx.Source.Antennas;
        _selectedAntenna = _antennas.Contains(settings.Antenna) || _antennas.Count == 0 ? settings.Antenna : _antennas[0];
        _selectedStep = Steps.FirstOrDefault(s => Math.Abs(s.Value - settings.StepHz) < 0.5) ?? Steps[0];
        Volume = settings.Volume * 100;
        SquelchDb = settings.SquelchOn ? Math.Max(settings.SquelchDb, SquelchOffDb) : SquelchOffDb;
        SquelchOn = SquelchDb > SquelchOffDb;
        AutoSquelch = settings.AutoSquelch;
        StereoOn = settings.Stereo;
        AutoGain = settings.AutoGain;
        BiasT = settings.BiasT;
        FmNotch = settings.FmNotch;
        Ppm = (decimal)Math.Clamp(double.IsFinite(settings.Ppm) ? settings.Ppm : 0, -MaxPpm, MaxPpm);
        GainStep = settings.GainStep;
        AutoMode = settings.AutoMode;
        Mode = settings.Mode;
        FrequencyHz = Math.Clamp(settings.FrequencyHz, MinHz, MaxHz);
        _loading = false;

        _rx.Source.Antenna = _selectedAntenna;
        _rx.Source.BiasT = BiasT;
        _rx.Source.FmNotch = FmNotch;
        _rx.Source.Ppm = (double)(Ppm ?? 0);
        _rx.Source.AutoGain = AutoGain;
        _rx.Source.GainStep = (int)GainStep;
        _rx.Volume = Volume / 100;
        _rx.Pan = Pan;
        _rx.SquelchDb = SquelchDb;
        _rx.SquelchEnabled = SquelchOn;
        _rx.AutoSquelch = AutoSquelch;
        _rx.StereoEnabled = StereoOn;     // said here too: off is the button's resting state, so setting it above told the receiver nothing
        _rx.LevelOffsetDb = settings.LevelOffsetDb;
        _rx.MultiMonitor = MultiMonitor;
        ApplyFrequency(settings.BandwidthHz);
        _rx.Failed += why => _post(() =>
        {
            IsOn = false;
            DeviceText = "Off";
            Status = why;
        });
    }

    /// <summary>Put what is kept between runs into this radio's settings.</summary>
    public void WriteTo(RadioSettings s)
    {
        s.FrequencyHz = FrequencyHz;
        s.AutoMode = AutoMode;
        s.Mode = Mode;
        s.BandwidthHz = SelectedWidth?.Value ?? s.BandwidthHz;
        s.StepHz = SelectedStep.Value;
        s.Volume = Volume / 100;
        s.Pan = Pan;
        s.SquelchDb = SquelchDb;
        s.SquelchOn = SquelchOn;
        s.AutoSquelch = AutoSquelch;
        s.Stereo = StereoOn;
        s.AutoGain = AutoGain;
        s.GainStep = (int)Math.Round(GainStep);
        // A radio with one socket has no choice to keep: leave the one made on a radio that has.
        if (Antennas.Count > 1) s.Antenna = SelectedAntenna;
        s.BiasT = BiasT;
        s.FmNotch = FmNotch;
        s.Ppm = (double)(Ppm ?? 0);
        s.LevelOffsetDb = _levelOffsetDb;
        s.MultiMonitor = MultiMonitor;
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
        Dirty = true;
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
        ModeText = ModeLabel(mode, pick.Value);
        OnPropertyChanged(nameof(IsAm));
        OnPropertyChanged(nameof(IsNfm));
        OnPropertyChanged(nameof(IsWfm));
        Dirty = true;
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

    /// <summary>Tune to a stored channel (and end a scan), as picking it in the list does.</summary>
    public void Tune(MemoryChannel c)
    {
        if (IsScanning) StopScan();
        Picked = c;
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
        var label = LabelAt(FrequencyHz);
        BandName = label.Title ?? "";
        DetailText = label.Detail;
        InfoText = label.Info;
    }

    /// <summary>
    /// What is stored about a frequency, for the display or a Multi Monitor row: the name, whose it is and the
    /// particulars. Where nothing is stored the band's name stands in, so the title is never null.
    /// </summary>
    private ChannelLabel LabelAt(double hz)
    {
        // In a scan only the channels being scanned speak for the frequency; otherwise all stored ones.
        var here = (IsScanning && !_searching ? _scanMembers : _memories.Channels)
            .Where(c => Math.Abs(c.FrequencyHz - hz) < 100).ToList();
        if (Picked is not null && here.Remove(Picked)) here.Insert(0, Picked);
        var label = ChannelLabel.For(here);
        if (label.Title is not null) return label;
        var band = BandPlan.Find(hz);
        return label with { Title = band.Name.Length > 0 ? band.Name : "—" };
    }

    /// <summary>The receive mode and channel width in words, as the display shows them ("NFM 12.5k").</summary>
    internal static string ModeLabel(Mode mode, double bandwidthHz)
    {
        bool narrow = bandwidthHz < 9000 || (mode == Mode.Nfm && bandwidthHz < 13000);
        return mode switch { Mode.Am => narrow ? "AM 8.33k" : "AM 25k", Mode.Wfm => "WFM", _ => narrow ? "NFM 12.5k" : "NFM 25k" };
    }

    /// <summary>A signal level as the meter's figure says it ("-87 dB").</summary>
    internal static string LevelLabel(double db) => $"{db:F0} dB";

    /// <summary>The stored channels have changed: show again what is stored for this frequency, unless a scan is showing its own.</summary>
    public void StoreChanged()
    {
        if (!IsScanning) ShowLabel();
        _relabel = true;
    }

    /// <summary>The stored channels were read again from the file and every channel is a new object: find the picked one among them.</summary>
    public void StoreReloaded()
    {
        if (Picked is { } was)
            Picked = _memories.FindAll(was.FrequencyHz).FirstOrDefault(c => c.Name == was.Name);
        StoreChanged();
    }

    // ---- scanning ---------------------------------------------------------------------------

    /// <summary>
    /// Scan these channels. <paramref name="members"/> are the stored channels the list was made
    /// from: while the scan runs, they are the ones whose details the display shows.
    /// </summary>
    public void StartScan(List<MemoryChannel> members, IReadOnlyList<ScanChannel> list)
    {
        if (!IsOn)
        {
            Status = "Start the radio first";
            return;
        }
        Status = "";
        EnsureSquelch();
        _searching = false;
        _scanMembers = members;
        Picked = null;
        _relabel = true;
        _headerHeld = -1;      // the line above the rows says again whether it scans or searches
        _rx.ScanSkipSeconds = 10;
        _rx.StartScan(list);
        IsScanning = _rx.Scanning;
    }

    /// <summary>Search these frequencies for anything active. A frequency skipped is left out for ten minutes.</summary>
    public void StartSearch(IReadOnlyList<ScanChannel> list)
    {
        Status = "";
        EnsureSquelch();
        _searching = true;
        _relabel = true;
        _headerHeld = -1;      // the line above the rows says again whether it scans or searches
        _rx.ScanSkipSeconds = 600;
        _rx.StartScan(list);
        IsScanning = _rx.Scanning;
        IsSearching = IsScanning;
    }

    public void StopScan()
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
    [RelayCommand(CanExecute = nameof(CanAvoidChannel))]
    private void AvoidChannel() => _rx.AvoidChannel();

    /// <summary>A Multi Monitor row's Skip: let go the channel that slot holds and pass it by for a while; the others carry on.</summary>
    private void SkipSlot(int slot) => _rx.SkipSlot(slot);

    /// <summary>A Multi Monitor row's Avoid: let go the channel that slot holds and keep it out as the main Avoid does.</summary>
    private void AvoidSlot(int slot) => _rx.AvoidSlot(slot);

    /// <summary>How long a Multi Monitor slot keeps its channel after the signal has dropped. Takes effect at once.</summary>
    public void SetMultiHangTime(Choice choice) => _rx.MultiHangSeconds = choice.Value;

    partial void OnMultiMonitorChanged(bool value)
    {
        if (!ShowSlots) ClearRows();
        if (_loading) return;
        _rx.MultiMonitor = value;
        Dirty = true;
    }

    /// <summary>How long Avoid keeps a frequency out, in minutes; 0 is until the app is closed. Applies to what is avoided from now on.</summary>
    public void SetAvoidTime(Choice choice)
    {
        _rx.AvoidTime = choice.Value > 0 ? TimeSpan.FromMinutes(choice.Value) : null;
    }

    /// <summary>The longest a scan stays on a busy channel, in seconds; 0 is until the signal drops. Takes effect at once.</summary>
    public void SetResumeTime(Choice choice) => _rx.ResumeSeconds = choice.Value > 0 ? choice.Value : null;

    /// <summary>The sound device by name; null is the system's default.</summary>
    public void SetAudioDevice(string? name) => _rx.AudioDevice = name;

    /// <summary>Show what the receiver is on now, without tuning it: the scan moves it, not us.</summary>
    private void ShowTuned()
    {
        _loading = true;
        FrequencyHz = _rx.FrequencyHz;
        Mode = _rx.Mode;
        _loading = false;
        ShowLabel();
        ModeText = ModeLabel(_rx.Mode, _rx.BandwidthHz);
        EntryText = (FrequencyHz / 1e6).ToString("0.#####", CultureInfo.InvariantCulture);
        Blocked = _rx.Blocked;
    }

    partial void OnIsScanningChanged(bool value)
    {
        if (!value) IsSearching = false;
        if (!ShowSlots) ClearRows();
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

    partial void OnSelectedStepChanged(Choice value) => Dirty = true;

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
        _rx.Volume = IsMuted ? 0 : value / 100;
        Dirty = true;
    }

    partial void OnIsMutedChanged(bool value) => _rx.Volume = value ? 0 : Volume / 100;

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>The Multi button beside Scan: switch Multi Monitor on or off for this radio.</summary>
    [RelayCommand]
    private void ToggleMultiMonitor() => MultiMonitor = !MultiMonitor;

    partial void OnPanChanged(double value)
    {
        // The slider's middle holds: a touch either side of it is the middle.
        double pan = Math.Abs(value) < 0.05 ? 0 : Math.Clamp(Math.Round(value, 2), -1, 1);
        if (pan != value)
        {
            Pan = pan;
            return;
        }
        if (_loading) return;
        _rx.Pan = value;
        Dirty = true;
    }

    /// <summary>Put the sound back in the middle.</summary>
    [RelayCommand]
    private void CentrePan() => Pan = 0;

    /// <summary>The squelch slider's left end, which is squelch off.</summary>
    public const double SquelchOffDb = -140;

    /// <summary>The squelch is on whenever its slider is off the left end.</summary>
    partial void OnSquelchDbChanged(double value)
    {
        _rx.SquelchDb = value;
        SquelchOn = value > SquelchOffDb;
        Dirty = true;
    }

    /// <summary>A scan or a search needs the squelch: if it is off, set it to the usual level.</summary>
    private void EnsureSquelch()
    {
        if (!SquelchOn) SquelchDb = new RadioSettings().SquelchDb;
    }

    [RelayCommand]
    private void ToggleAutoSquelch() => AutoSquelch = !AutoSquelch;

    partial void OnAutoSquelchChanged(bool value)
    {
        _rx.AutoSquelch = value;
        Dirty = true;
    }

    [RelayCommand]
    private void ToggleStereo() => StereoOn = !StereoOn;

    partial void OnSquelchOnChanged(bool value)
    {
        _rx.SquelchEnabled = value;
        Dirty = true;
    }

    partial void OnStereoOnChanged(bool value)
    {
        _rx.StereoEnabled = value;
        Dirty = true;
    }

    partial void OnAutoGainChanged(bool value)
    {
        if (_loading) return;
        _rx.Source.AutoGain = value;
        Dirty = true;
    }

    partial void OnGainStepChanged(double value)
    {
        if (_loading) return;
        _rx.Source.GainStep = (int)Math.Round(value);
        Dirty = true;
    }

    // ---- device and power -------------------------------------------------------------------

    /// <summary>Look again for radios, and show as chosen the one in use, or else the one that will be opened.</summary>
    public void RefreshRadios()
    {
        IReadOnlyList<RadioInfo> found;
        try { found = _rx.Source.Radios(); }
        catch (InvalidOperationException) { found = []; }     // said when the receiver is switched on
        string? serial = IsOn ? _rx.Source.Radio?.Serial : _rx.Source.Serial;
        // A helper keeps its one radio; and a radio another receiver here has is not this one's to take.
        if (IsHelper) found = found.Where(r => r.Serial == _rx.Source.Serial).ToList();
        else if (IsTaken is { } taken) found = found.Where(r => r.Serial == serial || !taken(r.Serial)).ToList();
        _loading = true;
        Radios = found;
        SelectedRadio = found.FirstOrDefault(r => r.Serial == serial);
        _loading = false;
        Name = Model ?? "Radio";
    }

    /// <summary>Change the window's own receiver over to another radio, as choosing it in the Receiver box once did.</summary>
    public void ChangeTo(RadioInfo radio)
    {
        if (IsHelper) return;
        if (!Radios.Contains(radio)) Radios = [.. Radios, radio];
        SelectedRadio = radio;
    }

    /// <summary>Another radio was chosen: remember it and, if the receiver is on, change over to it now.</summary>
    partial void OnSelectedRadioChanged(RadioInfo? value)
    {
        if (_loading || IsHelper || value is null || value.Serial == _rx.Source.Serial && !IsOn) return;
        _rx.Source.Serial = value.Serial;
        Dirty = true;
        if (IsOn && !IsBusy && _rx.Source.Radio?.Serial != value.Serial) _ = ChangeRadio();
    }

    private async Task ChangeRadio()
    {
        IsBusy = true;
        try
        {
            Status = "Changing receiver…";
            await Task.Run(_rx.Stop);
            IsScanning = false;
            await StartRadio();
        }
        catch (Exception e)
        {
            IsOn = false;
            DeviceText = "Off";
            Status = e.Message;
        }
        finally { IsBusy = false; }
        RefreshRadios();
    }

    /// <summary>Switch the radio on and take up what this one has: its name, its antenna sockets, its bias T.</summary>
    private async Task StartRadio()
    {
        if (Status.Length == 0) Status = "Starting the radio…";
        await Task.Run(_rx.Start);
        IsOn = true;
        Status = "";
        DeviceText = _rx.Source.Description;
        HasBiasT = _rx.Source.HasBiasT;   // the MW/FM notch is on the same models
        var antennas = _rx.Source.Antennas;
        if (!antennas.SequenceEqual(Antennas))
        {
            _loading = true;
            Antennas = antennas;
            SelectedAntenna = _rx.Source.Antenna;
            _loading = false;
        }
    }

    partial void OnSelectedAntennaChanged(string value)
    {
        if (_loading || value is null) return;
        _rx.Source.Antenna = value;
        Dirty = true;
    }

    public const double MaxPpm = 100;

    partial void OnFmNotchChanged(bool value)
    {
        if (_loading) return;
        _rx.Source.FmNotch = value;
        Dirty = true;
    }

    partial void OnPpmChanged(decimal? value)
    {
        if (_loading || value is null) return;
        _rx.Source.Ppm = Math.Clamp((double)value.Value, -MaxPpm, MaxPpm);
        Dirty = true;
    }

    partial void OnBiasTChanged(bool value)
    {
        if (_loading) return;
        _rx.Source.BiasT = value;
        Dirty = true;
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
                Status = "";
                await StartRadio();
            }
        }
        catch (Exception e)
        {
            IsOn = false;
            DeviceText = "Off";
            Status = e.Message;
        }
        finally { IsBusy = false; }
        RefreshRadios();
    }

    /// <summary>Bring the display up to date with the receiver. Called twenty times a second.</summary>
    public void Tick()
    {
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
            if (ShowSlots) ShowRows();
            else ClearRows();
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
            ClearRows();
        }
        _ticks++;
    }

    /// <summary>Multi Monitor: bring the four rows and the line above them up to date with the receiver's slots.</summary>
    private void ShowRows()
    {
        var slots = _rx.Slots;
        bool relabel = _relabel;
        _relabel = false;
        int held = 0;
        double first = 0;
        for (int k = 0; k < Slots.Count; k++)
        {
            if (k < slots.Count && slots[k] is { Holding: true } s)
            {
                if (held++ == 0) first = s.FrequencyHz;
                Slots[k].Show(s, relabel);
            }
            else Slots[k].Clear();
        }
        _rowsShown = true;
        // Remade only when what it says has changed: the count and the band, or with nothing held how fast it scans.
        string? band = held > 0 ? BandPlan.Find(first).Name : null;
        double rate = held > 0 ? -1 : _passRate;
        if (held == _headerHeld && ReferenceEquals(band, _headerBand) && rate == _headerRate) return;
        _headerHeld = held;
        _headerBand = band;
        _headerRate = rate;
        SlotsHeader = held > 0
            ? $"MULTI · {held} of {Slots.Count}" + (band is { Length: > 0 } ? $" · {band}" : "")
            : $"MULTI · 0 of {Slots.Count} · {(_searching ? "Searching" : "Scanning")} · {_passRate:F0} passes/s";
    }

    /// <summary>Multi Monitor: empty the rows (once), outside a scan or with the mode off.</summary>
    private void ClearRows()
    {
        if (!_rowsShown) return;
        _rowsShown = false;
        foreach (var row in Slots) row.Clear();
        _headerHeld = -1;
        _headerBand = null;
        _headerRate = -1;
        SlotsHeader = "";
    }

    /// <summary>Stop the radio and let it go (a helper program is ended).</summary>
    public void Dispose() => _rx.Dispose();
}
