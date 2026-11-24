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
        Assert.Equal(4, pair.Real.ResumeSeconds);
        Assert.Equal(TimeSpan.FromMinutes(60), pair.Real.AvoidTime);
        Assert.Equal(Mode.Am, pair.Real.Mode);
        // It stays so once the reports catch up.
        Thread.Sleep(200);
        Assert.Equal(-61, rx.SquelchDb);
        Assert.Equal(121.5e6, rx.FrequencyHz);
        Assert.Equal(4, rx.ResumeSeconds);
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
}
