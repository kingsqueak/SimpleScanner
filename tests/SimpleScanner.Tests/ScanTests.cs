using SimpleScanner.Dsp;
using SimpleScanner.Radio;

namespace SimpleScanner.Tests;

/// <summary>A pretend radio: noise, plus one carrier on a chosen frequency, delivered at real speed.</summary>
internal sealed class FakeSource : IIqSource
{
    private Thread? _thread;
    private volatile bool _run, _retuned;
    private double _center;
    public double CarrierHz;                 // 0 for none
    public int Tunes;

    public string Description => "fake";
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
    public bool AutoGain { get; set; }
    public int GainStep { get; set; }
    public int MaxGainStep => 0;
    public int ActiveGainStep => 0;
    public double GainDb => 0;
    public bool Overloaded => false;
    public double LastRetuneMs => 0;

    public void Start(double sampleRate, double centerHz)
    {
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
            for (int k = 0; k < n; k++, sample++)
            {
                double ph = 2 * Math.PI * offset * sample / SampleRate;
                re[k] = (float)((carrier ? 0.05 * Math.Cos(ph) : 0) + 1e-4 * (rng.NextDouble() - 0.5));
                im[k] = (float)((carrier ? 0.05 * Math.Sin(ph) : 0) + 1e-4 * (rng.NextDouble() - 0.5));
            }
            bool retuned = _retuned;
            _retuned = false;
            Samples?.Invoke(re, im, retuned);
            while (clock.Elapsed.TotalSeconds < sample / SampleRate) Thread.Sleep(0);
        }
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
}
