using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleScanner.Dsp;

namespace SimpleScanner.ViewModels;

/// <summary>
/// One of the four rows of a Multi Monitor display: a seat between the speakers and the channel the slot of
/// that seat holds, if any. <see cref="RadioViewModel"/> has four of these, the same four for its life, and
/// brings them up to date on the window's timer; a property is changed only when what it shows has changed.
/// </summary>
public sealed partial class SlotViewModel : ObservableObject
{
    private static readonly string[] SeatLabels = ["33% L", "33% R", "Hard L", "Hard R"];

    private readonly Action<int> _skip, _avoid;
    private readonly Func<double, ChannelLabel> _label;
    // What the mode and level texts were last made from, so that they are made again only when that changes.
    private Mode _textMode;
    private double _textBandwidthHz = -1;
    private double _textLevelDb = double.NaN;

    /// <param name="seat">Its slot and seat: 0 is 33% left, 1 is 33% right, 2 hard left, 3 hard right.</param>
    /// <param name="label">What is stored about a frequency, as the radio's display works it out (the title never null).</param>
    internal SlotViewModel(int seat, Action<int> skip, Action<int> avoid, Func<double, ChannelLabel> label)
    {
        Seat = seat;
        SeatLabel = SeatLabels[seat];
        _skip = skip;
        _avoid = avoid;
        _label = label;
    }

    /// <summary>Its slot, which is its seat: 0 is 33% left, 1 is 33% right, 2 hard left, 3 hard right.</summary>
    public int Seat { get; }
    /// <summary>Where the seat is, in a word: "33% L", "33% R", "Hard L", "Hard R".</summary>
    public string SeatLabel { get; }

    /// <summary>The slot holds a channel; an empty row shows nothing and is dimmed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SkipCommand))]
    [NotifyCanExecuteChangedFor(nameof(AvoidCommand))]
    private bool _holding;
    [ObservableProperty] private double _frequencyHz;
    /// <summary>The frequency in MHz ("121.6000"), for a row too small for the big readout.</summary>
    [ObservableProperty] private string _frequencyText = "";
    /// <summary>The stored channel's name, or the band's where nothing is stored (the main display's BandName).</summary>
    [ObservableProperty] private string _title = "";
    /// <summary>Whose channel it is: the stored channel's agency and description.</summary>
    [ObservableProperty] private string _detailText = "";
    /// <summary>The stored channel's particulars: tag, signal type, tone, station class, input, callsign.</summary>
    [ObservableProperty] private string _infoText = "";
    /// <summary>The receive mode and channel width ("NFM 12.5k").</summary>
    [ObservableProperty] private string _modeText = "";
    /// <summary>What a digital signal says (empty for an analog one).</summary>
    [ObservableProperty] private string _digitalText = "";
    [ObservableProperty] private bool _isEncrypted;
    [ObservableProperty] private double _level = -140;
    [ObservableProperty] private string _levelText = "";
    [ObservableProperty] private bool _squelchOpen;
    /// <summary>It holds a channel whose squelch has shut: the hang time, waiting for a reply.</summary>
    [ObservableProperty] private bool _waiting;

    /// <summary>Let go this slot's channel and pass it by for a while; the other slots carry on.</summary>
    [RelayCommand(CanExecute = nameof(Holding))]
    private void Skip() => _skip(Seat);

    /// <summary>Let go this slot's channel and keep it out of the scan for the Avoid Timer.</summary>
    [RelayCommand(CanExecute = nameof(Holding))]
    private void Avoid() => _avoid(Seat);

    /// <summary>Show what the slot has now.</summary>
    /// <param name="relabel">Work out what is stored about the frequency again, even if it has not changed.</param>
    internal void Show(SlotStatus s, bool relabel)
    {
        if (!s.Holding)
        {
            Clear();
            return;
        }
        if (relabel || !Holding || s.FrequencyHz != FrequencyHz)
        {
            FrequencyHz = s.FrequencyHz;
            FrequencyText = (s.FrequencyHz / 1e6).ToString("0.0000##", CultureInfo.InvariantCulture);
            var label = _label(s.FrequencyHz);
            Title = label.Title ?? "";
            DetailText = label.Detail;
            InfoText = label.Info;
        }
        if (s.Mode != _textMode || s.BandwidthHz != _textBandwidthHz)
        {
            _textMode = s.Mode;
            _textBandwidthHz = s.BandwidthHz;
            ModeText = RadioViewModel.ModeLabel(s.Mode, s.BandwidthHz);
        }
        DigitalText = s.Digital ?? "";
        IsEncrypted = s.Encrypted;
        Level = s.LevelDb;
        double shown = Math.Round(s.LevelDb, MidpointRounding.AwayFromZero);
        if (shown != _textLevelDb)
        {
            _textLevelDb = shown;
            LevelText = RadioViewModel.LevelLabel(s.LevelDb);
        }
        SquelchOpen = s.SquelchOpen;
        Waiting = !s.SquelchOpen;
        Holding = true;
    }

    /// <summary>Show an empty slot.</summary>
    internal void Clear()
    {
        Holding = false;
        FrequencyHz = 0;
        FrequencyText = Title = DetailText = InfoText = ModeText = DigitalText = LevelText = "";
        IsEncrypted = SquelchOpen = Waiting = false;
        Level = -140;
        _textBandwidthHz = -1;
        _textLevelDb = double.NaN;
    }
}
