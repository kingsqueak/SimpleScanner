// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;
using SimpleScanner.Digital;
using SimpleScanner.Dsp;
using SimpleScanner.Trunk;

namespace SimpleScanner.Tests;

/// <summary>
/// Trunk mode end to end, on the pretend radio: the generated control channel (Data/p25_trunk_follow_24k.raw, from
/// p25_trunk_follow.txt) on 851.0125 MHz, and the P25 test call (p25_call_24k.raw) on the voice frequency of a grant,
/// both sent over and over. At 2.4 MS/s the radio's span is 1.92 MHz: 851.5125 is inside it with the control channel,
/// 853.0125 is not.
/// </summary>
public class TrunkTests
{
    private const double ControlHz = 851_012_500, InsideHz = 851_512_500, OutsideHz = 853_012_500;
    private static readonly int[] Talkgroups = [1234, 1235, 5005, 6006, 4004];

    private static TrunkSiteDef Site(params double[] control) =>
        new(1, 2, "Test", "Site 1", 0x293, 0x123, 0xBEE00, 1, 1, control, [851_262_500, 851_387_500, InsideHz, OutsideHz]);

    // These talkgroups ticked, the rest of the script's not.
    private static TrunkFilter Ticked(params int[] tgs) => new(tgs, [.. Talkgroups.Except(tgs)], ListenUnknown: false);

