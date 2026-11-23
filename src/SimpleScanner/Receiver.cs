using System.Buffers;
using System.Collections.Concurrent;
using SimpleScanner.Audio;
using SimpleScanner.Dsp;
using SimpleScanner.Radio;

namespace SimpleScanner;

/// <summary>A channel to visit in a scan.</summary>
public sealed record ScanChannel(string Name, double FrequencyHz, Mode Mode, double BandwidthHz);

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
/// </summary>
public sealed class Receiver : IDisposable
{
    /// <summary>IQ rate asked of the radio: a multiple of 240 kHz, 12-bit on an RSPdx.</summary>
    public const double DefaultSampleRate = 7_680_000;
    private const int Cushion = ChannelReceiver.AudioRate * 60 / 1000;      // audio kept queued against jitter
    private const int TooMuch = ChannelReceiver.AudioRate * 250 / 1000;
    private const double SettleSeconds = 0.006, LookSeconds = 0.012, LookLongestSeconds = 0.05;
    private const double SurveySettleSeconds = 0.003;
    private const int SurveyFrames = 8;

    private enum ScanState { Off, Survey, Settle, Look, Listen }
    private readonly record struct Block(float[] Data, int Count, bool Retuned);

    private readonly IIqSource _source;
    private readonly bool _playAudio;
    private readonly double _rate;
    private readonly object _cfg = new(), _audioLock = new();
    private readonly ChannelReceiver _channel;
    private readonly Squelch _squelch = new();
    private BlockingCollection<Block>? _queue;
    private Thread? _thread;
    private AudioOut? _audio;
    private string? _audioDevice;
    private double _frequencyHz = 162_400_000, _offsetHz = ScanPlan.SingleOffsetHz, _volume = 0.5, _level = -140;
    private bool _blocked, _squelchOpen;
    private volatile bool _flushUntilRetuned;
    private float _gain;
    private float[] _left = [], _right = [], _frames = [];
    private static readonly float[] Silence = new float[2 * Cushion];

    // Scan state, all under _cfg.
    private IReadOnlyList<ScanChannel> _scanList = [];
    private List<ScanWindow> _plan = [];
    private double[] _skipUntil = [];
    private readonly Queue<int> _candidates = new();
    private ScanState _scan;
    private int _window, _scanIndex = -1, _surveySkip, _fftFill, _surveyCount;
    private double _scanTimer, _scanClock;
    private bool _scanSawSignal;
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
        _channel = new ChannelReceiver(sampleRate);
        _source.Samples += OnSamples;
        _source.Failed += why =>
        {
            Stop();
            Failed?.Invoke(why);
        };
        _channel.SetOffset(_offsetHz);

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
    public bool SquelchOpen => _squelchOpen;
    /// <summary>True on a frequency the scanner refuses to receive (cellular telephone bands).</summary>
    public bool Blocked => _blocked;
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

