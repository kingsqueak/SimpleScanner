using System.Buffers;
using System.Collections.Concurrent;
using SimpleScanner.Audio;
using SimpleScanner.Digital;
using SimpleScanner.Dsp;
using SimpleScanner.Radio;

namespace SimpleScanner;

/// <summary>A channel to visit in a scan.</summary>
public sealed record ScanChannel(string Name, double FrequencyHz, Mode Mode, double BandwidthHz);

/// <summary>
/// One of the four slots of Multi Monitor, as a display shows it: its seat (0 to 3: 33% left, 33% right,
/// hard left, hard right), whether it holds a channel, and if so which, how strong, whether its squelch
/// is open and what digital signal is on it. Plain values, so that it can be sent to another program.
/// </summary>
public sealed record SlotStatus(int Seat, bool Holding, ScanChannel? Channel, double FrequencyHz, Mode Mode, double BandwidthHz,
    double LevelDb, bool SquelchOpen, string? Digital, bool Encrypted, bool NothingToHear);

/// <summary>
/// The receiver: a radio, one channel taken out of its wide IQ stream, squelch and the speaker.
///
/// The radio captures several MHz at once. The wanted channel can be anywhere in that span
/// except the very centre, where a zero-IF receiver has a spur; tuning inside the span only
/// moves the channel filter, which is instant, and the radio itself is retuned only when the
/// wanted frequency leaves the span.
///
/// Scanning uses the same span. The scan list is divided into windows, each one tuning of the
/// radio (ScanPlan). In a window a short run of FFTs gives the level of every channel in it at
/// once. Channels above the squelch level are then listened to in turn, strongest first: if the
/// squelch opens, the scan stays until the signal has been gone for the hang time. Then the
/// next window.
///
/// Digital voice: in narrow FM the discriminator output also goes to P25, DMR, NXDN, D-STAR and
/// System Fusion (C4FM) decoders. When one finds its kind of signal on the channel, the analogue audio (which would be a rasp) gives way to
/// the decoded speech, and <see cref="Digital"/> says what the signal is. Encrypted calls are
/// reported and stay silent.
///
/// Multi Monitor (<see cref="MultiMonitor"/>): a scan that finds several active channels in a window
/// holds up to four of them at once, each in a slot of its own with its own filter, squelch and digital
/// voice, and plays them together, each in its own place between the speakers. While any slot holds a
/// channel the radio stays on the window and the window is looked over again a few times a second, so
/// that a new signal takes a free slot. A slot keeps its channel for <see cref="MultiHangSeconds"/> after
/// the signal has gone, to catch the reply. The scan moves on when every slot is free.
/// </summary>
public sealed class Receiver : IReceiver
{
    /// <summary>IQ rate asked of the radio: a multiple of 240 kHz, 12-bit on an RSPdx.</summary>
    public const double DefaultSampleRate = 7_680_000;
    private const int Cushion = ChannelReceiver.AudioRate * 60 / 1000;      // audio kept queued against jitter
    private const int TooMuch = ChannelReceiver.AudioRate * 250 / 1000;
    private const double SettleSeconds = 0.006, LookSeconds = 0.012, LookLongestSeconds = 0.05;
    private const double SurveySettleSeconds = 0.003;
    private const int SurveyFrames = 8;

    private const double ResurveySeconds = 0.2;      // Multi Monitor: how often a window with a slot held is looked over again

    // What the scan is doing with the radio: looking over a window, or staying on it while one slot or more
    // tries or holds a channel of it. What each slot is doing is the slot's own (SlotState).
    private enum ScanState { Off, Survey, Stay }
    private readonly record struct Block(float[] Data, int Count, bool Retuned);

    private readonly IIqSource _source;
    private readonly bool _playAudio;
    private readonly double _rate;
    private readonly object _cfg = new(), _audioLock = new();
    // One slot, or four once Multi Monitor has been switched on. The first is the channel being received when
    // no Multi Monitor scan is going. The array is replaced, and its slots change places, only under _cfg.
    private volatile MonitorSlot[] _slots;
    private SlotSound[] _mixSounds = [];        // the slots' sounds for the mixer, as they stood in this block: the DSP thread's
    private readonly SlotMixer _mixer = new();
    private volatile bool _multi, _disposed;
    private BlockingCollection<Block>? _queue;
    private Thread? _thread;
    private AudioOut? _audio;
    private string? _audioDevice;
    private double _volume = 0.5, _pan, _level = -140;
    private volatile bool _flushUntilRetuned;
    private bool _retunedUnsaid;            // on the radio's thread only
    private double _flushedSeconds;
    private const double FlushLongestSeconds = 0.8;
    private static readonly float[] Silence = new float[2 * Cushion];

    // Scan state, all under _cfg.
    private IReadOnlyList<ScanChannel> _scanList = [];
    private List<ScanWindow> _plan = [];
    private double[] _skipUntil = [];
    // Frequencies the user has asked to avoid (in units of 100 Hz), and when each comes back, by
    // Environment.TickCount64. Kept from one scan to the next, and gone when the app closes.
    private readonly Dictionary<long, long> _avoided = [];
    private long[] _avoidUntil = [];
    private readonly Queue<int> _candidates = new();
    private ScanState _scan;
    private int _window, _surveySkip, _fftFill, _surveyCount;
    private double _scanClock;
    // This stay in the window: a slot has listened and for how long since, the window is being left (every
    // slot let go, waiting for them to fade), and (Multi Monitor) when it is looked over next.
    private bool _stayed, _leaving, _resurveying;
    private double _stayTime, _resurveyIn;
    private long _passes;
    private readonly Fft _fft;
    private readonly float[] _fftWindow, _fftRe, _fftIm, _workRe, _workIm;
    private readonly double _fftNorm;
    private double[] _acc = [];

