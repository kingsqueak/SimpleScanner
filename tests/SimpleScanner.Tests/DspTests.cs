using SimpleScanner.Dsp;

namespace SimpleScanner.Tests;

public class DspTests
{
    private const double Fs = 2_400_000;

    /// <summary>A carrier at offsetHz in a 2.4 MS/s stream (or fs), modulated by a tone, with a little noise.</summary>
    private static (float[] re, float[] im) Signal(double seconds, double offsetHz, double amplitude,
        Func<double, double> phaseDeviation, Func<double, double> envelope, double noise = 1e-4, double fs = Fs)
    {
        int n = (int)(seconds * fs);
        var re = new float[n];
        var im = new float[n];
        var rng = new Random(1);
        for (int i = 0; i < n; i++)
        {
            double t = i / fs;
            double ph = 2 * Math.PI * offsetHz * t + phaseDeviation(t);
            double a = amplitude * envelope(t);
            re[i] = (float)(a * Math.Cos(ph) + noise * (rng.NextDouble() - 0.5));
            im[i] = (float)(a * Math.Sin(ph) + noise * (rng.NextDouble() - 0.5));
        }
        return (re, im);
    }

    private static float[] Run(ChannelReceiver rx, float[] re, float[] im, int block = 4096) => RunStereo(rx, re, im, block).left;

    private static (float[] left, float[] right) RunStereo(ChannelReceiver rx, float[] re, float[] im, int block = 4096)
    {
        var left = new List<float>();
        var right = new List<float>();
        var l = new float[rx.MaxAudio(block)];
        var r = new float[rx.MaxAudio(block)];
        for (int i = 0; i < re.Length; i += block)
        {
            int n = Math.Min(block, re.Length - i);
            int got = rx.Process(re.AsSpan(i, n), im.AsSpan(i, n), l, r);
            left.AddRange(l.AsSpan(0, got).ToArray());
            right.AddRange(r.AsSpan(0, got).ToArray());
        }
        return (left.ToArray(), right.ToArray());
    }

    /// <summary>Amplitude of one frequency in the second half of the audio (after the filters settle).</summary>
    private static double Tone(float[] audio, double hz)
    {
        int start = audio.Length / 2, n = audio.Length - start;
        double c = 0, s = 0;
        for (int i = 0; i < n; i++)
        {
            double ph = 2 * Math.PI * hz * i / ChannelReceiver.AudioRate;
            c += audio[start + i] * Math.Cos(ph);
            s += audio[start + i] * Math.Sin(ph);
        }
        return 2 * Math.Sqrt(c * c + s * s) / n;
    }

    [Fact]
    public void LowPassHasUnityGainAtDcAndStopsTheStopband()
    {
        var taps = FirDesign.LowPass(240000, 5500, 2500);
        Assert.Equal(1.0, taps.Sum(t => (double)t), 4);
        double Gain(double hz)
        {
            double c = 0, s = 0;
            for (int i = 0; i < taps.Length; i++)
            {
                c += taps[i] * Math.Cos(2 * Math.PI * hz * i / 240000);
                s += taps[i] * Math.Sin(2 * Math.PI * hz * i / 240000);
            }
            return 20 * Math.Log10(Math.Sqrt(c * c + s * s));
        }
        Assert.InRange(Gain(3000), -0.5, 0.5);
        Assert.InRange(Gain(5500), -7, -5);
        Assert.True(Gain(7000) < -55, $"7 kHz: {Gain(7000):F1} dB");
        Assert.True(Gain(12500) < -55, $"12.5 kHz: {Gain(12500):F1} dB");
    }

