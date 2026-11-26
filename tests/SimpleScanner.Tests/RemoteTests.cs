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

    // The report is one reading: the DSP thread opens the squelch and enters Listen together, so no report may
    // say "listening" with the squelch shut while the carrier stays on. Reports are taken as fast as they can be,
    // from before the scan starts until well after it has stopped on the channel, over several scans.
    [Fact]
    public void AReportNeverSaysListeningWithTheSquelchShut()
    {
        var source = new FakeSource { CarrierHz = 125.0e6 };
        using var real = new Receiver(source, playAudio: false, sampleRate: 2.4e6);
        real.SquelchDb = -60;
        real.ScanHangSeconds = 0.2;
        ScanChannel[] list =
        [
            new("one", 120.0e6, Mode.Am, BandPlan.AmAir), new("two", 125.0e6, Mode.Am, BandPlan.AmAir),
            new("three", 130.0e6, Mode.Am, BandPlan.AmAir),
        ];
        real.Start();
        int reports = 0, listening = 0;
        var mixed = new List<string>();
        for (int scan = 0; scan < 6; scan++)
        {
            var stop = new ManualResetEventSlim();
            var taker = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    var s = ReceiverHost.Snapshot(real, 0);
                    reports++;
                    if (!s.ScanListening) continue;
                    listening++;
                    if (!s.SquelchOpen || s.ScanChannel?.FrequencyHz != s.FrequencyHz)
                        lock (mixed) mixed.Add($"listening, squelch {(s.SquelchOpen ? "open" : "shut")}, on {s.FrequencyHz} for {s.ScanChannel?.Name}");
                }
            }) { IsBackground = true };
            taker.Start();
            Thread.Sleep(20);
            real.StartScan(list);
            Assert.True(Wait(() => real.ScanListening, 3), "did not stop on the carrier");
            Thread.Sleep(300);
            stop.Set();
            Assert.True(taker.Join(2000));
            real.StopScan();
        }
        Assert.True(listening > 0, $"none of {reports} reports while listening");
        Assert.True(mixed.Count == 0, $"{mixed.Count} mixed of {reports} reports: {string.Join("; ", mixed.Take(3))}");
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
        /// <summary>Called for each helper's radio and receiver as it is made, before the helper serves it.</summary>
        public Action<FakeSource, Receiver>? Made;
        /// <summary>Every message sent to each helper, in the order sent (its input, as written).</summary>
        public readonly List<Tap> Sent = [];

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
            Made?.Invoke(source, real);
            var tap = new Tap(down);
            Sent.Add(tap);
            thread.Start();
            return (tap, upEnd, () => thread.Join(3000));
        }

        /// <summary>The helper started as number <paramref name="index"/> says its radio has jammed and goes.</summary>
        public void AskForANewOne(int index)
        {
            Assert.True(Wait(() => Started[index].Ask is not null, 2));
            Started[index].Ask!("stopped sending samples (test)");
        }
    }

    /// <summary>A stream that passes on what is written to it and keeps a copy: the messages to a helper, in order.</summary>
    private sealed class Tap(Stream to) : Stream
    {
        private readonly MemoryStream _copy = new();

        /// <summary>The operation of each message so far ("set MultiMonitor" for a setting), in order.</summary>
        public List<string> Ops()
        {
            string text;
            lock (_copy) text = System.Text.Encoding.UTF8.GetString(_copy.ToArray());
            return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                var m = System.Text.Json.JsonDocument.Parse(line).RootElement;
                string op = m.GetProperty("Op").GetString()!;
                return m.TryGetProperty("Name", out var name) ? $"{op} {name.GetString()}" : op;
            })];
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_copy) _copy.Write(buffer);
            to.Write(buffer);
        }
        public override void Flush() => to.Flush();
        protected override void Dispose(bool disposing)
        {
            if (disposing) to.Dispose();
            base.Dispose(disposing);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
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

    // ---- Multi Monitor through the helper -----------------------------------------------------------

    // Two channels of one window (the pretend radio at 2.4 MS/s is 1.92 MHz wide) and one a retune away.
    private const double A = 121.6e6, B = 121.8e6;
    private static readonly ScanChannel[] MultiList =
    [
        new("a", A, Mode.Am, BandPlan.AmAir), new("b", B, Mode.Am, BandPlan.AmAir), new("far", 130.0e6, Mode.Am, BandPlan.AmAir),
    ];

    /// <summary>The four slots in a word: the name of the channel each holds, or a dash.</summary>
    private static string Held(IReceiver rx) => string.Join(" ", rx.Slots.Select(s => s.Holding ? s.Channel?.Name ?? "?" : "-"));

    private static void Carriers(FakeSource source)
    {
        source.Add(A, 0.05, 700);
        source.Add(B, 0.03, 1100);
    }

    [Fact]
    public void MultiMonitorWorksThroughTheHelper()
    {
        using var pair = new Pair();
        var rx = pair.Remote;
        Assert.Equal("? - - -", Held(rx));                  // outside a scan the first slot is the one channel
        rx.SquelchDb = -60;
        rx.ScanSkipSeconds = 30;
        rx.MultiMonitor = true;
        rx.MultiHangSeconds = 3;
        Assert.True(rx.MultiMonitor);                       // here at once...
        Assert.Equal(3, rx.MultiHangSeconds);
        Assert.True(Wait(() => pair.Real.MultiMonitor && pair.Real.MultiHangSeconds == 3, 2));     // ...and in the helper
        Carriers(pair.Source);
        rx.Start();
        rx.StartScan(MultiList);

        Assert.True(Wait(() => Held(rx) == "a b - -", 3), Held(rx));
        var slots = rx.Slots;
        Assert.Equal(4, slots.Count);
        Assert.Equal([0, 1, 2, 3], slots.Select(s => s.Seat));
        Assert.Equal((A, Mode.Am, BandPlan.AmAir), (slots[0].FrequencyHz, slots[0].Mode, slots[0].BandwidthHz));
        Assert.Equal((B, Mode.Am, BandPlan.AmAir), (slots[1].FrequencyHz, slots[1].Mode, slots[1].BandwidthHz));
        Assert.Equal(A, slots[0].Channel?.FrequencyHz);
        Assert.True(slots[0].SquelchOpen && slots[1].SquelchOpen);
        Assert.InRange(slots[0].LevelDb, -32, -20);
        Assert.True(rx.ScanListening && rx.SquelchOpen);
        Assert.Equal("a", rx.ScanChannel?.Name);
        Thread.Sleep(200);                                  // once the reports have caught up, still so
        Assert.True(rx.MultiMonitor);
        Assert.Equal(3, rx.MultiHangSeconds);

        // Skip the second slot: only it is let go; the first holds its channel all the while.
        rx.SkipSlot(1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 0.8)
        {
            Assert.StartsWith("a ", Held(rx));
            Thread.Sleep(5);
        }
        Assert.True(Wait(() => Held(rx) == "a - - -", 3), Held(rx));
        Assert.Equal("a - - -", Held(pair.Real));

        // Avoid the first: the answer is its channel, and the count is up in the report that came with it.
        Assert.Equal(0, rx.AvoidedCount);
        Assert.Equal("a", rx.AvoidSlot(0)?.Name);
        Assert.Equal(1, rx.AvoidedCount);
        Assert.Equal(1, pair.Real.AvoidedCount);
        Assert.True(Wait(() => Held(rx) == "- - - -", 1), Held(rx));
        // A slot that holds nothing, or no slot at all: nothing happens, and nothing is thrown.
        Assert.Null(rx.AvoidSlot(2));
        Assert.Null(rx.AvoidSlot(9));
        rx.SkipSlot(-1);
        Assert.Equal(1, rx.AvoidedCount);

        // Stopping the scan: one channel again, here at once.
        rx.StopScan();
        Assert.Equal("? - - -", Held(rx));
        Assert.True(rx.Slots[0].Holding);
        // A scan of nothing but channels the receiver refuses is no scan: it stays on its channel, here at once too.
        Assert.True(BandPlan.IsBlocked(880e6, 12500));
        rx.StartScan([new("cell", 880e6, Mode.Nfm, 12500)]);
        Assert.Equal("? - - -", Held(rx));
        Thread.Sleep(200);
        Assert.Equal("? - - -", Held(rx));
        Assert.False(pair.Real.Scanning);
        rx.Stop();
    }

    // A helper whose radio jammed is replaced: the new one is put in the mode, with the same hang time, before its
    // scan is started again, and holds the channels again.
    [Fact]
    public void ANewHelperIsInMultiMonitorBeforeItsScanStarts()
    {
        var log = Path.GetTempFileName();
        SimpleScanner.Radio.RadioLog.FileOverride = log;
        var hosts = new Hosts { Made = (source, _) => Carriers(source) };
        try
        {
            using var rx = new RemoteReceiver(hosts.Launch, replug: _ => { });
            Assert.True(rx.WaitForFirstReport(5000));
            rx.SquelchDb = -60;
            rx.MultiMonitor = true;
            rx.MultiHangSeconds = 4;
            rx.Start();
            rx.StartScan(MultiList);
            Assert.True(Wait(() => Held(rx) == "a b - -", 3), Held(rx));

            hosts.AskForANewOne(0);

            Assert.True(Wait(() => hosts.Started.Count == 2, 10), "no second helper");
            var second = hosts.Started[1];
            Assert.True(Wait(() => second.Real.IsRunning && second.Real.Scanning, 10), "the second helper is not scanning");
            Assert.True(second.Real.MultiMonitor);
            Assert.Equal(4, second.Real.MultiHangSeconds);
            Assert.True(Wait(() => Held(second.Real) == "a b - -", 3), Held(second.Real));
            Assert.True(Wait(() => rx.IsRunning && Held(rx) == "a b - -", 5), "the window does not see the new helper's slots: " + Held(rx));
            Assert.True(rx.MultiMonitor);
            Assert.Equal(4, rx.MultiHangSeconds);

            // The new helper was given the mode and the hang time before it was started, and started before its scan.
            var ops = hosts.Sent[1].Ops();
            int mode = ops.IndexOf("set MultiMonitor"), hang = ops.IndexOf("set MultiHangSeconds");
            int start = ops.IndexOf("start"), scan = ops.IndexOf("startScan");
            Assert.True(mode >= 0 && hang >= 0 && mode < start && hang < start && start < scan, string.Join(", ", ops));
        }
        finally
        {
            SimpleScanner.Radio.RadioLog.FileOverride = null;
            File.Delete(log);
        }
    }

    // A report without the slots (or with too few, or with an empty one) is taken: the window sees four, empty where nothing was said.
    [Fact]
    public void AReportWithoutSlotsIsTaken()
    {
        var down = new AnonymousPipeServerStream(PipeDirection.Out);
        var up = new AnonymousPipeServerStream(PipeDirection.Out);
        var upEnd = new AnonymousPipeClientStream(PipeDirection.In, up.ClientSafePipeHandle);
        using var helper = new StreamWriter(up) { AutoFlush = true };
        using var rx = new RemoteReceiver(down, upEnd);
        void Report(string status) => helper.WriteLine("""{"Op":"status","Status":""" + status + "}");
        Assert.Equal("- - - -", Held(rx));                  // before any report: four empty slots

        Report("""{"Seq":0,"IsRunning":true,"FrequencyHz":121500000}""");
        Assert.True(rx.WaitForFirstReport(2000));
        Assert.Equal(121.5e6, rx.FrequencyHz);
        Assert.Equal("- - - -", Held(rx));
        Assert.Equal([0, 1, 2, 3], rx.Slots.Select(s => s.Seat));

        Report("""{"Seq":0,"FrequencyHz":121600000,"Slots":null}""");
        Assert.True(Wait(() => rx.FrequencyHz == 121.6e6, 2));
        Assert.Equal("- - - -", Held(rx));

        Report("""{"Seq":0,"FrequencyHz":121700000,"Slots":[{"Seat":0,"Holding":true,"FrequencyHz":121700000,"Mode":0,"BandwidthHz":8000,"LevelDb":-30},null]}""");
        Assert.True(Wait(() => rx.FrequencyHz == 121.7e6, 2));
        Assert.Equal("? - - -", Held(rx));
        Assert.Equal(121.7e6, rx.Slots[0].FrequencyHz);
        Assert.Equal([0, 1, 2, 3], rx.Slots.Select(s => s.Seat));
        Assert.Equal(4, rx.Slots.Count);
    }

    [Fact]
    public void AFurtherRadiosHelperKeepsItsRadio()
    {
        using var pair = new Pair();
        Assert.False(pair.Remote.CanChangeRadio);
        Assert.Throws<NotSupportedException>(() => pair.Remote.Source.Serial = "other");
    }
}
