// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;
using SimpleScanner.Digital;
using SimpleScanner.Dsp;

namespace SimpleScanner.Tests;

/// <summary>
/// P25 Phase 2 above the native layer: the MAC PDUs read (<see cref="P25MacPdu"/>), against values op25's own code gives for
/// the same octets and against the generated channels; and <see cref="P25Phase2Decoder"/> on those channels (Data/README.md):
/// each slot's own call and speech, nothing played before a clear call is established or after it ends, never another slot's
/// or another talkgroup's, an encrypted call silent and shown, the wrong system nothing, and no speech decoder.
/// </summary>
public class P25Phase2Tests
{
    private const int Wacn = 0xBEE00, SysId = 0x123, Nac = 0x293;

    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name);
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    // ---- the MAC ----

    // Each PDU (FACCH 18 octets, SACCH 21, the CRC left off) with what op25's code reads in it. The values on the right were
    // printed by a harness made of op25's p25p2_tdma.cc (mac_msg_len, decode_mac_msg, and the field arithmetic of
    // handle_mac_ptt and handle_mac_end_ptt, copied) and tk_p25.py's decode_tdma_msg slices for 0x01 and 0x21, run on these
    // lines. The first six are p25p2gen's (PUSH-TO-TALK clear and encrypted, END-PUSH-TO-TALK, ACTIVE with the abbreviated and
    // the extended group voice channel user message, HANGTIME); the next three sdrtrunk's MacMessageFactoryTest structures
    // (0x45 8 octets, 0xC5 12, 0x4A 9, each followed by a MAC release, 0x31, 7); then a manufacturer's message with its length
    // byte, and IDLE with null information only.
    [Theory]
    [InlineData("301122334455667788998000001E8C5203EA", "type=1 offset=4 algid=0x80 keyid=0x0000 src=2002002 grp=1002 mi=112233445566778899")]
    [InlineData("301122334455667788998412341E8C5203EA", "type=1 offset=4 algid=0x84 keyid=0x1234 src=2002002 grp=1002 mi=112233445566778899")]
    [InlineData("5C0293000000000000000000001E886903E9", "type=2 offset=7 colorcd=0x293 src=2001001 grp=1001")]
    [InlineData("80010003E91E886900000000000000000000000000", "type=4 offset=0 msg op=0x01 mfid=0x00 len=7 opts=0x00 ga=1001 sa=2001001")]
    [InlineData("80218003E91E8869BEE001231E8869000000000000", "type=4 offset=0 msg op=0x21 mfid=0x00 len=14 opts=0x80 ga=1001 sa=2001001")]
    [InlineData("DC014003EA1E8C5200000000000000000000", "type=6 offset=7 msg op=0x01 mfid=0x00 len=7 opts=0x40 ga=1002 sa=2002002")]
    [InlineData("8045000003E80007D0310000109200000000000000", "type=4 offset=0 msg op=0x45 mfid=0x00 len=8 msg op=0x31 mfid=0x00 len=7")]
    [InlineData("80C5000003E8BEE001230007D03100001092000000", "type=4 offset=0 msg op=0xc5 mfid=0x00 len=12 msg op=0x31 mfid=0x00 len=7")]
    [InlineData("804A01234567896543213100001092000000000000", "type=4 offset=0 msg op=0x4a mfid=0x00 len=9 msg op=0x31 mfid=0x00 len=7")]
    [InlineData("80819005AABB010003E91E88690000000000", "type=4 offset=0 msg op=0x81 mfid=0x90 len=5 msg op=0x01 mfid=0x00 len=7 opts=0x00 ga=1001 sa=2001001")]
    [InlineData("7C0000000000000000000000000000000000", "type=3 offset=7")]
    public void MacPdusReadAsOp25ReadsThem(string octets, string op25)
    {
        var pdu = P25MacPdu.Parse(Hex(octets));
        Assert.NotNull(pdu);
        var s = new System.Text.StringBuilder($"type={pdu.Type} offset={pdu.Offset}");
        switch (pdu.Kind)
        {
            case P25MacType.PushToTalk:
                s.Append($" algid=0x{pdu.AlgId:x2} keyid=0x{pdu.KeyId:x4} src={pdu.Source} grp={pdu.Talkgroup} mi={Convert.ToHexString(pdu.Mi).ToLowerInvariant()}");
                break;
            case P25MacType.EndPushToTalk:
                s.Append($" colorcd=0x{pdu.ColorCode:x3} src={pdu.Source} grp={pdu.Talkgroup}");
                break;
            default:
                foreach (var m in pdu.Messages)
                {
                    s.Append($" msg op=0x{m.Opcode:x2} mfid=0x{m.Mfid:x2} len={m.Octets.Length}");
                    if (m.IsGroupVoiceUser) s.Append($" opts=0x{m.ServiceOptions:x2} ga={m.Talkgroup} sa={m.Source}");
                }
                break;
        }
        Assert.Equal(op25, s.ToString());
    }

    [Fact]
    public void MacPduMeanings()
    {
        Assert.False(P25MacPdu.Parse(Hex("301122334455667788998000001E8C5203EA"))!.Encrypted);
        Assert.True(P25MacPdu.Parse(Hex("301122334455667788998412341E8C5203EA"))!.Encrypted);
        var ext = P25MacPdu.Parse(Hex("80218003E91E8869BEE001231E8869000000000000"))!.GroupVoiceUser!;
        Assert.Equal((P25MacMessage.GroupVoiceUserExtended, 1001, 2001001, 0x80), (ext.Opcode, ext.Talkgroup, ext.Source, ext.ServiceOptions));
        Assert.Null(P25MacPdu.Parse(Hex("7C0000000000000000000000000000000000"))!.GroupVoiceUser);
        Assert.Null(P25MacPdu.Parse(new byte[10]));
        Assert.Equal((0x84, 0x1234), P25MacPdu.Ess(Hex("841234112233445566778899")));
    }

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

    // Every MAC PDU the native receiver read in a generated channel, by slot.
    private static List<(int Slot, P25MacPdu Pdu)> Pdus(string name, int wacn = Wacn, int sysId = SysId, int nac = Nac)
    {
        using var rx = P25Phase2Native.TryCreate()!;
        rx.SetSystem((uint)wacn, (uint)sysId, (uint)nac);
        var (re, im) = Iq(name);
        var list = new List<(int, P25MacPdu)>();
        for (int i = 0; i < re.Length; i += 4800)
        {
            int k = Math.Min(4800, re.Length - i);
            rx.Feed(re.AsSpan(i, k), im.AsSpan(i, k));
            foreach (var e in rx.ReadEvents())
                if (e.Kind == P25Phase2Native.EvBurst && e.Ok == 1) list.Add((e.Slot, P25MacPdu.Parse(e.Bytes())!));
        }
        return list;
    }

    [Fact]
    public void TheGeneratedChannelsMacPdusSayEachSlotsCall()
    {
        var pdus = Pdus("p25p2_voice_48k.raw");
        foreach (var (slot, tg, src) in new[] { (0, 1001, 2001001), (1, 1002, 2002002) })
        {
            var mine = pdus.Where(p => p.Slot == slot).Select(p => p.Pdu).ToList();
            var ptt = mine.Where(p => p.Kind == P25MacType.PushToTalk).ToList();
            Assert.Equal(6, ptt.Count);                                   // five FACCHs and the SACCH
            Assert.All(ptt, p => Assert.Equal((tg, src, P25MacPdu.Clear, false), (p.Talkgroup, p.Source, p.AlgId, p.Encrypted)));
            var users = mine.Where(p => p.Kind == P25MacType.Active).Select(p => p.GroupVoiceUser!).ToList();
            Assert.Equal(2, users.Count);
            Assert.All(users, u => Assert.Equal((tg, src, 0), (u.Talkgroup, u.Source, u.ServiceOptions)));
            var end = mine.Where(p => p.Kind == P25MacType.EndPushToTalk).ToList();
            Assert.Equal(6, end.Count);
            Assert.All(end, p => Assert.Equal((Nac, tg, src), (p.ColorCode, p.Talkgroup, p.Source)));
        }

        // Data/p25p2_late_enc_48k.raw: slot 0 joined late (no PUSH-TO-TALK), extended user message, emergency; slot 1 encrypted
        pdus = Pdus("p25p2_late_enc_48k.raw");
        Assert.DoesNotContain(pdus, p => p.Slot == 0 && p.Pdu.Kind == P25MacType.PushToTalk);
        var late = pdus.Where(p => p.Slot == 0 && p.Pdu.Kind == P25MacType.Active).Select(p => p.Pdu.GroupVoiceUser!).ToList();
        Assert.Equal(2, late.Count);
        Assert.All(late, u => Assert.Equal((P25MacMessage.GroupVoiceUserExtended, 1001, 2001001, 0x80), (u.Opcode, u.Talkgroup, u.Source, u.ServiceOptions)));
        var enc = pdus.Where(p => p.Slot == 1 && p.Pdu.Kind == P25MacType.PushToTalk).Select(p => p.Pdu).ToList();
        Assert.Equal(6, enc.Count);
        Assert.All(enc, p => Assert.Equal((0x84, 0x1234, true), (p.AlgId, p.KeyId, p.Encrypted)));
        Assert.All(pdus.Where(p => p.Slot == 1 && p.Pdu.Kind == P25MacType.Active), p => Assert.Equal(0x40, p.Pdu.GroupVoiceUser!.ServiceOptions));
        var hang = pdus.Where(p => p.Pdu.Kind == P25MacType.Hangtime).ToList();
        Assert.Equal(12, hang.Count);
        Assert.All(hang, p => Assert.Equal(1001 + p.Slot, p.Pdu.GroupVoiceUser!.Talkgroup));
    }

    // ---- the decoder ----

    /// <summary>The frames p25p2gen sent on a channel, as it wrote them.</summary>
    private static List<string> Sent(string file, int slot) =>
        File.ReadLines(Data(file)).Where(l => l[0] - '0' == slot).Select(l => Convert.ToHexString(l[2..51].Select(ch => (byte)(ch - '0')).ToArray())).ToList();

    private sealed record Run(List<string> Played, int Ends, List<P25Status> Statuses, int Samples);

    // The decoder on a whole file, fed in pieces of the given size: the frames given to the vocoder, the ends, the status after each
    // piece, and the speech samples it gave.
    private static Run Decode(string name, P25Phase2Params p, int piece = 4800, bool vocoder = true)
    {
        using var d = P25Phase2Decoder.TryCreate(vocoder)!;
        d.Configure(p);
        var played = new List<string>();
        int ends = 0, samples = 0;
        d.FrameTap = item =>
        {
            if (item[49] == P25Phase2Native.ErrsEnd) ends++;
            else played.Add(Convert.ToHexString(item, 0, 49));
        };
        var statuses = new List<P25Status>();
        var (re, im) = Iq(name);
        var voice = new float[8000];
        for (int i = 0; i < re.Length; i += piece)
        {
            int k = Math.Min(piece, re.Length - i);
            d.Feed(re.AsSpan(i, k), im.AsSpan(i, k));
            statuses.Add(d.Status);
            int n;
            while ((n = d.ReadVoice(voice)) > 0) samples += n;
        }
        return new Run(played, ends, statuses, samples);
    }

    [Theory]
    [InlineData(0, 1001, 2001001, 4800)]
    [InlineData(1, 1002, 2002002, 777)]
    public void EachSlotPlaysItsOwnCallFrameForFrame(int slot, int tg, int src, int piece)
    {
        var run = Decode("p25p2_voice_48k.raw", new P25Phase2Params(Wacn, SysId, Nac, slot), piece);
        // Every frame of the call, as sent, and none of the other slot's: the call starts with its PUSH-TO-TALK
        Assert.Equal(Sent("p25p2_voice_frames.txt", slot), run.Played);
        Assert.True(run.Ends >= 1);
        var inCall = run.Statuses.Where(s => s.InCall).ToList();
        Assert.NotEmpty(inCall);
        Assert.All(inCall, s => Assert.Equal((2, slot, tg, src, false, false), (s.Phase, s.Slot, s.Talkgroup, s.Source, s.Encrypted, s.Emergency)));
        Assert.Equal($"P25 Phase 2 · NAC 293 · TG {tg} · ID {src}", inCall[0].ToString());
        // Idle before the call and after its END-PUSH-TO-TALK
        Assert.False(run.Statuses[0].InCall);
        Assert.False(run.Statuses[^1].InCall);
        Assert.Equal("P25 Phase 2 · NAC 293", run.Statuses[^1].ToString());
        if (AmbeAvailable) Assert.Equal(run.Played.Count * 160, run.Samples);
    }

    private static bool AmbeAvailable
    {
        get
        {
            using var v = AmbeVocoder.TryCreate();
            return v is not null;
        }
    }

    [Fact]
    public void ACallJoinedLateIsPlayedOnceItsVoiceUserMessageCame()
    {
        // Slot 0 of Data/p25p2_late_enc_48k.raw has no PUSH-TO-TALK: its first voice superframe goes by before the ACTIVE that
        // names the call, at its end; the second is played. Emergency, from the service options.
        var run = Decode("p25p2_late_enc_48k.raw", new P25Phase2Params(Wacn, SysId, Nac, 0));
        Assert.Equal(Sent("p25p2_late_enc_frames.txt", 0).Skip(18), run.Played);
        var inCall = run.Statuses.Where(s => s.InCall).ToList();
        Assert.NotEmpty(inCall);
        Assert.All(inCall, s => Assert.Equal((1001, 2001001, true, false), (s.Talkgroup, s.Source, s.Emergency, s.Encrypted)));
        Assert.Equal("P25 Phase 2 · NAC 293 · TG 1001 · ID 2001001 · EMERGENCY", inCall[0].ToString());
        // The HANGTIME after the call: no call, the talkgroup kept
        Assert.Equal((false, 1001), (run.Statuses[^1].InCall, run.Statuses[^1].Talkgroup));
    }

    [Fact]
    public void AnEncryptedCallIsShownAndNeverPlayed()
    {
        var run = Decode("p25p2_late_enc_48k.raw", new P25Phase2Params(Wacn, SysId, Nac, 1));
        Assert.Empty(run.Played);
        Assert.Equal(0, run.Samples);
        var inCall = run.Statuses.Where(s => s.InCall).ToList();
        Assert.NotEmpty(inCall);
        Assert.All(inCall, s => Assert.True(s.Encrypted));
        Assert.All(inCall, s => Assert.True(s.NothingToHear));
        Assert.Equal("P25 Phase 2 · NAC 293 · TG 1002 · ID 2002002 · encrypted", inCall[0].ToString());
    }

    [Fact]
    public void TheWrongSystemPlaysNothing()
    {
        var run = Decode("p25p2_voice_48k.raw", new P25Phase2Params(Wacn, SysId, Nac ^ 1, 0));
        Assert.Empty(run.Played);
        Assert.DoesNotContain(run.Statuses, s => s.InCall);
    }

    [Fact]
    public void AnotherTalkgroupThanTheOneGrantedIsShownAndNotPlayed()
    {
        var run = Decode("p25p2_voice_48k.raw", new P25Phase2Params(Wacn, SysId, Nac, 1, Talkgroup: 4004));
        Assert.Empty(run.Played);
        Assert.Contains(run.Statuses, s => s is { InCall: true, Talkgroup: 1002 });
        // The one granted is
        run = Decode("p25p2_voice_48k.raw", new P25Phase2Params(Wacn, SysId, Nac, 1, Talkgroup: 1002));
        Assert.Equal(Sent("p25p2_voice_frames.txt", 1), run.Played);
    }

    [Fact]
    public void WithoutTheSpeechDecoderACallIsShownAndSilent()
    {
        var run = Decode("p25p2_voice_48k.raw", new P25Phase2Params(Wacn, SysId, Nac, 0), vocoder: false);
        Assert.Equal(0, run.Samples);
        var s = run.Statuses.First(s => s.InCall);
        Assert.False(s.VoiceAvailable);
        Assert.Equal("P25 Phase 2 · NAC 293 · TG 1001 · ID 2001001 · no voice codec", s.ToString());
    }

    [Fact]
    public void NoiseIsNotPlayed()
    {
        using var d = P25Phase2Decoder.TryCreate()!;
        d.Configure(new P25Phase2Params(Wacn, SysId, Nac, 0));
        int played = 0;
        d.FrameTap = item => played += item[49] == P25Phase2Native.ErrsEnd ? 0 : 1;
        var rng = new Random(9);
        var re = new float[48000 * 3];
        var im = new float[re.Length];
        for (int i = 0; i < re.Length; i++)
        {
            re[i] = (float)(rng.NextDouble() - 0.5);
            im[i] = (float)(rng.NextDouble() - 0.5);
        }
        d.Feed(re, im);
        Assert.Equal(0, played);
        Assert.False(d.Status.InCall);
    }

    // ---- digital voice ----

    [Fact]
    public void DigitalVoiceExpectingPhase2FeedsOnlyThePhase2Decoder()
    {
        using var voice = new DigitalVoice { DigitalOnly = true };
        var (re, im) = Iq("p25p2_voice_48k.raw");
        float[] left = new float[4800], right = new float[4800];
        var disc = new float[4800];
        voice.Expect(new P25Phase2Params(Wacn, SysId, Nac, 1, 1002));
        bool sawCall = false, spoke = false;
        for (int i = 0; i + 4800 <= re.Length; i += 4800)
        {
            voice.FeedBaseband(re.AsSpan(i, 4800), im.AsSpan(i, 4800), true);
            voice.Feed(disc, 2500);
            voice.Process(true, false, Mode.Nfm, left, right, left.Length);
            sawCall |= voice.Status is P25Status { Phase: 2, InCall: true, Talkgroup: 1002 };
            spoke |= voice.Speaking;
            Assert.False(voice.P25IqRunning);                // Phase 1's differential front end is not fed
        }
        Assert.True(sawCall);
        if (AmbeAvailable) Assert.True(spoke);
        // Cleared: as before
        voice.Expect(null);
        voice.Reset();
        voice.FeedBaseband(re.AsSpan(0, 4800), im.AsSpan(0, 4800), true);
        Assert.True(voice.P25IqRunning);
        Assert.Null(voice.Expected);
    }
}