    public Receiver(IIqSource source, bool playAudio = true, double sampleRate = DefaultSampleRate)
    {
        _source = source;
        _playAudio = playAudio;
        _rate = sampleRate;
        var first = new MonitorSlot(sampleRate);
        // Outside a Multi Monitor scan the first slot is always being received: it holds its channel alone, so it plays in the centre.
        first.Sound.Take(0);
        _slots = [first];
        _source.Samples += OnSamples;
        _source.Failed += why =>
        {
            Stop();
            Failed?.Invoke(why);
        };

        // FFT bins about 2 kHz wide: a few to each narrow channel.
        int size = 256;
        while (size < sampleRate / 1900 && size < 16384) size *= 2;
        _fft = new Fft(size);
        _fftWindow = new float[size];
        double sumSquares = 0;
        for (int i = 0; i < size; i++)
        {
            _fftWindow[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / size));
            sumSquares += _fftWindow[i] * _fftWindow[i];
        }
        _fftNorm = size * sumSquares;        // sum of |X|^2 over a band / this = mean power in the band
        _fftRe = new float[size];
        _fftIm = new float[size];
        _workRe = new float[size];
        _workIm = new float[size];
    }

    public IIqSource Source => _source;
    public double SampleRate => _rate;
    public bool IsRunning { get; private set; }
    /// <summary>The radio stopped by itself; the text says why.</summary>
    public event Action<string>? Failed;
    /// <summary>The audio as it goes to the speaker (48 kHz stereo, left and right interleaved, after squelch and volume), on the DSP thread.</summary>
    public event Action<ReadOnlySpan<float>>? AudioOutput;

    /// <summary>Channel level in dB: sample level less the radio's reported gain, plus <see cref="LevelOffsetDb"/>.</summary>
    public double LevelDb => Volatile.Read(ref _level);
    /// <summary>Added to the level reading, to line it up with a signal generator.</summary>
    public double LevelOffsetDb { get; set; }
    /// <summary>The squelch is open; with several slots, that of any of them.</summary>
    public bool SquelchOpen
    {
        get
        {
            foreach (var s in _slots)
                if (s.SquelchOpen) return true;
            return false;
        }
    }
    /// <summary>True on a frequency the scanner refuses to receive (cellular telephone bands).</summary>
    public bool Blocked => _slots[0].Blocked;

    // The slot the receiver speaks for: in a Multi Monitor scan the lowest one holding a channel, otherwise the first.
    private MonitorSlot Main
    {
        get
        {
            var slots = _slots;
            if (_multi && _scan == ScanState.Stay)
                foreach (var s in slots)
                    if (s.State == SlotState.Listen) return s;
            return slots[0];
        }
    }
    public int AudioUnderruns => _audio?.Underruns ?? 0;

    /// <summary>The sound output device by name; null or empty is the system's default. May be changed while running.</summary>
    public string? AudioDevice
    {
        get => _audioDevice;
        set
        {
            lock (_audioLock)
            {
                if (value == _audioDevice) return;
                _audioDevice = value;
                if (_audio is null) return;
                _audio.Dispose();
                _audio = null;
                try { _audio = new AudioOut(ChannelReceiver.AudioRate, 2, value); }
                catch (InvalidOperationException) { }
            }
        }
    }

    /// <summary>
    /// For a pan between -1 and 1: the gain of the left and of the right speaker, and how far left and
    /// right are brought together first (0 not at all, 1 into one).
    /// </summary>
    public static (float Left, float Right, float Mix) PanGains(double pan)
    {
        float p = (float)Math.Clamp(pan, -1, 1);
        return (p > 0 ? 1 - p : 1, p < 0 ? 1 + p : 1, Math.Abs(p));
    }

    public double Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 1);
    }

    /// <summary>
    /// Where the sound sits between the left speaker (-1) and the right (1). In the middle (0) left and
    /// right go out as they are. Towards one side the other is turned down, and a stereo station is
    /// brought together so that neither of its channels is lost.
    /// </summary>
    public double Pan
    {
        get => _pan;
        set => _pan = double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
    }

    // The squelch and stereo settings are the receiver's: these setters are where they are passed on to every slot.
    public double SquelchDb
    {
        get => _slots[0].Squelch.ThresholdDb;
        set
        {
            lock (_cfg)
                foreach (var s in _slots) s.Squelch.ThresholdDb = value;
        }
    }

    public bool SquelchEnabled
    {
        get => _slots[0].Squelch.Enabled;
        set
        {
            lock (_cfg)
                foreach (var s in _slots) s.Squelch.Enabled = value;
        }
    }

    /// <summary>
    /// Noise squelch on top of the level: stay shut on noise that is above the squelch level, as
    /// on a noisy band. Works in narrow FM (digital voice included) and AM; broadcast FM goes by level alone.
    /// </summary>
    public bool AutoSquelch
    {
        get => _slots[0].Squelch.Auto;
        set
        {
            lock (_cfg)
                foreach (var s in _slots) s.Squelch.Auto = value;
        }
    }

    /// <summary>What the auto squelch goes by, and the most it lets through: see <see cref="ChannelReceiver.Roughness"/>.</summary>
    public double Roughness => Main.Channel.Roughness;
    public double RoughnessLimit => Main.Channel.RoughnessLimit;

    /// <summary>A broadcast FM station is being received in stereo.</summary>
    public bool Stereo => Main.Channel.Stereo;

    public bool StereoEnabled
    {
        get => _slots[0].Channel.StereoEnabled;
        set
        {
            lock (_cfg)
                foreach (var s in _slots) s.Channel.StereoEnabled = value;
        }
    }

    public Mode Mode => Main.Channel.Mode;
    public double BandwidthHz => Main.Channel.BandwidthHz;

    /// <summary>The digital signal on the channel now (P25, DMR, NXDN, D-STAR or System Fusion), or null for none.</summary>
    public IDigitalStatus? Digital => Main.DigitalVoice.Status;
    /// <summary>False when the digital voice library could not be loaded.</summary>
    public bool DigitalAvailable => _slots[0].DigitalVoice.DigitalAvailable;
    /// <summary>False when the AMBE+2 speech decoder is not installed: DMR calls are shown, not heard.</summary>
    public bool DmrVoiceAvailable => _slots[0].DigitalVoice.DmrVoiceAvailable;

    /// <summary>Choosing a mode by hand ends a scan.</summary>
    public void SetMode(Mode mode, double bandwidthHz)
    {
        lock (_cfg)
        {
            var main = Main;
            if (mode == main.Channel.Mode && bandwidthHz == main.Channel.BandwidthHz) return;
            EndScanLocked();
            var slot = _slots[0];
            ConfigureLocked(slot, mode, bandwidthHz);
            // A wider filter may no longer fit where the channel sat in the span.
            if (IsRunning && !ScanPlan.Fits(slot.OffsetHz, bandwidthHz, _source.UsableBandwidthHz)) TuneLocked(slot.FrequencyHz);
        }
    }

    private static void ConfigureLocked(MonitorSlot slot, Mode mode, double bandwidthHz)
    {
        if (mode == slot.Channel.Mode && bandwidthHz == slot.Channel.BandwidthHz) return;
        slot.Channel.Configure(mode, bandwidthHz);
        slot.Blocked = BandPlan.IsBlocked(slot.FrequencyHz, bandwidthHz);
        slot.ResetSquelch();
        slot.ResetDigital();
    }

    /// <summary>Tuning by hand ends a scan.</summary>
    public double FrequencyHz
    {
        get => Main.FrequencyHz;
        set
        {
            lock (_cfg)
            {
                if (value == Main.FrequencyHz) return;
                EndScanLocked();
                TuneLocked(value);
            }
        }
    }

    // Puts the one channel (the first slot) on a frequency. Not for use during a scan.
    private void TuneLocked(double hz)
    {
        var slot = _slots[0];
        slot.FrequencyHz = hz;
        slot.Blocked = BandPlan.IsBlocked(hz, slot.Channel.BandwidthHz);
        slot.ResetSquelch();
        slot.ResetDigital();
        double offset = hz - _source.CenterHz;
        if (IsRunning && ScanPlan.Fits(offset, slot.Channel.BandwidthHz, _source.UsableBandwidthHz))
        {
            slot.OffsetHz = offset;
        }
        else
        {
            slot.OffsetHz = ScanPlan.SingleOffsetFor(hz);
            if (IsRunning) _flushUntilRetuned = true;
            _source.Tune(hz - slot.OffsetHz);
            CutLeavingLocked();
        }
        slot.Channel.SetOffset(slot.OffsetHz);
    }

    // ---- scanning -------------------------------------------------------------------------

    /// <summary>How long a channel must stay quiet before the scan moves on from it.</summary>
    public double ScanHangSeconds { get; set; } = 2;
    /// <summary>
    /// In Multi Monitor, in place of <see cref="ScanHangSeconds"/>: how long a slot keeps its channel after the
    /// signal has dropped, so that the reply is caught.
    /// </summary>
    public double MultiHangSeconds { get; set; } = 8;
    /// <summary>How long a scan passes by a digital channel it left because there was nothing to hear on it.</summary>
    public double SilentDigitalSkipSeconds { get; set; } = 60;
    /// <summary>The same after a digital call was heard there and ended: short, so the rest of the conversation is caught.</summary>
    public double AfterCallSkipSeconds { get; set; } = 5;
    /// <summary>How long a scan stays on a digital signal that has had nothing to hear since it stopped there.</summary>
    public double SilentDigitalLookSeconds { get; set; } = 0.4;
    /// <summary>How long a skipped channel is passed by.</summary>
    public double ScanSkipSeconds { get; set; } = 10;
    /// <summary>
    /// The longest a scan stays on one channel before it carries on, signal or no signal; null is
    /// no limit, and it stays until the signal drops. In Multi Monitor it is the longest stay in one
    /// window, counted from the first slot that holds a channel there.
    /// </summary>
    public double? ResumeSeconds { get; set; }
    /// <summary>How long a channel left on the resume timer is passed by, so that the rest get a turn.</summary>
    public double ResumePassBySeconds { get; set; } = 10;
    /// <summary>How long an avoided frequency is left out of scans and searches; null is until the app closes.</summary>
    public TimeSpan? AvoidTime { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>Frequencies being avoided now.</summary>
    public int AvoidedCount
    {
        get
        {
            long now = Environment.TickCount64;
            lock (_cfg) return _avoided.Values.Count(until => until > now);
        }
    }
    public bool Scanning => _scan != ScanState.Off;
    /// <summary>The scan is stopped on an active channel; in Multi Monitor, a slot holds one.</summary>
    public bool ScanListening
    {
        get
        {
            if (_scan != ScanState.Stay) return false;
            foreach (var s in _slots)
                if (s.State == SlotState.Listen) return true;
            return false;
        }
    }
    /// <summary>Times the scan has been through its whole list.</summary>
    public long ScanPasses => Interlocked.Read(ref _passes);
    /// <summary>Tunings of the radio one pass of the scan takes.</summary>
    public int ScanWindows => _plan.Count;

    /// <summary>
    /// The channel the scan is listening to or trying, or null when it is between channels or off. In Multi
    /// Monitor: the channel of the lowest slot that holds one.
    /// </summary>
    public ScanChannel? ScanChannel
    {
        get
        {
            lock (_cfg)
            {
                if (_scan != ScanState.Stay) return null;
                var slot = Main;
                if (_multi && slot.State != SlotState.Listen) return null;
                return slot.ScanIndex < 0 || slot.ScanIndex >= _scanList.Count ? null : _scanList[slot.ScanIndex];
            }
        }
    }

    /// <summary>
    /// A scan holds up to four active channels of a window at once, each in a slot of its own, and plays them
    /// together: see <see cref="Slots"/>. Off, a scan stops on one channel at a time. May be changed at any
    /// time, during a scan too. Outside a scan it does nothing.
    /// </summary>
    public bool MultiMonitor
    {
        get => _multi;
        set
        {
            // The other three slots are made the first time the mode is switched on (each has its filters and
            // five decoders), outside the lock: making them takes a moment.
            MonitorSlot[]? made = null;
            if (value && !_disposed && _slots.Length < SlotMixer.Seats)
            {
                made = new MonitorSlot[SlotMixer.Seats - 1];
                for (int k = 0; k < made.Length; k++) made[k] = new MonitorSlot(_rate);
            }
            lock (_cfg)
            {
                if (made is not null && !_disposed && _slots.Length < SlotMixer.Seats)
                {
                    var first = _slots[0];
                    foreach (var s in made)
                    {
                        s.Squelch.ThresholdDb = first.Squelch.ThresholdDb;
                        s.Squelch.Enabled = first.Squelch.Enabled;
                        s.Squelch.Auto = first.Squelch.Auto;
                        s.Channel.StereoEnabled = first.Channel.StereoEnabled;
                    }
                    _slots = [first, .. made];
                    made = null;
                }
                if (_disposed || _slots.Length < SlotMixer.Seats) value = false;       // disposed of: there is nothing to switch on
                if (value != _multi)
                {
                    _multi = value;
                    var slots = _slots;
                    if (value)
                    {
                        // A scan stopped on a channel carries on with it in the first slot; the rest are free for others.
                        if (_scan == ScanState.Stay)
                        {
                            if (slots[0].State != SlotState.Listen) slots[0].Sound.Leave();
                            _resurveyIn = ResurveySeconds;
                            _resurveying = _leaving = false;
                        }
                    }
                    else
                    {
                        // Back to one channel at a time, which is the first slot's: it keeps what it has, the others
                        // are let go. If it has nothing, the window is looked over afresh. (A first slot that was
                        // let go a moment ago and is still fading out finishes its fade meanwhile.)
                        for (int k = 1; k < slots.Length; k++) ReleaseLocked(slots[k]);
                        if (slots[0].State != SlotState.Leaving) TakeCentreLocked(slots[0]);
                        _resurveying = _leaving = false;
                        if (_scan == ScanState.Stay && slots[0].State is SlotState.Free or SlotState.Leaving) SurveyAgainLocked();
                    }
                }
            }
            // Made for nothing (another thread was first, or the receiver has been disposed of): never used, so free to go.
            if (made is not null)
                foreach (var s in made) s.Dispose();
        }
    }

    /// <summary>
    /// The four slots as they are now, in one reading. In a Multi Monitor scan each slot that holds a channel
    /// says which; otherwise the first is the one channel being received and the rest are empty.
    /// </summary>
    public IReadOnlyList<SlotStatus> Slots
    {
        get
        {
            var list = new SlotStatus[SlotMixer.Seats];
            lock (_cfg)
            {
                var slots = _slots;
                bool multi = _multi && _scan != ScanState.Off;
                for (int k = 0; k < list.Length; k++)
                {
                    var s = k < slots.Length ? slots[k] : null;
                    if (s is null || (multi ? s.State != SlotState.Listen : k != 0))
                    {
                        list[k] = new SlotStatus(k, false, null, 0, Mode.Nfm, 0, -140, false, null, false, false);
                        continue;
                    }
                    var channel = _scan == ScanState.Stay && s.State != SlotState.Free && s.ScanIndex >= 0 && s.ScanIndex < _scanList.Count
                        ? _scanList[s.ScanIndex] : null;
                    var digital = s.DigitalVoice.Status;
                    list[k] = new SlotStatus(k, true, channel, s.FrequencyHz, s.Channel.Mode, s.Channel.BandwidthHz,
                        multi ? s.Level : Volatile.Read(ref _level), s.SquelchOpen, digital?.ToString(),
                        digital?.Encrypted ?? false, digital?.NothingToHear ?? false);
                }
            }
            return list;
        }
    }

    /// <summary>
    /// Scan these channels. The radio must be running. The squelch decides what counts as
    /// active, so it is switched on.
    /// </summary>
    public void StartScan(IReadOnlyList<ScanChannel> channels)
    {
        lock (_cfg)
        {
            EndScanLocked();
            if (!IsRunning) return;
            _scanList = channels.Where(c => !BandPlan.IsBlocked(c.FrequencyHz, c.BandwidthHz)).OrderBy(c => c.FrequencyHz).ToList();
            if (_scanList.Count == 0) return;
            _plan = ScanPlan.Build(_scanList, _source.UsableBandwidthHz);
            _skipUntil = new double[_scanList.Count];
            _avoidUntil = new long[_scanList.Count];
            for (int i = 0; i < _scanList.Count; i++)
                _avoided.TryGetValue(AvoidKey(_scanList[i].FrequencyHz), out _avoidUntil[i]);
            _acc = new double[_scanList.Count];
            SquelchEnabled = true;
            _scanClock = 0;
            Interlocked.Exchange(ref _passes, 0);
            _window = -1;
            NextWindowLocked();
        }
    }

    /// <summary>
    /// Stop scanning and stay on the present channel. In Multi Monitor that is the channel of the lowest slot
    /// holding one; the others are let go.
    /// </summary>
    public void StopScan()
    {
        lock (_cfg) EndScanLocked();
    }

    // The scan is over: the receiver is on one channel again, in the first slot and in the centre. That is the
    // channel it was on, or in Multi Monitor the lowest slot's that holds one: that slot changes places with
    // the first, so that its sound goes on unbroken. Every other slot is let go.
    private void EndScanLocked()
    {
        _scan = ScanState.Off;
        OneChannelLocked(fade: true);
    }

    private void OneChannelLocked(bool fade)
    {
        var slots = _slots;
        if (_multi)
            for (int k = 1; k < slots.Length; k++)
            {
                if (slots[0].State == SlotState.Listen) break;
                if (slots[k].State == SlotState.Listen) (slots[0], slots[k]) = (slots[k], slots[0]);
            }
        for (int k = 1; k < slots.Length; k++)
        {
            if (fade) ReleaseLocked(slots[k]);
            else FreeLocked(slots[k]);
        }
        slots[0].State = SlotState.Free;
        slots[0].Quiet = false;
        TakeCentreLocked(slots[0]);
        _candidates.Clear();
        _stayed = _leaving = _resurveying = false;
    }

    // The one channel being received holds the first seat, and being alone there it plays in the centre.
    private static void TakeCentreLocked(MonitorSlot slot)
    {
        if (!slot.Sound.Holding || slot.Sound.Seat != 0) slot.Sound.Take(0);
    }

    /// <summary>
    /// Leave the channel the scan has stopped on, and pass it by for <see cref="ScanSkipSeconds"/>. In Multi
    /// Monitor: leave the window, and pass by every channel held in it.
    /// </summary>
    public void SkipChannel()
    {
        lock (_cfg)
        {
            if (_scan != ScanState.Stay) return;
            if (_multi)
            {
                foreach (var s in _slots)
                    if (s.State is SlotState.Settle or SlotState.Look or SlotState.Listen && s.ScanIndex >= 0)
                        _skipUntil[s.ScanIndex] = _scanClock + ScanSkipSeconds;
                LeaveWindowLocked();
                return;
            }
            var slot = _slots[0];
            if (slot.ScanIndex >= 0) _skipUntil[slot.ScanIndex] = _scanClock + ScanSkipSeconds;
            NextCandidateLocked(slot);
        }
    }

    /// <summary>
    /// Leave the channel the scan has stopped on, and keep it out of this scan and later ones
    /// for <see cref="AvoidTime"/>. Returns the channel avoided, or null if the scan was not on one.
    /// In Multi Monitor it does nothing: a channel is avoided by its slot, with <see cref="AvoidSlot"/>.
    /// </summary>
    public ScanChannel? AvoidChannel()
    {
        lock (_cfg)
        {
            if (_scan != ScanState.Stay || _multi) return null;
            var slot = _slots[0];
            if (slot.ScanIndex < 0) return null;
            var ch = _scanList[slot.ScanIndex];
            AvoidLocked(slot.ScanIndex);
            NextCandidateLocked(slot);
            return ch;
        }
    }

    private void AvoidLocked(int index)
    {
        long until = AvoidTime is { } t ? Environment.TickCount64 + (long)t.TotalMilliseconds : long.MaxValue;
        _avoided[AvoidKey(_scanList[index].FrequencyHz)] = until;
        _avoidUntil[index] = until;
    }

    /// <summary>
    /// Multi Monitor: let go the channel this slot (0 to 3) holds and pass it by for <see cref="ScanSkipSeconds"/>.
    /// The other slots carry on. With the mode off the first slot is the one channel, and this is <see cref="SkipChannel"/>.
    /// </summary>
    public void SkipSlot(int slot)
    {
        lock (_cfg)
        {
            if (!_multi)
            {
                if (slot == 0) SkipChannel();
                return;
            }
            if (HeldLocked(slot) is not { } s) return;
            _skipUntil[s.ScanIndex] = _scanClock + ScanSkipSeconds;
            ReleaseLocked(s);
        }
    }

    /// <summary>
    /// Multi Monitor: let go the channel this slot (0 to 3) holds and keep it out of this scan and later ones for
    /// <see cref="AvoidTime"/>. The other slots carry on. Returns the channel avoided, or null if the slot held
    /// none. With the mode off the first slot is the one channel, and this is <see cref="AvoidChannel"/>.
    /// </summary>
    public ScanChannel? AvoidSlot(int slot)
    {
        lock (_cfg)
        {
            if (!_multi) return slot == 0 ? AvoidChannel() : null;
            if (HeldLocked(slot) is not { } s) return null;
            var ch = _scanList[s.ScanIndex];
            AvoidLocked(s.ScanIndex);
            ReleaseLocked(s);
            return ch;
        }
    }

    // The slot of that number if a Multi Monitor scan has it holding a channel.
    private MonitorSlot? HeldLocked(int slot)
    {
        var slots = _slots;
        if (_scan != ScanState.Stay || slot < 0 || slot >= slots.Length) return null;
        var s = slots[slot];
        return s.State == SlotState.Listen && s.ScanIndex >= 0 && s.ScanIndex < _scanList.Count ? s : null;
    }

    /// <summary>Put every avoided frequency back in the scan.</summary>
    public void ClearAvoided()
    {
        lock (_cfg)
        {
            _avoided.Clear();
            Array.Clear(_avoidUntil);
        }
    }

    private static long AvoidKey(double frequencyHz) => (long)Math.Round(frequencyHz / 100);

    private void NextWindowLocked()
    {
        _window = (_window + 1) % _plan.Count;
        if (_window == 0) Interlocked.Increment(ref _passes);
        var w = _plan[_window];
        _surveySkip = 0;
        bool retune = Math.Abs(_source.CenterHz - w.CenterHz) > 1;
        if (retune)
        {
            _flushUntilRetuned = true;
            _source.Tune(w.CenterHz);
            _surveySkip = (int)(SurveySettleSeconds * _rate);
        }
        _scan = ScanState.Survey;
        var slots = _slots;
        for (int k = 0; k < slots.Length; k++)
        {
            var slot = slots[k];
            if (k == 0 && !_multi)
            {
                // The one channel: it stays the first slot's, between channels too.
                slot.State = SlotState.Free;
                slot.ScanIndex = -1;
                slot.SquelchOpen = false;
            }
            else if (slot.State != SlotState.Leaving || retune) FreeLocked(slot);     // one still fading goes on where the radio has not moved
        }
        _candidates.Clear();
        _stayed = _leaving = _resurveying = false;
        StartSurveyLocked();
    }

    // The same window is looked over from the start, without moving the radio.
    private void SurveyAgainLocked()
    {
        _scan = ScanState.Survey;
        _surveySkip = 0;
        _candidates.Clear();
        _stayed = _leaving = _resurveying = false;
        StartSurveyLocked();
    }

    private void StartSurveyLocked()
    {
        _fftFill = 0;
        _surveyCount = 0;
        foreach (int c in _plan[_window].Channels) _acc[c] = 0;
    }

    // Between windows: look over the new one, and go to what is active in it.
    private void SurveyLocked(ReadOnlySpan<float> re, ReadOnlySpan<float> im)
    {
        if (!SurveyFramesLocked(re, im)) return;
        double best = FindActiveLocked();
        double shown = Volatile.Read(ref _level);
        Volatile.Write(ref _level, best > shown ? best : shown + 0.3 * (best - shown));
        if (!_multi)
        {
            NextCandidateLocked(_slots[0]);
            return;
        }
        if (_candidates.Count == 0)
        {
            NextWindowLocked();
            return;
        }
        _scan = ScanState.Stay;
        _resurveyIn = ResurveySeconds;
        FillSlotsLocked();
    }

    // Look at every channel of the window at once: a few FFTs of the whole span, and the power
    // in each channel's bins. True once enough of them have been added up.
    private bool SurveyFramesLocked(ReadOnlySpan<float> re, ReadOnlySpan<float> im)
    {
        int n = re.Length, pos = 0, size = _fft.Size;
        if (_surveySkip > 0)
        {
            pos = Math.Min(_surveySkip, n);
            _surveySkip -= pos;
        }
        var w = _plan[_window];
        while (pos < n)
        {
            int take = Math.Min(size - _fftFill, n - pos);
            re.Slice(pos, take).CopyTo(_fftRe.AsSpan(_fftFill));
            im.Slice(pos, take).CopyTo(_fftIm.AsSpan(_fftFill));
            _fftFill += take;
            pos += take;
            if (_fftFill < size) break;
            _fftFill = 0;
            for (int i = 0; i < size; i++)
            {
                _workRe[i] = _fftRe[i] * _fftWindow[i];
                _workIm[i] = _fftIm[i] * _fftWindow[i];
            }
            _fft.Transform(_workRe, _workIm);
            double binHz = _rate / size;
            foreach (int c in w.Channels)
            {
                var ch = _scanList[c];
                double offset = ch.FrequencyHz - w.CenterHz;
                int first = (int)Math.Round((offset - ch.BandwidthHz / 2) / binHz), last = (int)Math.Round((offset + ch.BandwidthHz / 2) / binHz);
                double sum = 0;
                for (int k = first; k <= last; k++)
                {
                    int bin = ((k % size) + size) % size;
                    sum += _workRe[bin] * _workRe[bin] + _workIm[bin] * _workIm[bin];
                }
                _acc[c] += sum;
            }
            if (++_surveyCount >= SurveyFrames) return true;
        }
        return false;
    }

    // Which channels of the window are above the squelch level, and not passed by, avoided or in a slot
    // already? They become the candidates, strongest first. Returns the highest level in the window.
    private double FindActiveLocked()
    {
        var w = _plan[_window];
        double gain = _source.GainDb, best = -140;
        long now = Environment.TickCount64;
        var found = new List<(double level, int index)>();
        foreach (int c in w.Channels)
        {
            double level = 10 * Math.Log10(_acc[c] / (SurveyFrames * _fftNorm) + 1e-14) - gain + LevelOffsetDb;
            best = Math.Max(best, level);
            if (level >= SquelchDb && _scanClock >= _skipUntil[c] && now >= _avoidUntil[c] && !InSlotLocked(c)) found.Add((level, c));
        }
        _candidates.Clear();
        foreach (var (_, index) in found.OrderByDescending(f => f.level)) _candidates.Enqueue(index);
        return best;
    }

    private bool InSlotLocked(int index)
    {
        foreach (var s in _slots)
            if (s.ScanIndex == index && s.State is SlotState.Settle or SlotState.Look or SlotState.Listen) return true;
        return false;
    }

    // One channel at a time: the slot goes to the next candidate, or the scan to the next window.
    private void NextCandidateLocked(MonitorSlot slot)
    {
        if (_candidates.Count == 0)
        {
            NextWindowLocked();
            return;
        }
        StartCandidateLocked(slot, _candidates.Dequeue());
    }

    private void StartCandidateLocked(MonitorSlot slot, int index)
    {
        slot.ScanIndex = index;
        var ch = _scanList[index];
        ConfigureLocked(slot, ch.Mode, ch.BandwidthHz);
        // Inside the window already: only the channel filter moves.
        slot.FrequencyHz = ch.FrequencyHz;
        slot.Blocked = false;
        slot.OffsetHz = ch.FrequencyHz - _source.CenterHz;
        slot.Channel.SetOffset(slot.OffsetHz);
        slot.ResetSquelch();
        slot.ResetDigital();
        // In Multi Monitor a slot has a seat only once it holds the channel: one that is only trying moves nobody.
        if (_multi) slot.Sound.Leave();
        _scan = ScanState.Stay;
        slot.State = SlotState.Settle;
        slot.ScanTimer = 0;
        slot.ScanSawSignal = false;
        slot.Quiet = false;
        slot.Level = -140;
    }

    // Multi Monitor: candidates go to free slots, lowest first. With none free, the slot that has been quiet
    // longest (its signal gone, in its hang time) gives way; if every slot is busy the candidate waits.
    private void FillSlotsLocked()
    {
        var slots = _slots;
        long now = Environment.TickCount64;
        while (_candidates.Count > 0)
        {
            int c = _candidates.Peek();
            // Taken, skipped or avoided since the window was looked over.
            if (InSlotLocked(c) || _scanClock < _skipUntil[c] || now < _avoidUntil[c])
            {
                _candidates.Dequeue();
                continue;
            }
            MonitorSlot? slot = null, quiet = null;
            foreach (var s in slots)
            {
                if (s.State == SlotState.Free)
                {
                    slot = s;
                    break;
                }
                if (s.State == SlotState.Listen && s.Quiet && (quiet is null || s.ScanTimer > quiet.ScanTimer)) quiet = s;
            }
            if (slot is null)
            {
                if (quiet is null) return;
                ReleaseLocked(quiet);
                if (quiet.State != SlotState.Free) return;      // still fading: the candidate has it in a moment
                slot = quiet;
            }
            StartCandidateLocked(slot, _candidates.Dequeue());
        }
    }

    // A slot lets its channel go. If it can be heard it is kept running until it has faded out, and is free then.
    private void ReleaseLocked(MonitorSlot slot)
    {
        if (slot.State is SlotState.Free or SlotState.Leaving) return;
        if (!slot.SquelchOpen && slot.Sound.Gain == 0)
        {
            FreeLocked(slot);
            return;
        }
        slot.State = SlotState.Leaving;
        slot.ScanIndex = -1;
        slot.SquelchOpen = false;
        slot.Quiet = false;
    }

    private static void FreeLocked(MonitorSlot slot)
    {
        slot.State = SlotState.Free;
        slot.ScanIndex = -1;
        slot.SquelchOpen = false;
        slot.Quiet = false;
        slot.Level = -140;
        slot.Sound.Leave();
        slot.DigitalVoice.ClearStatus();
    }

    // The radio has been moved: a slot still fading out has nothing of its channel left to fade.
    private void CutLeavingLocked()
    {
        foreach (var s in _slots)
            if (s.State == SlotState.Leaving) FreeLocked(s);
    }

    // Multi Monitor: every slot is let go and nothing new is taken; the scan moves on when they have faded out.
    private void LeaveWindowLocked()
    {
        foreach (var s in _slots) ReleaseLocked(s);
        _candidates.Clear();
        _leaving = true;
        _resurveying = false;
    }

    // Multi Monitor, once a block while the scan stays in a window, after the slots have been worked: the
    // resume time, another look over the window when it is due, candidates to slots, and on to the next
    // window when every slot is free and nothing waits.
    private void WindowStepLocked(ReadOnlySpan<float> re, ReadOnlySpan<float> im, double seconds)
    {
        var slots = _slots;
        if (_stayed) _stayTime += seconds;
        if (!_leaving && _stayed && ResumeSeconds is { } limit && _stayTime >= limit)
        {
            // Those still busy would be found again at once: without a pause the scan would be straight back.
            foreach (var s in slots)
                if (s.State == SlotState.Listen && s.SquelchOpen && s.ScanIndex >= 0)
                    _skipUntil[s.ScanIndex] = _scanClock + ResumePassBySeconds;
            LeaveWindowLocked();
        }
        if (!_leaving)
        {
            if (!_resurveying && (_resurveyIn -= seconds) <= 0)
            {
                _resurveying = true;
                _surveySkip = 0;
                StartSurveyLocked();
            }
            if (_resurveying && SurveyFramesLocked(re, im))
            {
                _resurveying = false;
                _resurveyIn = ResurveySeconds;
                FindActiveLocked();
            }
            FillSlotsLocked();
        }
        foreach (var s in slots)
            if (s.State != SlotState.Free) return;
        if (_candidates.Count == 0) NextWindowLocked();
    }

    /// <summary>One block's worth of a slot's part in a scan. Returns whether the audio may pass.</summary>
    private bool ScanStepLocked(MonitorSlot slot, int seat, double level, bool open, double seconds)
    {
        slot.ScanTimer += seconds;
        switch (slot.State)
        {
            case SlotState.Settle:
                // The filters still hold the last channel.
                if (slot.ScanTimer >= SettleSeconds)
                {
                    slot.State = SlotState.Look;
                    slot.ScanTimer = 0;
                    slot.ResetSquelch();
                }
                return false;
            case SlotState.Look:
                if (level >= slot.Squelch.ThresholdDb) slot.ScanSawSignal = true;
                if (open)
                {
                    slot.State = SlotState.Listen;
                    slot.ScanTimer = 0;
                    slot.ListenTime = 0;
                    slot.ScanHeardVoice = false;
                    slot.Quiet = false;
                    // It holds the channel now, and has its seat.
                    if (!slot.Sound.Holding || slot.Sound.Seat != seat) slot.Sound.Take(seat);
                    if (!_stayed)
                    {
                        _stayed = true;
                        _stayTime = 0;
                    }
                    return true;
                }
                // Nothing there after all, or only noise: move on. Something there: give the squelch time to make up its mind.
                if (slot.ScanTimer >= (slot.ScanSawSignal && !slot.Squelch.Noisy ? LookLongestSeconds : LookSeconds))
                {
                    if (_multi) FreeLocked(slot);       // the next candidate, if there is one, goes to the lowest free slot
                    else NextCandidateLocked(slot);
                }
                return false;
            case SlotState.Listen:
                // A digital signal with nothing to listen to (a control channel, a repeater sending idle bursts,
                // an encrypted call) keeps the squelch open for as long as it likes. If a call was heard on this
                // stop, give it the hang time for a reply and come back soon; if not, leave as soon as it is clear
                // what the signal is, and pass it by for longer.
                slot.ListenTime += seconds;
                if (!_multi && ResumeSeconds is { } limit && slot.ListenTime >= limit)
                {
                    // Still busy, or it would have been left already: without a pause the scan would be straight back.
                    if (slot.ScanIndex >= 0) _skipUntil[slot.ScanIndex] = _scanClock + ResumePassBySeconds;
                    NextWindowLocked();
                    return false;
                }
                bool silent = open && slot.DigitalVoice.Status is { NothingToHear: true };
                if (open && slot.DigitalVoice.Status is { NothingToHear: false }) slot.ScanHeardVoice = true;
                slot.Quiet = !open || silent;
                if (open && !silent) slot.ScanTimer = 0;
                else if (slot.ScanTimer >= (silent && !slot.ScanHeardVoice ? SilentDigitalLookSeconds : _multi ? MultiHangSeconds : ScanHangSeconds))
                {
                    if (silent && slot.ScanIndex >= 0)
                        _skipUntil[slot.ScanIndex] = _scanClock + (slot.ScanHeardVoice ? AfterCallSkipSeconds : SilentDigitalSkipSeconds);
                    if (_multi)
                    {
                        // Only this slot: the others carry on, and the window is left when all are free.
                        ReleaseLocked(slot);
                        return false;
                    }
                    NextWindowLocked();
                }
                return open;
            default:
                return open;
        }
    }

    // ---- running --------------------------------------------------------------------------

    public void Start()
    {
        if (IsRunning) return;
        lock (_audioLock) _audio = _playAudio ? new AudioOut(ChannelReceiver.AudioRate, 2, _audioDevice) : null;
        try
        {
            _queue = new BlockingCollection<Block>(512);
            var slot = _slots[0];
            slot.OffsetHz = ScanPlan.SingleOffsetFor(slot.FrequencyHz);
            slot.Channel.SetOffset(slot.OffsetHz);
            _flushUntilRetuned = false;
            slot.ResetSquelch();
            IdleSlots();
            _source.Start(_rate, slot.FrequencyHz - slot.OffsetHz);
        }
        catch
        {
            lock (_audioLock)
            {
                _audio?.Dispose();
                _audio = null;
            }
            throw;
        }
        IsRunning = true;
        var queue = _queue;
        _thread = new Thread(() => Run(queue)) { IsBackground = true, Name = "receiver DSP", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _scan = ScanState.Off;
        _source.Stop();
        _queue?.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread?.Join(1000);
        _thread = null;
        lock (_audioLock)
        {
            _audio?.Dispose();
            _audio = null;
        }
        // Back on one channel, as after StopScan. The DSP thread has ended, or is stuck in a radio that does
        // not answer and may hold the lock: then the slots are left as they are rather than waited for.
        if (Monitor.TryEnter(_cfg, 200))
        {
            try { OneChannelLocked(fade: false); }
            finally { Monitor.Exit(_cfg); }
        }
        else IdleSlots();
        foreach (var s in _slots)
        {
            s.SquelchOpen = false;
            s.DigitalVoice.ClearStatus();
        }
        Volatile.Write(ref _level, -140);
    }

    // No slot is part of a scan, and only the first has a seat: the state a receiver that is not scanning is in.
    // Without the lock, for when it cannot be had (Stop) and when no DSP thread is running (Start).
    private void IdleSlots()
    {
        var slots = _slots;
        for (int k = 0; k < slots.Length; k++)
        {
            slots[k].State = SlotState.Free;
            slots[k].Quiet = false;
            if (k > 0) slots[k].Sound.Leave();
        }
        TakeCentreLocked(slots[0]);
    }

    // Stops, and gives back the decoders' memory. The receiver cannot be started again after this.
    public void Dispose()
    {
        var thread = _thread;
        _disposed = true;           // no more slots are made
        Stop();
        // The decoders belong to the DSP thread: free them only once it has ended. If it will not end
        // (or this is that thread), they are left: a small leak, not a call into freed memory.
        if (thread is not null && (thread == Thread.CurrentThread || !thread.Join(2000))) return;
        lock (_cfg)
            foreach (var s in _slots) s.Dispose();
    }

    // Blocks of samples thrown away because the DSP thread had fallen 512 blocks behind (since the start).
    public long DroppedBlocks => Interlocked.Read(ref _dropped);
    private long _dropped;

    // On the radio's thread: copy the block and hand it to the DSP thread.
    private void OnSamples(ReadOnlySpan<float> i, ReadOnlySpan<float> q, bool retuned)
    {
        var queue = _queue;
        if (queue is null || queue.IsAddingCompleted) return;
        int n = i.Length;
        float[] data = ArrayPool<float>.Shared.Rent(2 * n);
        i.CopyTo(data);
        q.CopyTo(data.AsSpan(n));
        // A block that finds the queue full is dropped. If it was the one that says the radio has
        // retuned, the next one says so in its place: lose that and the DSP thread, which throws
        // blocks away until it sees it, would wait for ever.
        retuned |= _retunedUnsaid;
        bool added = false;
        try { added = queue.TryAdd(new Block(data, n, retuned)); }
        catch (InvalidOperationException) { }
        _retunedUnsaid = retuned && !added;
        if (!added)
        {
            ArrayPool<float>.Shared.Return(data);
            Interlocked.Increment(ref _dropped);
        }
    }

    private void Run(BlockingCollection<Block> queue)
    {
        foreach (var block in queue.GetConsumingEnumerable())
        {
            try { Process(block); }
            finally { ArrayPool<float>.Shared.Return(block.Data); }
        }
    }

    private void Process(Block block)
    {
        SlotSound[] sounds;
        lock (_cfg)
        {
            var slots = _slots;
            // After a retune, blocks already queued are from the old frequency.
            if (block.Retuned)
            {
                _flushUntilRetuned = false;
                _flushedSeconds = 0;
                foreach (var s in slots) s.ResetSquelch();
            }
            else if (_flushUntilRetuned)
            {
                // The radio answers in milliseconds (and gives up by itself in half a second). If
                // word of the retune has not come by now it was lost: carry on rather than stall.
                _flushedSeconds += block.Count / _rate;
                if (_flushedSeconds < FlushLongestSeconds) return;
                _flushUntilRetuned = false;
                _flushedSeconds = 0;
                foreach (var s in slots) s.ResetSquelch();
            }

            int n = block.Count;
            double seconds = n / _rate;
            var i = block.Data.AsSpan(0, n);
            var q = block.Data.AsSpan(n, n);
            if (_scan != ScanState.Off) _scanClock += seconds;
            // While a window is looked over nothing is received; only a slot let go a moment ago is still fading out.
            bool survey = _scan == ScanState.Survey;
            if (survey) SurveyLocked(i, q);

            // Every slot that is trying or holding a channel; outside a scan the first. An idle slot costs nothing.
            int given = 0;
            bool metered = false;
            double best = -140;
            for (int k = 0; k < slots.Length; k++)
            {
                var slot = slots[k];
                if (survey ? slot.State != SlotState.Leaving : slot.State == SlotState.Free && (k != 0 || _scan != ScanState.Off)) continue;
                if (!ChannelLocked(slot, k, i, q, seconds, out double level)) continue;
                given++;
                if (slot.State == SlotState.Leaving) continue;
                best = metered ? Math.Max(best, level) : level;
                metered = true;
            }
            if (metered)
            {
                // The meter falls slowly and rises at once, like a scanner's bar. With several slots it shows the strongest.
                double shown = Volatile.Read(ref _level);
                Volatile.Write(ref _level, best > shown ? best : shown + 0.1 * (best - shown));
            }
            if (!survey && _multi && _scan == ScanState.Stay) WindowStepLocked(i, q, seconds);
            if (given == 0) return;

            // The slots may change places under the lock: the mixer gets them as they stood in this block.
            if (_mixSounds.Length != slots.Length) _mixSounds = new SlotSound[slots.Length];
            for (int k = 0; k < slots.Length; k++) _mixSounds[k] = slots[k].Sound;
            sounds = _mixSounds;
        }

        // The slots' sound, each through its fade to its place, as one block at the volume and pan.
        var a = _mixer.Mix(sounds, _volume, _pan);
        int count = a.Length / 2;
        if (count == 0) return;
        bool open = _mixer.AnyOpen;         // with several slots: any of them

        AudioOutput?.Invoke(a);
        lock (_audioLock)
        {
            var audio = _audio;
            if (audio is null) return;
            int queued = audio.Queued;
            if (queued < count) audio.Write(Silence);           // ran dry: rebuild the cushion first
            else if (queued > TooMuch && (!open || queued > 2 * TooMuch)) return;   // the radio's clock runs fast: shed some
            audio.Write(a);
        }
    }

    // One block of the radio's IQ through one slot: its channel, its level, its squelch, its part of a scan
    // and its digital voice. The audio it gives, if any, is handed to the mixer. False if the slot was not
    // worked after all (it had faded out, and is free).
    private bool ChannelLocked(MonitorSlot slot, int seat, ReadOnlySpan<float> i, ReadOnlySpan<float> q, double seconds, out double level)
    {
        level = -140;
        if (slot.State == SlotState.Leaving && slot.Sound.Gain == 0)
        {
            FreeLocked(slot);
            return false;
        }
        int max = slot.Channel.MaxAudio(i.Length);
        if (slot.Left.Length < max)
        {
            slot.Left = new float[max];
            slot.Right = new float[max];
        }
        int count = slot.Channel.Process(i, q, slot.Left, slot.Right);
        if (slot.State == SlotState.Leaving)
        {
            // Let go while it could be heard: its sound (decoded speech, if that is what it was) goes on under
            // a shut squelch for the few milliseconds the fade takes.
            if (count > 0) slot.DigitalVoice.Process(true, false, slot.Channel.Mode, slot.Left, slot.Right, count);
            slot.Sound.Give(slot.Left.AsSpan(0, count), slot.Right.AsSpan(0, count), false);
            return true;
        }
        level = slot.Channel.LevelDbfs - _source.GainDb + LevelOffsetDb;
        if (slot.Blocked) level = -140;
        slot.Level = level > slot.Level ? level : slot.Level + 0.1 * (level - slot.Level);
        bool open = slot.Squelch.Update(level, seconds, slot.Channel.Roughness, slot.Channel.RoughnessLimit) && !slot.Blocked;
        if (_scan != ScanState.Off && slot.State != SlotState.Free) open = ScanStepLocked(slot, seat, level, open, seconds);
        slot.SquelchOpen = open;
        // Is there P25, DMR, NXDN, D-STAR or System Fusion on the channel? If so its decoded speech takes the place of the analogue audio.
        // (A slot let go in this block while it could be heard keeps its speech for the fade.)
        if (count > 0)
            slot.DigitalVoice.Process(open || slot.State == SlotState.Leaving, _scan != ScanState.Off && slot.State is SlotState.Look or SlotState.Settle,
                slot.Channel.Mode, slot.Left, slot.Right, count);
        slot.Sound.Give(slot.Left.AsSpan(0, count), slot.Right.AsSpan(0, count), open);
        return true;
    }
}
