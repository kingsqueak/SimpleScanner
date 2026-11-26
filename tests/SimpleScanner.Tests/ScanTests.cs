using SimpleScanner.Dsp;
using SimpleScanner.Radio;

namespace SimpleScanner.Tests;

/// <summary>
/// One more signal of the pretend radio. It can be switched on and off, moved and made stronger or weaker
/// while the radio runs. Plain, or with a tone on it (amplitude modulation, half depth), or frequency
/// modulated with a recording of a discriminator (see Data/README.md), which is repeated.
/// </summary>
internal sealed class FakeCarrier
{
    public double Hz;
    public volatile bool On = true;
    public double Level = 0.05;             // its amplitude; full scale is 1
    public double ToneHz;                   // 0 for none
    public float[]? Deviation;              // at 24 kHz, in units of DeviationHz
    public double DeviationHz = 2500;

    // The generator's: where the carrier and the tone are in their turn, and the place in the recording.
    internal double Re = 1, Im, ToneRe = 1, ToneIm;
    internal int At, Part;
}

/// <summary>
/// A pretend radio: noise, plus one carrier on a chosen frequency (<see cref="CarrierHz"/>) and any number
/// more, each with its own level and switch (<see cref="Add"/>), delivered at real speed.
/// </summary>
internal sealed class FakeSource : IIqSource
{
    private volatile FakeCarrier[] _carriers = [];

    /// <summary>One more carrier, on from now.</summary>
    public FakeCarrier Add(double hz, double level = 0.05, double toneHz = 0, float[]? deviation = null)
    {
        var c = new FakeCarrier { Hz = hz, Level = level, ToneHz = toneHz, Deviation = deviation };
        lock (this) _carriers = [.. _carriers, c];
        return c;
    }

    private Thread? _thread;
    private volatile bool _run, _retuned;
    private double _center;
    public double CarrierHz;                 // 0 for none
    public double Noise = 1e-4;              // peak to peak, in each of I and Q
    public volatile bool NeverSaysRetuned;   // the word that the radio has retuned gets lost
    public int Tunes;

    public string Description => "fake";
    public IReadOnlyList<RadioInfo> Radios() => [];
    public RadioInfo? Radio => null;
    public string? Serial { get; set; }
    public double SampleRate { get; private set; }
    public double CenterHz => Volatile.Read(ref _center);
    public bool IsRunning => _run;
    public double UsableBandwidthHz => SampleRate * 0.8;
    public event IqHandler? Samples;
#pragma warning disable CS0067
    public event Action<string>? Failed;
#pragma warning restore CS0067
    public IReadOnlyList<string> Antennas => ["Antenna"];
    public string Antenna { get; set; } = "Antenna";
    public bool HasBiasT => false;
    public bool BiasT { get; set; }
    public bool HasFmNotch => false;
    public bool FmNotch { get; set; }
    public double Ppm { get; set; }
    public bool AutoGain { get; set; }
    public int GainStep { get; set; }
    public int MaxGainStep => 0;
    public int ActiveGainStep => 0;
    public double GainDb => 0;
    public bool Overloaded => false;
    public double LastRetuneMs => 0;

    public string? FailToStart;              // the reason it gives for not starting
    public int Refused;                      // retunes to a centre no radio has, refused as the API refuses them

    // What the SDRplay service checks ("rfHz out of range (0:2000000000) = -200000", 2026-11-26).
    private static bool InRange(double centerHz) => centerHz > 0 && centerHz <= 2e9;

    public void Start(double sampleRate, double centerHz)
    {
        if (FailToStart is { } why) throw new InvalidOperationException(why);
        if (!InRange(centerHz)) throw new InvalidOperationException("Could not start the radio: sdrplay_api_Fail.");
        SampleRate = sampleRate;
        _center = centerHz;
        _run = true;
        _thread = new Thread(Run) { IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join();
    }

    public void Tune(double centerHz)
    {
        if (!InRange(centerHz))
        {
            Interlocked.Increment(ref Refused);
            return;
        }
        Volatile.Write(ref _center, centerHz);
        Interlocked.Increment(ref Tunes);
        _retuned = true;
    }

    public void Dispose() => Stop();

    private void Run()
    {
        int n = (int)(SampleRate / 1000);    // a millisecond
        var re = new float[n];
        var im = new float[n];
        var rng = new Random(3);
        long sample = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_run)
        {
            double offset = Volatile.Read(ref CarrierHz) - CenterHz;
            bool carrier = CarrierHz != 0 && Math.Abs(offset) < SampleRate / 2;
            double noise = Volatile.Read(ref Noise);
            for (int k = 0; k < n; k++, sample++)
            {
                double ph = 2 * Math.PI * offset * sample / SampleRate;
                re[k] = (float)((carrier ? 0.05 * Math.Cos(ph) : 0) + noise * (rng.NextDouble() - 0.5));
                im[k] = (float)((carrier ? 0.05 * Math.Sin(ph) : 0) + noise * (rng.NextDouble() - 0.5));
            }
            foreach (var c in _carriers) AddCarrier(c, re, im);
            bool retuned = _retuned && !NeverSaysRetuned;
            _retuned = false;
            Samples?.Invoke(re, im, retuned);
            while (clock.Elapsed.TotalSeconds < sample / SampleRate) Thread.Sleep(0);
        }
    }

