using System.Runtime.InteropServices;
using SimpleScanner.Digital;
using SimpleScanner.Dsp;

namespace SimpleScanner.Tests;

/// <summary>NXDN against generated test signals (see Data/README.md).</summary>
public class NxdnTests
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

    private static short[] Reference() =>
        MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "dmr_voice_reference_8k.raw"))).ToArray();

    /// <summary>Feeds a signal 20 ms at a time: the last status seen in a call with its addresses, and the speech.</summary>
    private static (NxdnStatus? call, float[] voice) Run(NxdnDecoder nxdn, float[] disc)
    {
        NxdnStatus? call = null;
        var voice = new List<float>();
        var buf = new float[4000];
        for (int at = 0; at < disc.Length; at += 480)
        {
            nxdn.Feed24k(disc.AsSpan(at, Math.Min(480, disc.Length - at)));
            if (nxdn.Status is { InCall: true, HaveAddresses: true } s) call = s;
            int n;
            while ((n = nxdn.ReadVoice(buf)) > 0) voice.AddRange(buf.AsSpan(0, n));
        }
        return (call, voice.ToArray());
    }

    private static double Rms(ReadOnlySpan<float> x, int at)
    {
        double s = 0;
        for (int i = 0; i < Frame; i++) s += x[at + i] * x[at + i];
        return Math.Sqrt(s / Frame);
    }

    /// <summary>How alike two recordings are in loudness from one 20 ms frame to the next: 1 is the same.</summary>
    private static double EnvelopeMatch(float[] a, short[] b)
    {
        int frames = Math.Min(a.Length, b.Length) / Frame;
        var bf = Array.ConvertAll(b, v => v / 32768f);
        var x = Enumerable.Range(0, frames).Select(f => Rms(a, f * Frame)).ToArray();
        var y = Enumerable.Range(0, frames).Select(f => Rms(bf, f * Frame)).ToArray();
        double mx = x.Average(), my = y.Average();
        double sxy = x.Zip(y, (p, q) => (p - mx) * (q - my)).Sum();
        return sxy / Math.Sqrt(x.Sum(p => (p - mx) * (p - mx)) * y.Sum(q => (q - my) * (q - my)));
    }

    [Fact]
    public void ANarrowCallGivesItsDetailsAndItsSpeech()
    {
        using var nxdn = NxdnDecoder.TryCreate();
        Assert.NotNull(nxdn);
        Assert.True(nxdn.VoiceAvailable);
        var (call, voice) = Run(nxdn, Disc("nxdn_4800_24k.raw"));
        Assert.NotNull(call);
        Assert.Equal(4800, call.Rate);
        Assert.Equal(5, call.Ran);
        Assert.True(call.Group);
        Assert.Equal(200, call.Destination);
        Assert.Equal(1001, call.Source);
        Assert.False(call.Encrypted);
        Assert.False(call.NothingToHear);
        Assert.Equal("NXDN 4800 · RAN 5 · TG 200 · ID 1001", call.ToString());
        Assert.False(nxdn.Status.InCall);                // the call ended with its release message
        Assert.True(nxdn.Status.NothingToHear);
        Assert.Equal(104 * Frame, voice.Length);
        Assert.True(EnvelopeMatch(voice, Reference()) > 0.85, $"envelope match {EnvelopeMatch(voice, Reference()):F2}");
    }

    [Fact]
    public void AWideCallToOneRadio()
    {
        using var nxdn = NxdnDecoder.TryCreate();
        Assert.NotNull(nxdn);
        var (call, voice) = Run(nxdn, Disc("nxdn_9600_24k.raw"));
        Assert.NotNull(call);
        Assert.Equal(9600, call.Rate);
        Assert.Equal(12, call.Ran);
        Assert.False(call.Group);
        Assert.Equal(77, call.Destination);
        Assert.Equal(4321, call.Source);
        Assert.True(call.Emergency);
        Assert.Equal("NXDN 9600 · RAN 12 · to 77 · ID 4321 · EMERGENCY", call.ToString());
        Assert.Equal(104 * Frame, voice.Length);
        Assert.True(EnvelopeMatch(voice, Reference()) > 0.85);
    }

    [Fact]
    public void OtherSignalsAreNotNxdn()
    {
        using var nxdn = NxdnDecoder.TryCreate();
        Assert.NotNull(nxdn);
        var rng = new Random(5);
        var noise = new float[96000];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 4 - 2);
        nxdn.Feed(noise, 2500);
        nxdn.Feed24k(Disc("p25_call_24k.raw"));
        nxdn.Feed24k(Disc("dmr_repeater_24k.raw"));
        Assert.Equal(0, nxdn.Frames);
        Assert.False(nxdn.Active);

        using var dmr = DmrDecoder.TryCreate();
        using var p25 = P25Decoder.TryCreate();
        Assert.NotNull(dmr);
        Assert.NotNull(p25);
        foreach (string f in new[] { "nxdn_4800_24k.raw", "nxdn_9600_24k.raw" })
        {
            dmr.Feed24k(Disc(f));
            p25.Feed24k(Disc(f));
        }
        p25.ReadEvents();
        Assert.Equal(0, dmr.Bursts);
        Assert.Equal(0, p25.Units);
    }

    /// <summary>The whole receive path, at each speed: a radio signal through the channel filter and discriminator.</summary>
    [Theory]
    [InlineData("nxdn_4800_24k.raw", 4800, 200)]
    [InlineData("nxdn_9600_24k.raw", 9600, 77)]
    public void ACallIsDecodedFromARadioSignal(string file, int rate, int destination)
    {
        const double fs = 2_400_000;
        var disc = Disc(file);
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

        using var nxdn = NxdnDecoder.TryCreate();
        Assert.NotNull(nxdn);
        var rx = new ChannelReceiver(fs);
        rx.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        rx.SetOffset(300000);
        rx.DiscriminatorTap = d => nxdn.Feed(d, rx.DiscriminatorHzPerUnit);
        var l = new float[rx.MaxAudio(4096)];
        var r = new float[rx.MaxAudio(4096)];
        NxdnStatus? call = null;
        int voice = 0;
        var buf = new float[4000];
        for (int i = 0; i < n; i += 4096)
        {
            rx.Process(re.AsSpan(i, Math.Min(4096, n - i)), im.AsSpan(i, Math.Min(4096, n - i)), l, r);
            if (nxdn.Status is { InCall: true, HaveAddresses: true } s) call = s;
            int got;
            while ((got = nxdn.ReadVoice(buf)) > 0) voice += got;
        }
        Assert.NotNull(call);
        Assert.Equal(rate, call.Rate);
        Assert.Equal(destination, call.Destination);
        Assert.Equal(104 * Frame, voice);
    }
}
