using System.Diagnostics;
using System.Runtime.InteropServices;
using SimpleScanner.Dsp;

namespace SimpleScanner.Tests;

/// <summary>
/// Multi Monitor: a scan that holds up to four active channels of a window at once, each in a slot and a
/// seat of its own. The pretend radio sends several carriers, each with its own tone, so that what comes
/// out of the speakers says which channel is where.
/// </summary>
public class MultiMonitorTests
{
    // Five channels of one window (the pretend radio is 1.92 MHz wide) and one a retune away.
    private const double A = 121.6e6, B = 121.8e6, C = 121.5e6, D = 121.9e6, E = 121.7e6, Far = 130.0e6;
    // No tone is a harmonic of another.
    private const double ToneA = 700, ToneB = 1100, ToneC = 1500, ToneD = 1900, ToneE = 2300;
    private static readonly ScanChannel[] List =
    [
        new("a", A, Mode.Am, BandPlan.AmAir), new("b", B, Mode.Am, BandPlan.AmAir), new("c", C, Mode.Am, BandPlan.AmAir),
        new("d", D, Mode.Am, BandPlan.AmAir), new("e", E, Mode.Am, BandPlan.AmAir), new("far", Far, Mode.Am, BandPlan.AmAir),
    ];

    private static Receiver Make(FakeSource source, bool multi = true) =>
        new(source, playAudio: false, sampleRate: 2.4e6) { SquelchDb = -60, MultiMonitor = multi };

    private static bool Wait(Func<bool> condition, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    /// <summary>The four slots in a word: the name of the channel each holds, or a dash.</summary>
    private static string Held(Receiver rx) => string.Join(" ", rx.Slots.Select(s => s.Holding ? s.Channel?.Name ?? "?" : "-"));

    /// <summary>The slots must show this, and go on showing it for the given time.</summary>
    private static void Stays(Receiver rx, string held, double seconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            Assert.Equal(held, Held(rx));
            Thread.Sleep(5);
        }
    }

    /// <summary>Takes down what the receiver sends to the speakers (left and right interleaved).</summary>
    private sealed class Tape : IDisposable
    {
        private readonly Receiver _rx;
        private readonly List<float> _frames = [];

        public Tape(Receiver rx)
        {
            _rx = rx;
            rx.AudioOutput += Take;
        }

        private void Take(ReadOnlySpan<float> a)
        {
            lock (_frames) _frames.AddRange(a);
        }

        /// <summary>What has been taken down since the last cut.</summary>
        public float[] Cut()
        {
            lock (_frames)
            {
                var all = _frames.ToArray();
                _frames.Clear();
                return all;
            }
        }

        public void Dispose() => _rx.AudioOutput -= Take;
    }

    private static float[] Listen(Receiver rx, double seconds)
    {
        using var tape = new Tape(rx);
        Thread.Sleep((int)(seconds * 1000));
        return tape.Cut();
    }

    /// <summary>How strong a tone is in the left and in the right speaker (its amplitude).</summary>
    private static (double Left, double Right) Tone(ReadOnlySpan<float> frames, double hz)
    {
        double lc = 0, ls = 0, rc = 0, rs = 0;
        int n = frames.Length / 2;
        for (int k = 0; k < n; k++)
        {
            double w = 2 * Math.PI * hz * k / ChannelReceiver.AudioRate, c = Math.Cos(w), s = Math.Sin(w);
            lc += frames[2 * k] * c;
            ls += frames[2 * k] * s;
            rc += frames[2 * k + 1] * c;
            rs += frames[2 * k + 1] * s;
        }
        return (2 * Math.Sqrt(lc * lc + ls * ls) / Math.Max(n, 1), 2 * Math.Sqrt(rc * rc + rs * rs) / Math.Max(n, 1));
    }

    /// <summary>
    /// Where a tone is heard: seat 0 is 33% left (the right speaker at 0.67 of the left), 1 is 33% right,
    /// 2 hard left, 3 hard right, and -1 the centre.
    /// </summary>
    private static void AssertSeat(ReadOnlySpan<float> frames, double toneHz, int seat)
    {
        var (l, r) = Tone(frames, toneHz);
        string heard = $"the {toneHz} Hz tone: left {l:F4}, right {r:F4}";
        Assert.True(Math.Max(l, r) > 0.03, "not heard, " + heard);
        switch (seat)
        {
            case -1: Assert.True(Math.Abs(r / l - 1) < 0.03, "not in the centre, " + heard); break;
            case 0: Assert.True(Math.Abs(r / l - 0.67) < 0.03, "not 33% left, " + heard); break;
            case 1: Assert.True(Math.Abs(l / r - 0.67) < 0.03, "not 33% right, " + heard); break;
            case 2: Assert.True(r < 0.02 * l, "not hard left, " + heard); break;
            default: Assert.True(l < 0.02 * r, "not hard right, " + heard); break;
        }
    }

    private static void AssertSilent(ReadOnlySpan<float> frames, double toneHz)
    {
        var (l, r) = Tone(frames, toneHz);
        Assert.True(Math.Max(l, r) < 0.005, $"the {toneHz} Hz tone is still heard: left {l:F4}, right {r:F4}");
    }

