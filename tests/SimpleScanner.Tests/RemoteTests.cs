using System.IO.Pipes;
using SimpleScanner.Dsp;
using SimpleScanner.Remote;

namespace SimpleScanner.Tests;

/// <summary>A receiver worked through the helper's messages: here both ends are in the test, joined by two pipes.</summary>
public class RemoteTests
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

    private sealed class Pair : IDisposable
    {
        public readonly FakeSource Source = new();
        public readonly Receiver Real;
        public readonly RemoteReceiver Remote;
        private readonly Thread _host;
        private readonly AnonymousPipeServerStream _up;

        public Pair()
        {
            var down = new AnonymousPipeServerStream(PipeDirection.Out);
            var downEnd = new AnonymousPipeClientStream(PipeDirection.In, down.ClientSafePipeHandle);
            var up = _up = new AnonymousPipeServerStream(PipeDirection.Out);
            var upEnd = new AnonymousPipeClientStream(PipeDirection.In, up.ClientSafePipeHandle);
            Real = new Receiver(Source, playAudio: false, sampleRate: 2.4e6);
            _host = new Thread(() =>
            {
                ReceiverHost.Run(Real, downEnd, up);
                up.Dispose();
            }) { IsBackground = true };
            _host.Start();
            Remote = new RemoteReceiver(down, upEnd);
            Assert.True(Remote.WaitForFirstReport(5000), "no report from the host");
        }

        public bool HostEnded(int ms) => _host.Join(ms);
        public void Cut() => _up.Dispose();
        public void Dispose() => Remote.Dispose();
    }

    [Fact]
    public void WhatIsSetArrivesAndReadsBackAtOnce()
    {
        using var pair = new Pair();
        var rx = pair.Remote;
        rx.SquelchDb = -61;
        rx.Volume = 0.25;
        rx.Pan = -0.5;
        rx.ResumeSeconds = 4;
        rx.AvoidTime = TimeSpan.FromMinutes(60);
        rx.SetMode(Mode.Am, BandPlan.AmAir);
        rx.FrequencyHz = 121.5e6;
        rx.Source.GainStep = 2;
        // Here at once, whatever the reports in flight say...
        Assert.Equal(-61, rx.SquelchDb);
        Assert.Equal(121.5e6, rx.FrequencyHz);
        Assert.Equal(Mode.Am, rx.Mode);
        Assert.Equal(2, rx.Source.GainStep);
        // ...and in the real receiver soon after.
        Assert.True(Wait(() => pair.Real.FrequencyHz == 121.5e6 && pair.Source.GainStep == 2, 2));
        Assert.Equal(-61, pair.Real.SquelchDb);
        Assert.Equal(0.25, pair.Real.Volume, 6);
        Assert.Equal(-0.5, pair.Real.Pan);
        Assert.Equal(-0.5, rx.Pan);
        Assert.Equal(4, pair.Real.ResumeSeconds);
        Assert.Equal(TimeSpan.FromMinutes(60), pair.Real.AvoidTime);
        Assert.Equal(Mode.Am, pair.Real.Mode);
        // It stays so once the reports catch up.
        Thread.Sleep(200);
        Assert.Equal(-61, rx.SquelchDb);
        Assert.Equal(121.5e6, rx.FrequencyHz);
        Assert.Equal(4, rx.ResumeSeconds);
        Assert.Equal(-0.5, rx.Pan);
    }

    [Fact]
    public void ItStartsScansStopsOnASignalAndAvoidsIt()
    {
        using var pair = new Pair();
        var rx = pair.Remote;
        rx.SquelchDb = -60;
        rx.ScanHangSeconds = 0.2;
        ScanChannel[] list =
        [
            new("one", 120.0e6, Mode.Am, BandPlan.AmAir), new("two", 125.0e6, Mode.Am, BandPlan.AmAir),
            new("three", 130.0e6, Mode.Am, BandPlan.AmAir),
        ];
        rx.Start();
        Assert.True(rx.IsRunning);
        Assert.True(pair.Real.IsRunning);
        Assert.Equal("fake", rx.Source.Description);
        rx.StartScan(list);
        Assert.True(Wait(() => rx.ScanPasses >= 2, 3), "the scan did not go round");
        Assert.True(rx.Scanning);

        pair.Source.CarrierHz = 125.0e6;
        Assert.True(Wait(() => rx.ScanListening && rx.ScanChannel?.Name == "two", 3), "did not stop on the carrier");
        Assert.True(rx.SquelchOpen);
        Assert.Equal(125.0e6, rx.FrequencyHz);

        var avoided = rx.AvoidChannel();
        Assert.Equal("two", avoided?.Name);
        Assert.True(Wait(() => rx.AvoidedCount == 1 && !rx.ScanListening, 2));
        long before = rx.ScanPasses;
        Assert.True(Wait(() => rx.ScanPasses >= before + 2, 3), "the scan did not go on after the avoid");

        rx.StopScan();
        Assert.False(rx.Scanning);
        rx.Stop();
        Assert.False(rx.IsRunning);
        Assert.False(pair.Real.IsRunning);
    }

    [Fact]
    public void AFailureToStartComesBackAsTheReason()
    {
        using var pair = new Pair();
        pair.Source.FailToStart = "No radio here.";
        var e = Assert.Throws<InvalidOperationException>(() => pair.Remote.Start());
        Assert.Equal("No radio here.", e.Message);
        Assert.False(pair.Remote.IsRunning);
    }

    [Fact]
    public void ClosingTheWindowsEndStopsTheReceiver()
    {
        var pair = new Pair();
        pair.Remote.Start();
        Assert.True(pair.Real.IsRunning);
        pair.Remote.Dispose();
        Assert.True(pair.HostEnded(5000), "the host did not end");
        Assert.False(pair.Real.IsRunning);
    }

    [Fact]
    public void WhenTheHelperGoesTheWindowIsTold()
    {
        using var pair = new Pair();
        string? told = null;
        pair.Remote.Failed += why => told = why;
        pair.Remote.Start();
        pair.Cut();                          // as if the helper's program had died
        Assert.True(Wait(() => told is not null, 3), "nothing was said");
        Assert.False(pair.Remote.IsRunning);
        Assert.Throws<InvalidOperationException>(() => pair.Remote.Start());
    }

    // A helper whose radio jammed asks for a new one and goes; the window replugs the radio, starts
    // another helper and gives it what the first had: settings, mode, running, scanning.
    [Fact]
    public void AHelperThatAsksForANewOneIsReplacedWithItsSettings()
    {
        var log = Path.GetTempFileName();
        SimpleScanner.Radio.RadioLog.FileOverride = log;
        var hosts = new List<(FakeSource Source, Receiver Real, Action<string>? Ask, Thread Thread)>();
        int replugs = 0;
        (Stream, Stream, Action?) Launch()
        {
            var down = new AnonymousPipeServerStream(PipeDirection.Out);
            var downEnd = new AnonymousPipeClientStream(PipeDirection.In, down.ClientSafePipeHandle);
            var up = new AnonymousPipeServerStream(PipeDirection.Out);
            var upEnd = new AnonymousPipeClientStream(PipeDirection.In, up.ClientSafePipeHandle);
            var source = new FakeSource();
            var real = new Receiver(source, playAudio: false, sampleRate: 2.4e6);
            int index = hosts.Count;
            var thread = new Thread(() =>
            {
                ReceiverHost.Run(real, downEnd, up,
                    handOver: ask => hosts[index] = hosts[index] with { Ask = ask },
                    exit: () => { real.Stop(); up.Dispose(); });     // as the helper's process ends
                up.Dispose();
            }) { IsBackground = true };
            hosts.Add((source, real, null, thread));
            thread.Start();
            return (down, upEnd, () => thread.Join(3000));
        }
        try
        {
            using var rx = new RemoteReceiver(Launch, replug: _ => replugs++);
            Assert.True(rx.WaitForFirstReport(5000));
            rx.SquelchDb = -61;
            rx.Volume = 0.25;
            rx.ScanHangSeconds = 0.2;
            rx.Source.GainStep = 2;
            rx.SetMode(Mode.Am, BandPlan.AmAir);
            rx.FrequencyHz = 121.5e6;
            rx.Start();
            ScanChannel[] list = [new("one", 120.0e6, Mode.Am, BandPlan.AmAir), new("two", 125.0e6, Mode.Am, BandPlan.AmAir)];
            rx.StartScan(list);
            Assert.True(Wait(() => rx.ScanPasses >= 2, 3), "the first helper did not scan");
            string? told = null;
            rx.Failed += why => told = why;

            Assert.True(Wait(() => hosts[0].Ask is not null, 2));
            hosts[0].Ask!("radio stalled (test)");

            Assert.True(Wait(() => hosts.Count == 2, 10), "no second helper");
            var second = hosts[1];
            Assert.True(Wait(() => second.Real.IsRunning && second.Real.Scanning, 10), "the second helper is not scanning");
            Assert.Equal(1, replugs);
            Assert.Null(told);
            Assert.Equal(-61, second.Real.SquelchDb);
            Assert.Equal(0.25, second.Real.Volume, 6);
            Assert.Equal(2, second.Source.GainStep);
            Assert.Equal(Mode.Am, second.Real.Mode);
            Assert.True(Wait(() => rx.ScanPasses >= 2 && rx.IsRunning && rx.Scanning, 5), "the window does not see the new helper");
            Assert.Contains("running again in a new helper, scanning", File.ReadAllText(log));

            // A second call for a new one within a minute is not answered: the window is told instead.
            Assert.True(Wait(() => hosts[1].Ask is not null, 2));
            hosts[1].Ask!("radio stalled again (test)");
            Assert.True(Wait(() => told is not null, 10), "nothing was said");
            Assert.Equal(2, hosts.Count);
            Assert.Contains("Unplug it", told);
        }
        finally
        {
            SimpleScanner.Radio.RadioLog.FileOverride = null;
            File.Delete(log);
        }
    }

    // Helpers started one after another, as the window starts them: each a real receiver on a fake radio.
    private sealed class Hosts
    {
        public readonly List<(FakeSource Source, Receiver Real, Action<string>? Ask)> Started = [];

        public (Stream, Stream, Action?) Launch()
        {
            var down = new AnonymousPipeServerStream(PipeDirection.Out);
            var downEnd = new AnonymousPipeClientStream(PipeDirection.In, down.ClientSafePipeHandle);
            var up = new AnonymousPipeServerStream(PipeDirection.Out);
            var upEnd = new AnonymousPipeClientStream(PipeDirection.In, up.ClientSafePipeHandle);
            var source = new FakeSource();
            var real = new Receiver(source, playAudio: false, sampleRate: 2.4e6);
            int index = Started.Count;
            var thread = new Thread(() =>
            {
                ReceiverHost.Run(real, downEnd, up,
                    handOver: ask => Started[index] = Started[index] with { Ask = ask },
                    exit: () => { real.Stop(); up.Dispose(); });     // as the helper's process ends
                up.Dispose();
            }) { IsBackground = true };
            Started.Add((source, real, null));
            thread.Start();
            return (down, upEnd, () => thread.Join(3000));
        }

        /// <summary>The helper started as number <paramref name="index"/> says its radio has jammed and goes.</summary>
        public void AskForANewOne(int index)
        {
            Assert.True(Wait(() => Started[index].Ask is not null, 2));
            Started[index].Ask!("stopped sending samples (test)");
        }
    }

    // What happened on 2026-11-26: a stray click on the frequency display sent the lowest frequency there is
    // (100 kHz), the scan went on, and nothing looked wrong. Six minutes later the radio jammed; the new helper
    // was given 100 kHz again, asked the radio for a centre of -200 kHz and was refused, as was every start
    // after it until the app was closed.
    [Fact]
    public void AfterAStrayLowFrequencyANewHelperStillStartsAndScans()
    {
        var log = Path.GetTempFileName();
        SimpleScanner.Radio.RadioLog.FileOverride = log;
        var hosts = new Hosts();
        try
        {
            using var rx = new RemoteReceiver(hosts.Launch, replug: _ => { });
            Assert.True(rx.WaitForFirstReport(5000));
            string? told = null;
            rx.Failed += why => told = why;
            rx.SquelchDb = -60;
            rx.SetMode(Mode.Am, BandPlan.AmAir);
            rx.FrequencyHz = 121.5e6;
            rx.Start();
            rx.FrequencyHz = 100e3;                              // the stray click
            ScanChannel[] list = [new("one", 120.0e6, Mode.Am, BandPlan.AmAir), new("two", 125.0e6, Mode.Am, BandPlan.AmAir)];
            rx.StartScan(list);
            Assert.True(Wait(() => rx.ScanPasses >= 2, 3), "the first helper did not scan");

            hosts.AskForANewOne(0);

            Assert.True(Wait(() => hosts.Started.Count == 2, 10), "no second helper");
            var second = hosts.Started[1];
            Assert.True(Wait(() => second.Real.IsRunning && second.Real.Scanning || told is not null, 10), "the second helper is not scanning");
            Assert.Null(told);
            Assert.True(Wait(() => rx.IsRunning && rx.Scanning && rx.ScanPasses >= 2, 5), "the window does not see the new helper scanning");
            Assert.Equal(0, hosts.Started[0].Source.Refused);    // the click's own retune was refused too ("1 refused" in the log)
            Assert.Equal(0, second.Source.Refused);
            Assert.Contains("running again in a new helper, scanning", File.ReadAllText(log));
        }
        finally
        {
            SimpleScanner.Radio.RadioLog.FileOverride = null;
            File.Delete(log);
        }
    }

    // A scan moves the receiver without the window sending a frequency. A new helper is put where the old one
    // had got to (what the display shows), not where the window last sent it by hand, however long ago.
    [Fact]
    public void ANewHelperIsPutWhereTheOldOneWasNotWhereItWasLastSentByHand()
    {
        var log = Path.GetTempFileName();
        SimpleScanner.Radio.RadioLog.FileOverride = log;
        var hosts = new Hosts();
        try
        {
            using var rx = new RemoteReceiver(hosts.Launch, replug: _ => { });
            Assert.True(rx.WaitForFirstReport(5000));
            rx.SquelchDb = -60;
            rx.ScanHangSeconds = 5;
            rx.SetMode(Mode.Nfm, 12500);
            rx.FrequencyHz = 162.4e6;                            // by hand, before the scan
            rx.Start();
            hosts.Started[0].Source.CarrierHz = 125.0e6;
            rx.StartScan([new("one", 120.0e6, Mode.Am, BandPlan.AmAir), new("two", 125.0e6, Mode.Am, BandPlan.AmAir)]);
            Assert.True(Wait(() => rx.ScanListening && rx.FrequencyHz == 125.0e6, 3), "did not stop on the carrier");
            rx.StopScan();                                       // it stays on the channel it had stopped on
            Assert.True(Wait(() => !hosts.Started[0].Real.Scanning, 2));
            Assert.Equal(125.0e6, hosts.Started[0].Real.FrequencyHz);

            hosts.AskForANewOne(0);

            Assert.True(Wait(() => hosts.Started.Count == 2, 10), "no second helper");
            var second = hosts.Started[1];
            Assert.True(Wait(() => second.Real.IsRunning, 10), "the second helper is not running");
            Assert.False(second.Real.Scanning);
            Assert.Equal(125.0e6, second.Real.FrequencyHz);
            Assert.Equal(Mode.Am, second.Real.Mode);
            Assert.Equal(BandPlan.AmAir, second.Real.BandwidthHz);
            Assert.True(Wait(() => rx.IsRunning, 5));
            Assert.Equal(125.0e6, rx.FrequencyHz);
            Assert.Equal(Mode.Am, rx.Mode);
        }
        finally
        {
            SimpleScanner.Radio.RadioLog.FileOverride = null;
            File.Delete(log);
        }
    }

    // A new helper whose radio will not start (it was unplugged, say) stays: switching on later works.
    [Fact]
    public void ANewHelperWhoseRadioWillNotStartCanBeStartedLater()
    {
        var log = Path.GetTempFileName();
        SimpleScanner.Radio.RadioLog.FileOverride = log;
        var hosts = new List<(FakeSource Source, Receiver Real, Action<string>? Ask)>();
        (Stream, Stream, Action?) Launch()
        {
            var down = new AnonymousPipeServerStream(PipeDirection.Out);
            var downEnd = new AnonymousPipeClientStream(PipeDirection.In, down.ClientSafePipeHandle);
            var up = new AnonymousPipeServerStream(PipeDirection.Out);
            var upEnd = new AnonymousPipeClientStream(PipeDirection.In, up.ClientSafePipeHandle);
            var source = new FakeSource();
            if (hosts.Count > 0) source.FailToStart = "Receiver not found.";
            var real = new Receiver(source, playAudio: false, sampleRate: 2.4e6);
            int index = hosts.Count;
            var thread = new Thread(() =>
            {
                ReceiverHost.Run(real, downEnd, up,
                    handOver: ask => hosts[index] = hosts[index] with { Ask = ask },
                    exit: () => { real.Stop(); up.Dispose(); });
                up.Dispose();
            }) { IsBackground = true };
            hosts.Add((source, real, null));
            thread.Start();
            return (down, upEnd, () => thread.Join(3000));
        }
        try
        {
            using var rx = new RemoteReceiver(Launch, replug: _ => { });
            Assert.True(rx.WaitForFirstReport(5000));
            rx.Start();
            string? told = null;
            rx.Failed += why => told = why;
            Assert.True(Wait(() => hosts[0].Ask is not null, 2));
            hosts[0].Ask!("reported unplugged by the API, and would not restart");
            Assert.True(Wait(() => told is not null, 10), "nothing was said");
            Assert.Contains("was unplugged", told);
            Assert.Equal(2, hosts.Count);
            hosts[1].Source.FailToStart = null;          // plugged back in
            rx.Start();
            Assert.True(hosts[1].Real.IsRunning);
        }
        finally
        {
            SimpleScanner.Radio.RadioLog.FileOverride = null;
            File.Delete(log);
        }
    }

    // The window's own receiver runs in a helper too. Choosing another radio starts a helper for it at the next
    // start, with what the first had been given; a helper that has gone is replaced when the radio is switched on.
    [Fact]
    public void TheWindowsOwnHelperTakesAnotherRadioAndComesBackAfterItWent()
    {
        var log = Path.GetTempFileName();
        SimpleScanner.Radio.RadioLog.FileOverride = log;
        var hosts = new List<(string? Serial, FakeSource Source, Receiver Real, AnonymousPipeServerStream Up, Thread Thread)>();
        (Stream, Stream, Action?) Launch(string? serial)
        {
            var down = new AnonymousPipeServerStream(PipeDirection.Out);
            var downEnd = new AnonymousPipeClientStream(PipeDirection.In, down.ClientSafePipeHandle);
            var up = new AnonymousPipeServerStream(PipeDirection.Out);
            var upEnd = new AnonymousPipeClientStream(PipeDirection.In, up.ClientSafePipeHandle);
            var source = new FakeSource { Serial = serial };
            var real = new Receiver(source, playAudio: false, sampleRate: 2.4e6);
            var thread = new Thread(() =>
            {
                ReceiverHost.Run(real, downEnd, up, exit: () => { real.Stop(); up.Dispose(); });
                up.Dispose();
            }) { IsBackground = true };
            hosts.Add((serial, source, real, up, thread));
            thread.Start();
            return (down, upEnd, () => thread.Join(3000));
        }
        try
        {
            using var rx = new RemoteReceiver(Launch, null, "AAA", changeable: true, replug: _ => { });
            Assert.True(rx.WaitForFirstReport(5000));
            Assert.Equal("AAA", rx.Source.Serial);
            rx.SquelchDb = -61;
            rx.Source.GainStep = 2;
            rx.SetMode(Mode.Am, BandPlan.AmAir);
            rx.FrequencyHz = 121.5e6;
            rx.Start();
            Assert.True(hosts[0].Real.IsRunning);

            // Another radio chosen while running: nothing changes until it is stopped and started, as in this program.
            rx.Source.Serial = "BBB";
            Assert.Equal("BBB", rx.Source.Serial);
            Assert.Single(hosts);
            rx.Stop();
            rx.Start();
            Assert.Equal(2, hosts.Count);
            Assert.True(hosts[0].Thread.Join(3000), "the first helper did not go");
            Assert.False(hosts[0].Real.IsRunning);
            Assert.Equal("BBB", hosts[1].Serial);
            Assert.True(hosts[1].Real.IsRunning);
            Assert.Equal(-61, hosts[1].Real.SquelchDb);
            Assert.Equal(2, hosts[1].Source.GainStep);
            Assert.Equal(Mode.Am, hosts[1].Real.Mode);
            Assert.True(Wait(() => rx.IsRunning && rx.Source.Serial == "BBB", 3), "the window does not see the new helper");

            // Started again on the same radio: the same helper.
            rx.Stop();
            rx.Start();
            Assert.Equal(2, hosts.Count);

            // The helper goes (it crashed): the window is told, and switching on starts a new one for the same radio.
            string? told = null;
            rx.Failed += why => told = why;
            hosts[1].Up.Dispose();
            Assert.True(Wait(() => told is not null, 5), "nothing was said");
            Assert.False(rx.IsRunning);
            rx.Start();
            Assert.Equal(3, hosts.Count);
            Assert.Equal("BBB", hosts[2].Serial);
            Assert.True(hosts[2].Real.IsRunning);
            Assert.Equal(-61, hosts[2].Real.SquelchDb);
        }
        finally
        {
            SimpleScanner.Radio.RadioLog.FileOverride = null;
            File.Delete(log);
        }
    }

    [Fact]
    public void AFurtherRadiosHelperKeepsItsRadio()
    {
        using var pair = new Pair();
        Assert.False(pair.Remote.CanChangeRadio);
        Assert.Throws<NotSupportedException>(() => pair.Remote.Source.Serial = "other");
    }
}