    // One block of one carrier, added to the noise. The carrier is a pointer turned a step with every sample,
    // so that it runs on unbroken from block to block, through a retune too.
    private void AddCarrier(FakeCarrier c, float[] re, float[] im)
    {
        double offset = Volatile.Read(ref c.Hz) - CenterHz;
        if (!c.On || Math.Abs(offset) >= SampleRate / 2) return;
        double step = 2 * Math.PI * offset / SampleRate, cs = Math.Cos(step), sn = Math.Sin(step);
        double tone = 2 * Math.PI * c.ToneHz / SampleRate, tc = Math.Cos(tone), ts = Math.Sin(tone);
        double level = Volatile.Read(ref c.Level), x = c.Re, y = c.Im, tx = c.ToneRe, ty = c.ToneIm;
        var dev = c.Deviation;
        int up = (int)Math.Round(SampleRate / 24000);       // the recording's samples are this many of the radio's
        double perUnit = 2 * Math.PI * c.DeviationHz / SampleRate;
        for (int k = 0; k < re.Length; k++)
        {
            double a = level;
            if (c.ToneHz != 0)
            {
                (tx, ty) = (tx * tc - ty * ts, tx * ts + ty * tc);
                a *= 1 + 0.5 * ty;
            }
            (x, y) = (x * cs - y * sn, x * sn + y * cs);
            if (dev is not null)
            {
                // Straight-line interpolation of the discriminator waveform up to the radio's rate; the turn it
                // adds is small enough for the first terms of the sine and cosine.
                double d0 = dev[c.At], d1 = dev[(c.At + 1) % dev.Length];
                double turn = perUnit * (d0 + (d1 - d0) * c.Part / up);
                double dc = 1 - turn * turn / 2, ds = turn - turn * turn * turn / 6;
                (x, y) = (x * dc - y * ds, x * ds + y * dc);
                if (++c.Part == up)
                {
                    c.Part = 0;
                    c.At = (c.At + 1) % dev.Length;
                }
            }
            re[k] += (float)(a * x);
            im[k] += (float)(a * y);
        }
        // The pointers are kept at length one against the creep of rounding.
        double r = 1 / Math.Sqrt(x * x + y * y), tr = 1 / Math.Sqrt(tx * tx + ty * ty);
        (c.Re, c.Im, c.ToneRe, c.ToneIm) = (x * r, y * r, tx * tr, ty * tr);
    }
}

public class ScanTests
{
    private static bool Wait(Func<bool> condition, double seconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    [Fact]
    public void TheScanStopsOnTheActiveChannelAndMovesOnWhenItGoesQuiet()
    {
        var source = new FakeSource();
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6) { SquelchDb = -60, ScanHangSeconds = 0.2 };
        ScanChannel[] list =
        [
            new("one", 120.0e6, Mode.Am, BandPlan.AmAir), new("two", 120.1e6, Mode.Am, BandPlan.AmAir),
            new("three", 125.0e6, Mode.Am, BandPlan.AmAir), new("four", 130.0e6, Mode.Am, BandPlan.AmAir),
        ];
        rx.Start();
        rx.StartScan(list);

        // Nothing on the air: it keeps going round.
        Thread.Sleep(400);
        Assert.True(rx.Scanning);
        Assert.False(rx.ScanListening);
        Assert.True(source.Tunes >= 6, $"only {source.Tunes} retunes in 0.4 s");

        // A carrier on "three": it stops there with the squelch open.
        source.CarrierHz = 125.0e6;
        Assert.True(Wait(() => rx.ScanListening, 2), "did not stop on the carrier");
        Assert.Equal("three", rx.ScanChannel?.Name);
        Assert.Equal(125.0e6, rx.FrequencyHz);
        Assert.True(rx.SquelchOpen);
        Thread.Sleep(400);
        Assert.True(rx.ScanListening);                     // still there well past the hang time

        // The carrier goes: after the hang time it moves on.
        source.CarrierHz = 0;
        Assert.True(Wait(() => !rx.ScanListening, 2), "did not leave the quiet channel");
        Assert.True(rx.Scanning);

        // Tuning by hand ends the scan.
        rx.FrequencyHz = 162.4e6;
        Assert.False(rx.Scanning);
        rx.Stop();
    }

