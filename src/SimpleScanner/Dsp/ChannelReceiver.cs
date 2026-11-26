namespace SimpleScanner.Dsp;

public enum Mode { Am, Nfm, Wfm }

/// <summary>A run of samples handed to a listener, valid only during the call.</summary>
public delegate void SamplesHandler(ReadOnlySpan<float> samples);

/// <summary>
/// One channel out of a wide IQ stream: shift it to centre, filter it down, demodulate it,
/// and give 48 kHz audio (left and right; the same in both except for stereo broadcast FM)
/// and the channel's level.
///
/// The input rate must be a multiple of 240 kHz. The first filter brings any such rate to
/// 240 kHz, wide enough for broadcast FM, in two stages where that is cheaper (see
/// <see cref="WideStages"/>). Narrow modes go on to 48 kHz through the channel
/// filter and are demodulated there; broadcast FM is demodulated at 240 kHz and its audio
/// brought down to 48 kHz.
/// </summary>
public sealed class ChannelReceiver
{
    public const int AudioRate = 48000;
    private const int WideRate = 240000;

    private readonly double _inputRate;
    private readonly Mixer _mixer = new();
    private readonly ComplexFirDecimator? _toMid;      // the first of two stages to 240 kHz, where there are two
    private readonly ComplexFirDecimator _toWide;
    private ComplexFirDecimator? _toNarrow;
    private FirDecimator? _wfmAudio, _wfmDiff;
    private StereoPilot? _pilot;
    private Deemphasis? _deemphRight;
    private Biquad? _highPassRight;
    private FmDemod? _fm;
    private readonly AmDemod _am = new(AudioRate);
    private Deemphasis? _deemph;
    private Biquad[] _highPass = [], _lowPass = [];
    private float _audioGain = 1;
    private float[] _re = [], _im = [], _mRe = [], _mIm = [], _wRe = [], _wIm = [], _nRe = [], _nIm = [], _disc = [], _sub = [], _hf = [], _mag = [];
    private Biquad[] _roughPass = [];
    private double _roughPower, _roughMean, _roughScale = 1;
    private long _roughCount;

    public ChannelReceiver(double inputRate)
    {
        int factor = (int)Math.Round(inputRate / WideRate);
        if (factor < 1 || Math.Abs(factor * WideRate - inputRate) > 1)
            throw new ArgumentException($"The sample rate must be a multiple of {WideRate} Hz.", nameof(inputRate));
        _inputRate = inputRate;
        var stages = WideStages(factor);
        if (stages.Length == 2) _toMid = new ComplexFirDecimator(stages[0].Taps, stages[0].Factor);
        _toWide = new ComplexFirDecimator(stages[^1].Taps, stages[^1].Factor);
        Configure(Mode.Nfm, 11000);
    }

    /// <summary>
    /// The filters that take a rate of <paramref name="factor"/> times 240 kHz down to 240 kHz,
    /// first to last: one or two. Either way +-100 kHz is passed flat and everything that would
    /// fold back into it is stopped by 60 dB (59.4 right at the edge of the band, as before).
    ///
    /// One filter does it all at the input rate: -6 dB at 120 kHz, 40 kHz of transition. At
    /// 7.68 MS/s that is 697 taps, and its cost per input sample is much the same at any rate.
    /// Two are cheaper where the factor splits: the first brings the rate down to a few times
    /// 240 kHz with a short filter whose transition runs from 120 kHz up to 120 kHz short of
    /// that middle rate (only what lies within 120 kHz of a multiple of the middle rate folds
    /// onto the wanted band; what folds anywhere else is left to the second). The second is
    /// the single filter's shape at the middle rate. For 7.68 MS/s: 43 taps /8, then 89 taps /4.
    /// The split is the one with the fewest multiplications per input sample, and is used only
    /// where it costs no more than four fifths of the single filter.
    /// </summary>
    internal static (float[] Taps, int Factor)[] WideStages(int factor)
    {
        if (factor == 1) return [([1f], 1)];
        double fs = (double)factor * WideRate;
        var single = FirDesign.LowPass(fs, PassHz + EdgeHz, 2 * EdgeHz);
        double singleCost = (single.Length + PerOutputCost) / factor, bestCost = double.MaxValue;
        (float[] Taps, int Factor)[] best = [(single, factor)];
        for (int second = 2; second <= factor / 2; second++)
        {
            if (factor % second != 0) continue;
            int first = factor / second;
            double mid = (double)second * WideRate;
            var t1 = FirDesign.LowPass(fs, mid / 2, mid - 2 * (PassHz + EdgeHz), FirstStageAttenDb);
            var t2 = FirDesign.LowPass(mid, PassHz + EdgeHz, 2 * EdgeHz);
            double cost = (t1.Length + PerOutputCost) / first + (t2.Length + PerOutputCost) / factor;
            if (cost < bestCost && cost < 0.8 * singleCost)
            {
                bestCost = cost;
                best = [(t1, first), (t2, second)];
            }
        }
        return best;
    }

