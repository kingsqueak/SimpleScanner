// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;
using SimpleScanner.Digital;
using SimpleScanner.Dsp;

namespace SimpleScanner.Tests;

/// <summary>DMR against generated test signals (see Data/README.md).</summary>
public class DmrTests
{
    private const double CountsPerKhz = 6553.6;
    private const int Frame = AmbeVocoder.FrameSamples;

    /// <summary>A test file as discriminator samples in units of 2.5 kHz deviation, at 24 kHz.</summary>
    private static float[] Disc(string name)
    {
        var raw = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name)));
        var x = new float[raw.Length];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(raw[i] / CountsPerKhz * 1000 / 2500);
        return x;
    }

    /// <summary>
    /// Feeds a signal 20 ms at a time. Returns the last status seen during a call once its
    /// addresses were in, and all the speech.
    /// </summary>
    private static (DmrStatus? call, float[] voice) Run(DmrDecoder dmr, float[] disc, Action<float[]>? spoil = null)
    {
        spoil?.Invoke(disc);
        DmrStatus? call = null;
        var voice = new List<float>();
        var buf = new float[4000];
        for (int at = 0; at < disc.Length; at += 480)
        {
            dmr.Feed24k(disc.AsSpan(at, Math.Min(480, disc.Length - at)));
            if (dmr.Status is { InCall: true, HaveAddresses: true } s) call = s;
            int n;
            while ((n = dmr.ReadVoice(buf)) > 0) voice.AddRange(buf.AsSpan(0, n));
        }
        return (call, voice.ToArray());
    }

    /// <summary>How alike two recordings are in loudness from one 20 ms frame to the next: 1 is the same.</summary>
    private static double EnvelopeMatch(ReadOnlySpan<float> a, ReadOnlySpan<short> b)
    {
        int frames = Math.Min(a.Length, b.Length) / Frame;
        var x = new double[frames];
        var y = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sa = 0, sb = 0;
            for (int i = 0; i < Frame; i++)
            {
                sa += a[f * Frame + i] * a[f * Frame + i];
                sb += (b[f * Frame + i] / 32768.0) * (b[f * Frame + i] / 32768.0);
            }
            x[f] = Math.Sqrt(sa / Frame);
            y[f] = Math.Sqrt(sb / Frame);
        }
        double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int f = 0; f < frames; f++)
        {
            sxy += (x[f] - mx) * (y[f] - my);
            sxx += (x[f] - mx) * (x[f] - mx);
            syy += (y[f] - my) * (y[f] - my);
        }
        return sxy / Math.Sqrt(sxx * syy);
    }

    private static short[] Reference() =>
        MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "dmr_voice_reference_8k.raw"))).ToArray();

    [Fact]
    public void ARepeaterCallGivesItsDetailsAndItsSpeech()
    {
        using var dmr = DmrDecoder.TryCreate();
        Assert.NotNull(dmr);
        Assert.True(dmr.VoiceAvailable);
        var (call, voice) = Run(dmr, Disc("dmr_repeater_24k.raw"));
        Assert.NotNull(call);
        Assert.Equal(DmrSource.Repeater, call.From);
        Assert.Equal(7, call.ColorCode);
        Assert.Equal(2, call.Slot);
        Assert.True(call.Group);
        Assert.Equal(3133, call.Destination);
        Assert.Equal(3133001, call.Source);
        Assert.Equal("KC2RGW Chris", call.Alias);
        Assert.False(call.Encrypted);
        Assert.False(call.Emergency);
        Assert.False(call.OtherSlotBusy);
        Assert.Equal("DMR · CC 7 · TS2 · TG 3133 · ID 3133001 · KC2RGW Chris", call.ToString());
        // The call ended with its terminator; the repeater went on sending idle bursts.
        Assert.Equal(DmrActivity.Idle, dmr.Status.Activity);
        Assert.Equal("DMR · CC 7 · idle", dmr.Status.ToString());
        Assert.True(dmr.Active);

        // 36 bursts of three frames. The speech is the same words at the same loudness as a radio's
        // own decoder gives for these frames; a different decoder, so not the same samples.
        Assert.Equal(108 * Frame, voice.Length);
        var want = Reference();
        Assert.True(EnvelopeMatch(voice, want) > 0.85, $"envelope match {EnvelopeMatch(voice, want):F2}");
        double rms = Math.Sqrt(voice.Sum(v => (double)v * v) / voice.Length) * 32768;
        double wantRms = Math.Sqrt(want.Sum(v => (double)v * v) / want.Length);
        Assert.InRange(rms / wantRms, 0.6, 1.6);
    }

    [Fact]
    public void AMobileHeardFromTheMiddleOfItsCall()
    {
        using var dmr = DmrDecoder.TryCreate();
        Assert.NotNull(dmr);
        // A mobile sends in every other slot. Between its bursts a discriminator gives loud noise.
        var rng = new Random(11);
        var (call, voice) = Run(dmr, Disc("dmr_mobile_late_24k.raw"), d =>
        {
            int quiet = 0;
            for (int i = 0; i < d.Length; i++)
            {
                quiet = d[i] == 0 ? quiet + 1 : 0;
                d[i] += (float)((rng.NextDouble() * 2 - 1) * (quiet > 10 ? 3.0 : 0.1));
            }
        });
        Assert.NotNull(call);
        Assert.Equal(DmrSource.Mobile, call.From);
        Assert.Equal(3, call.ColorCode);
        Assert.Equal(0, call.Slot);                      // a mobile's bursts do not say which slot they are
        Assert.Equal(9, call.Destination);
        Assert.Equal(1234567, call.Source);
        Assert.Equal("DMR · CC 3 · TG 9 · ID 1234567", call.ToString());
        // No header was heard, so the first bursts are held back until the call has said it is not
        // encrypted: its addresses come round once in the first six bursts.
        Assert.InRange(voice.Length, 90 * Frame, 102 * Frame);
        Assert.False(dmr.Status.InCall);
    }

    [Fact]
    public void AnEncryptedCallIsShownAndGivesNoSpeech()
    {
        using var dmr = DmrDecoder.TryCreate();
        Assert.NotNull(dmr);
        var (call, voice) = Run(dmr, Disc("dmr_direct_private_24k.raw"));
        Assert.NotNull(call);
        Assert.Equal(DmrSource.Direct, call.From);
        Assert.Equal(2, call.Slot);
        Assert.False(call.Group);
        Assert.Equal(99, call.Destination);
        Assert.Equal(42, call.Source);
        Assert.True(call.Encrypted);
        Assert.True(call.Emergency);
        Assert.Equal("DMR · CC 1 · TS2 · to 99 · ID 42 · EMERGENCY · encrypted", call.ToString());
        Assert.Empty(voice);
    }

    [Fact]
    public void NoiseAndP25AreNotDmr()
    {
        using var dmr = DmrDecoder.TryCreate();
        Assert.NotNull(dmr);
        var rng = new Random(5);
        var noise = new float[96000];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 4 - 2);
        dmr.Feed(noise, 2500);
        Assert.False(dmr.Active);
        Assert.Equal(0, dmr.Bursts);
        dmr.Feed24k(Disc("p25_call_24k.raw"));
        Assert.Equal(0, dmr.Bursts);

        using var p25 = P25Decoder.TryCreate();
        Assert.NotNull(p25);
        p25.Feed24k(Disc("dmr_repeater_24k.raw"));
        p25.ReadEvents();
        Assert.Equal(0, p25.Units);
    }

    [Fact]
    public void LosingTheSignalStartsAfresh()
    {
        using var dmr = DmrDecoder.TryCreate();
        Assert.NotNull(dmr);
        var disc = Disc("dmr_repeater_24k.raw");
        dmr.Feed24k(disc.AsSpan(0, disc.Length / 2));
        Assert.True(dmr.Status.InCall);
        dmr.SignalLost();
        Assert.False(dmr.Active);
        Assert.Equal(-1, dmr.Status.ColorCode);
        Assert.Equal(0, dmr.ReadVoice(new float[100]));
        // And the same signal again is followed again.
        var (call, voice) = Run(dmr, disc);
        Assert.NotNull(call);
        Assert.Equal(108 * Frame, voice.Length);
    }

    /// <summary>
    /// The whole receive path: the repeater's signal at 2.4 MS/s, 300 kHz off centre, through the
    /// channel filter and discriminator to the decoder.
    /// </summary>
    [Fact]
    public void ACallIsDecodedFromARadioSignal()
    {
        const double fs = 2_400_000;
        var disc = Disc("dmr_repeater_24k.raw");
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

        using var dmr = DmrDecoder.TryCreate();
        Assert.NotNull(dmr);
        var rx = new ChannelReceiver(fs);
        rx.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        rx.SetOffset(300000);
        rx.DiscriminatorTap = d => dmr.Feed(d, rx.DiscriminatorHzPerUnit);
        var l = new float[rx.MaxAudio(4096)];
        var r = new float[rx.MaxAudio(4096)];
        DmrStatus? call = null;
        var voice = new List<float>();
        var buf = new float[4000];
        for (int i = 0; i < n; i += 4096)
        {
            rx.Process(re.AsSpan(i, Math.Min(4096, n - i)), im.AsSpan(i, Math.Min(4096, n - i)), l, r);
            if (dmr.Status is { InCall: true, HaveAddresses: true } s) call = s;
            int got;
            while ((got = dmr.ReadVoice(buf)) > 0) voice.AddRange(buf.AsSpan(0, got));
        }
        Assert.NotNull(call);
        Assert.Equal(7, call.ColorCode);
        Assert.Equal(2, call.Slot);
        Assert.Equal(3133, call.Destination);
        Assert.Equal(3133001, call.Source);
        Assert.Equal(108 * Frame, voice.Count);
        Assert.True(EnvelopeMatch(voice.ToArray(), Reference()) > 0.85);
    }
}