    // A channel on its own sits 300 kHz above the centre. For 100 kHz that centre would be -200 kHz, which the
    // radio refuses: the start that failed ten times out of ten on 2026-11-26. It is tuned from above instead.
    [Fact]
    public void AFrequencyTooLowForTheUsualCentreIsReceivedFromACentreAboveIt()
    {
        var source = new FakeSource { CarrierHz = 100e3 };
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6) { SquelchDb = -60, ScanHangSeconds = 0.2 };
        rx.SetMode(Mode.Am, BandPlan.AmAir);
        rx.FrequencyHz = 100e3;
        rx.Start();
        Assert.True(source.CenterHz > 0, $"started at a centre of {source.CenterHz} Hz");
        Assert.True(Wait(() => rx.SquelchOpen, 2), "the carrier on 100 kHz is not heard");

        // The same when it is tuned there while running, and when a scan goes there.
        rx.FrequencyHz = 162.4e6;
        Assert.True(Wait(() => !rx.SquelchOpen, 2));
        source.CarrierHz = 250e3;
        rx.FrequencyHz = 250e3;
        Assert.True(Wait(() => rx.SquelchOpen, 2), "the carrier on 250 kHz is not heard");
        rx.FrequencyHz = 162.4e6;
        source.CarrierHz = 150e3;
        rx.StartScan([new("low", 150e3, Mode.Am, BandPlan.AmAir), new("air", 125.0e6, Mode.Am, BandPlan.AmAir)]);
        Assert.True(Wait(() => rx.ScanListening, 3), "the scan did not stop on 150 kHz");
        Assert.Equal("low", rx.ScanChannel?.Name);
        Assert.Equal(0, source.Refused);
        rx.Stop();
    }

    [Fact]
    public void ThePlanKeepsEveryCentreInsideTheRadiosRange()
    {
        ScanChannel[] list =
        [
            new("a", 100e3, Mode.Am, BandPlan.AmAir), new("b", 150e3, Mode.Am, BandPlan.AmAir), new("c", 290e3, Mode.Am, BandPlan.AmAir),
            new("d", 1.0e6, Mode.Am, BandPlan.AmAir), new("e", 1999.99e6, Mode.Nfm, 12500), new("f", 2000e6, Mode.Nfm, 12500),
        ];
        foreach (double usable in new[] { 1.9e6, 6.4e6 })
        {
            var windows = ScanPlan.Build(list, usable);
            Assert.Equal(list.Length, windows.Sum(w => w.Channels.Length));
            foreach (var w in windows)
            {
                Assert.InRange(w.CenterHz, 1, 2e9);
                foreach (int k in w.Channels)
                    Assert.True(ScanPlan.Fits(list[k].FrequencyHz - w.CenterHz, list[k].BandwidthHz, usable), $"{list[k].Name} does not fit at {w.CenterHz}");
            }
        }
        // A channel on its own: above the centre as a rule, below it where the centre would leave the range.
        Assert.Equal(ScanPlan.SingleOffsetHz, ScanPlan.SingleOffsetFor(162.4e6));
        Assert.Equal(ScanPlan.SingleOffsetHz, ScanPlan.SingleOffsetFor(1.0e6));
        Assert.Equal(-ScanPlan.SingleOffsetHz, ScanPlan.SingleOffsetFor(100e3));
        Assert.Equal(-ScanPlan.SingleOffsetHz, ScanPlan.SingleOffsetFor(299e3));
        // The top of the range needs nothing: the centre is below the channel there.
        Assert.Equal(ScanPlan.SingleOffsetHz, ScanPlan.SingleOffsetFor(2000e6));
        Assert.Equal(2000e6 - ScanPlan.SingleOffsetHz, Assert.Single(ScanPlan.Build([new("top", 2000e6, Mode.Nfm, 12500)], 6.4e6)).CenterHz);
        Assert.Equal(100e3 + ScanPlan.SingleOffsetHz, Assert.Single(ScanPlan.Build([new("bottom", 100e3, Mode.Am, BandPlan.AmAir)], 6.4e6)).CenterHz);
    }

    [Fact]
    public void ChannelsCloseTogetherShareOneTuningOfTheRadio()
    {
        var source = new FakeSource();
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6) { SquelchDb = -60 };
        rx.Start();
        rx.StartScan([
            new("a", 121.65e6, Mode.Am, BandPlan.AmAir), new("b", 121.75e6, Mode.Am, BandPlan.AmAir),
            new("c", 121.9e6, Mode.Am, BandPlan.AmAir),
        ]);
        Thread.Sleep(500);
        rx.Stop();
        Assert.True(source.Tunes <= 2, $"{source.Tunes} retunes for three channels 250 kHz apart");
        Assert.True(rx.ScanPasses > 20, $"only {rx.ScanPasses} passes in half a second");
    }

    [Fact]
    public void OfTwoActiveChannelsInAWindowSkipMovesToTheOther()
    {
        var source = new FakeSource { CarrierHz = 121.75e6 };
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6) { SquelchDb = -60 };
        rx.Start();
        rx.StartScan([
            new("a", 121.65e6, Mode.Am, BandPlan.AmAir), new("b", 121.75e6, Mode.Am, BandPlan.AmAir),
            new("c", 121.9e6, Mode.Am, BandPlan.AmAir),
        ]);
        Assert.True(Wait(() => rx.ScanListening, 2));
        Assert.Equal("b", rx.ScanChannel?.Name);
        // Skipped, it is passed by although still active.
        rx.SkipChannel();
        Thread.Sleep(300);
        Assert.False(rx.ScanListening);
        Assert.True(rx.Scanning);
        rx.Stop();
    }

    [Fact]
    public void AnAvoidedChannelStaysOutOfLaterScansUntilItsTimeIsUp()
    {
        var source = new FakeSource { CarrierHz = 121.75e6 };
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6) { SquelchDb = -60 };
        ScanChannel[] list = [new("a", 121.65e6, Mode.Am, BandPlan.AmAir), new("b", 121.75e6, Mode.Am, BandPlan.AmAir)];
        rx.Start();
        rx.StartScan(list);
        Assert.True(Wait(() => rx.ScanListening, 2));
        Assert.Equal("b", rx.AvoidChannel()?.Name);
        Assert.Equal(1, rx.AvoidedCount);
        Thread.Sleep(300);
        Assert.False(rx.ScanListening);
        // A skip is forgotten when the scan is started again; an avoid is not.
        rx.StopScan();
        rx.StartScan(list);
        Thread.Sleep(300);
        Assert.False(rx.ScanListening);
        Assert.True(rx.Scanning);
        // With a short timer the channel comes back by itself.
        rx.ClearAvoided();
        Assert.True(Wait(() => rx.ScanListening, 2));
        rx.AvoidTime = TimeSpan.FromSeconds(0.5);
        Assert.NotNull(rx.AvoidChannel());
        Thread.Sleep(200);
        Assert.False(rx.ScanListening);
        Assert.True(Wait(() => rx.ScanListening, 2));
        Assert.Equal(0, rx.AvoidedCount);
        rx.Stop();
    }

    [Fact]
    public void WithAResumeTimeTheScanLeavesAChannelThatIsStillBusy()
    {
        var source = new FakeSource { CarrierHz = 121.75e6 };
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6) { SquelchDb = -60 };
        rx.Start();
        rx.StartScan([new("a", 121.65e6, Mode.Am, BandPlan.AmAir), new("b", 121.75e6, Mode.Am, BandPlan.AmAir)]);
        // Hold: it stays for as long as the carrier does.
        Assert.True(Wait(() => rx.ScanListening, 2));
        Thread.Sleep(600);
        Assert.True(rx.ScanListening);
        // With a time set it goes, passes the channel by for a while, then comes back to it.
        rx.ResumePassBySeconds = 1;
        rx.ResumeSeconds = 0.3;
        Assert.True(Wait(() => !rx.ScanListening, 2));
        Assert.True(rx.Scanning);
        Thread.Sleep(400);
        Assert.False(rx.ScanListening);
        Assert.True(Wait(() => rx.ScanListening, 3));
        rx.Stop();
    }

    private static readonly double[] Airband =
    [
        118.25, 120.6, 121.3, 121.65, 121.75, 121.9, 124.725, 124.9, 127.2, 128.8, 132.225, 133.0, 134.75, 135.9,
    ];

    [Theory]
    [InlineData(6.44e6, 3)]
    [InlineData(1.41e6, 11)]
    public void ThePlanCoversEveryChannelInFewWindows(double usableHz, int mostWindows)
    {
        var list = Airband.Select(f => new ScanChannel($"{f}", f * 1e6, Mode.Am, BandPlan.AmAir)).ToList();
        var plan = ScanPlan.Build(list, usableHz);
        Assert.True(plan.Count <= mostWindows, $"{plan.Count} windows");
        Assert.Equal(Enumerable.Range(0, list.Count), plan.SelectMany(w => w.Channels));
        foreach (var w in plan)
            foreach (int c in w.Channels)
                Assert.True(ScanPlan.Fits(list[c].FrequencyHz - w.CenterHz, list[c].BandwidthHz, usableHz),
                    $"{list[c].Name} does not fit a window centred on {w.CenterHz / 1e6}");
    }

    [Fact]
    public void AChannelInTheMiddleOfAGroupDoesNotLandOnTheCentreSpur()
    {
        ScanChannel[] list = [new("lo", 120.0e6, Mode.Am, 10000), new("mid", 121.0e6, Mode.Am, 10000), new("hi", 122.0e6, Mode.Am, 10000)];
        var plan = ScanPlan.Build(list, 6.44e6);
        Assert.Single(plan);
        Assert.True(Math.Abs(plan[0].CenterHz - 121.0e6) >= 45000);
    }

    [Fact]
    public void MemoriesSurviveASaveAndLoad()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ss-mem-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MemoryStore(path);
            store.Channels.Add(new MemoryChannel { Name = "Tower", FrequencyHz = 128.8e6, Groups = ["Logan"] });
            store.Channels.Add(new MemoryChannel { Name = "Repeater", FrequencyHz = 146.94e6, Mode = Mode.Nfm, BandwidthHz = 16000, Scan = false });
            store.Save();
            var back = MemoryStore.Load(path);
            Assert.Equal(2, back.Channels.Count);
            Assert.Equal(["Logan"], back.Find(128.8e6)?.Groups);
            Assert.Null(back.Find(128.8e6)?.Mode);
            Assert.Equal(Mode.Nfm, back.Find(146.94e6 + 50)?.Mode);
            Assert.False(back.Channels[1].Scan);
            Assert.Null(back.Find(100e6));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WithAutoSquelchTheScanPassesNoiseAboveTheSquelchLevelAndStillStopsOnACarrier()
    {
        // Noise 20 dB over the squelch level on every channel, as on a noisy band.
        var source = new FakeSource { Noise = 0.3 };
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6)
            { SquelchDb = -60, ScanHangSeconds = 0.2, AutoSquelch = true };
        ScanChannel[] list =
        [
            new("one", 33.0e6, Mode.Nfm, BandPlan.NfmWide), new("two", 33.1e6, Mode.Nfm, BandPlan.NfmWide),
            new("three", 33.4e6, Mode.Nfm, BandPlan.NfmWide), new("four", 39.0e6, Mode.Nfm, BandPlan.NfmWide),
        ];
        rx.Start();
        rx.StartScan(list);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 1.0)
        {
            Assert.False(rx.ScanListening, "stopped on noise");
            Thread.Sleep(2);
        }
        Assert.True(rx.ScanPasses >= 3, $"only {rx.ScanPasses} passes in a second");

        // A carrier 14 dB over that noise: it stops there.
        source.CarrierHz = 33.4e6;
        Assert.True(Wait(() => rx.ScanListening, 2), "did not stop on the carrier");
        Assert.Equal("three", rx.ScanChannel?.Name);
        source.CarrierHz = 0;
        Assert.True(Wait(() => !rx.ScanListening, 2), "did not leave when the carrier went");

        // Without it the level alone decides, and the noise holds the scan.
        rx.AutoSquelch = false;
        Assert.True(Wait(() => rx.ScanListening, 2), "the plain squelch should open on this noise");
        rx.Stop();
    }

    [Fact]
    public void TheScanCarriesOnWhenWordOfARetuneIsLost()
    {
        var source = new FakeSource();
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6) { SquelchDb = -60 };
        ScanChannel[] list = [new("one", 120.0e6, Mode.Am, BandPlan.AmAir), new("two", 130.0e6, Mode.Am, BandPlan.AmAir)];
        rx.Start();
        rx.StartScan(list);
        Assert.True(Wait(() => rx.ScanPasses >= 3, 2));
        source.NeverSaysRetuned = true;
        Thread.Sleep(100);
        long before = rx.ScanPasses;
        Assert.True(Wait(() => rx.ScanPasses >= before + 2, 6), "the scan stalled waiting for the retune");
        rx.Stop();
    }

    [Fact]
    public void TwoReceiversScanAtOnceAndEachStopsOnItsOwnSignal()
    {
        var a = new FakeSource();
        var b = new FakeSource();
        var first = new Receiver(a, playAudio: false, sampleRate: 2.4e6) { SquelchDb = -60, ScanHangSeconds = 0.2 };
        var second = new Receiver(b, playAudio: false, sampleRate: 2.4e6) { SquelchDb = -60, ScanHangSeconds = 0.2 };
        ScanChannel[] low = [new("one", 120.0e6, Mode.Am, BandPlan.AmAir), new("two", 125.0e6, Mode.Am, BandPlan.AmAir)];
        ScanChannel[] high = [new("three", 130.0e6, Mode.Am, BandPlan.AmAir), new("four", 135.0e6, Mode.Am, BandPlan.AmAir)];
        first.Start();
        second.Start();
        first.StartScan(low);
        second.StartScan(high);
        Assert.True(Wait(() => first.ScanPasses >= 2 && second.ScanPasses >= 2, 3), "one of the two did not go round");

        // A carrier for the second only: it stops, the first keeps going round.
        b.CarrierHz = 135.0e6;
        Assert.True(Wait(() => second.ScanListening, 2), "the second did not stop on its carrier");
        Assert.Equal("four", second.ScanChannel?.Name);
        long before = first.ScanPasses;
        Assert.True(Wait(() => first.ScanPasses >= before + 2, 3), "the first stopped going round");
        Assert.False(first.ScanListening);
        Assert.Equal(0, first.DroppedBlocks);

        // Disposing one leaves the other running; disposing twice does no harm.
        first.Dispose();
        first.Dispose();
        Assert.True(second.IsRunning);
        Assert.True(second.ScanListening);
        second.Dispose();
    }

    private static (double Left, double Right) Loudness(Receiver rx, double seconds)
    {
        double left = 0, right = 0;
        var gate = new object();
        void Take(ReadOnlySpan<float> a)
        {
            lock (gate)
            {
                for (int k = 0; k + 1 < a.Length; k += 2)
                {
                    left += a[k] * a[k];
                    right += a[k + 1] * a[k + 1];
                }
            }
        }
        rx.AudioOutput += Take;
        Thread.Sleep((int)(seconds * 1000));
        rx.AudioOutput -= Take;
        lock (gate) return (left, right);
    }

    [Fact]
    public void ThePanPutsTheSoundToOneSideAndTheMiddleLeavesItAlone()
    {
        Assert.Equal((1f, 1f, 0f), Receiver.PanGains(0));
        Assert.Equal((1f, 0f, 1f), Receiver.PanGains(-1));
        Assert.Equal((0f, 1f, 1f), Receiver.PanGains(1));
        Assert.Equal((1f, 0.5f, 0.5f), Receiver.PanGains(-0.5));
        Assert.Equal((0f, 1f, 1f), Receiver.PanGains(3));

        // Noise with the squelch off is sound in both speakers alike.
        var source = new FakeSource { Noise = 0.02 };
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6) { SquelchEnabled = false, Volume = 1 };
        rx.Start();
        Thread.Sleep(200);
        var (l, r) = Loudness(rx, 0.3);
        Assert.True(l > 0 && Math.Abs(l - r) < 0.02 * l, $"middle: {l} and {r}");
        rx.Pan = -1;
        Thread.Sleep(50);
        var (leftOnly, none) = Loudness(rx, 0.3);
        Assert.True(leftOnly > 0);
        Assert.Equal(0, none);
        rx.Pan = 0.5;
        Thread.Sleep(50);
        var (half, full) = Loudness(rx, 0.3);
        Assert.InRange(half / full, 0.2, 0.3);       // half the level is a quarter of the power
        rx.Pan = double.NaN;
        Assert.Equal(0, rx.Pan);
    }
}