    private static float[] Disc(string name)
    {
        var raw = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name)));
        var x = new float[raw.Length];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(raw[i] / 6553.6 * 1000 / 2500);
        return x;
    }

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

    // A p25gen --cqpsk --iq16 file as the complex baseband FakeCarrier plays: I and Q interleaved, amplitude 1.
    private static float[] Baseband(string name)
    {
        var raw = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", name)));
        var x = new float[raw.Length];
        for (int i = 0; i < x.Length; i++) x[i] = raw[i] / 16384f / 0.5f;
        return x;
    }

    private static (FakeSource Source, FakeCarrier Control, Receiver Rx) Radio()
    {
        var source = new FakeSource();
        var control = source.Add(ControlHz, 0.05, deviation: Disc("p25_trunk_follow_24k.raw"));
        var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6);
        rx.Start();
        return (source, control, rx);
    }

    // The loudness of what goes to the speaker over this long: the root mean square of its loudest 50 ms.
    private static double SpeechRms(Receiver rx, double seconds)
    {
        var heard = new List<float>();
        void Take(ReadOnlySpan<float> a)
        {
            lock (heard)
                foreach (float x in a) heard.Add(x);
        }
        rx.AudioOutput += Take;
        Thread.Sleep((int)(seconds * 1000));
        rx.AudioOutput -= Take;
        double best = 0;
        lock (heard)
            for (int at = 0; at + 4800 <= heard.Count; at += 4800)
                best = Math.Max(best, Math.Sqrt(heard.Skip(at).Take(4800).Sum(x => (double)x * x) / 4800));
        return best;
    }

    private static TrunkCall? Logged(Receiver rx, int talkgroup) => rx.Trunk?.Calls.LastOrDefault(c => c.Talkgroup == talkgroup);

    [Fact]
    public void ACallInsideTheSpanIsFollowedWithNoRetuneAndItsSpeechIsHeard()
    {
        var (source, _, rx) = Radio();
        using var _rx = rx;
        var voice = source.Add(InsideHz, 0.05, deviation: Disc("p25_call_24k.raw"));
        rx.StartTrunk(Site(ControlHz), Ticked(1234));
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 3), $"no control channel: {rx.Trunk?.State}");
        int tunes = source.Tunes;
        Assert.Equal(ControlHz, rx.Trunk!.ControlHz);
        Assert.Equal(0x293, rx.Trunk.Nac);
        Assert.Equal(0x123, rx.Trunk.SysId);
        Assert.True(rx.Trunk.MatchesSite);
        Assert.Contains("control channel", rx.Digital?.ToString());

        Assert.True(Wait(() => rx.Trunk?.Active?.Talkgroup == 1234, 3), "the call was not followed");
        Assert.Equal(InsideHz, rx.FrequencyHz);
        Assert.True(Wait(() => rx.Trunk?.Active?.Flags.HasFlag(TrunkCallFlags.Voice) == true, 4), "no speech decoded");
        Assert.Contains("TG 1234", rx.Digital?.ToString());
        // The control channel is read on through the call.
        long messages = rx.Trunk!.Messages;
        Assert.True(Wait(() => rx.Trunk?.Messages > messages + 10, 2), "the control channel was not read during the call");

        double rms = SpeechRms(rx, 1);
        Assert.True(rms > 0.02, $"no speech on the audio output: {rms:F4}");
        // The voice goes: the call ends, and after the hang time trunk mode is back on the control channel.
        voice.On = false;
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 3), $"not back on the control channel: {rx.Trunk?.State}");
        var call = Logged(rx, 1234);
        Assert.Equal(TrunkOutcome.Heard, call?.Outcome);
        Assert.Equal(5678, call!.Source);
        Assert.False(call.Flags.HasFlag(TrunkCallFlags.Hop));
        Assert.Equal(ControlHz, rx.FrequencyHz);
        Assert.Equal(tunes, source.Tunes);
        Assert.Equal(0, rx.Trunk!.Hops);
        Assert.Equal(0, rx.DroppedBlocks);
        rx.Stop();
        Assert.Null(rx.Trunk);
    }

    /// <summary>
    /// A simulcast site: the same control channel sent by CQPSK transmitters, heard as two of them 30 us apart (2 dB and 3 Hz
    /// between them; Data/p25_trunk_follow_simulcast_48k.raw). The control channel is read through the differential front
    /// end, and the grant is followed as on a C4FM site.
    /// </summary>
    [Fact]
    public void ASimulcastControlChannelIsReadAndItsCallFollowed()
    {
        var source = new FakeSource();
        source.Add(ControlHz, 0.05, baseband: Baseband("p25_trunk_follow_simulcast_48k.raw"));
        using var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6);
        rx.Start();
        source.Add(InsideHz, 0.05, deviation: Disc("p25_call_24k.raw"));
        rx.StartTrunk(Site(ControlHz), Ticked(1234));
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 3), $"no control channel: {rx.Trunk?.State}");
        Assert.Equal(0x293, rx.Trunk!.Nac);
        Assert.Equal(0x123, rx.Trunk.SysId);
        Assert.True(rx.Trunk.MatchesSite);
        Assert.True(Wait(() => rx.TrunkControlUsingIq, 3), "the control channel is not read through the differential front end");
        Assert.True(Wait(() => rx.Trunk?.Active?.Talkgroup == 1234, 3), "the call was not followed");
        Assert.Equal(InsideHz, rx.FrequencyHz);
        Assert.True(Wait(() => rx.Trunk?.Active?.Flags.HasFlag(TrunkCallFlags.Voice) == true, 4), "no speech decoded");
        long messages = rx.Trunk!.Messages;
        Assert.True(Wait(() => rx.Trunk?.Messages > messages + 10, 2), "the control channel was not read during the call");
        double rms = SpeechRms(rx, 1);
        Assert.True(rms > 0.02, $"no speech on the audio output: {rms:F4}");
        Assert.Equal(0, rx.DroppedBlocks);
    }

    [Fact]
    public void ACallOutsideTheSpanIsFollowedWithOneHopThereAndOneBack()
    {
        var (source, _, rx) = Radio();
        using var _rx = rx;
        var voice = source.Add(OutsideHz, 0.05, deviation: Disc("p25_call_24k.raw"));
        rx.StartTrunk(Site(ControlHz), Ticked(1235));
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 3), $"no control channel: {rx.Trunk?.State}");
        int tunes = source.Tunes;
        Assert.True(Wait(() => rx.Trunk?.Active?.Talkgroup == 1235, 3), "the call was not followed");
        Assert.Equal(tunes + 1, source.Tunes);
        Assert.Equal(OutsideHz, rx.FrequencyHz);
        Assert.True(Wait(() => rx.Trunk?.Active?.Flags.HasFlag(TrunkCallFlags.Voice) == true, 4), "no speech decoded");
        double rms = SpeechRms(rx, 1);
        Assert.True(rms > 0.02, $"no speech on the audio output: {rms:F4}");
        voice.On = false;
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 3), $"not back on the control channel: {rx.Trunk?.State}");
        var call = Logged(rx, 1235);
        Assert.Equal(TrunkOutcome.Heard, call?.Outcome);
        Assert.True(call!.Flags.HasFlag(TrunkCallFlags.Hop));
        Assert.Equal(tunes + 2, source.Tunes);
        Assert.Equal(1, rx.Trunk!.Hops);
        // The control channel is heard again, and the grant it still repeats is not followed a second time.
        long messages = rx.Trunk.Messages;
        Assert.True(Wait(() => rx.Trunk?.Messages > messages + 10, 2), "the control channel is not heard again");
        Thread.Sleep(500);
        Assert.Null(rx.Trunk!.Active);
        Assert.Equal(tunes + 2, source.Tunes);
        Assert.Equal(0, rx.DroppedBlocks);
    }

    /// <summary>
    /// Calls one after another on the same voice channel, as a busy site gives them: each is followed with nothing made anew,
    /// the voice channel's decoders other than P25 (DMR, NXDN, D-STAR, System Fusion) are never fed, and the DSP thread keeps
    /// up: no block dropped, none taking near the half second the queue holds. (Each call is left by Skip, and the control
    /// channel then falls silent a little over the engine's two seconds of repeats, so that its next grant is a new call.)
    /// </summary>
    [Fact]
    public void CallAfterCallTheReceiverKeepsUp()
    {
        var (source, control, rx) = Radio();
        using var _rx = rx;
        source.Add(InsideHz, 0.05, deviation: Disc("p25_call_24k.raw"));
        rx.StartTrunk(Site(ControlHz), Ticked(1234));
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 3), $"no control channel: {rx.Trunk?.State}");
        long others = rx.OtherDecodersFed;
        rx.TakeDspTiming();
        double slowest = 0;
        for (int call = 1; call <= 3; call++)
        {
            Assert.True(Wait(() => rx.Trunk?.Active?.Talkgroup == 1234, 3), $"call {call} was not followed: {rx.Trunk?.State}");
            Assert.True(Wait(() => rx.Trunk?.Active?.Flags.HasFlag(TrunkCallFlags.Voice) == true, 4), $"no speech in call {call}");
            if (call == 3) break;
            rx.SkipChannel();
            Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 2), $"call {call} was not left: {rx.Trunk?.State}");
            control.On = false;
            Thread.Sleep(2300);
            control.On = true;
            slowest = Math.Max(slowest, rx.TakeDspTiming().SlowestMs);
        }
        slowest = Math.Max(slowest, rx.TakeDspTiming().SlowestMs);
        Assert.Equal(2, rx.Trunk!.Calls.Count(c => c.Talkgroup == 1234 && c.Outcome == TrunkOutcome.Heard));
        Assert.Equal(others, rx.OtherDecodersFed);
        Assert.Equal(0, rx.DroppedBlocks);
        Assert.True(slowest < 100, $"a block took {slowest:F1} ms");
        // Out of trunk mode the channel is looked at for every kind of digital voice again.
        rx.StopTrunk();
        Assert.True(Wait(() => rx.OtherDecodersFed > others, 2), "the other decoders are not fed after trunk mode");
    }

    [Fact]
    public void GrantsNotWantedAreLoggedAndNotFollowed()
    {
        var (source, _, rx) = Radio();
        using var _rx = rx;
        source.Add(InsideHz, 0.05, deviation: Disc("p25_call_24k.raw"));
        // 5005 is ticked, but its grant says encrypted.
        rx.StartTrunk(Site(ControlHz), Ticked(5005));
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 3), $"no control channel: {rx.Trunk?.State}");
        int tunes = source.Tunes;
        Assert.True(Wait(() => rx.Trunk?.Calls.Length >= 5, 3), "not every grant was logged");
        var calls = rx.Trunk!.Calls;
        Assert.Equal(TrunkOutcome.Encrypted, calls.Single(c => c.Talkgroup == 5005).Outcome);
        Assert.Equal(TrunkOutcome.NotTicked, calls.Single(c => c.Talkgroup == 1234).Outcome);
        Assert.Equal(TrunkOutcome.NotTicked, calls.Single(c => c.Talkgroup == 1235).Outcome);
        Assert.Equal(TrunkOutcome.NotTicked, calls.Single(c => c.Talkgroup == 6006).Outcome);
        var tdma = calls.Single(c => c.Talkgroup == 4004);
        Assert.Equal(TrunkOutcome.NotTicked, tdma.Outcome);
        Assert.Equal((2, 1, InsideHz), (tdma.Phase, tdma.Slot, tdma.Hz));
        Thread.Sleep(500);
        Assert.Null(rx.Trunk!.Active);
        Assert.Equal(TrunkState.Control, rx.Trunk.State);
        Assert.Equal(ControlHz, rx.FrequencyHz);
        Assert.Equal(tunes, source.Tunes);
        Assert.Equal(0, rx.DroppedBlocks);
    }

    // ---- Phase 2 ----

    // The Phase 2 control channel (Data/p25_trunk_tdma_24k.raw): talkgroup 1001 granted slot 0 and 1002 slot 1 of 851.5125 MHz.
    private static readonly int[] TdmaTalkgroups = [1001, 1002];
    private static TrunkFilter TdmaTicked(params int[] tgs) => new(tgs, [.. TdmaTalkgroups.Except(tgs)], ListenUnknown: false);

    // A p25p2gen channel as FakeCarrier plays it, with two seconds of nothing after it: the call it carries ends once, by its
    // END-PUSH-TO-TALK, and does not start again before the trunk engine has gone back to the control channel.
    private static float[] Phase2(string name) => [.. Baseband(name), .. new float[2 * 48000 * 2]];

    private static List<string> SentFrames(string file, int slot) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", file)).Where(l => l[0] - '0' == slot)
            .Select(l => Convert.ToHexString(l[2..51].Select(ch => (byte)(ch - '0')).ToArray())).ToList();

    private static bool AmbeAvailable
    {
        get
        {
            using var v = AmbeVocoder.TryCreate();
            return v is not null;
        }
    }

    // Trunk mode on the Phase 2 site with these talkgroups ticked: once the call is followed, the Phase 2 channel is put on
    // its frequency (from its start), and the frames the decoder gives the vocoder are kept.
    private static (FakeSource Source, Receiver Rx, List<string> Played) FollowPhase2(string channel, params int[] ticked)
    {
        var source = new FakeSource();
        source.Add(ControlHz, 0.05, deviation: Disc("p25_trunk_tdma_24k.raw"));
        var rx = new Receiver(source, playAudio: false, sampleRate: 2.4e6);
        rx.Start();
        rx.StartTrunk(new TrunkSiteDef(1, 2, "Test", "Site 1", 0x293, 0x123, 0xBEE00, 1, 1, [ControlHz], [InsideHz]), TdmaTicked(ticked));
        Assert.True(Wait(() => rx.Trunk?.Active is not null, 4), $"no call followed: {rx.Trunk?.State}");
        var played = new List<string>();
        var decoder = rx.Phase2Decoder;
        Assert.NotNull(decoder);
        decoder.FrameTap = item =>
        {
            if (item[49] != 255)
                lock (played) played.Add(Convert.ToHexString(item, 0, 49));
        };
        source.Add(InsideHz, 0.05, baseband: Phase2(channel));
        return (source, rx, played);
    }

    [Theory]
    [InlineData(0, 1001, 2001001)]
    [InlineData(1, 1002, 2002002)]
    public void APhase2CallIsFollowedToItsSlotAndHeard(int slot, int tg, int src)
    {
        var (_, rx, played) = FollowPhase2("p25p2_voice_48k.raw", tg);
        using var _rx = rx;
        var active = rx.Trunk!.Active!;
        Assert.Equal((tg, 2, slot, InsideHz), (active.Talkgroup, active.Phase, active.Slot, active.Hz));
        Assert.Equal(InsideHz, rx.FrequencyHz);
        if (AmbeAvailable)
        {
            Assert.True(Wait(() => rx.Trunk?.Active?.Flags.HasFlag(TrunkCallFlags.Voice) == true, 4), "no speech decoded");
            Assert.Contains($"P25 Phase 2 · NAC 293 · TG {tg} · ID {src}", rx.Digital?.ToString());
            double rms = SpeechRms(rx, 0.5);
            Assert.True(rms > 0.02, $"no speech on the audio output: {rms:F4}");
        }
        // Its END-PUSH-TO-TALK, the hang time, and back on the control channel
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control && Logged(rx, tg) is not null, 6), $"not back on the control channel: {rx.Trunk?.State}");
        var call = Logged(rx, tg)!;
        Assert.Equal((AmbeAvailable ? TrunkOutcome.Heard : TrunkOutcome.Lost, 2, slot), (call.Outcome, call.Phase, call.Slot));
        // The source comes from the slot's MAC (1002's grant names none)
        Assert.Equal(src, call.Source);
        Assert.Equal(ControlHz, rx.FrequencyHz);
        // This slot's speech, in order, and nothing of the other slot's
        var want = SentFrames("p25p2_voice_frames.txt", slot);
        var other = SentFrames("p25p2_voice_frames.txt", slot ^ 1).ToHashSet();
        lock (played)
        {
            Assert.True(played.Count >= want.Count - 4, $"{played.Count} frames played");
            Assert.DoesNotContain(played, other.Contains);
            int at = want.IndexOf(played[0]);
            Assert.True(at >= 0);
            Assert.Equal(want.Skip(at).Take(played.Count), played);
        }
        // The other slot's grant was logged, not followed
        Assert.Equal(TrunkOutcome.NotTicked, Logged(rx, tg == 1001 ? 1002 : 1001)?.Outcome);
        Assert.Equal(0, rx.DroppedBlocks);
    }

    [Fact]
    public void AnEncryptedPhase2CallIsSilentAndLogged()
    {
        // Slot 1 of Data/p25p2_late_enc_48k.raw: its PUSH-TO-TALK says algorithm 0x84. The grant said nothing of it.
        var (_, rx, played) = FollowPhase2("p25p2_late_enc_48k.raw", 1002);
        using var _rx = rx;
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control && Logged(rx, 1002) is not null, 4), $"not back: {rx.Trunk?.State}");
        var call = Logged(rx, 1002)!;
        Assert.Equal((TrunkOutcome.Encrypted, 2, 1), (call.Outcome, call.Phase, call.Slot));
        Assert.True(call.Flags.HasFlag(TrunkCallFlags.Encrypted));
        Assert.False(call.Flags.HasFlag(TrunkCallFlags.Voice));
        lock (played) Assert.Empty(played);
    }

    [Fact]
    public void AStalePhase2GrantIsLeftWhenTheSlotCarriesAnotherTalkgroup()
    {
        // Data/p25p2_unscrambled_48k.raw on the granted frequency: its slot 0 carries talkgroup 4001, not 1001.
        var (_, rx, played) = FollowPhase2("p25p2_unscrambled_48k.raw", 1001);
        using var _rx = rx;
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control && Logged(rx, 1001) is not null, 4), $"not back: {rx.Trunk?.State}");
        Assert.Equal(TrunkOutcome.Lost, Logged(rx, 1001)!.Outcome);
        lock (played) Assert.Empty(played);
    }

    [Fact]
    public void ADeadControlFrequencyIsPassedForTheNext()
    {
        var (_, _, rx) = Radio();
        using var _rx = rx;
        rx.StartTrunk(Site(851_037_500, ControlHz), Ticked());
        Assert.Equal(TrunkState.Hunt, rx.Trunk?.State);
        Assert.Equal(851_037_500, rx.Trunk!.ControlHz);
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 4), $"the second control frequency was not found: {rx.Trunk?.State}");
        Assert.Equal(ControlHz, rx.Trunk!.ControlHz);
        Assert.Equal(0, rx.DroppedBlocks);
    }

    [Fact]
    public void AControlChannelLostIsHuntedForAndFoundAgain()
    {
        var (_, control, rx) = Radio();
        using var _rx = rx;
        rx.StartTrunk(Site(ControlHz), Ticked());
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 3));
        Assert.True(Wait(() => rx.Trunk?.BlocksPerSecond > 10, 2), $"messages a second: {rx.Trunk?.BlocksPerSecond}");
        control.On = false;
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Lost, 5), $"not lost: {rx.Trunk?.State}");
        control.On = true;
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 3), $"not found again: {rx.Trunk?.State}");
        Assert.Equal(0, rx.DroppedBlocks);
    }

    [Fact]
    public void AScanATuneOrAModeEndsTrunkModeAndTrunkModeEndsAScan()
    {
        var (_, _, rx) = Radio();
        using var _rx = rx;
        rx.MultiMonitor = true;
        ScanChannel[] list = [new("one", 120.0e6, Mode.Am, BandPlan.AmAir), new("two", 125.0e6, Mode.Am, BandPlan.AmAir)];
        rx.StartScan(list);
        Assert.True(rx.Scanning);
        rx.StartTrunk(Site(ControlHz), Ticked());
        Assert.False(rx.Scanning);
        Assert.NotNull(rx.Trunk);
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 3));
        // Multi Monitor has nothing to do with it: one channel.
        Assert.Single(rx.Slots, s => s.Holding);

        rx.StartScan(list);
        Assert.Null(rx.Trunk);
        Assert.True(rx.Scanning);

        rx.StartTrunk(Site(ControlHz), Ticked());
        rx.FrequencyHz = 162.4e6;
        Assert.Null(rx.Trunk);
        rx.StartTrunk(Site(ControlHz), Ticked());
        rx.SetMode(Mode.Am, BandPlan.AmAir);
        Assert.Null(rx.Trunk);
        rx.StartTrunk(Site(ControlHz), Ticked());
        Assert.Equal(Mode.Nfm, rx.Mode);
        rx.StopTrunk();
        Assert.Null(rx.Trunk);
        Assert.False(rx.Scanning);
        // A receiver that is not running does not start it.
        rx.Stop();
        rx.StartTrunk(Site(ControlHz), Ticked());
        Assert.Null(rx.Trunk);
    }

    [Fact]
    public void ACallIsFollowedWhenWordOfTheRetuneIsLost()
    {
        var (source, _, rx) = Radio();
        using var _rx = rx;
        var voice = source.Add(OutsideHz, 0.05, deviation: Disc("p25_call_24k.raw"));
        rx.StartTrunk(Site(ControlHz), Ticked(1235));
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control, 3));
        source.NeverSaysRetuned = true;
        Assert.True(Wait(() => rx.Trunk?.Active?.Flags.HasFlag(TrunkCallFlags.Voice) == true, 6), $"no speech: {rx.Trunk?.State}");
        voice.On = false;
        Assert.True(Wait(() => rx.Trunk?.State == TrunkState.Control && rx.Trunk.Messages > 0, 5), $"not back: {rx.Trunk?.State}");
        long messages = rx.Trunk!.Messages;
        Assert.True(Wait(() => rx.Trunk?.Messages > messages + 10, 3), "the control channel is not heard again");
        Assert.Equal(TrunkOutcome.Heard, Logged(rx, 1235)?.Outcome);
        Assert.Equal(0, rx.DroppedBlocks);
    }

    [Fact]
    public void SkipLeavesTheCall()
    {
        var (source, _, rx) = Radio();
        using var _rx = rx;
        source.Add(InsideHz, 0.05, deviation: Disc("p25_call_24k.raw"));
        rx.StartTrunk(Site(ControlHz), Ticked(1234));
        Assert.True(Wait(() => rx.Trunk?.Active?.Talkgroup == 1234, 4), "the call was not followed");
        rx.SkipChannel();
        Assert.Null(rx.Trunk!.Active);
        Assert.Equal(TrunkState.Control, rx.Trunk.State);
        Assert.True(Logged(rx, 1234)!.Flags.HasFlag(TrunkCallFlags.Skipped));
        Assert.Equal(ControlHz, rx.FrequencyHz);
        // With nothing ticked, nothing is followed.
        rx.SetTrunkFilter(Ticked());
        Thread.Sleep(300);
        Assert.Null(rx.Trunk!.Active);
    }

    // A trunked voice channel is silent but for decoded speech: never the analogue sound of the digital signal, or of noise.
    [Fact]
    public void DigitalOnlyLetsNoAnalogueSoundThrough()
    {
        using var voice = new DigitalVoice();
        var rng = new Random(5);
        var noise = new float[4800];
        for (int k = 0; k < noise.Length; k++) noise[k] = (float)(rng.NextDouble() - 0.5);
        float[] left = new float[noise.Length], right = new float[noise.Length];
        void Fill()
        {
            for (int k = 0; k < left.Length; k++) left[k] = right[k] = noise[k];
        }
        voice.Feed(noise, 2500);
        Fill();
        voice.Process(true, false, Mode.Nfm, left, right, left.Length);
        Assert.Equal(noise, left);                     // a channel of its own lets the sound through
        voice.DigitalOnly = true;
        Fill();
        voice.Process(true, false, Mode.Nfm, left, right, left.Length);
        Assert.All(left, x => Assert.Equal(0, x));
        Assert.All(right, x => Assert.Equal(0, x));
        Fill();
        voice.Process(true, false, Mode.Am, left, right, left.Length);
        Assert.All(left, x => Assert.Equal(0, x));
        Assert.False(voice.Speaking);
    }

    [Fact]
    public void TheBasebandTapGivesTheChannelAt48kHz()
    {
        const double fs = 2_400_000, offset = 300_000, tone = 1000;
        var rx = new ChannelReceiver(fs);
        rx.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        rx.SetOffset(offset);
        var re = new List<float>();
        var im = new List<float>();
        rx.BasebandTap = (r, i) =>
        {
            foreach (float x in r) re.Add(x);
            foreach (float x in i) im.Add(x);
        };
        int n = 240_000;            // 0.1 s
        float[] inRe = new float[n], inIm = new float[n];
        for (int k = 0; k < n; k++)
        {
            double ph = 2 * Math.PI * (offset + tone) * k / fs;
            inRe[k] = (float)(0.1 * Math.Cos(ph));
            inIm[k] = (float)(0.1 * Math.Sin(ph));
        }
        var l = new float[rx.MaxAudio(n)];
        var r2 = new float[rx.MaxAudio(n)];
        int audio = rx.Process(inRe, inIm, l, r2);
        Assert.Equal(audio, re.Count);
        Assert.InRange(re.Count, 4790, 4810);
        // A carrier 1 kHz above the channel turns 1000 times a second there.
        double turn = 0;
        for (int k = 1000; k < re.Count; k++)
            turn += Math.Atan2(im[k] * re[k - 1] - re[k] * im[k - 1], re[k] * re[k - 1] + im[k] * im[k - 1]);
        Assert.Equal(tone, turn / (2 * Math.PI) / ((re.Count - 1000) / 48000.0), tolerance: 5.0);
    }

    [Fact]
    public void TheSiteCentreHoldsTheControlChannelAndAllTheVoiceChannelsThatFit()
    {
        const double usable = 1.92e6, bw = BandPlan.NfmNarrow;
        double[] voice = [851_262_500, 851_387_500, InsideHz, OutsideHz];
        double center = ScanPlan.CenterFor(ControlHz, voice, bw, usable, out int held);
        Assert.Equal(3, held);
        Assert.True(ScanPlan.Fits(ControlHz - center, bw, usable));
        foreach (double hz in voice[..3]) Assert.True(ScanPlan.Fits(hz - center, bw, usable), $"{hz} at {center}");
        Assert.False(ScanPlan.Fits(OutsideHz - center, bw, usable));

        // With room for all, all; and none on the centre's spur.
        center = ScanPlan.CenterFor(ControlHz, voice, bw, 6.14e6, out held);
        Assert.Equal(4, held);
        foreach (double hz in voice.Append(ControlHz)) Assert.True(ScanPlan.Fits(hz - center, bw, 6.14e6));

        // A voice channel right where the centre would go is moved off it.
        center = ScanPlan.CenterFor(851_000_000, [851_500_000, 852_000_000], bw, usable, out held);
        Assert.Equal(2, held);
        Assert.True(Math.Abs(center - 851_500_000) >= bw / 2 + ScanPlan.DcGuardHz);
    }
}