    /// <summary>
    /// The tone never dropped out: it is there at its usual strength in every 5 ms of the recording, taken
    /// every 1.25 ms. Each piece is weighted towards its middle (a Hann window), which at this length has its
    /// nulls on the other tones, 400 Hz apart, and a gap of 2 ms takes the piece it falls in to under half.
    /// </summary>
    private static void AssertUnbroken(ReadOnlySpan<float> frames, double toneHz)
    {
        const int piece = ChannelReceiver.AudioRate / 200;
        int n = frames.Length / 2;
        Assert.True(n >= 20 * piece, "too little was recorded");
        var levels = new List<double>();
        for (int at = 0; at + piece <= n; at += piece / 4)
        {
            double lc = 0, ls = 0, rc = 0, rs = 0;
            for (int k = 0; k < piece; k++)
            {
                double w = 2 * Math.PI * toneHz * k / ChannelReceiver.AudioRate, hann = 1 - Math.Cos(2 * Math.PI * (k + 0.5) / piece);
                double c = hann * Math.Cos(w), s = hann * Math.Sin(w);
                lc += frames[2 * (at + k)] * c;
                ls += frames[2 * (at + k)] * s;
                rc += frames[2 * (at + k) + 1] * c;
                rs += frames[2 * (at + k) + 1] * s;
            }
            levels.Add(2 * Math.Sqrt(Math.Max(lc * lc + ls * ls, rc * rc + rs * rs)) / piece);
        }
        double usual = levels.Order().ElementAt(levels.Count / 2), least = levels.Min();
        Assert.True(usual > 0.03, $"the {toneHz} Hz tone is not heard");
        Assert.True(least > 0.7 * usual, $"the {toneHz} Hz tone fell to {least:F4} from {usual:F4}, {levels.IndexOf(least) * (piece / 4) * 1000 / ChannelReceiver.AudioRate} ms into the recording");
    }

