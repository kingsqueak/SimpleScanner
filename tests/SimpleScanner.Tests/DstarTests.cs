using System.Runtime.InteropServices;
using SimpleScanner.Digital;
using SimpleScanner.Dsp;

namespace SimpleScanner.Tests;

/// <summary>D-STAR against generated test signals (see Data/README.md).</summary>
public class DstarTests
{
    private const double CountsPerKhz = 6553.6;
    private const int Frame = AmbeVocoder.FrameSamples;

    private static float[] Disc(string name)
    {
        var raw = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name)));
        var x = new float[raw.Length];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(raw[i] / CountsPerKhz * 1000 / 2500);
        return x;
    }

    /// <summary>Feeds a signal 20 ms at a time: the last status seen during the transmission, and the speech.</summary>
    private static (DstarStatus? call, float[] voice) Run(DstarDecoder dstar, float[] disc)
    {
        DstarStatus? call = null;
        var voice = new List<float>();
        var buf = new float[4000];
        for (int at = 0; at < disc.Length; at += 480)
        {
            dstar.Feed24k(disc.AsSpan(at, Math.Min(480, disc.Length - at)));
            if (dstar.Status is { InCall: true } s) call = s;
            int n;
            while ((n = dstar.ReadVoice(buf)) > 0) voice.AddRange(buf.AsSpan(0, n));
        }
        return (call, voice.ToArray());
    }

    [Fact]
    public void ATransmissionGivesItsCallsignsAndMessage()
    {
        using var dstar = DstarDecoder.TryCreate();
        Assert.NotNull(dstar);
        Assert.True(dstar.VoiceAvailable);
        var (call, voice) = Run(dstar, Disc("dstar_call_24k.raw"));
        Assert.NotNull(call);
        Assert.True(call.HaveCallsigns);
        Assert.Equal("KC2RGW", call.My);
        Assert.Equal("IC91", call.Suffix);
        Assert.Equal("CQCQCQ", call.Your);
        Assert.Equal("NE1DS  C", call.Repeater);
        Assert.Equal("NE1DS  G", call.Repeater2);
        Assert.Equal("SimpleScanner test", call.Text);
        Assert.False(call.NothingToHear);
        Assert.Equal("D-STAR · KC2RGW/IC91 to CQCQCQ · via NE1DS  C · SimpleScanner test", call.ToString());
        Assert.False(dstar.Status.InCall);               // it ended with its end pattern
        Assert.Equal(1, dstar.Status.Calls);
        // 105 frames of D-STAR's silence frame: every one arrives, decodes without a bit error
        // (the vocoder would mute or repeat otherwise) and is quiet.
        Assert.Equal(105, dstar.Frames);
        Assert.Equal(105 * Frame, voice.Length);
        Assert.True(voice.Max(MathF.Abs) < 0.01f);
    }

    [Fact]
    public void ALateEntryWithTheDeviationTurnedOver()
    {
        using var dstar = DstarDecoder.TryCreate();
        Assert.NotNull(dstar);
        var disc = Disc("dstar_late_inverted_24k.raw");
        var rng = new Random(3);
        for (int i = 0; i < disc.Length; i++) disc[i] += (float)((rng.NextDouble() * 2 - 1) * 0.15);
        var (call, voice) = Run(dstar, disc);
        Assert.NotNull(call);
        // No header was heard: the callsigns come from the copy the transmission carries in its slow data.
        Assert.True(call.HaveCallsigns);
        Assert.Equal("W1AW", call.My);
        Assert.Equal("CQCQCQ", call.Your);
        Assert.Equal("DIRECT", call.Repeater);
        Assert.Equal("D-STAR · W1AW to CQCQCQ · late and inverted", call.ToString());
        // The signal has 105 frames; the first few went by before the first sync pattern.
        Assert.InRange(dstar.Frames, 95, 105);
        Assert.Equal(dstar.Frames * Frame, voice.Length);
    }

    [Fact]
    public void OtherSignalsAreNotDstar()
    {
        using var dstar = DstarDecoder.TryCreate();
        Assert.NotNull(dstar);
        var rng = new Random(5);
        var noise = new float[480000];                   // ten seconds: sync patterns turn up in noise by chance
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 4 - 2);
        dstar.Feed(noise, 2500);
        foreach (string f in new[] { "p25_call_24k.raw", "dmr_repeater_24k.raw", "nxdn_4800_24k.raw", "nxdn_9600_24k.raw" })
            dstar.Feed24k(Disc(f));
        Assert.Equal(0, dstar.Frames);
        Assert.Equal(0, dstar.Status.Calls);
        Assert.Equal(0, dstar.ReadVoice(new float[100]));

        using var dmr = DmrDecoder.TryCreate();
        using var p25 = P25Decoder.TryCreate();
        using var nxdn = NxdnDecoder.TryCreate();
        Assert.NotNull(dmr);
        Assert.NotNull(p25);
        Assert.NotNull(nxdn);
        foreach (string f in new[] { "dstar_call_24k.raw", "dstar_late_inverted_24k.raw" })
        {
            dmr.Feed24k(Disc(f));
            p25.Feed24k(Disc(f));
            nxdn.Feed24k(Disc(f));
        }
        p25.ReadEvents();
        Assert.Equal(0, dmr.Bursts);
        Assert.Equal(0, p25.Units);
        Assert.Equal(0, nxdn.Frames);
    }

    /// <summary>The whole receive path: a radio signal through the channel filter and discriminator.</summary>
    [Fact]
    public void ATransmissionIsDecodedFromARadioSignal()
    {
        const double fs = 2_400_000;
        var disc = Disc("dstar_call_24k.raw");
        int n = disc.Length * 100;
        var re = new float[n];
        var im = new float[n];
        double phase = 0;
        var rng = new Random(7);
        for (int i = 0; i < n; i++)
        {
            int k = i / 100;
            double frac = (i % 100) / 100.0;
            double hz = 2500 * (disc[k] + ((k + 1 < disc.Length ? disc[k + 1] : disc[k]) - disc[k]) * frac);
            phase += 2 * Math.PI * (300000 + hz) / fs;
            re[i] = (float)(0.05 * Math.Cos(phase) + 2e-4 * (rng.NextDouble() - 0.5));
            im[i] = (float)(0.05 * Math.Sin(phase) + 2e-4 * (rng.NextDouble() - 0.5));
        }

        using var dstar = DstarDecoder.TryCreate();
        Assert.NotNull(dstar);
        var rx = new ChannelReceiver(fs);
        rx.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        rx.SetOffset(300000);
        rx.DiscriminatorTap = d => dstar.Feed(d, rx.DiscriminatorHzPerUnit);
        var l = new float[rx.MaxAudio(4096)];
        var r = new float[rx.MaxAudio(4096)];
        DstarStatus? call = null;
        for (int i = 0; i < n; i += 4096)
        {
            rx.Process(re.AsSpan(i, Math.Min(4096, n - i)), im.AsSpan(i, Math.Min(4096, n - i)), l, r);
            if (dstar.Status is { InCall: true } s) call = s;
        }
        Assert.NotNull(call);
        Assert.Equal("KC2RGW", call.My);
        Assert.Equal("SimpleScanner test", call.Text);
        Assert.Equal(105, dstar.Frames);
    }
}
