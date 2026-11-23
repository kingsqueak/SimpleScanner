namespace SimpleScanner.Dsp;

public enum Mode { Am, Nfm, Wfm }

/// <summary>A run of samples handed to a listener, valid only during the call.</summary>
public delegate void SamplesHandler(ReadOnlySpan<float> samples);

/// <summary>
/// One channel out of a wide IQ stream: shift it to centre, filter it down, demodulate it,
/// and give 48 kHz audio (left and right; the same in both except for stereo broadcast FM)
/// and the channel's level.
///
/// The input rate must be a multiple of 240 kHz. The first stage brings any such rate to
/// 240 kHz, wide enough for broadcast FM. Narrow modes go on to 48 kHz through the channel
/// filter and are demodulated there; broadcast FM is demodulated at 240 kHz and its audio
/// brought down to 48 kHz.
/// </summary>
public sealed class ChannelReceiver
{
    public const int AudioRate = 48000;
    private const int WideRate = 240000;

    private readonly double _inputRate;
    private readonly Mixer _mixer = new();
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
    private float[] _re = [], _im = [], _wRe = [], _wIm = [], _nRe = [], _nIm = [], _disc = [], _sub = [];

    public ChannelReceiver(double inputRate)
    {
        int factor = (int)Math.Round(inputRate / WideRate);
        if (factor < 1 || Math.Abs(factor * WideRate - inputRate) > 1)
            throw new ArgumentException($"The sample rate must be a multiple of {WideRate} Hz.", nameof(inputRate));
        _inputRate = inputRate;
        // Pass +-100 kHz; everything that would fold back into that after decimation is stopped.
        _toWide = new ComplexFirDecimator(
            factor == 1 ? [1f] : FirDesign.LowPass(inputRate, 120000, 40000), factor);
        Configure(Mode.Nfm, 11000);
    }

    public Mode Mode { get; private set; }
    public double BandwidthHz { get; private set; }

    /// <summary>The channel's power over the last block, in dB relative to full scale.</summary>
    public double LevelDbfs { get; private set; } = -140;

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
        _toWide.Reset();
        _am.Reset();
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

        int maxW = _toWide.MaxOutput(n);
        Ensure(ref _wRe, maxW);
        Ensure(ref _wIm, maxW);
        int w = _toWide.Process(_re.AsSpan(0, n), _im.AsSpan(0, n), _wRe, _wIm);
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
            else _am.Process(_nRe.AsSpan(0, count), _nIm.AsSpan(0, count), audio);
        }

        var a = audio[..count];
        foreach (var f in _highPass) f.Process(a);
        _deemph?.Process(a);
        foreach (var f in _lowPass) f.Process(a);
        for (int i = 0; i < a.Length; i++) a[i] = Math.Clamp(a[i] * _audioGain, -1f, 1f);
        a.CopyTo(right);
        return count;
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
