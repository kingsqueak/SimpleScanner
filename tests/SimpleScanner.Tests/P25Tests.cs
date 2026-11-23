using System.Runtime.InteropServices;
using SimpleScanner.Digital;
using SimpleScanner.Dsp;

namespace SimpleScanner.Tests;

/// <summary>P25 Phase 1 against generated test signals (see Data/README.md).</summary>
public class P25Tests
{
    private const double CountsPerKhz = 6553.6;

    /// <summary>A test file as discriminator samples in units of 2.5 kHz deviation, at 24 kHz.</summary>
    private static float[] Disc(string name)
    {
        var raw = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name)));
        var x = new float[raw.Length];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(raw[i] / CountsPerKhz * 1000 / 2500);
        return x;
    }

    private static short[] Reference() =>
        MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "p25_call_voice_8k.raw"))).ToArray();

    [Fact]
    public void ACallGivesItsDetailsAndTheExactSpeech()
    {
        using var p25 = P25Decoder.TryCreate();
        Assert.NotNull(p25);
        p25.Feed24k(Disc("p25_call_24k.raw"));
        p25.ReadEvents();
        Assert.Equal(0x293, p25.Status.Nac);
        Assert.Equal(1234, p25.Status.Talkgroup);
        Assert.Equal(5678, p25.Status.Source);
        Assert.False(p25.Status.Encrypted);
        Assert.False(p25.Status.ControlChannel);
        Assert.False(p25.Status.InCall);                 // the call ended with its terminator

        var voice = new float[20000];
        int n = p25.ReadVoice(voice);
        var want = Reference();
        Assert.Equal(want.Length, n);
        for (int i = 0; i < n; i++) Assert.Equal(want[i], (short)MathF.Round(voice[i] * 32768));
    }

    [Fact]
    public void AControlChannelIsRecognisedWithItsSystem()
    {
        using var p25 = P25Decoder.TryCreate();
        Assert.NotNull(p25);
        p25.Feed24k(Disc("p25_control_24k.raw"));
        p25.ReadEvents();
        Assert.True(p25.Status.ControlChannel);
        Assert.Equal(0x293, p25.Status.Nac);
        Assert.Equal(0x123, p25.Status.SystemId);
        Assert.Equal(0xBEE00, p25.Status.Wacn);
        Assert.Equal(40, p25.Units);
        Assert.Equal(0, p25.ReadVoice(new float[100]));
        Assert.Contains("control channel", p25.Status.ToString());
    }

    [Fact]
    public void NoiseIsNotP25()
    {
        using var p25 = P25Decoder.TryCreate();
        Assert.NotNull(p25);
        var rng = new Random(5);
        var noise = new float[48000];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 4 - 2);
        p25.Feed(noise, 2500);
        Assert.False(p25.Active);
        Assert.Equal(0, p25.Units);
    }

    /// <summary>
    /// The whole receive path: the call as a radio signal at 2.4 MS/s, 300 kHz off centre, through
    /// the channel filter and discriminator to the decoder. This is what proves the tap's scale
    /// and sign, and the 48 kHz to 24 kHz step.
    /// </summary>
    [Fact]
    public void ACallIsDecodedFromARadioSignal()
    {
        const double fs = 2_400_000;
        var disc = Disc("p25_call_24k.raw");
        int n = disc.Length * 100;
        var re = new float[n];
        var im = new float[n];
        double phase = 0;
        var rng = new Random(7);
        for (int i = 0; i < n; i++)
        {
            // Straight-line interpolation of the discriminator waveform up to the radio's rate.
            int k = i / 100;
            double frac = (i % 100) / 100.0;
            double hz = 2500 * (disc[k] + ((k + 1 < disc.Length ? disc[k + 1] : disc[k]) - disc[k]) * frac);
            phase += 2 * Math.PI * (300000 + hz) / fs;
            re[i] = (float)(0.05 * Math.Cos(phase) + 2e-4 * (rng.NextDouble() - 0.5));
            im[i] = (float)(0.05 * Math.Sin(phase) + 2e-4 * (rng.NextDouble() - 0.5));
        }

        using var p25 = P25Decoder.TryCreate();
        Assert.NotNull(p25);
        var rx = new ChannelReceiver(fs);
        rx.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        rx.SetOffset(300000);
        rx.DiscriminatorTap = d => p25.Feed(d, rx.DiscriminatorHzPerUnit);
        var l = new float[rx.MaxAudio(4096)];
        var r = new float[rx.MaxAudio(4096)];
        bool sawCall = false;
        for (int i = 0; i < n; i += 4096)
        {
            rx.Process(re.AsSpan(i, Math.Min(4096, n - i)), im.AsSpan(i, Math.Min(4096, n - i)), l, r);
            sawCall |= p25.Status.InCall;
        }
        Assert.True(sawCall);
        Assert.Equal(0x293, p25.Status.Nac);
        Assert.Equal(1234, p25.Status.Talkgroup);
        Assert.Equal(5678, p25.Status.Source);
        var voice = new float[20000];
        Assert.Equal(Reference().Length, p25.ReadVoice(voice));
    }
}