    private const double PassHz = 100000, EdgeHz = 20000;
    // The Kaiser design's estimate falls a little short of its target at some lengths; this
    // keeps the first filter's stopband past 60 dB at every split.
    private const double FirstStageAttenDb = 63;
    // What one output costs beyond its multiplications, in multiplications: the call, the
    // vector sum and the odd taps at the end.
    private const double PerOutputCost = 8;

    public Mode Mode { get; private set; }
    public double BandwidthHz { get; private set; }

    /// <summary>The channel's power over the last block, in dB relative to full scale.</summary>
    public double LevelDbfs { get; private set; } = -140;

    /// <summary>
    /// How rough the demodulated signal is above the frequencies a transmission can put there,
    /// averaged over the last few milliseconds: what a noise squelch goes by. In narrow FM it is
    /// the discriminator's output above the channel's edge, in Hz RMS: a carrier quiets it and
    /// noise alone, however strong, does not. In AM it is the same part of the envelope, as a
    /// fraction of the carrier: a carrier's envelope moves only as fast as its modulation, the
    /// envelope of noise as fast as the filter lets it. Zero in broadcast FM, where it is not measured.
    /// </summary>
    public double Roughness => Mode switch
    {
        Mode.Nfm => Math.Sqrt(_roughPower) * _roughScale,
        Mode.Am => _roughMean > 1e-12 ? Math.Sqrt(_roughPower) / _roughMean : 0,
        _ => 0,
    };

    /// <summary>
    /// The most <see cref="Roughness"/> a signal worth hearing has; noise alone is well above it.
    /// Zero where roughness is not measured. Set from generated signals: noise alone reads about
    /// 3200 Hz in the 11 kHz FM filter and 3900 in the 16 kHz one, a carrier 6 dB over the noise
    /// with speech on it under 1300 and 1900; in AM noise reads 0.2 or more (far more when it is
    /// impulses) and a carrier 6 dB over it about 0.06, with speech peaks to 0.14.
    /// </summary>
    public double RoughnessLimit => Mode switch
    {
        Mode.Nfm => 600 + 0.13 * BandwidthHz,
        Mode.Am => 0.16,
        _ => 0,
    };

    /// <summary>Start the roughness average again: the channel has moved.</summary>
    public void ResetRoughness()
    {
        _roughCount = 0;
        _roughPower = _roughMean = 0;
        foreach (var f in _roughPass) f.Reset();
    }

    /// <summary>
    /// In narrow FM, the discriminator's output at 48 kHz before any audio filtering: what a
    /// digital voice decoder wants. <see cref="DiscriminatorHzPerUnit"/> gives its scale.
    /// </summary>
    public SamplesHandler? DiscriminatorTap { get; set; }

    /// <summary>The frequency deviation that reads 1.0 at the discriminator tap.</summary>
    public double DiscriminatorHzPerUnit => BandwidthHz < 13000 ? 2500 : 5000;

    /// <summary>A broadcast FM station's stereo pilot is being followed: left and right differ.</summary>
    public bool Stereo => Mode == Mode.Wfm && StereoEnabled && _pilot is { Locked: true };

    /// <summary>Decode stereo when a broadcast station sends it. Off gives mono, quieter on a weak station.</summary>
    public bool StereoEnabled { get; set; } = true;

    /// <summary>Where the wanted channel sits, in Hz from the centre of the input.</summary>
    public void SetOffset(double offsetHz) => _mixer.SetShift(-offsetHz, _inputRate);

