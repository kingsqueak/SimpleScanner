// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;
using SimpleScanner.Digital;
using SimpleScanner.Dsp;

namespace SimpleScanner.Tests;

/// <summary>System Fusion (C4FM) against generated test signals (see Data/README.md).</summary>
public class YsfTests
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

    private static short[] Reference(string name) =>
        MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name))).ToArray();

    /// <summary>Feeds a signal 20 ms at a time: the last status seen during the transmission, and the speech.</summary>
    private static (YsfStatus? call, float[] voice) Run(YsfDecoder ysf, float[] disc)
    {
        YsfStatus? call = null;
        var voice = new List<float>();
        var buf = new float[4000];
        for (int at = 0; at < disc.Length; at += 480)
        {
            ysf.Feed24k(disc.AsSpan(at, Math.Min(480, disc.Length - at)));
            if (ysf.Status is { InCall: true } s) call = s;
            int n;
            while ((n = ysf.ReadVoice(buf)) > 0) voice.AddRange(buf.AsSpan(0, n));
        }
        return (call, voice.ToArray());
    }

    private static double Rms(float[] x, int at)
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
    public void ADnTransmissionGivesItsCallsignsAndItsSpeech()
    {
        using var ysf = YsfDecoder.TryCreate();
        Assert.NotNull(ysf);
        Assert.True(ysf.VoiceAvailable);
        var (call, voice) = Run(ysf, Disc("ysf_dn_24k.raw"));
        Assert.NotNull(call);
        Assert.Equal(YsfMode.VoiceData2, call.Mode);
        Assert.True(call.HaveCallsigns);
        Assert.False(call.Late);
        Assert.Equal("KC2RGW", call.Source);
        Assert.Equal("ALL", call.Destination);
        Assert.Equal("W1XYZ-RPT", call.Downlink);
        Assert.Equal("KC2RGW", call.Uplink);
        Assert.Equal(12, call.GroupId);
        Assert.False(call.NothingToHear);
        Assert.Equal("C4FM DN · KC2RGW to ALL · via W1XYZ-RPT · group 12", call.ToString());
        Assert.False(ysf.Status.InCall);                 // it ended with its end frame
        Assert.Equal(1, ysf.Status.Calls);
        Assert.Equal(23, ysf.Frames);                    // the header, 21 of traffic, the end

        // 21 frames of five voice frames each, and they are the speech that went in.
        Assert.Equal(105 * Frame, voice.Length);
        double match = EnvelopeMatch(voice, Reference("dmr_voice_reference_8k.raw"));
        Assert.True(match > 0.85, $"envelope match {match:F2}");
    }

    [Fact]
    public void ALateEntryInTheOtherVoiceAndDataModeWithTheDeviationTurnedOver()
    {
        using var ysf = YsfDecoder.TryCreate();
        Assert.NotNull(ysf);
        var disc = Disc("ysf_vd1_late_inverted_24k.raw");
        var rng = new Random(3);
        for (int i = 0; i < disc.Length; i++) disc[i] += (float)((rng.NextDouble() * 2 - 1) * 0.15);
        var (call, voice) = Run(ysf, disc);
        Assert.NotNull(call);
        // No header was heard: the callsigns come from the traffic frames' data channel.
        Assert.True(call.Late);
        Assert.Equal(YsfMode.VoiceData1, call.Mode);
        Assert.Equal("W1AW", call.Source);
        Assert.Equal("K1ABC", call.Destination);
        Assert.Equal("C4FM V/D 1 · W1AW to K1ABC", call.ToString());
        Assert.Equal(105 * Frame, voice.Length);
        Assert.True(EnvelopeMatch(voice, Reference("dmr_voice_reference_8k.raw")) > 0.85);
    }

    [Fact]
    public void FullRateVoiceGivesTheExactSpeech()
    {
        using var ysf = YsfDecoder.TryCreate();
        Assert.NotNull(ysf);
        var (call, voice) = Run(ysf, Disc("ysf_vw_24k.raw"));
        Assert.NotNull(call);
        Assert.Equal(YsfMode.VoiceFullRate, call.Mode);
        Assert.Equal("KC2RGW", call.Source);
        Assert.Equal("C4FM VW · KC2RGW to ALL", call.ToString());
        // The first traffic frame has two voice frames after its data channel, the other twenty five each. The
        // codec, its frames and its decoder are the ones the P25 test uses, so the speech is the same sample for sample.
        var want = Reference("p25_call_voice_8k.raw");
        Assert.Equal(102 * Frame, voice.Length);
        Assert.Equal(54 * Frame, want.Length);           // the reference is the shorter
        for (int i = 0; i < want.Length; i++) Assert.Equal(want[i], (short)MathF.Round(voice[i] * 32768));
        Assert.True(Enumerable.Range(54, 48).Average(f => Rms(voice, f * Frame)) > 0.01);        // and the rest is not silence
    }

    [Fact]
    public void OtherSignalsAreNotSystemFusion()
    {
        using var ysf = YsfDecoder.TryCreate();
        Assert.NotNull(ysf);
        var rng = new Random(5);
        var noise = new float[480000];                   // ten seconds
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 4 - 2);
        ysf.Feed(noise, 2500);
        foreach (string f in new[] { "p25_call_24k.raw", "dmr_repeater_24k.raw", "nxdn_4800_24k.raw", "nxdn_9600_24k.raw", "dstar_call_24k.raw" })
            ysf.Feed24k(Disc(f));
        Assert.Equal(0, ysf.Frames);
        Assert.Equal(0, ysf.Status.Calls);
        Assert.Equal(0, ysf.ReadVoice(new float[100]));

        using var dmr = DmrDecoder.TryCreate();
        using var p25 = P25Decoder.TryCreate();
        using var nxdn = NxdnDecoder.TryCreate();
        using var dstar = DstarDecoder.TryCreate();
        Assert.NotNull(dmr);
        Assert.NotNull(p25);
        Assert.NotNull(nxdn);
        Assert.NotNull(dstar);
        foreach (string f in new[] { "ysf_dn_24k.raw", "ysf_vd1_late_inverted_24k.raw", "ysf_vw_24k.raw" })
        {
            dmr.Feed24k(Disc(f));
            p25.Feed24k(Disc(f));
            nxdn.Feed24k(Disc(f));
            dstar.Feed24k(Disc(f));
            p25.ReadEvents();
            Assert.True(dmr.Bursts == 0, $"DMR in {f}");
            Assert.True(p25.Units == 0, $"P25 in {f}: {p25.Units} units, NAC {p25.Status.Nac:X}");
            // NXDN's frame check is a short sync and one parity bit, so a frame or two turn up by chance in other
            // four-level signals. They never make a call; the receiver asks the System Fusion decoder first.
            Assert.True(nxdn.Frames <= 3 && !nxdn.Status.InCall && nxdn.ReadVoice(new float[100]) == 0, $"NXDN in {f}");
            Assert.True(dstar.Frames == 0, $"D-STAR in {f}");
        }
    }

    /// <summary>The whole receive path: a radio signal through the channel filter and discriminator.</summary>
    [Fact]
    public void ATransmissionIsDecodedFromARadioSignal()
    {
        const double fs = 2_400_000;
        var disc = Disc("ysf_dn_24k.raw");
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

        using var ysf = YsfDecoder.TryCreate();
        Assert.NotNull(ysf);
        var rx = new ChannelReceiver(fs);
        rx.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        rx.SetOffset(300000);
        rx.DiscriminatorTap = d => ysf.Feed(d, rx.DiscriminatorHzPerUnit);
        var l = new float[rx.MaxAudio(4096)];
        var r = new float[rx.MaxAudio(4096)];
        YsfStatus? call = null;
        var voice = new List<float>();
        var buf = new float[4000];
        for (int i = 0; i < n; i += 4096)
        {
            rx.Process(re.AsSpan(i, Math.Min(4096, n - i)), im.AsSpan(i, Math.Min(4096, n - i)), l, r);
            if (ysf.Status is { InCall: true } s) call = s;
            int got;
            while ((got = ysf.ReadVoice(buf)) > 0) voice.AddRange(buf.AsSpan(0, got));
        }
        Assert.NotNull(call);
        Assert.Equal("KC2RGW", call.Source);
        Assert.Equal("W1XYZ-RPT", call.Downlink);
        Assert.Equal(23, ysf.Frames);
        Assert.Equal(105 * Frame, voice.Count);
    }
}