    [Fact]
    public void TheCheckForUnbrokenSoundNoticesAGapOfTwoMilliseconds()
    {
        // Three tones together, as three slots give them; then the same with 2 ms of one of them missing.
        var frames = new float[2 * ChannelReceiver.AudioRate / 2];
        void Fill(int gapAt, int gap)
        {
            for (int k = 0; k < frames.Length / 2; k++)
            {
                double t = (double)k / ChannelReceiver.AudioRate;
                double a = k >= gapAt && k < gapAt + gap ? 0 : 0.15 * Math.Sin(2 * Math.PI * ToneA * t);
                double others = 0.15 * Math.Sin(2 * Math.PI * ToneB * t) + 0.15 * Math.Sin(2 * Math.PI * ToneC * t);
                frames[2 * k] = (float)(a + others);
                frames[2 * k + 1] = (float)(0.67 * a + others);
            }
        }
        Fill(0, 0);
        AssertUnbroken(frames, ToneA);
        AssertUnbroken(frames, ToneB);
        foreach (int gapAt in new[] { 9000, 9120, 9240, 9333 })
        {
            Fill(gapAt, ChannelReceiver.AudioRate * 2 / 1000);
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertUnbroken(frames, ToneA));
            AssertUnbroken(frames, ToneB);
        }
    }

    [Fact]
    public void TwoCarriersInAWindowTakeTwoSlotsOneEachSide()
    {
        var source = new FakeSource();
        source.Add(A, 0.05, ToneA);
        source.Add(B, 0.03, ToneB);
        using var rx = Make(source);
        rx.Start();
        rx.StartScan(List);

        // The stronger in the first slot, the other in the second, and nothing in the rest.
        Assert.True(Wait(() => Held(rx) == "a b - -", 3), Held(rx));
        var slots = rx.Slots;
        Assert.Equal(4, slots.Count);
        Assert.Equal([0, 1, 2, 3], slots.Select(s => s.Seat));
        Assert.Equal((A, Mode.Am, BandPlan.AmAir), (slots[0].FrequencyHz, slots[0].Mode, slots[0].BandwidthHz));
        Assert.Equal((B, Mode.Am, BandPlan.AmAir), (slots[1].FrequencyHz, slots[1].Mode, slots[1].BandwidthHz));
        Assert.True(slots[0].SquelchOpen && slots[1].SquelchOpen);
        Assert.InRange(slots[0].LevelDb, -32, -20);                 // an amplitude of 0.05
        Assert.InRange(slots[1].LevelDb, -37, -25);
        Assert.Null(slots[0].Digital);
        Assert.False(slots[2].Holding || slots[3].Holding);
        Assert.Null(slots[2].Channel);

        // The receiver speaks for the lowest slot that holds a channel, and its meter shows the strongest.
        Assert.True(rx.ScanListening);
        Assert.True(rx.SquelchOpen);
        Assert.Equal("a", rx.ScanChannel?.Name);
        Assert.Equal(A, rx.FrequencyHz);
        Assert.Equal(Mode.Am, rx.Mode);
        Assert.InRange(rx.LevelDb, -32, -20);

        // By ear: one 33% left, the other 33% right.
        Thread.Sleep(300);
        var heard = Listen(rx, 0.3);
        AssertSeat(heard, ToneA, 0);
        AssertSeat(heard, ToneB, 1);

        // The radio stays on the window while they are held.
        int tunes = source.Tunes;
        Thread.Sleep(300);
        Assert.Equal(tunes, source.Tunes);
        Assert.Equal("a b - -", Held(rx));
        rx.Stop();
    }

    [Fact]
    public void WithOneCarrierTheModeChangesNothing()
    {
        // The same scan with the mode off and on: the same channel, the same sound in the centre, and the scan
        // moves on as long after the carrier has gone.
        static (string? Name, double Left, double Right, double Gone) Run(bool multi)
        {
            var source = new FakeSource();
            var a = source.Add(A, 0.05, ToneA);
            using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6)
                { SquelchDb = -60, ScanHangSeconds = 0.4, MultiHangSeconds = 0.4, MultiMonitor = multi };
            rx.Start();
            // Not scanning, the first slot is the one channel being received, whatever the mode.
            Assert.Equal("? - - -", Held(rx));
            Assert.Equal(rx.FrequencyHz, rx.Slots[0].FrequencyHz);

            rx.StartScan(List);
            Assert.True(Wait(() => rx.ScanListening, 3), "did not stop on the carrier");
            string? name = rx.ScanChannel?.Name;
            Assert.Equal(A, rx.FrequencyHz);
            Assert.True(rx.SquelchOpen);
            Assert.Equal("a - - -", Held(rx));
            Assert.True(rx.Slots[0].SquelchOpen);
            Thread.Sleep(250);
            var (l, r) = Tone(Listen(rx, 0.3), ToneA);

            int tunes = source.Tunes;
            a.On = false;
            var sw = Stopwatch.StartNew();
            Assert.True(Wait(() => !rx.ScanListening, 3), "did not leave the quiet channel");
            double gone = sw.Elapsed.TotalSeconds;
            Assert.True(Wait(() => source.Tunes > tunes, 1), "did not move on");
            Assert.True(rx.Scanning);
            rx.Stop();
            return (name, l, r, gone);
        }

        var off = Run(false);
        var on = Run(true);
        Assert.Equal("a", off.Name);
        Assert.Equal("a", on.Name);
        Assert.True(off.Left > 0.03, $"mode off: the tone is at {off.Left:F4}");
        Assert.True(Math.Abs(off.Right / off.Left - 1) < 0.01, $"mode off: left {off.Left:F4}, right {off.Right:F4}");
        Assert.True(Math.Abs(on.Right / on.Left - 1) < 0.01, $"mode on: left {on.Left:F4}, right {on.Right:F4}");
        Assert.True(Math.Abs(on.Left / off.Left - 1) < 0.02, $"left: {off.Left:F4} with the mode off, {on.Left:F4} with it on");
        // The squelch shuts 0.12 s after the carrier, then the hang time.
        Assert.InRange(off.Gone, 0.45, 1.2);
        Assert.InRange(on.Gone, 0.45, 1.2);
        Assert.True(Math.Abs(on.Gone - off.Gone) < 0.15, $"moved on after {off.Gone:F2} s with the mode off, {on.Gone:F2} s with it on");
    }

    [Fact]
    public void AThirdAndFourthTakeTheOuterSeatsAndAFifthWaitsForASlotGoneQuiet()
    {
        var source = new FakeSource();
        source.Add(A, 0.05, ToneA);
        var b = source.Add(B, 0.03, ToneB);
        using var rx = Make(source);
        rx.MultiHangSeconds = 30;
        rx.Start();
        rx.StartScan(List);
        Assert.True(Wait(() => Held(rx) == "a b - -", 3), Held(rx));
        Thread.Sleep(300);

        // Two more come up, one after the other: hard left and hard right, and the first two neither move nor drop out.
        using (var tape = new Tape(rx))
        {
            Thread.Sleep(100);
            source.Add(C, 0.05, ToneC);
            Assert.True(Wait(() => Held(rx) == "a b c -", 2), Held(rx));
            Thread.Sleep(100);
            source.Add(D, 0.05, ToneD);
            Assert.True(Wait(() => Held(rx) == "a b c d", 2), Held(rx));
            Thread.Sleep(300);
            var through = tape.Cut();
            AssertUnbroken(through, ToneA);
            AssertUnbroken(through, ToneB);
            AssertSeat(through, ToneA, 0);
            AssertSeat(through, ToneB, 1);
        }
        var heard = Listen(rx, 0.3);
        AssertSeat(heard, ToneA, 0);
        AssertSeat(heard, ToneB, 1);
        AssertSeat(heard, ToneC, 2);
        AssertSeat(heard, ToneD, 3);
        Assert.True(rx.Slots.All(s => s.Holding && s.SquelchOpen));

        // A fifth, with all four busy: it waits.
        source.Add(E, 0.05, ToneE);
        Stays(rx, "a b c d", 0.7);
        AssertSilent(Listen(rx, 0.2), ToneE);

        // One of the four stops sending. It would keep its slot for the hang time (30 s here), but the one
        // waiting has it instead, and its seat.
        b.On = false;
        Assert.True(Wait(() => Held(rx) == "a e c d", 2), Held(rx));
        Thread.Sleep(300);
        heard = Listen(rx, 0.3);
        AssertSeat(heard, ToneE, 1);
        AssertSeat(heard, ToneA, 0);
        AssertSeat(heard, ToneC, 2);
        AssertSeat(heard, ToneD, 3);
        rx.Stop();
    }

    [Fact]
    public void ADroppedCarrierKeepsItsSlotForTheHangTimeAndTheWindowIsLeftWhenAllAreFree()
    {
        var source = new FakeSource();
        var a = source.Add(A, 0.05, ToneA);
        var b = source.Add(B, 0.03, ToneB);
        using var rx = Make(source);
        rx.MultiHangSeconds = 1.0;
        rx.Start();
        rx.StartScan(List);
        Assert.True(Wait(() => Held(rx) == "a b - -", 3), Held(rx));
        Thread.Sleep(300);
        int tunes = source.Tunes;

        // The second goes quiet: well into the hang time it still has its slot, squelch shut.
        b.On = false;
        Assert.True(Wait(() => !rx.Slots[1].SquelchOpen, 1), "the squelch did not shut");
        Stays(rx, "a b - -", 0.6);
        Assert.True(rx.SquelchOpen);                        // the first is still open

        // It comes back inside the hang time (the reply): heard again, in the same seat.
        b.On = true;
        Assert.True(Wait(() => rx.Slots[1].SquelchOpen, 1), "the squelch did not open again");
        Assert.Equal("a b - -", Held(rx));
        Thread.Sleep(100);
        var heard = Listen(rx, 0.3);
        AssertSeat(heard, ToneB, 1);
        AssertSeat(heard, ToneA, 0);

        // Gone for good: the slot is free after the hang time and not before, and the other, alone now, is in the centre.
        b.On = false;
        var sw = Stopwatch.StartNew();
        Assert.True(Wait(() => Held(rx) == "a - - -", 3), Held(rx));
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.0, 2.2);      // not before the hang time; the upper mark is loose, for a busy machine
        Thread.Sleep(300);
        AssertSeat(Listen(rx, 0.3), ToneA, -1);
        Assert.True(rx.ScanListening);
        Assert.Equal(tunes, source.Tunes);                  // the radio has not left the window in all this

        // The last one goes too: only now does the scan move on.
        a.On = false;
        sw.Restart();
        Assert.True(Wait(() => !rx.ScanListening, 3), "did not let the last channel go");
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.0, 2.2);      // not before the hang time; the upper mark is loose, for a busy machine
        Assert.True(Wait(() => source.Tunes > tunes, 1), "did not move on");
        Assert.True(rx.Scanning);
        Assert.Equal("- - - -", Held(rx));
        rx.Stop();
    }

    [Fact]
    public void SkipSlotAndAvoidSlotLetGoOnlyTheirOwnChannel()
    {
        var source = new FakeSource();
        source.Add(A, 0.05, ToneA);
        source.Add(B, 0.03, ToneB);
        source.Add(C, 0.018, ToneC);
        using var rx = Make(source);
        rx.ScanSkipSeconds = 1.5;
        rx.Start();
        rx.StartScan(List);
        Assert.True(Wait(() => Held(rx) == "a b c -", 3), Held(rx));
        Thread.Sleep(300);

        // Skip the second: it is passed by although still there, and the others carry on unbroken.
        using (var tape = new Tape(rx))
        {
            Thread.Sleep(100);
            rx.SkipSlot(1);
            Assert.True(Wait(() => Held(rx) == "a - c -", 1), Held(rx));
            Stays(rx, "a - c -", 0.8);
            var through = tape.Cut();
            AssertUnbroken(through, ToneA);
            AssertUnbroken(through, ToneC);
            AssertSilent(through.AsSpan(through.Length / 2), ToneB);
        }
        // When the skip time is up it is back, in the lowest free slot.
        Assert.True(Wait(() => Held(rx) == "a b c -", 3), Held(rx));

        // The main Avoid does nothing in this mode; a slot's avoids its own channel only.
        Assert.Null(rx.AvoidChannel());
        Assert.Equal(0, rx.AvoidedCount);
        Assert.Equal("a", rx.AvoidSlot(0)?.Name);
        Assert.Equal(1, rx.AvoidedCount);
        Assert.True(Wait(() => Held(rx) == "- b c -", 1), Held(rx));
        Assert.Equal("b", rx.ScanChannel?.Name);            // the lowest slot holding a channel now
        Assert.Equal(B, rx.FrequencyHz);
        Stays(rx, "- b c -", 0.6);

        // An empty slot, or a number that is no slot: nothing happens.
        rx.SkipSlot(0);
        rx.SkipSlot(3);
        rx.SkipSlot(9);
        Assert.Null(rx.AvoidSlot(3));
        Assert.Null(rx.AvoidSlot(-1));
        Assert.Equal("- b c -", Held(rx));
        Assert.Equal(1, rx.AvoidedCount);
        rx.ClearAvoided();
        Assert.True(Wait(() => Held(rx) == "a b c -", 2), Held(rx));
        rx.Stop();
    }

    [Fact]
    public void SkipChannelLeavesTheWindowAndTheResumeTimeEndsTheStay()
    {
        var source = new FakeSource();
        source.Add(A, 0.05, ToneA);
        source.Add(B, 0.03, ToneB);
        using var rx = Make(source);
        rx.ScanSkipSeconds = 1.0;
        rx.Start();
        rx.StartScan(List);
        Assert.True(Wait(() => Held(rx) == "a b - -", 3), Held(rx));

        // The main Skip: both are let go and passed by, and the scan goes round without them.
        int tunes = source.Tunes;
        rx.SkipChannel();
        Assert.True(Wait(() => !rx.ScanListening && source.Tunes > tunes, 1), "did not leave the window");
        Stays(rx, "- - - -", 0.6);
        Assert.True(rx.Scanning);
        Assert.True(source.Tunes > tunes + 4, "the scan is not going round");
        Assert.True(Wait(() => Held(rx) == "a b - -", 2), Held(rx));

        // With a resume time the stay in the window ends though both are still there; they are passed by for a
        // while, and then it is back.
        rx.ScanSkipSeconds = 30;
        rx.ResumePassBySeconds = 1.0;
        rx.ResumeSeconds = 0.5;
        Assert.True(Wait(() => !rx.ScanListening, 2), "the resume time did not end the stay");
        var sw = Stopwatch.StartNew();
        Stays(rx, "- - - -", 0.6);
        Assert.True(Wait(() => Held(rx) == "a b - -", 2), Held(rx));
        // The stay is counted from the first slot that took a channel.
        Assert.True(Wait(() => !rx.ScanListening, 2), "the resume time did not end the second stay");
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.3, 2.8);  // a second passed by, half a second there (loose above, for a busy machine)
        rx.Stop();
    }

    [Fact]
    public void StoppingTheScanLeavesTheReceiverOnTheLowestHeldChannelInTheCentre()
    {
        var source = new FakeSource();
        source.Add(A, 0.05, ToneA);
        source.Add(B, 0.03, ToneB);
        source.Add(C, 0.018, ToneC);
        using var rx = Make(source);
        rx.ScanSkipSeconds = 30;
        rx.Start();
        rx.StartScan(List);
        Assert.True(Wait(() => Held(rx) == "a b c -", 3), Held(rx));
        rx.SkipSlot(0);
        Assert.True(Wait(() => Held(rx) == "- b c -", 1), Held(rx));
        Thread.Sleep(300);

        // StopScan: on "b", the lowest slot's channel, alone and in the centre, without a break in its sound.
        using (var tape = new Tape(rx))
        {
            Thread.Sleep(100);
            rx.StopScan();
            Assert.False(rx.Scanning);
            Assert.False(rx.ScanListening);
            Assert.Null(rx.ScanChannel);
            Assert.Equal(B, rx.FrequencyHz);
            Assert.Equal("? - - -", Held(rx));
            Assert.Equal(B, rx.Slots[0].FrequencyHz);
            Thread.Sleep(400);
            AssertUnbroken(tape.Cut(), ToneB);
        }
        Assert.True(rx.SquelchOpen);
        var heard = Listen(rx, 0.3);
        AssertSeat(heard, ToneB, -1);
        AssertSilent(heard, ToneA);
        AssertSilent(heard, ToneC);

        // Tuning by hand during a scan of this mode: on the frequency asked for, alone.
        rx.StartScan(List);
        Assert.True(Wait(() => Held(rx) == "a b c -", 3), Held(rx));
        rx.FrequencyHz = C;
        Assert.False(rx.Scanning);
        Assert.Equal(C, rx.FrequencyHz);
        Assert.Equal("? - - -", Held(rx));
        Thread.Sleep(400);
        heard = Listen(rx, 0.3);
        AssertSeat(heard, ToneC, -1);
        AssertSilent(heard, ToneA);
        AssertSilent(heard, ToneB);

        // Choosing a mode by hand ends it too, on the lowest slot's channel.
        rx.StartScan(List);
        Assert.True(Wait(() => Held(rx) == "a b c -", 3), Held(rx));
        rx.SkipSlot(0);
        Assert.True(Wait(() => Held(rx) == "- b c -", 1), Held(rx));
        rx.SetMode(Mode.Nfm, BandPlan.NfmNarrow);
        Assert.False(rx.Scanning);
        Assert.Equal((B, Mode.Nfm), (rx.FrequencyHz, rx.Mode));
        Assert.Equal("? - - -", Held(rx));

        // Stopping the radio in the middle of such a scan, and starting it again: one channel.
        rx.SetMode(Mode.Am, BandPlan.AmAir);
        rx.StartScan(List);
        Assert.True(Wait(() => Held(rx) == "a b c -", 3), Held(rx));
        rx.Stop();
        Assert.Equal("? - - -", Held(rx));
        Assert.Equal(A, rx.FrequencyHz);
        rx.Start();
        Assert.True(Wait(() => rx.SquelchOpen, 2), "not on the carrier after a stop and a start");
        Thread.Sleep(300);
        AssertSeat(Listen(rx, 0.3), ToneA, -1);
        rx.Stop();
    }

    [Fact]
    public void TheModeCanBeSwitchedDuringAScan()
    {
        var source = new FakeSource();
        source.Add(A, 0.05, ToneA);
        source.Add(B, 0.03, ToneB);
        source.Add(C, 0.018, ToneC);
        // Off to begin with: one channel at a time, the strongest.
        using var rx = Make(source, multi: false);
        rx.ScanSkipSeconds = 30;
        rx.Start();
        rx.StartScan(List);
        Assert.True(Wait(() => rx.ScanListening, 3));
        Assert.Equal("a - - -", Held(rx));
        Stays(rx, "a - - -", 0.3);

        // On: it keeps the one it has, and the others come up beside it.
        using (var tape = new Tape(rx))
        {
            Thread.Sleep(100);
            rx.MultiMonitor = true;
            Assert.True(Wait(() => Held(rx) == "a b c -", 2), Held(rx));
            Thread.Sleep(300);
            AssertUnbroken(tape.Cut(), ToneA);
        }
        var heard = Listen(rx, 0.3);
        AssertSeat(heard, ToneA, 0);
        AssertSeat(heard, ToneB, 1);
        AssertSeat(heard, ToneC, 2);

        // Off: the first slot's channel is the one channel again, in the centre; the others are let go.
        rx.MultiMonitor = false;
        Assert.True(Wait(() => Held(rx) == "a - - -", 1), Held(rx));
        Assert.True(rx.ScanListening);
        Assert.Equal("a", rx.ScanChannel?.Name);
        Thread.Sleep(300);
        heard = Listen(rx, 0.3);
        AssertSeat(heard, ToneA, -1);
        AssertSilent(heard, ToneB);
        AssertSilent(heard, ToneC);

        // Off while the first slot holds nothing: the window is looked over afresh, for one channel.
        rx.MultiMonitor = true;
        Assert.True(Wait(() => Held(rx) == "a b c -", 2), Held(rx));
        rx.SkipSlot(0);
        Assert.True(Wait(() => Held(rx) == "- b c -", 1), Held(rx));
        rx.MultiMonitor = false;
        Assert.True(Wait(() => Held(rx) == "b - - -", 2), Held(rx));
        Thread.Sleep(300);
        AssertSeat(Listen(rx, 0.3), ToneB, -1);

        // Back and forth in a hurry, in the middle of everything: nothing breaks, and it ends up with both
        // the channels that are not skipped.
        for (int k = 0; k < 60; k++)
        {
            rx.MultiMonitor = k % 2 == 0;
            Thread.Sleep(k * 7 % 23);
            _ = rx.Slots;
        }
        rx.MultiMonitor = true;
        Assert.True(Wait(() => rx.Slots.Where(s => s.Holding).Select(s => s.Channel?.Name).Order().SequenceEqual(["b", "c"]), 3), Held(rx));
        Assert.True(rx.IsRunning && rx.Scanning);

        // Outside a scan the mode does nothing.
        rx.StopScan();
        rx.MultiMonitor = false;
        rx.MultiMonitor = true;
        Assert.Equal("? - - -", Held(rx));
        Assert.True(Wait(() => rx.SquelchOpen, 1));
        rx.Stop();
    }

    [Fact]
    public void ChannelsOfTwoWindowsAreNeverHeldTogether()
    {
        var source = new FakeSource();
        source.Add(A, 0.05, ToneA);
        source.Add(B, 0.03, ToneB);
        source.Add(Far, 0.05, ToneC);
        using var rx = Make(source);
        // A short stay in each window, so that the scan goes back and forth between the two.
        rx.ResumeSeconds = 0.25;
        rx.ResumePassBySeconds = 0.3;
        rx.Start();
        rx.StartScan(List);
        var seen = new HashSet<string>();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 3)
        {
            var held = rx.Slots.Where(s => s.Holding).ToList();
            foreach (var s in held) seen.Add(s.Channel?.Name ?? "?");
            if (held.Count > 0)
                Assert.True(held.Max(s => s.FrequencyHz) - held.Min(s => s.FrequencyHz) < source.UsableBandwidthHz, Held(rx));
            Thread.Sleep(2);
        }
        Assert.Equal(["a", "b", "far"], seen.Order());
        rx.Stop();
    }

    /// <summary>A test file as discriminator samples in units of 2.5 kHz deviation, at 24 kHz (see Data/README.md).</summary>
    private static float[] Disc(string name)
    {
        var raw = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name)));
        var x = new float[raw.Length];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(raw[i] / 6553.6 * 1000 / 2500);
        return x;
    }

    // ---- one signal, one slot ----

    /// <summary>A tone as a discriminator recording: one second at 24 kHz, in units of the carrier's DeviationHz.</summary>
    private static float[] Sine(double hz, double depth = 1)
    {
        var x = new float[24000];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(depth * Math.Sin(2 * Math.PI * hz * i / 24000));
        return x;
    }

    /// <summary>Nine narrow FM channels 12.5 kHz apart, named by their place from the middle one: "-4" to "+4".</summary>
    private static ScanChannel[] Raster(double middleHz) =>
        Enumerable.Range(-4, 9).Select(k => new ScanChannel($"{k:+0;-0;0}", middleHz + 12500 * k, Mode.Nfm, BandPlan.NfmNarrow)).ToArray();

    [Fact]
    public void OneWideSignalOnAFinerRasterTakesOneSlot()
    {
        // A broadcast station (75 kHz... here 50 kHz of deviation) on a raster of 100 kHz with channels 200 kHz
        // wide: the channel on it and the one each side all hold it, and the two beyond touch it.
        var source = new FakeSource();
        source.Add(100.3e6, 0.05, deviation: Sine(1000)).DeviationHz = 50000;
        using var rx = Make(source);
        rx.ScanSkipSeconds = 1.0;
        rx.Start();
        rx.StartScan(Enumerable.Range(0, 7).Select(k => new ScanChannel($"100.{k}", 100.0e6 + 100e3 * k, Mode.Wfm, BandPlan.Wfm)).ToArray());
        Assert.Equal(1, rx.ScanWindows);

        // One slot, on the channel where it is strongest, and it stays one.
        Assert.True(Wait(() => Held(rx) == "100.3 - - -", 3), Held(rx));
        Stays(rx, "100.3 - - -", 1.0);

        // Skipped, it is not back at once on the next point of the raster: nothing is held until the skip is over.
        rx.SkipSlot(0);
        Assert.True(Wait(() => Held(rx) == "- - - -", 1), Held(rx));
        Stays(rx, "- - - -", 0.6);
        Assert.True(Wait(() => Held(rx) == "100.3 - - -", 2), Held(rx));
        rx.Stop();
    }

    [Fact]
    public void SwitchingTheModeOnDoesNotHandOutWhatTheLastLookQueued()
    {
        // With the mode off the scan stops on the station's strongest point and keeps the other points of the
        // raster as its next candidates. Switched on, those are not given the other slots.
        var source = new FakeSource();
        source.Add(100.3e6, 0.05, deviation: Sine(1000)).DeviationHz = 50000;
        using var rx = Make(source, multi: false);
        rx.Start();
        rx.StartScan(Enumerable.Range(0, 7).Select(k => new ScanChannel($"100.{k}", 100.0e6 + 100e3 * k, Mode.Wfm, BandPlan.Wfm)).ToArray());
        Assert.True(Wait(() => rx.ScanListening && Held(rx) == "100.3 - - -", 3), Held(rx));
        Thread.Sleep(100);
        rx.MultiMonitor = true;
        Stays(rx, "100.3 - - -", 1.0);
        rx.Stop();
    }

    [Fact]
    public void ACandidateThatNeverOpensDoesNotHideTheSignalBesideIt()
    {
        // In the middle channel two carriers 3 kHz apart: strong, but to the auto squelch it is noise (the
        // discriminator jumps at every beat), so the squelch never opens there. In the next channel a clean
        // signal 15 dB weaker, which the strong one would keep out of every look as its skirt.
        var source = new FakeSource();
        source.Add(155.3e6 - 1500, 0.035);
        source.Add(155.3e6 + 1500, 0.031);
        source.Add(155.3125e6, 0.008, deviation: Sine(1300, 0.8));
        using var rx = Make(source);
        rx.SquelchDb = -80;
        rx.AutoSquelch = true;
        rx.Start();
        rx.StartScan(Raster(155.3e6));
        // The strong one is tried, found to be nothing, and the window is looked over again without it.
        Assert.True(Wait(() => Held(rx) == "+1 - - -", 2), Held(rx));
        Assert.True(rx.Slots[0].SquelchOpen);
        // It is tried again every second or so, finds nothing each time, and the weak one keeps its slot.
        Stays(rx, "+1 - - -", 2.5);
        rx.Stop();
    }

    [Fact]
    public void TwoSignalsInNeighbouringChannelsStillTakeTwoSlots()
    {
        // Two transmitters 12.5 kHz apart, 3 dB between them: each is its own signal.
        var source = new FakeSource();
        source.Add(155.3e6, 0.05, deviation: Sine(900, 0.8));
        source.Add(155.3125e6, 0.035, deviation: Sine(1300, 0.8));
        using var rx = Make(source);
        rx.SquelchDb = -80;
        rx.Start();
        rx.StartScan(Raster(155.3e6));
        Assert.True(Wait(() => Held(rx) == "0 +1 - -", 3), Held(rx));
        Stays(rx, "0 +1 - -", 0.8);
        Assert.True(rx.Slots[0].SquelchOpen && rx.Slots[1].SquelchOpen);
        rx.Stop();
    }

    [Fact]
    public void AWeakSignalBesideAStrongOneWaitsUntilTheStrongOneHasGone()
    {
        // 40 dB weaker and in the next channel: while the strong one is on the air it cannot be told from its
        // skirt, and gets no slot. (It is well over the squelch level, and without the guard it would have one.)
        var source = new FakeSource();
        var strong = source.Add(155.3e6, 0.05, deviation: Sine(900, 0.8));
        source.Add(155.3125e6, 0.0005, deviation: Sine(1300, 0.8));
        using var rx = Make(source);
        rx.SquelchDb = -80;
        rx.MultiHangSeconds = 1.0;
        rx.Start();
        rx.StartScan(Raster(155.3e6));
        Assert.True(Wait(() => Held(rx) == "0 - - -", 3), Held(rx));
        Stays(rx, "0 - - -", 1.0);

        // The strong one stops: the weak one has a slot at once, while the first is still in its hang time.
        strong.On = false;
        Assert.True(Wait(() => Held(rx) == "0 +1 - -", 0.9), Held(rx));
        Assert.True(Wait(() => rx.Slots[1].SquelchOpen, 1));
        Assert.InRange(rx.Slots[1].LevelDb, -72, -60);
        Assert.True(Wait(() => Held(rx) == "- +1 - -", 2), Held(rx));

        // And when the strong one comes back, the weak one keeps the slot it has.
        strong.On = true;
        Assert.True(Wait(() => Held(rx) == "0 +1 - -", 1), Held(rx));
        Stays(rx, "0 +1 - -", 0.5);
        rx.Stop();
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(-80)]
    [InlineData(-100)]
    public void ASignalWiderThanItsChannelTakesOneSlotWhateverTheSquelch(double squelchDb)
    {
        // The P25 test recording has stretches of full-scale noise, which as a radio signal is 5 kHz of
        // deviation in an 11 kHz channel: in the channels beside it it reaches 14 dB under its own level, two
        // channels off 42 dB under, three 48, four 65. Without the guard those take the other three slots.
        var source = new FakeSource();
        source.Add(155.3e6, 0.05, deviation: Disc("p25_call_24k.raw"));
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6)
            { SquelchDb = squelchDb, MultiMonitor = true, MultiHangSeconds = 2, SilentDigitalLookSeconds = 60 };
        rx.Start();
        rx.StartScan(Raster(155.3e6));
        Assert.True(Wait(() => Held(rx) == "0 - - -", 3), Held(rx));
        Stays(rx, "0 - - -", 3.5);                  // the recording is 1.6 s long: twice through
        rx.Stop();
    }

    [Fact]
    public void TwoDigitalSignalsAreDecodedSideBySide()
    {
        // A P25 call and a DMR repeater with a call, 200 kHz apart in one window, each sent over and over.
        const double p25Hz = 155.1e6, dmrHz = 155.3e6;
        var source = new FakeSource();
        // Whichever part of a transmission the scan lands on, it stays to see the call.
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6)
            { SquelchDb = -60, MultiMonitor = true, MultiHangSeconds = 3, SilentDigitalLookSeconds = 30 };
        Assert.True(rx.DigitalAvailable);
        rx.Start();
        rx.StartScan([
            new("p25", p25Hz, Mode.Nfm, BandPlan.NfmNarrow), new("dmr", dmrHz, Mode.Nfm, BandPlan.NfmNarrow),
            new("other", 160.0e6, Mode.Nfm, BandPlan.NfmNarrow),
        ]);
        source.Add(p25Hz, 0.05, deviation: Disc("p25_call_24k.raw"));
        source.Add(dmrHz, 0.03, deviation: Disc("dmr_repeater_24k.raw"));
        Assert.True(Wait(() => Held(rx) == "p25 dmr - -", 3), Held(rx));

        // Each slot says what its own signal is.
        Assert.True(Wait(() => rx.Slots[0].Digital?.Contains("TG 1234") == true, 8), $"the first slot says: {rx.Slots[0].Digital}");
        Assert.True(Wait(() => rx.Slots[1].Digital?.Contains("TG 3133") == true, 8), $"the second slot says: {rx.Slots[1].Digital}");
        Assert.StartsWith("P25 · NAC 293", rx.Slots[0].Digital);
        Assert.StartsWith("DMR · CC 7", rx.Slots[1].Digital);
        Assert.False(rx.Slots[0].Encrypted || rx.Slots[1].Encrypted);
        Assert.StartsWith("P25", rx.Digital?.ToString());   // the receiver speaks for the lowest slot

        // And each gives speech. The first is 33% left and the second 33% right, so left is the first plus
        // 0.67 of the second and right the other way round: that gives the two back apart. While a slot shows
        // a call, what it plays is the decoded speech and nothing else.
        static bool InCall(SlotStatus s) => s.Holding && s.SquelchOpen && !s.NothingToHear && s.Digital?.Contains("TG") == true;
        static double Rms(double[] x) => Math.Sqrt(x.Sum(v => v * v) / Math.Max(x.Length, 1));
        Thread.Sleep(300);
        double p25 = 0, dmr = 0;
        bool wantDmr = rx.DmrVoiceAvailable;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 10 && (p25 < 0.02 || (wantDmr && dmr < 0.02)))
        {
            var before = rx.Slots;
            var heard = Listen(rx, 0.15);
            var after = rx.Slots;
            int n = heard.Length / 2;
            double[] first = new double[n], second = new double[n];
            for (int k = 0; k < n; k++)
            {
                double l = heard[2 * k], r = heard[2 * k + 1];
                first[k] = (l - 0.67 * r) / (1 - 0.67 * 0.67);
                second[k] = (r - 0.67 * l) / (1 - 0.67 * 0.67);
            }
            if (InCall(before[0]) && InCall(after[0])) p25 = Math.Max(p25, Rms(first));
            if (InCall(before[1]) && InCall(after[1])) dmr = Math.Max(dmr, Rms(second));
        }
        Assert.True(p25 >= 0.02, $"no speech from the P25 call: {p25:F4}");
        if (wantDmr) Assert.True(dmr >= 0.02, $"no speech from the DMR call: {dmr:F4}");
        Assert.Equal("p25 dmr - -", Held(rx));
        rx.Stop();
    }
}