    public double Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 1);
    }

    public double SquelchDb
    {
        get => _squelch.ThresholdDb;
        set => _squelch.ThresholdDb = value;
    }

    public bool SquelchEnabled
    {
        get => _squelch.Enabled;
        set => _squelch.Enabled = value;
    }

    /// <summary>A broadcast FM station is being received in stereo.</summary>
    public bool Stereo => _channel.Stereo;

    public bool StereoEnabled
    {
        get => _channel.StereoEnabled;
        set => _channel.StereoEnabled = value;
    }

    public Mode Mode => _channel.Mode;
    public double BandwidthHz => _channel.BandwidthHz;

    /// <summary>Choosing a mode by hand ends a scan.</summary>
    public void SetMode(Mode mode, double bandwidthHz)
    {
        lock (_cfg)
        {
            if (mode == _channel.Mode && bandwidthHz == _channel.BandwidthHz) return;
            _scan = ScanState.Off;
            ConfigureLocked(mode, bandwidthHz);
            // A wider filter may no longer fit where the channel sat in the span.
            if (IsRunning && !ScanPlan.Fits(_offsetHz, bandwidthHz, _source.UsableBandwidthHz)) TuneLocked(_frequencyHz);
        }
    }

    private void ConfigureLocked(Mode mode, double bandwidthHz)
    {
        if (mode == _channel.Mode && bandwidthHz == _channel.BandwidthHz) return;
        _channel.Configure(mode, bandwidthHz);
        _blocked = BandPlan.IsBlocked(_frequencyHz, bandwidthHz);
        _squelch.Reset();
    }

    /// <summary>Tuning by hand ends a scan.</summary>
    public double FrequencyHz
    {
        get => _frequencyHz;
        set
        {
            lock (_cfg)
            {
                if (value == _frequencyHz) return;
                _scan = ScanState.Off;
                TuneLocked(value);
            }
        }
    }

    private void TuneLocked(double hz)
    {
        _frequencyHz = hz;
        _blocked = BandPlan.IsBlocked(hz, _channel.BandwidthHz);
        _squelch.Reset();
        double offset = hz - _source.CenterHz;
        if (IsRunning && ScanPlan.Fits(offset, _channel.BandwidthHz, _source.UsableBandwidthHz))
        {
            _offsetHz = offset;
        }
        else
        {
            _offsetHz = ScanPlan.SingleOffsetHz;
            if (IsRunning) _flushUntilRetuned = true;
            _source.Tune(hz - _offsetHz);
        }
        _channel.SetOffset(_offsetHz);
    }

    // ---- scanning -------------------------------------------------------------------------

    /// <summary>How long a channel must stay quiet before the scan moves on from it.</summary>
    public double ScanHangSeconds { get; set; } = 2;
    /// <summary>How long a skipped channel is passed by.</summary>
    public double ScanSkipSeconds { get; set; } = 10;
    public bool Scanning => _scan != ScanState.Off;
    /// <summary>The scan is stopped on an active channel.</summary>
    public bool ScanListening => _scan == ScanState.Listen;
    /// <summary>Times the scan has been through its whole list.</summary>
    public long ScanPasses => Interlocked.Read(ref _passes);
    /// <summary>Tunings of the radio one pass of the scan takes.</summary>
    public int ScanWindows => _plan.Count;

    /// <summary>The channel the scan is listening to or trying, or null when it is between channels or off.</summary>
    public ScanChannel? ScanChannel
    {
        get
        {
            lock (_cfg)
                return _scan is ScanState.Off or ScanState.Survey || _scanIndex < 0 || _scanIndex >= _scanList.Count
                    ? null : _scanList[_scanIndex];
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
            _scan = ScanState.Off;
            if (!IsRunning) return;
            _scanList = channels.Where(c => !BandPlan.IsBlocked(c.FrequencyHz, c.BandwidthHz)).OrderBy(c => c.FrequencyHz).ToList();
            if (_scanList.Count == 0) return;
            _plan = ScanPlan.Build(_scanList, _source.UsableBandwidthHz);
            _skipUntil = new double[_scanList.Count];
            _acc = new double[_scanList.Count];
            _squelch.Enabled = true;
            _scanClock = 0;
            Interlocked.Exchange(ref _passes, 0);
            _window = -1;
            NextWindowLocked();
        }
    }

    /// <summary>Stop scanning and stay on the present channel.</summary>
    public void StopScan()
    {
        lock (_cfg) _scan = ScanState.Off;
    }

    /// <summary>Leave the channel the scan has stopped on, and pass it by for <see cref="ScanSkipSeconds"/>.</summary>
    public void SkipChannel()
    {
        lock (_cfg)
        {
            if (_scan is ScanState.Off or ScanState.Survey) return;
            if (_scanIndex >= 0) _skipUntil[_scanIndex] = _scanClock + ScanSkipSeconds;
            NextCandidateLocked();
        }
    }

    private void NextWindowLocked()
    {
        _window = (_window + 1) % _plan.Count;
        if (_window == 0) Interlocked.Increment(ref _passes);
        var w = _plan[_window];
        _surveySkip = 0;
        if (Math.Abs(_source.CenterHz - w.CenterHz) > 1)
        {
            _flushUntilRetuned = true;
            _source.Tune(w.CenterHz);
            _surveySkip = (int)(SurveySettleSeconds * _rate);
        }
        _scan = ScanState.Survey;
        _scanIndex = -1;
        _fftFill = 0;
        _surveyCount = 0;
        foreach (int c in w.Channels) _acc[c] = 0;
        _squelchOpen = false;
    }

    // Look at every channel of the window at once: a few FFTs of the whole span, and the power
    // in each channel's bins.
    private void SurveyLocked(ReadOnlySpan<float> re, ReadOnlySpan<float> im)
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
            if (++_surveyCount < SurveyFrames) continue;

            // Which channels are above the squelch level? Strongest first.
            double gain = _source.GainDb, best = -140;
            var found = new List<(double level, int index)>();
            foreach (int c in w.Channels)
            {
                double level = 10 * Math.Log10(_acc[c] / (SurveyFrames * _fftNorm) + 1e-14) - gain + LevelOffsetDb;
                best = Math.Max(best, level);
                if (level >= _squelch.ThresholdDb && _scanClock >= _skipUntil[c]) found.Add((level, c));
            }
            double shown = Volatile.Read(ref _level);
            Volatile.Write(ref _level, best > shown ? best : shown + 0.3 * (best - shown));
            _candidates.Clear();
            foreach (var (_, index) in found.OrderByDescending(f => f.level)) _candidates.Enqueue(index);
            NextCandidateLocked();
            return;
        }
    }

    private void NextCandidateLocked()
    {
        if (_candidates.Count == 0)
        {
            NextWindowLocked();
            return;
        }
        _scanIndex = _candidates.Dequeue();
        var ch = _scanList[_scanIndex];
        ConfigureLocked(ch.Mode, ch.BandwidthHz);
        // Inside the window already: only the channel filter moves.
        _frequencyHz = ch.FrequencyHz;
        _blocked = false;
        _offsetHz = ch.FrequencyHz - _source.CenterHz;
        _channel.SetOffset(_offsetHz);
        _squelch.Reset();
        _scan = ScanState.Settle;
        _scanTimer = 0;
        _scanSawSignal = false;
    }

    /// <summary>One block's worth of listening to a scan candidate. Returns whether the audio may pass.</summary>
    private bool ScanStepLocked(double level, bool open, double seconds)
    {
        _scanTimer += seconds;
        switch (_scan)
        {
            case ScanState.Settle:
                // The filters still hold the last channel.
                if (_scanTimer >= SettleSeconds)
                {
                    _scan = ScanState.Look;
                    _scanTimer = 0;
                    _squelch.Reset();
                }
                return false;
            case ScanState.Look:
                if (level >= _squelch.ThresholdDb) _scanSawSignal = true;
                if (open)
                {
                    _scan = ScanState.Listen;
                    _scanTimer = 0;
                    return true;
                }
                // Nothing there after all: move on. Something there: give the squelch time to make up its mind.
                if (_scanTimer >= (_scanSawSignal ? LookLongestSeconds : LookSeconds)) NextCandidateLocked();
                return false;
            case ScanState.Listen:
                if (open) _scanTimer = 0;
                else if (_scanTimer >= ScanHangSeconds) NextWindowLocked();
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
            _offsetHz = ScanPlan.SingleOffsetHz;
            _channel.SetOffset(_offsetHz);
            _flushUntilRetuned = false;
            _squelch.Reset();
            _source.Start(_rate, _frequencyHz - _offsetHz);
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
        _squelchOpen = false;
        Volatile.Write(ref _level, -140);
    }

    public void Dispose() => Stop();

    // On the radio's thread: copy the block and hand it to the DSP thread.
    private void OnSamples(ReadOnlySpan<float> i, ReadOnlySpan<float> q, bool retuned)
    {
        var queue = _queue;
        if (queue is null || queue.IsAddingCompleted) return;
        int n = i.Length;
        float[] data = ArrayPool<float>.Shared.Rent(2 * n);
        i.CopyTo(data);
        q.CopyTo(data.AsSpan(n));
        try
        {
            if (!queue.TryAdd(new Block(data, n, retuned))) ArrayPool<float>.Shared.Return(data);
        }
        catch (InvalidOperationException) { ArrayPool<float>.Shared.Return(data); }
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
        int count;
        bool open;
        lock (_cfg)
        {
            // After a retune, blocks already queued are from the old frequency.
            if (block.Retuned)
            {
                _flushUntilRetuned = false;
                _squelch.Reset();
            }
            else if (_flushUntilRetuned) return;

            int n = block.Count;
            double seconds = n / _rate;
            if (_scan != ScanState.Off) _scanClock += seconds;
            if (_scan == ScanState.Survey)
            {
                SurveyLocked(block.Data.AsSpan(0, n), block.Data.AsSpan(n, n));
                return;
            }

            int max = _channel.MaxAudio(n);
            if (_left.Length < max)
            {
                _left = new float[max];
                _right = new float[max];
                _frames = new float[2 * max];
            }
            count = _channel.Process(block.Data.AsSpan(0, n), block.Data.AsSpan(n, n), _left, _right);
            double level = _channel.LevelDbfs - _source.GainDb + LevelOffsetDb;
            if (_blocked) level = -140;
            // The meter falls slowly and rises at once, like a scanner's bar.
            double shown = Volatile.Read(ref _level);
            Volatile.Write(ref _level, level > shown ? level : shown + 0.1 * (level - shown));
            open = _squelch.Update(level, seconds) && !_blocked;
            if (_scan != ScanState.Off) open = ScanStepLocked(level, open, seconds);
            _squelchOpen = open;
        }
        if (count == 0) return;

        // Fade in and out over a few milliseconds so the squelch does not click.
        var a = _frames.AsSpan(0, 2 * count);
        float target = open ? (float)(_volume * _volume) : 0f, g = _gain;
        const float step = 1f / 240;
        for (int k = 0; k < count; k++)
        {
            g += Math.Clamp(target - g, -step, step);
            a[2 * k] = _left[k] * g;
            a[2 * k + 1] = _right[k] * g;
        }
        _gain = g;

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
}
