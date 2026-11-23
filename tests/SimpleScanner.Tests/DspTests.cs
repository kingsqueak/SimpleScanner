using SimpleScanner.Dsp;

namespace SimpleScanner.Tests;

public class DspTests
{
    private const double Fs = 2_400_000;

    /// <summary>A carrier at offsetHz in a 2.4 MS/s stream, modulated by a tone, with a little noise.</summary>
    private static (float[] re, float[] im) Signal(double seconds, double offsetHz, double amplitude,
        Func<double, double> phaseDeviation, Func<double, double> envelope, double noise = 1e-4)
    {
        int n = (int)(seconds * Fs);
        var re = new float[n];
        var im = new float[n];
        var rng = new Random(1);
        for (int i = 0; i < n; i++)
        {
            double t = i / Fs;
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