    [Fact]
    public void DecimatorGivesTheSameOutputHoweverTheInputIsCut()
    {
        var taps = FirDesign.LowPass(240000, 8000, 4000);
        var rng = new Random(2);
        var x = Enumerable.Range(0, 20000).Select(_ => (float)rng.NextDouble()).ToArray();
        var whole = new float[5000];
        int nWhole = new FirDecimator(taps, 5).Process(x, whole);

        var cut = new FirDecimator(taps, 5);
        var pieces = new List<float>();
        var buf = new float[5000];
        for (int i = 0; i < x.Length;)
        {
            int n = Math.Min(rng.Next(1, 700), x.Length - i);
            int got = cut.Process(x.AsSpan(i, n), buf);
            pieces.AddRange(buf.AsSpan(0, got).ToArray());
            i += n;
        }
        Assert.Equal(nWhole, pieces.Count);
        for (int i = 0; i < nWhole; i++) Assert.Equal(whole[i], pieces[i], 5);
    }

    [Fact]
    public void NarrowFmRecoversATone()
    {
        // 1 kHz tone at 1.5 kHz deviation on a 2.5 kHz-deviation channel, 300 kHz above centre.
        var (re, im) = Signal(0.4, 300000, 0.1, t => 1500.0 / 1000 * Math.Sin(2 * Math.PI * 1000 * t), _ => 1);
        var rx = new ChannelReceiver(Fs);
        rx.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        rx.SetOffset(300000);
        var audio = Run(rx, re, im);
        Assert.InRange(audio.Length, 19000, 19300);
        // 0.6 of full deviation; de-emphasis from 300 Hz takes 1 kHz down to 0.29; gain 3.
        Assert.InRange(Tone(audio, 1000), 0.35, 0.65);
        Assert.True(Tone(audio, 2000) < 0.03);
        Assert.InRange(rx.LevelDbfs, -20.5, -19.5);
    }

    [Fact]
    public void NarrowFmRemovesACtcssTone()
    {
        var (re, im) = Signal(0.4, 300000, 0.1, t => 500.0 / 100 * Math.Sin(2 * Math.PI * 100 * t), _ => 1);
        var rx = new ChannelReceiver(Fs);
        rx.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        rx.SetOffset(300000);
        Assert.True(Tone(Run(rx, re, im), 100) < 0.02);
    }

    [Fact]
    public void AmRecoversAToneWhateverTheSignalLevel()
    {
        foreach (double level in new[] { 0.5, 0.001 })
        {
            var (re, im) = Signal(0.5, -250000, level, _ => 0, t => 1 + 0.5 * Math.Sin(2 * Math.PI * 1000 * t), noise: 0);
            var rx = new ChannelReceiver(Fs);
            rx.Configure(Mode.Am, BandPlan.AmAir);
            rx.SetOffset(-250000);
            // 50 % modulation, audio gain 1.2.
            Assert.InRange(Tone(Run(rx, re, im), 1000), 0.5, 0.7);
        }
    }

    [Fact]
    public void BroadcastFmRecoversATone()
    {
        var (re, im) = Signal(0.3, 300000, 0.1, t => 37500.0 / 1000 * Math.Sin(2 * Math.PI * 1000 * t), _ => 1);
        var rx = new ChannelReceiver(Fs);
        rx.Configure(Mode.Wfm, BandPlan.Wfm);
        rx.SetOffset(300000);
        // Half deviation, 75 us de-emphasis takes 1 kHz down by about 0.9 dB, gain 0.9.
        Assert.InRange(Tone(Run(rx, re, im), 1000), 0.36, 0.46);
    }