    /// <summary>
    /// Choose the mode and the channel filter's full width in Hz. For FM the width also sets
    /// the deviation expected: under 13 kHz is 2.5 kHz deviation, otherwise 5 kHz.
    /// </summary>
    public void Configure(Mode mode, double bandwidthHz)
    {
        Mode = mode;
        BandwidthHz = bandwidthHz;
        _toMid?.Reset();
        _toWide.Reset();
        _am.Reset();
        // Above the edge of the channel filter there is nothing of a transmission left.
        _roughPass = mode == Mode.Wfm ? [] : Biquad.Butterworth4(AudioRate, RoughCornerHz(mode, bandwidthHz), highPass: true);
        _roughScale = mode == Mode.Nfm ? (bandwidthHz < 13000 ? 2500 : 5000) : 1;
        ResetRoughness();
        switch (mode)
        {
            case Mode.Wfm:
                _toNarrow = null;
                _fm = new FmDemod(WideRate, 75000);
                // 15 kHz audio; the 19 kHz pilot is well down. The same filter on the sum and the
                // difference keeps the two in step, which the stereo separation depends on.
                var audioTaps = FirDesign.LowPass(WideRate, 16500, 5000);
                _wfmAudio = new FirDecimator(audioTaps, WideRate / AudioRate);
                _wfmDiff = new FirDecimator(audioTaps, WideRate / AudioRate);
                _pilot = new StereoPilot(WideRate);
                _deemph = new Deemphasis(AudioRate, 75e-6);
                _deemphRight = new Deemphasis(AudioRate, 75e-6);
                _highPass = [Biquad.HighPass(AudioRate, 30, 0.7071)];              // a station off tune is a DC offset
                _highPassRight = Biquad.HighPass(AudioRate, 30, 0.7071);
                _lowPass = [];
                _audioGain = 0.9f;
                break;
            case Mode.Nfm:
                _toNarrow = NarrowFilter(bandwidthHz);
                _wfmAudio = _wfmDiff = null;
                _pilot = null;
                _fm = new FmDemod(AudioRate, bandwidthHz < 13000 ? 2500 : 5000);
                // Land-mobile and amateur FM is pre-emphasised 6 dB per octave from 300 Hz.
                _deemph = new Deemphasis(AudioRate, 1 / (2 * Math.PI * 300));
                _highPass = Biquad.Butterworth4(AudioRate, 300, highPass: true);   // takes out CTCSS tones
                _lowPass = Biquad.Butterworth4(AudioRate, 3000, highPass: false);
                _audioGain = 3f;    // makes up what the de-emphasis takes off speech frequencies
                break;
            default:
                _toNarrow = NarrowFilter(bandwidthHz);
                _wfmAudio = _wfmDiff = null;
                _pilot = null;
                _fm = null;
                _deemph = null;
                _highPass = [Biquad.HighPass(AudioRate, 200, 0.7071)];
                _lowPass = Biquad.Butterworth4(AudioRate, Math.Min(3400, bandwidthHz / 2), highPass: false);
                _audioGain = 1.2f;
                break;
        }
    }

    private static ComplexFirDecimator NarrowFilter(double bandwidthHz) =>
        new(FirDesign.LowPass(WideRate, bandwidthHz / 2, Math.Max(2000, bandwidthHz * 0.2)), WideRate / AudioRate);

    /// <summary>The most audio samples a block of this many input samples can give.</summary>
    public int MaxAudio(int inputCount) => inputCount / (int)(_inputRate / AudioRate) + 8;

