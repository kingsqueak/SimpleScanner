// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;
using SimpleScanner.Digital;
using Xunit.Abstractions;

namespace SimpleScanner.Tests;

/// <summary>
/// P25 Phase 2's native layer (native/digital/p25p2*.c, through <see cref="P25Phase2Native"/>): the codes and the
/// scrambling sequence against values made by op25's own code (Data/p25p2_reference.txt), and the receiver against the
/// voice channels made by p25p2gen (see Data/README.md), clean, with noise and a carrier offset, with the wrong system
/// and without one.
/// </summary>
public unsafe class P25Phase2NativeTests(ITestOutputHelper output)
{
    private const uint Wacn = 0xBEE00, SysId = 0x123, Nac = 0x293;
    private const uint Wacn2 = 0x92A47, SysId2 = 0x3F5, Nac2 = 0x8A1;

    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);

    private static P25Phase2Native Rx()
    {
        var rx = P25Phase2Native.TryCreate();
        Assert.NotNull(rx);
        return rx;
    }

    // ---- the codes against op25's ----

    private static IEnumerable<string[]> Reference(string kind) =>
        File.ReadLines(Data("p25p2_reference.txt")).Where(l => l.StartsWith(kind + " ")).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static byte[] Hex(string s) => Enumerable.Range(0, s.Length / 2).Select(i => Convert.ToByte(s.Substring(2 * i, 2), 16)).ToArray();

    [Fact]
    public void LibraryStructsAreOurs() => Assert.True(P25Phase2Native.AbiMatches);

    [Fact]
    public void ScramblingSequenceIsOp25s()
    {
        int n = 0;
        foreach (var f in Reference("scramble"))
        {
            var seq = new byte[540];
            fixed (byte* p = seq) P25Phase2Native.p25p2_test_scramble(Convert.ToUInt32(f[1], 16), Convert.ToUInt32(f[2], 16), Convert.ToUInt32(f[3], 16), p);
            Assert.Equal(f[4], Convert.ToHexString(seq).ToLowerInvariant());
            n++;
        }
        Assert.Equal(3, n);
    }

    [Fact]
    public void IschCodeWordsAreOp25sAndCorrectSevenErrors()
    {
        var rng = new Random(1);
        int n = 0;
        foreach (var f in Reference("isch"))
        {
            int v = int.Parse(f[1]);
            ulong cw = Convert.ToUInt64(f[2], 16);
            Assert.Equal(cw, P25Phase2Native.p25p2_test_isch_encode(v));
            n++;
        }
        Assert.Equal(128, n);
        ulong sisch = Convert.ToUInt64(Reference("sisch").Single()[1], 16);
        Assert.Equal(0x575D57F7FFul, sisch);
        int errs;
        Assert.Equal(-1, P25Phase2Native.p25p2_test_isch_decode(sisch, &errs));
        // Every value that can be sent (channel 0 or 1, location 0 to 2), with up to 7 bits wrong
        for (int v = 0; v < 512; v++)
        {
            if ((v >> 5 & 3) > 1 || (v >> 3 & 3) == 3) continue;
            ulong cw = P25Phase2Native.p25p2_test_isch_encode(v);
            for (int e = 0; e <= 7; e++)
            {
                ulong bad = cw ^ Flips(rng, 40, e);
                Assert.Equal(v, P25Phase2Native.p25p2_test_isch_decode(bad, &errs));
                Assert.Equal(e, errs);
            }
        }
    }

    private static ulong Flips(Random rng, int bits, int count)
    {
        ulong m = 0;
        while (ulong.PopCount(m) < (ulong)count) m |= 1ul << rng.Next(bits);
        return m;
    }

    [Fact]
    public void DuidIsOp25sTable()
    {
        int errs;
        foreach (var f in Reference("duid"))
            Assert.Equal(Convert.ToInt32(f[2], 16), P25Phase2Native.p25p2_test_duid_encode(int.Parse(f[1])));
        var table = Reference("duidtable").Single().Skip(1).Select(int.Parse).ToArray();
        Assert.Equal(256, table.Length);
        for (int cw = 0; cw < 256; cw++) Assert.Equal(table[cw], P25Phase2Native.p25p2_test_duid_decode(cw, &errs));
        for (int v = 0; v < 16; v++)
            for (int b = 0; b < 8; b++)
            {
                Assert.Equal(v, P25Phase2Native.p25p2_test_duid_decode(P25Phase2Native.p25p2_test_duid_encode(v) ^ 1 << b, &errs));
                Assert.Equal(1, errs);
            }
    }

    [Fact]
    public void ReedSolomonEncodersAreOp25s()
    {
        int n = 0;
        foreach (var f in Reference("rs"))
        {
            int code = f[1] switch { "facch" => P25Phase2Native.RsFacch, "sacch" => P25Phase2Native.RsSacch, _ => P25Phase2Native.RsEss };
            int colon = Array.IndexOf(f, ":");
            byte[] info = f[2..colon].Select(s => Convert.ToByte(s, 16)).ToArray();
            byte[] want = f[(colon + 1)..].Select(s => Convert.ToByte(s, 16)).ToArray();
            var sent = new byte[63];
            fixed (byte* i = info, s = sent) P25Phase2Native.p25p2_test_rs_encode(code, i, s);
            Assert.Equal(want, sent[..want.Length]);
            n++;
        }
        Assert.Equal(12, n);
    }

    [Theory]
    [InlineData(P25Phase2Native.RsFacch, 26, 45, 9)]
    [InlineData(P25Phase2Native.RsSacch, 30, 52, 11)]
    [InlineData(P25Phase2Native.RsEss, 16, 44, 14)]
    public void ReedSolomonCorrectsToItsLimitAndRefusesOneMore(int code, int k, int sent, int t)
    {
        var rng = new Random(code + 10);
        for (int trial = 0; trial < 200; trial++)
        {
            var info = new byte[k];
            for (int i = 0; i < k; i++) info[i] = (byte)rng.Next(64);
            var word = new byte[63];
            fixed (byte* i = info, w = word) P25Phase2Native.p25p2_test_rs_encode(code, i, w);
            for (int e = 0; e <= t + 1; e++)
            {
                var bad = (byte[])word.Clone();
                foreach (int at in Enumerable.Range(0, sent).OrderBy(_ => rng.Next()).Take(e)) bad[at] ^= (byte)rng.Next(1, 64);
                int rc;
                fixed (byte* b = bad) rc = P25Phase2Native.p25p2_test_rs_decode(code, b, null);
                if (e <= t)
                {
                    Assert.Equal(e, rc);
                    Assert.Equal(word[..sent], bad[..sent]);
                }
                else
                {
                    Assert.Equal(-1, rc);
                }
            }
        }
    }

    [Fact]
    public void EssCorrectsErasuresAndErrorsTogether()
    {
        // A missing ESS-B fragment is four erasures: with one missing, 12 errors are still corrected
        var rng = new Random(5);
        for (int trial = 0; trial < 100; trial++)
        {
            var info = new byte[16];
            for (int i = 0; i < 16; i++) info[i] = (byte)rng.Next(64);
            var word = new byte[63];
            fixed (byte* i = info, w = word) P25Phase2Native.p25p2_test_rs_encode(P25Phase2Native.RsEss, i, w);
            var bad = (byte[])word.Clone();
            var erased = new byte[44];
            int miss = rng.Next(4);
            for (int i = 0; i < 4; i++)
            {
                erased[4 * miss + i] = 1;
                bad[4 * miss + i] = 0;
            }
            foreach (int at in Enumerable.Range(0, 44).Where(x => erased[x] == 0).OrderBy(_ => rng.Next()).Take(12)) bad[at] ^= (byte)rng.Next(1, 64);
            int rc;
            fixed (byte* b = bad, er = erased) rc = P25Phase2Native.p25p2_test_rs_decode(P25Phase2Native.RsEss, b, er);
            Assert.Equal(12, rc);
            Assert.Equal(word[..44], bad[..44]);
        }
    }

    [Fact]
    public void Crc12IsOp25s()
    {
        int n = 0;
        foreach (var f in Reference("crc12"))
        {
            byte[] oct = Hex(f[2]);
            fixed (byte* o = oct) Assert.Equal(Convert.ToInt32(f[3], 16), P25Phase2Native.p25p2_test_crc12(o, int.Parse(f[1])));
            n++;
        }
        Assert.Equal(8, n);
    }

    // ---- the receiver ----

    /// <summary>A p25p2gen file (48 kHz, signed 16-bit I/Q, 16384 = 1.0).</summary>
    private static (float[] Re, float[] Im) Iq(string name)
    {
        var raw = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Data(name)));
        int n = raw.Length / 2;
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = raw[2 * i] / 16384f;
            im[i] = raw[2 * i + 1] / 16384f;
        }
        return (re, im);
    }

    /// <summary>The signal moved up offsetHz, with white noise at esN0 dB (symbol energy of its mean power over N0).</summary>
    private static (float[] Re, float[] Im) Spoil((float[] Re, float[] Im) s, double offsetHz, double esN0, int seed)
    {
        int n = s.Re.Length;
        double p = 0;
        for (int i = 0; i < n; i++) p += s.Re[i] * s.Re[i] + s.Im[i] * s.Im[i];
        p /= n;
        double sigma = Math.Sqrt(p * 8 / Math.Pow(10, esN0 / 10) / 2);
        var rng = new Random(seed);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++)
        {
            double w = 2 * Math.PI * offsetHz * i / 48000, c = Math.Cos(w), sn = Math.Sin(w);
            re[i] = (float)(s.Re[i] * c - s.Im[i] * sn + sigma * Gauss());
            im[i] = (float)(s.Re[i] * sn + s.Im[i] * c + sigma * Gauss());
        }
        return (re, im);
    }

    private sealed record Run(List<P25Phase2Native.Event> Events, List<byte[]> Voice, P25Phase2Native.Stats Stats, bool EverLocked);

    // A tenth of a second at a time, as the app will
    private static Run Decode((float[] Re, float[] Im) s, int slot, (uint, uint, uint)? system)
    {
        using var rx = Rx();
        if (system is var (w, y, c)) rx.SetSystem(w, y, c);
        rx.SetSlot(slot);
        var events = new List<P25Phase2Native.Event>();
        var voice = new List<byte[]>();
        bool locked = false;
        for (int i = 0; i < s.Re.Length; i += 4800)
        {
            int k = Math.Min(4800, s.Re.Length - i);
            rx.Feed(s.Re.AsSpan(i, k), s.Im.AsSpan(i, k));
            locked |= rx.Locked;
            events.AddRange(rx.ReadEvents());
            voice.AddRange(rx.ReadVoice());
        }
        return new Run(events, voice, rx.Statistics, locked);
    }

    /// <summary>The frames p25p2gen sent on each channel, as it wrote them (Data/p25p2_voice_frames.txt).</summary>
    private static List<byte[]> SentFrames(int slot) =>
        File.ReadLines(Data("p25p2_voice_frames.txt")).Where(l => l[0] - '0' == slot).Select(l => l[2..51].Select(ch => (byte)(ch - '0')).ToArray()).ToList();

    [DllImport("ss_digital")] private static extern nint dmrrx_new(int rate, float hzPerUnit);
    [DllImport("ss_digital")] private static extern void dmrrx_free(nint r);
    [DllImport("ss_digital")] private static extern void dmrrx_feed(nint r, float* disc, int n);
    [DllImport("ss_digital")] private static extern int dmrrx_read_voice(nint r, byte* items, int max);

    /// <summary>The AMBE+2 frames of the DMR test call, as the DMR receiver reads them (what p25p2gen starts from).</summary>
    private static List<byte[]> DmrFrames()
    {
        var raw = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Data("dmr_repeater_24k.raw")));
        var x = new float[raw.Length];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(raw[i] / 6553.6);
        nint r = dmrrx_new(24000, 1000);
        var frames = new List<byte[]>();
        var items = new byte[64 * 50];
        try
        {
            for (int at = 0; at < x.Length; at += 4800)
            {
                fixed (float* p = &x[at]) dmrrx_feed(r, p, Math.Min(4800, x.Length - at));
                int n;
                fixed (byte* it = items)
                    while ((n = dmrrx_read_voice(r, it, 64)) > 0)
                        for (int i = 0; i < n; i++)
                            if (items[i * 50 + 49] == 0) frames.Add(items.AsSpan(i * 50, 49).ToArray());
            }
        }
        finally { dmrrx_free(r); }
        return frames;
    }

    [Fact]
    public void GeneratorFramesAreTheDmrCallsFrames()
    {
        var dmr = DmrFrames();
        Assert.True(dmr.Count >= 72);
        Assert.Equal(dmr.GetRange(0, 36).Select(Convert.ToHexString), SentFrames(0).Select(Convert.ToHexString));
        Assert.Equal(dmr.GetRange(36, 36).Select(Convert.ToHexString), SentFrames(1).Select(Convert.ToHexString));
    }

    // What p25p2gen sends in each superframe (its PH_* order), and the MAC PDU of a FACCH or SACCH burst
    private static readonly string[] Phases = ["idle", "ptt", "voice", "voice", "end", "idle"];
    private static readonly (uint Tg, uint Src, byte[] Mi)[] Calls =
    [
        (1001, 2001001, new byte[9]),
        (1002, 2002002, [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99]),
    ];

    private static int Position(int ts) => ts >= 10 ? 5 : ts / 2;

    // Which of p25p2gen's superframes a burst is from: the signal starts with the first's first timeslot
    private static int Superframe(P25Phase2Native.Event b) => (int)Math.Round((b.Symbol - b.Ts * 180.0) / 2160);

    private static int ExpectedDuid(string phase, int ts, bool scrambled = true)
    {
        int pos = Position(ts);
        if (phase == "voice" && pos < 5) return pos < 4 ? P25Phase2Native.Duid4V : P25Phase2Native.Duid2V;
        return pos == 5 ? (scrambled ? P25Phase2Native.DuidSacchS : P25Phase2Native.DuidSacchU) : (scrambled ? P25Phase2Native.DuidFacchS : P25Phase2Native.DuidFacchU);
    }

    private static byte[] ExpectedPdu(string phase, int ts, int slot, (uint Tg, uint Src, byte[] Mi) call, uint nac)
    {
        int pos = Position(ts);
        var o = new byte[pos == 5 ? 21 : 18];
        void Addresses()
        {
            o[13] = (byte)(call.Src >> 16); o[14] = (byte)(call.Src >> 8); o[15] = (byte)call.Src;
            o[16] = (byte)(call.Tg >> 8); o[17] = (byte)call.Tg;
        }
        switch (phase)
        {
            case "idle": o[0] = 3 << 5 | 7 << 2; break;
            case "ptt":
                o[0] = (byte)(1 << 5 | (pos == 5 ? 0 : 4 - pos) << 2);
                call.Mi.CopyTo(o, 1);
                o[10] = 0x80;
                Addresses();
                break;
            case "end":
                o[0] = 2 << 5 | 7 << 2;
                o[1] = (byte)(nac >> 8 & 15); o[2] = (byte)nac;
                Addresses();
                break;
            default:
                o[0] = 4 << 5;
                o[1] = 0x01;
                o[3] = (byte)(call.Tg >> 8); o[4] = (byte)call.Tg;
                o[5] = (byte)(call.Src >> 16); o[6] = (byte)(call.Src >> 8); o[7] = (byte)call.Src;
                break;
        }
        _ = slot;
        return o;
    }

    [Fact]
    public void GeneratedChannelDecodesBothSlotsExactly()
    {
        var iq = Iq("p25p2_voice_48k.raw");
        for (int slot = 0; slot < 2; slot++)
        {
            var run = Decode(iq, slot, (Wacn, SysId, Nac));
            Assert.Equal(P25Phase2Native.EvSync, run.Events[0].Kind);
            Assert.Equal(1, run.Stats.Syncs);
            Assert.Equal(P25Phase2Native.EvLost, run.Events[^1].Kind);       // the transmitter stops at the end
            var bursts = run.Events.Where(e => e.Kind == P25Phase2Native.EvBurst).ToList();
            // Found within the first superframe; everything after that, in both channels, as sent
            Assert.True(bursts.Count >= 6 * 12 - 6, $"{bursts.Count} bursts");
            Assert.Equal(new[] { 0, 1 }, bursts.Select(b => b.Slot).Distinct().Order());
            foreach (var b in bursts)
            {
                string phase = Phases[Superframe(b)];
                Assert.Equal(new[] { 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 1, 0 }[b.Ts], b.Slot);
                Assert.Equal(ExpectedDuid(phase, b.Ts), b.Duid);
                Assert.Equal(0, b.DuidErrs);
                Assert.Equal(1, b.Readable);
                if (b.Duid is P25Phase2Native.DuidFacchS or P25Phase2Native.DuidSacchS)
                {
                    Assert.Equal(1, b.Ok);
                    Assert.Equal(1, b.CrcOk);
                    Assert.Equal(0, b.RsErrs);
                    Assert.Equal(ExpectedPdu(phase, b.Ts, b.Slot, Calls[b.Slot], Nac), b.Bytes());
                }
                else
                {
                    Assert.Equal(0, b.VoiceErrs);
                }
                // The I-ISCHs: location, channel, the superframe count; the sync pattern before the others
                if (b.Ts % 4 < 2) Assert.Equal((b.Ts % 4) << 5 | (b.Ts / 4) << 3, b.Isch & ~3);
                else Assert.Equal(-1, b.Isch);
            }
            // The ESS of each voice superframe
            var ess = run.Events.Where(e => e.Kind == P25Phase2Native.EvEss).ToList();
            Assert.Equal(4, ess.Count);
            foreach (var e in ess)
            {
                Assert.Equal(1, e.Ok);
                Assert.Equal(new byte[] { 0x80, 0, 0 }.Concat(Calls[e.Slot].Mi), e.Bytes());
            }
            // This slot's speech, frame for frame, and the end of the call after it
            var frames = run.Voice.Where(v => v[49] != P25Phase2Native.ErrsEnd).ToList();
            Assert.Equal(SentFrames(slot).Select(Convert.ToHexString), frames.Select(v => Convert.ToHexString(v, 0, 49)));
            Assert.All(frames, v => Assert.Equal(0, v[49]));
            Assert.Equal(P25Phase2Native.ErrsEnd, run.Voice[^1][49]);
        }
    }

    [Theory]
    [InlineData(500, 14, 1)]
    [InlineData(-500, 14, 2)]
    public void NoiseAndOffsetLeaveMostFramesAndNoneInTheWrongSlot(double offsetHz, double esN0, int seed)
    {
        var iq = Spoil(Iq("p25p2_voice_48k.raw"), offsetHz, esN0, seed);
        for (int slot = 0; slot < 2; slot++)
        {
            var run = Decode(iq, slot, (Wacn, SysId, Nac));
            var want = SentFrames(slot).Select(Convert.ToHexString).ToList();
            var other = SentFrames(slot ^ 1).Select(Convert.ToHexString).ToHashSet();
            var got = run.Voice.Where(v => v[49] != P25Phase2Native.ErrsEnd).Select(v => Convert.ToHexString(v, 0, 49)).ToList();
            int exact = got.Count(want.Contains);
            int mac = run.Events.Count(e => e.Kind == P25Phase2Native.EvBurst && e.Ok == 1);
            output.WriteLine($"offset {offsetHz} Hz, Es/N0 {esN0} dB, slot {slot}: {exact} of {want.Count} frames exact, {got.Count} given, " +
                             $"{mac} MAC PDUs good, {run.Stats.MacBad} bad, offset measured {run.Stats.OffsetHz:F0} Hz");
            Assert.True(run.EverLocked);
            Assert.True(exact >= want.Count * 85 / 100, $"{exact} of {want.Count} frames");
            Assert.DoesNotContain(got, other.Contains);
            Assert.True(mac >= 44, $"{mac} MAC PDUs");
            // Whatever passed its CRC is what was sent
            foreach (var b in run.Events.Where(e => e.Kind == P25Phase2Native.EvBurst && e.Ok == 1))
                Assert.Equal(ExpectedPdu(Phases[Superframe(b)], b.Ts, b.Slot, Calls[b.Slot], Nac), b.Bytes());
        }
    }

    [Fact]
    public void WrongNacGivesNoScrambledBurst()
    {
        var run = Decode(Iq("p25p2_voice_48k.raw"), 0, (Wacn, SysId, Nac ^ 1));
        Assert.True(run.EverLocked);                                          // the ISCH is not scrambled
        Assert.Contains(run.Events, e => e.Kind == P25Phase2Native.EvBurst && e.Duid == P25Phase2Native.DuidFacchS && e.Readable == 1);
        Assert.DoesNotContain(run.Events, e => e.Ok == 1);
        var want = SentFrames(0).Select(Convert.ToHexString).ToHashSet();
        Assert.DoesNotContain(run.Voice, v => want.Contains(Convert.ToHexString(v, 0, 49)));
    }

    [Fact]
    public void WithoutTheSystemScrambledBurstsAreNotRead()
    {
        var run = Decode(Iq("p25p2_voice_48k.raw"), 0, null);
        var bursts = run.Events.Where(e => e.Kind == P25Phase2Native.EvBurst).ToList();
        Assert.True(bursts.Count > 60);
        Assert.All(bursts, b => Assert.Equal(1, b.Scrambled));
        Assert.All(bursts, b => Assert.Equal(0, b.Readable));
        Assert.Equal(0, run.Stats.HaveSystem);
        Assert.Empty(run.Voice);
    }

    [Fact]
    public void UnscrambledSignallingIsReadWithoutTheSystem()
    {
        // p25p2_unscrambled_48k.raw: another system, FACCH and SACCH sent unscrambled, one voice superframe
        var iq = Iq("p25p2_unscrambled_48k.raw");
        string[] phases = ["idle", "ptt", "voice", "end", "idle"];
        (uint, uint, byte[])[] calls = [(4001, 123, new byte[9]), (4002, 456, Calls[1].Mi)];
        var run = Decode(iq, 0, null);
        var mac = run.Events.Where(e => e.Kind == P25Phase2Native.EvBurst && e.Duid is P25Phase2Native.DuidFacchU or P25Phase2Native.DuidSacchU).ToList();
        // Found later than the scrambled channel is: unscrambled idle bursts are mostly zeros, the same phase step symbol
        // after symbol, which the demodulator takes for a carrier offset until the PTT's bursts come
        Assert.True(mac.Count >= 24, $"{mac.Count} MAC PDUs");
        foreach (var b in mac)
        {
            Assert.Equal(ExpectedDuid(phases[Superframe(b)], b.Ts, scrambled: false), b.Duid);
            Assert.Equal(1, b.Ok);
            Assert.Equal(ExpectedPdu(phases[Superframe(b)], b.Ts, b.Slot, calls[b.Slot], Nac2), b.Bytes());
        }
        Assert.Empty(run.Voice);                                              // voice is scrambled still
        // With this system set, its voice and SACCH are read too: a second scrambling sequence, end to end
        var dmr = DmrFrames();
        for (int slot = 0; slot < 2; slot++)
        {
            run = Decode(iq, slot, (Wacn2, SysId2, Nac2));
            var frames = run.Voice.Where(v => v[49] != P25Phase2Native.ErrsEnd).Select(v => Convert.ToHexString(v, 0, 49));
            Assert.Equal(dmr.GetRange(slot == 0 ? 0 : 36, 18).Select(Convert.ToHexString), frames);
            Assert.Equal(2, run.Events.Count(e => e.Kind == P25Phase2Native.EvEss && e.Ok == 1));
        }
    }

    [Fact]
    public void NoiseIsNotPhase2()
    {
        var rng = new Random(9);
        int n = 48000 * 20;
        var re = new float[n];
        var im = new float[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = (float)(0.2 * Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble()));
            im[i] = (float)(0.2 * Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble()));
        }
        var run = Decode((re, im), 0, (Wacn, SysId, Nac));
        Assert.False(run.EverLocked);
        Assert.Empty(run.Events);
        Assert.Empty(run.Voice);
    }

    [Fact]
    public void Phase1CqpskIsNotPhase2()
    {
        var run = Decode(Iq("p25_trunk_control_cqpsk_48k.raw"), 0, (Wacn, SysId, Nac));
        Assert.False(run.EverLocked);
        Assert.Empty(run.Events);
    }
}
