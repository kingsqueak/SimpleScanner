// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;
using SimpleScanner.Digital;
using SimpleScanner.Dsp;
using Xunit.Abstractions;

namespace SimpleScanner.Tests;

/// <summary>
/// P25 Phase 1's differential front end (native/digital/cqpsk.c, through P25Decoder.TryCreateIq) and the choice between it
/// and the FM one (P25FrontEnds), against generated signals: the CQPSK control channels made by p25gen --cqpsk (see
/// Data/README.md), and the C4FM test call turned into a radio signal.
/// </summary>
public class P25CqpskTests(ITestOutputHelper output)
{
    private const double CountsPerKhz = 6553.6;

    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);

    /// <summary>A test file as discriminator samples in units of 2.5 kHz deviation, at 24 kHz.</summary>
    private static float[] Disc(string name)
    {
        var raw = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Data(name)));
        var x = new float[raw.Length];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(raw[i] / CountsPerKhz * 1000 / 2500);
        return x;
    }

    /// <summary>A p25gen --cqpsk --iq16 file (48 kHz, 16384 = 1.0), as I and Q, <paramref name="times"/> over.</summary>
    private static (float[] Re, float[] Im) Iq(string name, int times = 1)
    {
        var raw = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Data(name)));
        int n = raw.Length / 2;
        var re = new float[n * times];
        var im = new float[n * times];
        for (int t = 0; t < times; t++)
            for (int i = 0; i < n; i++)
            {
                re[t * n + i] = raw[2 * i] / 16384f;
                im[t * n + i] = raw[2 * i + 1] / 16384f;
            }
        return (re, im);
    }

    // Through the differential front end, a tenth of a second at a time, with a little silence after so the last symbols are out.
    private static List<P25Tsbk> DecodeIq(float[] re, float[] im, out long units)
    {
        using var p25 = P25Decoder.TryCreateIq();
        Assert.NotNull(p25);
        var all = Feed(p25, re, im, out _);
        units = p25.Units;
        return all;
    }

    // The messages as they come (the decoder keeps only the last 512); offsetHz: the carrier offset measured at the end of the signal
    private static List<P25Tsbk> Feed(P25Decoder p25, float[] re, float[] im, out double offsetHz)
    {
        const int block = 4800;
        var all = new List<P25Tsbk>();
        for (int i = 0; i < re.Length; i += block)
        {
            int k = Math.Min(block, re.Length - i);
            p25.FeedBaseband(re.AsSpan(i, k), im.AsSpan(i, k));
            all.AddRange(p25.ReadTsbks());
        }
        offsetHz = p25.IqStats.OffsetHz;
        var zero = new float[block];
        p25.FeedBaseband(zero, zero);
        all.AddRange(p25.ReadTsbks());
        return all;
    }

    // What the FM path makes of the same complex signal: the app's discriminator, then the FM front end.
    private static int GoodThroughFm(float[] re, float[] im)
    {
        using var p25 = P25Decoder.TryCreate();
        Assert.NotNull(p25);
        var fm = new FmDemod(48000, 2500);
        var disc = new float[re.Length];
        fm.Process(re, im, disc);
        int good = 0;
        for (int i = 0; i < disc.Length; i += 4800)
        {
            p25.Feed(disc.AsSpan(i, Math.Min(4800, disc.Length - i)), 2500);
            good += p25.ReadTsbks().Count(m => m.CrcOk);
        }
        p25.Feed(new float[4800], 2500);
        return good + p25.ReadTsbks().Count(m => m.CrcOk);
    }

    private static List<P25Tsbk> FmFixture()
    {
        using var p25 = P25Decoder.TryCreate();
        Assert.NotNull(p25);
        p25.Feed24k(Disc("p25_trunk_control_24k.raw"));
        p25.ReadEvents();
        return p25.ReadTsbks();
    }

    private static void Rotate(float[] re, float[] im, double hz)
    {
        for (int i = 0; i < re.Length; i++)
        {
            double a = 2 * Math.PI * hz * i / 48000, c = Math.Cos(a), s = Math.Sin(a);
            (re[i], im[i]) = ((float)(re[i] * c - im[i] * s), (float)(re[i] * s + im[i] * c));
        }
    }

    private static short[] Reference() =>
        MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Data("p25_call_voice_8k.raw"))).ToArray();

    /// <summary>
    /// The C4FM test call as a radio signal at 2.4 MS/s, 300 kHz off centre (as P25Tests makes it), through the channel
    /// filter; the channel's baseband tap goes to <paramref name="baseband"/> and its discriminator to <paramref name="disc"/>.
    /// </summary>
    private static void RadioCall(ComplexSamplesHandler baseband, SamplesHandler? disc = null)
    {
        const double fs = 2_400_000;
        var d = Disc("p25_call_24k.raw");
        int n = d.Length * 100;
        var re = new float[n];
        var im = new float[n];
        double phase = 0;
        var rng = new Random(7);
        for (int i = 0; i < n; i++)
        {
            int k = i / 100;
            double frac = (i % 100) / 100.0;
            double hz = 2500 * (d[k] + ((k + 1 < d.Length ? d[k + 1] : d[k]) - d[k]) * frac);
            phase += 2 * Math.PI * (300000 + hz) / fs;
            re[i] = (float)(0.05 * Math.Cos(phase) + 2e-4 * (rng.NextDouble() - 0.5));
            im[i] = (float)(0.05 * Math.Sin(phase) + 2e-4 * (rng.NextDouble() - 0.5));
        }
        var rx = new ChannelReceiver(fs);
        rx.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        rx.SetOffset(300000);
        rx.BasebandTap = baseband;
        if (disc is not null) rx.DiscriminatorTap = disc;
        var l = new float[rx.MaxAudio(4096)];
        var r = new float[rx.MaxAudio(4096)];
        for (int i = 0; i < n; i += 4096)
            rx.Process(re.AsSpan(i, Math.Min(4096, n - i)), im.AsSpan(i, Math.Min(4096, n - i)), l, r);
        // A little silence after, so the last symbols are out of the filters.
        var z = new float[4096];
        for (int i = 0; i < 12; i++) rx.Process(z, z, l, r);
    }

    /// <summary>
    /// A C4FM transmitter through the differential front end: its phase moves by the same steps from one symbol centre
    /// to the next as CQPSK's, so the speech must come out bit for bit. This is what proves the sign and the mapping of
    /// phase steps to symbols.
    /// </summary>
    [Fact]
    public void AC4fmCallThroughTheBasebandFrontEndGivesItsDetailsAndTheExactSpeech()
    {
        using var p25 = P25Decoder.TryCreateIq();
        Assert.NotNull(p25);
        Assert.True(p25.IsIq);
        bool sawCall = false;
        RadioCall((re, im) =>
        {
            p25.FeedBaseband(re, im);
            sawCall |= p25.Status.InCall;
        });
        Assert.True(sawCall);
        Assert.Equal(0x293, p25.Status.Nac);
        Assert.Equal(1234, p25.Status.Talkgroup);
        Assert.Equal(5678, p25.Status.Source);
        Assert.False(p25.Status.Encrypted);
        Assert.False(p25.Status.InCall);              // ended by its terminator

        var voice = new float[20000];
        int n = p25.ReadVoice(voice);
        var want = Reference();
        Assert.Equal(want.Length, n);
        for (int i = 0; i < n; i++) Assert.Equal(want[i], (short)MathF.Round(voice[i] * 32768));
    }

    /// <summary>The generated control channel as a CQPSK transmitter sends it: every block as the FM fixture gives it.</summary>
    [Fact]
    public void ACqpskControlChannelDecodesFully()
    {
        var (re, im) = Iq("p25_trunk_control_cqpsk_48k.raw");
        var got = DecodeIq(re, im, out long units);
        var want = FmFixture();
        Assert.Equal(11, units);                     // seven TSDUs and four packets
        Assert.Equal(want.Count, got.Count);
        for (int i = 0; i < want.Count; i++)
        {
            Assert.Equal(want[i].CrcOk, got[i].CrcOk);
            Assert.Equal(want[i].Raw, got[i].Raw);
            Assert.Equal(want[i].RawData, got[i].RawData);
            Assert.Equal(want[i].Nac, got[i].Nac);
        }
        Assert.Equal(22, got.Count(m => m.CrcOk));   // all but the block sent with its CRC broken
    }

    /// <summary>
    /// Simulcast, as a receiver between two sites hears it: the control channel twice over with a second copy 30 us
    /// later, 2 dB down and 3 Hz up (Data/p25_trunk_control_echo_48k.raw). The differential front end must get 95 % of
    /// the blocks; the FM path's count is written to the test output for comparison.
    /// </summary>
    [Fact]
    public void ASimulcastEchoStillDecodes()
    {
        var (re, im) = Iq("p25_trunk_control_echo_48k.raw");
        const int sent = 2 * 22;                     // good blocks sent: twice the script, less the broken one each time
        int iq = DecodeIq(re, im, out _).Count(m => m.CrcOk);
        int fm = GoodThroughFm(re, im);
        output.WriteLine($"echo 30 us, -2 dB, +3 Hz: {iq} of {sent} blocks through the differential front end, {fm} through FM");
        Assert.True(iq >= sent * 95 / 100.0, $"only {iq} of {sent} blocks (FM: {fm})");
        Assert.True(iq <= sent);
    }

    /// <summary>
    /// A kilohertz of tuning error either way (more than the symbols alone can tell apart: their fourth power repeats
    /// every 1200 Hz at 4800 symbols a second). The carrier loop pulls it in within the first pass of the control channel;
    /// everything after decodes.
    /// </summary>
    [Theory]
    [InlineData(1000)]
    [InlineData(-1000)]
    public void AKilohertzOffTuneStillDecodes(double hz)
    {
        var (re, im) = Iq("p25_trunk_control_cqpsk_48k.raw", times: 3);
        Rotate(re, im, hz);
        using var p25 = P25Decoder.TryCreateIq();
        Assert.NotNull(p25);
        int good = Feed(p25, re, im, out double offset).Count(m => m.CrcOk);
        output.WriteLine($"{hz:+0;-0} Hz: {good} of 66 blocks; offset measured {offset:F0} Hz");
        // The last two passes whole, and some of the first
        Assert.True(good >= 2 * 22 + 11, $"only {good} of 66 blocks");
        Assert.InRange(offset, hz - 150, hz + 150);
    }

    [Fact]
    public void NoiseIsNotP25ThroughEitherFrontEnd()
    {
        var rng = new Random(11);
        int n = 3 * 48000;
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = (float)(0.02 * (rng.NextDouble() - 0.5));
            im[i] = (float)(0.02 * (rng.NextDouble() - 0.5));
        }
        using var both = P25FrontEnds.TryCreate();
        Assert.NotNull(both);
        var fm = new FmDemod(48000, 2500);
        var disc = new float[4800];
        for (int i = 0; i < n; i += 4800)
        {
            fm.Process(re.AsSpan(i, 4800), im.AsSpan(i, 4800), disc);
            both.FeedBaseband(re.AsSpan(i, 4800), im.AsSpan(i, 4800));
            both.Feed(disc, 2500);
        }
        Assert.False(both.Active);
        Assert.False(both.AnyActive);
        Assert.Equal(0, both.Fm.Units);
        Assert.Equal(0, both.Iq!.Units);
        Assert.False(both.HasTsbks);
    }

    /// <summary>Both front ends on a CQPSK simulcast signal: the differential one is chosen, and only its messages are read.</summary>
    [Fact]
    public void TheFrontEndThatDecodesIsTheOneRead()
    {
        var (re, im) = Iq("p25_trunk_control_echo_48k.raw");
        using var both = P25FrontEnds.TryCreate();
        Assert.NotNull(both);
        var fm = new FmDemod(48000, 2500);
        var disc = new float[4800];
        var read = new List<P25Tsbk>();
        for (int i = 0; i < re.Length; i += 4800)
        {
            int k = Math.Min(4800, re.Length - i);
            fm.Process(re.AsSpan(i, k), im.AsSpan(i, k), disc);
            both.FeedBaseband(re.AsSpan(i, k), im.AsSpan(i, k));
            both.Feed(disc.AsSpan(0, k), 2500);
            read.AddRange(both.ReadTsbks());
        }
        Assert.True(both.UsingIq);
        Assert.Same(both.Iq, both.Selected);
        Assert.Equal(0x8A1, both.Status.Nac);
        Assert.True(both.Status.ControlChannel);
        // No block read twice (one from each front end)
        var raws = read.Where(m => m.CrcOk).Select(m => Convert.ToHexString(m.Raw) + Convert.ToHexString(m.RawData)).ToList();
        Assert.True(raws.Count >= 40, $"only {raws.Count} blocks read");
        Assert.True(raws.Count <= 44);

        both.SignalLost();
        Assert.False(both.UsingIq);
    }

    /// <summary>
    /// A C4FM call with both front ends: both decode it (whichever had its header first is the one in use), and the
    /// speech is the exact speech, once: never some of each.
    /// </summary>
    [Fact]
    public void AC4fmCallWithBothFrontEndsGivesTheExactSpeechOnce()
    {
        using var both = P25FrontEnds.TryCreate();
        Assert.NotNull(both);
        var voice = new List<float>();
        var buf = new float[4096];
        RadioCall((re, im) => both.FeedBaseband(re, im), d =>
        {
            both.Feed(d, 2500);
            int k;
            while ((k = both.ReadVoice(buf)) > 0) voice.AddRange(buf.AsSpan(0, k).ToArray());
        });
        output.WriteLine($"in use at the end: {(both.UsingIq ? "the differential front end" : "FM")}");
        Assert.Equal(1234, both.Status.Talkgroup);
        Assert.Equal(8, both.Fm.Units);
        Assert.True(both.Iq!.Units >= 8, $"the differential front end had {both.Iq.Units} units");
        var want = Reference();
        Assert.Equal(want.Length, voice.Count);
        for (int i = 0; i < want.Length; i++) Assert.Equal(want[i], (short)MathF.Round(voice[i] * 32768));
    }

    /// <summary>
    /// The differential front end is not run where there is no P25 to find: on an analogue signal it looks for three
    /// seconds and stops, with the squelch shut it does not start, and on a trunked voice channel it always runs.
    /// </summary>
    [Fact]
    public void TheBasebandFrontEndRunsOnlyWhereP25MayBe()
    {
        using var dv = new DigitalVoice();
        Assert.True(dv.DigitalAvailable);
        var re = new float[4800];
        var im = new float[4800];
        var disc = new float[4800];
        var rng = new Random(3);
        void Block(bool open)
        {
            // A carrier with a 1 kHz tone on it, 3 kHz deviation: analogue FM
            for (int i = 0; i < 4800; i++)
            {
                re[i] = (float)Math.Cos(3 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0)) + (float)(0.01 * (rng.NextDouble() - 0.5));
                im[i] = (float)Math.Sin(3 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
                disc[i] = (float)(3000 * Math.Cos(2 * Math.PI * 1000 * i / 48000.0) / 2500);
            }
            dv.FeedBaseband(re, im, open);
            dv.Feed(disc, 2500);
        }
        Block(false);
        Assert.False(dv.P25IqRunning);              // squelch shut, no P25: not started
        for (int k = 0; k < 10; k++) Block(true);
        Assert.True(dv.P25IqRunning);               // a signal: it looks
        for (int k = 0; k < 25; k++) Block(true);
        Assert.False(dv.P25IqRunning);              // three seconds and nothing: it stops
        Block(false);
        Block(true);
        Assert.True(dv.P25IqRunning);               // a new signal: it looks again

        dv.DigitalOnly = true;
        for (int k = 0; k < 40; k++) Block(false);
        Assert.True(dv.P25IqRunning);               // a trunked voice channel: always
    }

    /// <summary>
    /// The off-air capture this front end was written against (a weak simulcast control channel, recorded by rsp-bench
    /// record into artifacts/, which git does not keep): run only where the file is, skipped elsewhere.
    /// </summary>
    [CaptureFact("manch_cc_859.4875.iq")]
    public void TheOffAirSimulcastCaptureDecodesBetterThanThroughFm()
    {
        var f = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(CaptureFactAttribute.PathOf("manch_cc_859.4875.iq")));
        int n = f.Length / 2;
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = f[2 * i];
            im[i] = f[2 * i + 1];
        }
        var all = DecodeIq(re, im, out _);
        int iq = all.Count(m => m.CrcOk), fm = GoodThroughFm(re, im);
        output.WriteLine($"{n / 48000.0:F0} s: {iq} good blocks through the differential front end, {fm} through FM");
        Assert.True(iq > 3 * fm / 2, $"{iq} against FM's {fm}");
        var net = all.First(m => m.CrcOk && m.Kind == P25TsbkKind.Network);
        var rfss = all.First(m => m.CrcOk && m.Kind == P25TsbkKind.Rfss);
        Assert.Equal((0x8A1, 0xBEE00, 0x8A6), (net.Nac, net.Wacn, net.SystemId));
        Assert.Equal((0x8A6, 1, 1), (rfss.SystemId, rfss.Rfss, rfss.Site));
    }
}

/// <summary>A test on an off-air capture in artifacts/captures (never in the repository): skipped, saying so, where the file is not.</summary>
public sealed class CaptureFactAttribute : FactAttribute
{
    public CaptureFactAttribute(string name)
    {
        if (!File.Exists(PathOf(name))) Skip = $"the off-air capture artifacts/captures/{name} is not on this machine";
    }

    // The repository's root is five folders above the test's output (tests/SimpleScanner.Tests/bin/<config>/net10.0).
    public static string PathOf(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "captures", name));
}