    /// <summary>Process a block of IQ. Returns the number of audio samples written to each of left and right.</summary>
    public int Process(ReadOnlySpan<float> inRe, ReadOnlySpan<float> inIm, Span<float> audio, Span<float> right)
    {
        int n = inRe.Length;
        Ensure(ref _re, n);
        Ensure(ref _im, n);
        inRe.CopyTo(_re);
        inIm.CopyTo(_im);
        _mixer.Process(_re.AsSpan(0, n), _im.AsSpan(0, n));

        ReadOnlySpan<float> xRe = _re.AsSpan(0, n), xIm = _im.AsSpan(0, n);
        if (_toMid is not null)
        {
            int maxM = _toMid.MaxOutput(n);
            Ensure(ref _mRe, maxM);
            Ensure(ref _mIm, maxM);
            int m = _toMid.Process(xRe, xIm, _mRe, _mIm);
            xRe = _mRe.AsSpan(0, m);
            xIm = _mIm.AsSpan(0, m);
        }
        int maxW = _toWide.MaxOutput(xRe.Length);
        Ensure(ref _wRe, maxW);
        Ensure(ref _wIm, maxW);
        int w = _toWide.Process(xRe, xIm, _wRe, _wIm);
        if (w == 0) return 0;

        int count;
        if (Mode == Mode.Wfm)
        {
            Measure(_wRe.AsSpan(0, w), _wIm.AsSpan(0, w));
            Ensure(ref _disc, w);
            _fm!.Process(_wRe.AsSpan(0, w), _wIm.AsSpan(0, w), _disc);
            Ensure(ref _sub, w);
            _pilot!.Process(_disc.AsSpan(0, w), _sub);
            count = _wfmAudio!.Process(_disc.AsSpan(0, w), audio);
            _wfmDiff!.Process(_sub.AsSpan(0, w), right);
            if (Stereo)
            {
                var l = audio[..count];
                var r = right[..count];
                for (int i = 0; i < count; i++)
                {
                    float sum = l[i], diff = r[i];
                    l[i] = sum + diff;
                    r[i] = sum - diff;
                }
                _highPassRight!.Process(r);
                _deemphRight!.Process(r);
                foreach (var f in _highPass) f.Process(l);
                _deemph!.Process(l);
                for (int i = 0; i < count; i++)
                {
                    l[i] = Math.Clamp(l[i] * _audioGain, -1f, 1f);
                    r[i] = Math.Clamp(r[i] * _audioGain, -1f, 1f);
                }
                return count;
            }
        }
        else
        {
            int maxN = _toNarrow!.MaxOutput(w);
            Ensure(ref _nRe, maxN);
            Ensure(ref _nIm, maxN);
            count = _toNarrow.Process(_wRe.AsSpan(0, w), _wIm.AsSpan(0, w), _nRe, _nIm);
            if (count == 0) return 0;
            Measure(_nRe.AsSpan(0, count), _nIm.AsSpan(0, count));
            if (Mode == Mode.Nfm)
            {
                _fm!.Process(_nRe.AsSpan(0, count), _nIm.AsSpan(0, count), audio);
                DiscriminatorTap?.Invoke(audio[..count]);
            }
            else
            {
                _am.Process(_nRe.AsSpan(0, count), _nIm.AsSpan(0, count), audio);
                Ensure(ref _mag, count);
                for (int i = 0; i < count; i++) _mag[i] = MathF.Sqrt(_nRe[i] * _nRe[i] + _nIm[i] * _nIm[i]);
            }
            MeasureRoughness(Mode == Mode.Nfm ? audio[..count] : _mag.AsSpan(0, count), Mode == Mode.Am);
        }

        var a = audio[..count];
        foreach (var f in _highPass) f.Process(a);
        _deemph?.Process(a);
        foreach (var f in _lowPass) f.Process(a);
        for (int i = 0; i < a.Length; i++) a[i] = Math.Clamp(a[i] * _audioGain, -1f, 1f);
        a.CopyTo(right);
        return count;
    }

    /// <summary>Where the roughness measure starts: above the audio, and above the edge of the channel filter.</summary>
    public static double RoughCornerHz(Mode mode, double bandwidthHz) =>
        Math.Clamp(bandwidthHz * 0.55, 4000, 12000);

    private const int RoughSpan = AudioRate * 8 / 1000;

    // FM: give it the discriminator's output. AM: the envelope, which is measured against its
    // own mean over the same few milliseconds, so that a burst of impulses cannot pass for a carrier.
    private void MeasureRoughness(ReadOnlySpan<float> x, bool envelope)
    {
        int n = x.Length;
        Ensure(ref _hf, n);
        var h = _hf.AsSpan(0, n);
        x.CopyTo(h);
        double mean = _roughMean, p = _roughPower;
        long k = _roughCount, k0 = k;
        if (envelope)
            for (int i = 0; i < n; i++)
            {
                if (k0 < RoughSpan) k0++;
                mean += (h[i] - mean) / k0;
            }
        foreach (var f in _roughPass) f.Process(h);
        // A plain mean to begin with, so the figure is right from the first samples after a
        // reset, then a running one.
        for (int i = 0; i < n; i++)
        {
            if (k < RoughSpan) k++;
            p += (h[i] * h[i] - p) / k;
        }
        _roughMean = mean;
        _roughPower = p;
        _roughCount = k;
    }

    private void Measure(ReadOnlySpan<float> re, ReadOnlySpan<float> im)
    {
        double sum = 0;
        for (int i = 0; i < re.Length; i++) sum += re[i] * re[i] + im[i] * im[i];
        LevelDbfs = 10 * Math.Log10(sum / re.Length + 1e-14);
    }

    private static void Ensure(ref float[] buf, int n)
    {
        if (buf.Length < n) buf = new float[Math.Max(n, 2 * buf.Length)];
    }
}