    /// <summary>A stereo broadcast: the composite of left, right and pilot, frequency-modulated at 75 kHz.</summary>
    private static (float[] re, float[] im) StereoBroadcast(double seconds, Func<double, double> left, Func<double, double> right, bool pilot = true)
    {
        int n = (int)(seconds * Fs);
        var re = new float[n];
        var im = new float[n];
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / Fs, l = left(t), r = right(t), w = 2 * Math.PI * 19000 * t;
            double composite = 0.45 * (l + r) + (pilot ? 0.09 * Math.Sin(w) + 0.45 * (l - r) * Math.Sin(2 * w) : 0);
            phase += 2 * Math.PI * (300000 + 75000 * composite) / Fs;
            re[i] = (float)(0.1 * Math.Cos(phase));
            im[i] = (float)(0.1 * Math.Sin(phase));
        }
        return (re, im);
    }

    [Fact]
    public void BroadcastStereoSeparatesLeftFromRight()
    {
        // 1 kHz on the left only, 2.5 kHz on the right only.
        var (re, im) = StereoBroadcast(0.6, t => Math.Sin(2 * Math.PI * 1000 * t), t => Math.Sin(2 * Math.PI * 2500 * t));
        var rx = new ChannelReceiver(Fs);
        rx.Configure(Mode.Wfm, BandPlan.Wfm);
        rx.SetOffset(300000);
        var (left, right) = RunStereo(rx, re, im);
        Assert.True(rx.Stereo);
        double l1 = Tone(left, 1000), l2 = Tone(left, 2500), r1 = Tone(right, 1000), r2 = Tone(right, 2500);
        Assert.InRange(l1, 0.5, 0.9);
        Assert.InRange(r2, 0.3, 0.9);
        Assert.True(20 * Math.Log10(l1 / r1) > 25, $"1 kHz leaks into the right at {20 * Math.Log10(r1 / l1):F1} dB");
        Assert.True(20 * Math.Log10(r2 / l2) > 25, $"2.5 kHz leaks into the left at {20 * Math.Log10(l2 / r2):F1} dB");
        Assert.True(Tone(left, 19000) < 0.01, "pilot in the audio");
    }

    [Fact]
    public void AMonoBroadcastStaysMono()
    {
        var (re, im) = StereoBroadcast(0.4, t => Math.Sin(2 * Math.PI * 1000 * t), t => Math.Sin(2 * Math.PI * 1000 * t), pilot: false);
        var rx = new ChannelReceiver(Fs);
        rx.Configure(Mode.Wfm, BandPlan.Wfm);
        rx.SetOffset(300000);
        var (left, right) = RunStereo(rx, re, im);
        Assert.False(rx.Stereo);
        Assert.Equal(left, right);
        Assert.InRange(Tone(left, 1000), 0.6, 0.85);
    }

    [Fact]
    public void ASignalInTheNextChannelIsRejected()
    {
        // A strong carrier 12.5 kHz away must not show in a narrow channel's level.
        var (re, im) = Signal(0.2, 312500, 0.5, _ => 0, _ => 1, noise: 0);
        var rx = new ChannelReceiver(Fs);
        rx.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        rx.SetOffset(300000);
        Run(rx, re, im);
        Assert.True(rx.LevelDbfs < -6 - 55, $"level {rx.LevelDbfs:F1} dBFS");
    }

    [Fact]
    public void TheRadiosRateIsBroughtToTheWideChannelInTwoStages()
    {
        // 7.68 MS/s: /8 to 960 kHz with a short filter, then /4 with the shape the single filter had.
        var stages = ChannelReceiver.WideStages(32);
        Assert.Equal([8, 4], stages.Select(s => s.Factor));
        Assert.Equal(43, stages[0].Taps.Length);
        Assert.Equal(FirDesign.LowPass(960000, 120000, 40000), stages[1].Taps);
        // A prime factor has no split, and 240 kHz is passed through.
        Assert.Equal([5], ChannelReceiver.WideStages(5).Select(s => s.Factor));
        Assert.Equal([1], ChannelReceiver.WideStages(1).Select(s => s.Factor));
    }

    /// <summary>A filter's gain in dB at hz, running at rate fs.</summary>
    private static double GainDb(float[] taps, double hz, double fs)
    {
        double c = 0, s = 0;
        for (int i = 0; i < taps.Length; i++)
        {
            c += taps[i] * Math.Cos(2 * Math.PI * hz * i / fs);
            s += taps[i] * Math.Sin(2 * Math.PI * hz * i / fs);
        }
        return 10 * Math.Log10(c * c + s * s + 1e-30);
    }

    [Fact]
    public void EveryRateGetsAFirstFilterAsGoodAsTheSingleOne()
    {
        for (int factor = 1; factor <= 40; factor++)
        {
            double fs = factor * 240000.0;
            var stages = ChannelReceiver.WideStages(factor);
            Assert.InRange(stages.Length, 1, 2);
            Assert.Equal(factor, stages.Aggregate(1, (p, s) => p * s.Factor));
            float[] single = factor == 1 ? [1f] : FirDesign.LowPass(fs, 120000, 40000);
            double Chain(double hz) => GainDb(stages[0].Taps, hz, fs)
                + (stages.Length == 2 ? GainDb(stages[1].Taps, hz, fs / stages[0].Factor) : 0);
            // +-100 kHz as flat as it was.
            for (double hz = 0; hz <= 100000; hz += 5000)
                Assert.True(Math.Abs(Chain(hz) - GainDb(single, hz, fs)) < 0.1, $"x{factor}: {hz / 1000} kHz");
            // Whatever folds into +-100 kHz at 240 kHz is stopped: 59 dB at the very edge of the
            // stopband (where the single filter has 59.3), 60 beyond it.
            for (int k = 1; k * 240000 - 100000 <= fs / 2; k++)
                for (double hz = k * 240000 - 100000; hz <= Math.Min(fs / 2, k * 240000 + 100000); hz += 2000)
                    Assert.True(Chain(hz) < (hz < 150000 ? -59 : -60), $"x{factor}: {hz / 1000} kHz at {Chain(hz):F1} dB");
        }
    }

    /// <summary>Where a broadcast FM channel is tuned in a stream of this rate: 300 kHz up where there is room.</summary>
    private static double WideTuning(double fs) => fs >= 960000 ? 300000 : 0;

    [Theory]
    [InlineData(7_680_000)]     // two stages, /8 then /4
    [InlineData(6_000_000)]     // /5 then /5
    [InlineData(2_400_000)]     // /5 then /2
    [InlineData(1_920_000)]     // /4 then /2
    [InlineData(1_200_000)]     // one
    [InlineData(240_000)]       // none
    public void ACarrierReadsTheSameLevelAnywhereInTheWideChannel(double fs)
    {
        double tune = WideTuning(fs);
        foreach (double off in new double[] { -100000, -75000, -40000, -10000, 0, 25000, 60000, 90000, 100000 })
        {
            var (re, im) = Signal(0.02, tune + off, 0.1, _ => 0, _ => 1, noise: 0, fs: fs);
            var rx = new ChannelReceiver(fs);
            rx.Configure(Mode.Wfm, BandPlan.Wfm);
            rx.SetOffset(tune);
            Run(rx, re, im);
            Assert.True(Math.Abs(rx.LevelDbfs + 20) < 0.1, $"{off / 1000} kHz: {rx.LevelDbfs:F3} dBFS");
        }
    }

    [Theory]
    [InlineData(7_680_000)]
    [InlineData(6_000_000)]
    [InlineData(2_400_000)]
    [InlineData(1_920_000)]
    [InlineData(1_200_000)]
    public void StrongSignalsThatWouldFoldOntoTheWideChannelAreRejected(double fs)
    {
        // 240 kHz away folds onto the tuned frequency itself; within 100 kHz of a multiple of the
        // first stage's rate is what that stage alone must stop.
        var stages = ChannelReceiver.WideStages((int)Math.Round(fs / 240000));
        var away = new List<double> { 150000, 180000, 240000, 300000, 340000 };
        if (stages.Length == 2)
        {
            double mid = fs / stages[0].Factor;
            for (int k = 1; k <= 2; k++)
                foreach (double d in new double[] { -100000, -60000, 0, 60000, 100000 })
                    away.Add(k * mid + d);
        }
        foreach (double d in away.Where(d => d <= fs / 2).SelectMany(d => new[] { d, -d }))
        {
            // The two half a distance either side of the middle, so both are well inside the stream.
            var (re, im) = Signal(0.02, d / 2, 0.5, _ => 0, _ => 1, noise: 0, fs: fs);
            var rx = new ChannelReceiver(fs);
            rx.Configure(Mode.Wfm, BandPlan.Wfm);
            rx.SetOffset(-d / 2);
            Run(rx, re, im);
            Assert.True(rx.LevelDbfs < -6 - 60, $"{d / 1000} kHz away: {rx.LevelDbfs:F1} dBFS");
        }
        // Right at the edge of the stopband (folding onto the band's edge) the single filter had
        // 59.3 dB; so does the second stage.
        foreach (double d in new double[] { 140000, 142500, 145000, -142500 })
        {
            var (re, im) = Signal(0.02, d / 2, 0.5, _ => 0, _ => 1, noise: 0, fs: fs);
            var rx = new ChannelReceiver(fs);
            rx.Configure(Mode.Wfm, BandPlan.Wfm);
            rx.SetOffset(-d / 2);
            Run(rx, re, im);
            Assert.True(rx.LevelDbfs < -6 - 59, $"{d / 1000} kHz away: {rx.LevelDbfs:F1} dBFS");
        }
    }

    [Fact]
    public void TheOutputDoesNotDependOnHowTheInputIsCut()
    {
        const double fs = 7_680_000;
        var (re, im) = Signal(0.1, 300000, 0.1, t => 37500.0 / 1000 * Math.Sin(2 * Math.PI * 1000 * t), _ => 1, noise: 0.01, fs: fs);
        var whole = new ChannelReceiver(fs);
        whole.Configure(Mode.Wfm, BandPlan.Wfm);
        whole.SetOffset(300000);
        var expected = Run(whole, re, im, 16384);

        var cut = new ChannelReceiver(fs);
        cut.Configure(Mode.Wfm, BandPlan.Wfm);
        cut.SetOffset(300000);
        var rng = new Random(3);
        var got = new List<float>();
        var l = new float[cut.MaxAudio(20000)];
        var r = new float[cut.MaxAudio(20000)];
        for (int i = 0; i < re.Length;)
        {
            int n = Math.Min(rng.Next(1, 20000), re.Length - i);
            int k = cut.Process(re.AsSpan(i, n), im.AsSpan(i, n), l, r);
            got.AddRange(l.AsSpan(0, k).ToArray());
            i += n;
        }
        Assert.Equal(expected.Length, got.Count);
        // The mixer's phasor is pulled back onto the unit circle once a block, which changes
        // the last bits; nothing else may differ.
        double worst = expected.Zip(got, (x, y) => Math.Abs(x - y)).Max();
        Assert.True(worst < 1e-4, $"differs by {worst:G3}");
    }

    [Fact]
    public void ConfigureClearsBothStagesOfTheFirstFilter()
    {
        const double fs = 7_680_000;
        var (re, im) = Signal(0.01, 300000, 0.5, _ => 0, _ => 1, noise: 0, fs: fs);
        var rx = new ChannelReceiver(fs);
        rx.Configure(Mode.Wfm, BandPlan.Wfm);
        rx.SetOffset(300000);
        Run(rx, re, im);
        Assert.InRange(rx.LevelDbfs, -6.2, -5.9);
        // After Configure, silence in gives silence out: nothing of the carrier is left in either stage.
        rx.Configure(Mode.Wfm, BandPlan.Wfm);
        var zeros = new float[2000];
        rx.Process(zeros, zeros, new float[rx.MaxAudio(2000)], new float[rx.MaxAudio(2000)]);
        Assert.Equal(-140, rx.LevelDbfs, 6);
    }

    [Theory]
    [InlineData(7_680_000)]
    [InlineData(1_920_000)]
    [InlineData(1_200_000)]
    [InlineData(240_000)]
    public void NarrowFmRecoversAToneAtAnyRate(double fs)
    {
        double tune = fs >= 960000 ? 300000 : 30000;
        var (re, im) = Signal(0.4, tune, 0.1, t => 1500.0 / 1000 * Math.Sin(2 * Math.PI * 1000 * t), _ => 1, fs: fs);
        var rx = new ChannelReceiver(fs);
        rx.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        rx.SetOffset(tune);
        var audio = Run(rx, re, im);
        Assert.InRange(audio.Length, 19000, 19300);
        Assert.InRange(Tone(audio, 1000), 0.35, 0.65);
        Assert.True(Tone(audio, 2000) < 0.03);
        Assert.InRange(rx.LevelDbfs, -20.5, -19.5);
    }

    [Fact]
    public void FftPutsAToneInItsBinAndKeepsTheEnergy()
    {
        var fft = new Fft(1024);
        var re = new float[1024];
        var im = new float[1024];
        for (int i = 0; i < 1024; i++)
        {
            re[i] = (float)(0.5 * Math.Cos(2 * Math.PI * -37 * i / 1024.0));
            im[i] = (float)(0.5 * Math.Sin(2 * Math.PI * -37 * i / 1024.0));
        }
        fft.Transform(re, im);
        double total = 0;
        for (int k = 0; k < 1024; k++) total += re[k] * re[k] + im[k] * im[k];
        double bin = re[1024 - 37] * re[1024 - 37] + im[1024 - 37] * im[1024 - 37];
        Assert.Equal(1024.0 * 1024 * 0.25, bin, 0);                 // (N A)^2, all of it in bin -37
        Assert.InRange(bin / total, 0.9999, 1.0001);
    }

    [Fact]
    public void SquelchOpensAndClosesWithoutChatter()
    {
        var sq = new Squelch { ThresholdDb = -90 };
        Assert.False(sq.Update(-80, 0.005));            // above, but not for long enough yet
        Assert.False(sq.Update(-80, 0.005));
        Assert.True(sq.Update(-80, 0.005));
        Assert.True(sq.Update(-91, 0.2));               // inside the hysteresis: stays open
        Assert.True(sq.Update(-100, 0.05));             // a short fade
        Assert.True(sq.Update(-80, 0.05));
        Assert.True(sq.Update(-100, 0.06));
        Assert.False(sq.Update(-100, 0.06));
        sq.Enabled = false;
        Assert.True(sq.Update(-140, 0.01));
    }

    [Fact]
    public void AutoSquelchStaysShutOnNoiseAboveTheLevel()
    {
        var sq = new Squelch { ThresholdDb = -90, Auto = true };
        for (int i = 0; i < 100; i++) Assert.False(sq.Update(-70, 0.005, roughness: 3000, roughLimit: 2000));
        Assert.True(sq.Noisy);
        Assert.False(sq.Update(-70, 0.005, 1700, 2000));        // under the limit, but not clean enough to open on
        Assert.False(sq.Update(-70, 0.005, 300, 2000));
        Assert.False(sq.Update(-70, 0.005, 300, 2000));
        Assert.True(sq.Update(-70, 0.005, 300, 2000));
        Assert.True(sq.Update(-70, 0.05, 1900, 2000));          // open: only over the limit counts against it
        Assert.True(sq.Update(-70, 0.05, 2600, 2000));          // a speech peak
        Assert.True(sq.Update(-70, 0.05, 300, 2000));
        Assert.True(sq.Update(-70, 0.06, 3000, 2000));          // the transmission ends and the noise is back
        Assert.False(sq.Update(-70, 0.06, 3000, 2000));
        Assert.False(sq.Update(-100, 0.1, 300, 2000));          // clean but under the level
        // No limit (broadcast FM), or Auto off: the level alone.
        sq.Reset();
        Assert.True(sq.Update(-70, 0.02, 3000, 0));
        sq.Reset();
        sq.Auto = false;
        Assert.True(sq.Update(-70, 0.02, 3000, 2000));
    }

    /// <summary>
    /// Gaussian noise across the stream with, in a channel 30 kHz up, a carrier that many dB over
    /// the noise in the channel's width (none for null), speech-like modulation on it; optionally
    /// impulses 120 times a second. Returns the lowest and highest roughness after the start.
    /// </summary>
    private static (double low, double high) Roughness(Mode mode, double bandwidthHz, double? carrierDb, bool impulses = false)
    {
        const double fs = 240000, sigma = 0.001;
        var rx = new ChannelReceiver(fs);
        rx.Configure(mode, bandwidthHz);
        rx.SetOffset(30000);
        var rng = new Random(11);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        double amp = carrierDb is { } db ? Math.Sqrt(2 * sigma * sigma * bandwidthHz / fs * Math.Pow(10, db / 10)) : 0;
        double deviation = mode == Mode.Nfm ? (bandwidthHz < 13000 ? 2500 : 5000) : 0, depth = mode == Mode.Am ? 0.8 : 0;
        const int block = 480;
        var re = new float[block];
        var im = new float[block];
        var l = new float[rx.MaxAudio(block)];
        var r = new float[rx.MaxAudio(block)];
        double ph = 0, tone = 0, low = double.MaxValue, high = 0;
        long t = 0;
        for (int b = 0; b < 500; b++)
        {
            for (int i = 0; i < block; i++, t++)
            {
                double sec = t / fs;
                // A tone wandering over 300 to 2700 Hz at syllable speed and loudness.
                tone += 2 * Math.PI * (1500 + 1200 * Math.Sin(2 * Math.PI * 3.1 * sec)) / fs;
                double m = Math.Sin(tone) * (0.55 + 0.45 * Math.Sin(2 * Math.PI * 4.3 * sec));
                ph += 2 * Math.PI * (30000 + deviation * m) / fs;
                double spike = impulses && t % 2000 == 0 ? 0.5 : 0;
                re[i] = (float)(amp * (1 + depth * m) * Math.Cos(ph) + sigma * Gauss() + spike);
                im[i] = (float)(amp * (1 + depth * m) * Math.Sin(ph) + sigma * Gauss() + 0.6 * spike);
            }
            rx.Process(re, im, l, r);
            if (b < 20) continue;
            low = Math.Min(low, rx.Roughness);
            high = Math.Max(high, rx.Roughness);
        }
        return (low, high);
    }

    [Theory]
    [InlineData(Mode.Nfm, BandPlan.NfmNarrow)]
    [InlineData(Mode.Nfm, BandPlan.NfmWide)]
    [InlineData(Mode.Am, BandPlan.AmAir833)]
    [InlineData(Mode.Am, BandPlan.AmAir)]
    public void RoughnessTellsNoiseFromASignalWhateverTheLevel(Mode mode, double bandwidthHz)
    {
        var rx = new ChannelReceiver(240000);
        rx.Configure(mode, bandwidthHz);
        double limit = rx.RoughnessLimit, clean = 0.75 * limit;
        Assert.True(limit > 0);

        // Noise alone, and noise with impulses, never comes down to where the squelch could open.
        Assert.True(Roughness(mode, bandwidthHz, null).low > clean);
        Assert.True(Roughness(mode, bandwidthHz, null, impulses: true).low > clean);
        // A weak signal with speech on it (10 dB over the noise) and a strong one never look like noise.
        Assert.True(Roughness(mode, bandwidthHz, 10).high < limit);
        Assert.True(Roughness(mode, bandwidthHz, 40).high < clean);
    }

    [Fact]
    public void BroadcastFmHasNoRoughnessTest()
    {
        var rx = new ChannelReceiver(240000);
        rx.Configure(Mode.Wfm, BandPlan.Wfm);
        Assert.Equal(0, rx.RoughnessLimit);
        Assert.Equal(0, rx.Roughness);
    }

    [Fact]
    public void BandPlanKnowsTheCommonBandsAndBlocksCellular()
    {
        Assert.Equal(Mode.Am, BandPlan.Find(121.5e6).Mode);
        Assert.Equal("NOAA weather", BandPlan.Find(162.55e6).Name);
        Assert.Equal(Mode.Am, BandPlan.Find(1030e3).Mode);
        Assert.Equal(Mode.Wfm, BandPlan.Find(98.1e6).Mode);
        Assert.Equal("2 m amateur", BandPlan.Find(146.52e6).Name);
        Assert.Equal("VHF public service", BandPlan.Find(155.475e6).Name);
        Assert.True(BandPlan.IsBlocked(880e6));
        Assert.True(BandPlan.IsBlocked(823.998e6, 11000));
        Assert.False(BandPlan.IsBlocked(855e6, 11000));
    }
}
