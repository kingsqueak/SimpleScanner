// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Globalization;
using SimpleScanner.Digital;
using SimpleScanner.Trunk;

namespace SimpleScanner.Tests;

/// <summary>
/// The trunk engine alone, on a pretend clock: each case is a run of steps, each step what happens and the state (and
/// the action, with the control frequency for a watch) the engine must be in after it, then the calls it logged.
/// </summary>
public class TrunkControllerTests
{
    // The site: system 0x123, NAC 0x293, RFSS 1, site 1; two control frequencies. Identifier 1: 851.00625 MHz + n x 6.25 kHz.
    private static readonly TrunkSiteDef Def = new(7, 8, "Sys", "Site", 0x293, 0x123, 0xBEE00, 1, 1, [851_012_500, 851_537_500],
        [851_512_500, 853_012_500]);
    // Ticked 100, 200 and 300; 400 is in the database and not ticked; anything else is not in it, and not followed.
    private static readonly TrunkFilter Filter = new([100, 200, 300], [400], ListenUnknown: false);
    private const double Step = 0.02;

    private static P25Tsbk Rfss(int site) => new()
    {
        CrcOk = true, Kind = P25TsbkKind.Rfss, Opcode = 0x3A, Nac = 0x293, SystemId = 0x123, Rfss = 1, Site = site, Channel = 0x1001,
    };
    private static P25Tsbk Iden => new()
    {
        CrcOk = true, Kind = P25TsbkKind.Identifier, Opcode = 0x3D, Nac = 0x293, IdenId = 1, IdenForm = 0x3D, BaseHz = 851_006_250,
        SpacingHz = 6250, BandwidthHz = 12500, Slots = 1,
    };
    private static P25Tsbk IdenTdma => new()
    {
        CrcOk = true, Kind = P25TsbkKind.Identifier, Opcode = 0x33, Nac = 0x293, IdenId = 3, IdenForm = 0x33, BaseHz = 851_006_250,
        SpacingHz = 6250, Slots = 2, Tdma = true, ChannelType = 3,
    };
    private static P25Tsbk Grant(int ch, int tg, int svc = 0, bool unit = false) => new()
    {
        CrcOk = true, Kind = P25TsbkKind.Grant, Opcode = unit ? 0x04 : 0x00, Nac = 0x293, Channel = ch, Talkgroup = unit ? 0 : tg,
        Target = unit ? tg : 0, UnitToUnit = unit, Source = 1000 + tg, ServiceOptions = svc, HasServiceOptions = true,
    };

    private sealed class Rig
    {
        public readonly TrunkController C = new(Def, Filter, new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc));
        private long _ticks;
        // The grant last sent: what "fadeupd" repeats.
        private P25Tsbk? _granted;
        public double Now => _ticks * Step;

        public TrunkAction Run(string verb, double arg)
        {
            var action = TrunkAction.None;
            void Note(TrunkAction a)
            {
                if (a != TrunkAction.None) action = a;
            }
            TrunkAction One(P25Tsbk[]? tsbks = null, bool heard = true, TrunkVoice voice = default)
            {
                _ticks++;
                return C.Step(Now, tsbks ?? [], heard, voice);
            }
            void For(double seconds, bool heard, TrunkVoice voice)
            {
                for (int k = 0; k < (int)Math.Round(seconds / Step); k++) Note(One(null, heard, voice));
            }
            P25Tsbk Sent(P25Tsbk t) => (_granted = t);
            int tg = (int)arg, ch = 0x1000 | (tg & 0xFF);
            var talking = new TrunkVoice(true, false, 0, true);
            // A grant that comes during a call comes while it is being talked on.
            var during = C.State == TrunkState.Call ? talking : default;
            switch (verb)
            {
                case "start": Note(C.Start(Now)); break;
                case "status": Note(One([Rfss(1), Iden, IdenTdma])); break;
                case "other": Note(One([Rfss(9)])); break;
                case "bad": Note(One([Rfss(1) with { CrcOk = false }])); break;
                case "quiet": For(arg, true, default); break;
                case "voice": For(arg, true, talking); break;
                case "away": For(arg, false, talking); break;
                case "awayquiet": For(arg, false, default); break;
                case "envoice": Note(One(null, true, new TrunkVoice(true, true, 0, false))); break;
                case "end": Note(One()); break;
                case "grant": Note(One([Sent(Grant(ch, tg))], voice: during)); break;
                case "regrant": Note(One([Sent(Grant(ch + 1, tg))], voice: during)); break;
                // Granted on another channel while its voice is not heard here (between transmissions, or in a fade)
                case "regrantquiet": Note(One([Sent(Grant(ch + 1, tg))])); break;
                // The voice gone without its end, while the control channel repeats the last grant every 0.2 s (an update)
                case "fadeupd":
                    for (int k = 0; k < (int)Math.Round(arg / Step); k++) Note(One(k % 10 == 9 && _granted is { } g ? [g] : null));
                    break;
                // Voice, while the control channel repeats the last grant every 0.2 s
                case "voiceupd":
                    for (int k = 0; k < (int)Math.Round(arg / Step); k++) Note(One(k % 10 == 9 && _granted is { } g ? [g] : null, true, talking));
                    break;
                // The voice channel sends the end of the transmission
                case "over": Note(One(null, true, new TrunkVoice(false, false, 0, false, Ended: true))); break;
                case "enc": Note(One([Grant(ch, tg, svc: 0x40)], voice: during)); break;
                case "tdma": Note(One([Grant(0x3000 | (tg & 0xFF), tg)], voice: during)); break;
                // The other slot of the carrier of a "tdma 300" grant (0x302C: carrier 22, slot 0)
                case "tdma1": Note(One([Grant(0x302D, tg)], voice: during)); break;
                // A Phase 2 slot naming another talkgroup than the grant's
                case "p2other": Note(One(null, true, new TrunkVoice(true, false, 0, false, Talkgroup: 999))); break;
                // A Phase 2 call shown, not heard: no speech decoder
                case "nocodec": For(arg, true, new TrunkVoice(true, false, 0, false, Talkgroup: 300, NoVoiceCodec: true)); break;
                case "unit": Note(One([Grant(ch, tg, unit: true)], voice: during)); break;
                case "skip": Note(C.Skip()); break;
                case "untick": Note(C.SetFilter(Filter with { Listen = [.. Filter.Listen.Where(t => t != tg)] })); break;
                case "hop": C.MarkHop(); break;
                default: throw new ArgumentException(verb);
            }
            return action;
        }
    }

    private static readonly HashSet<string> TakesArg = ["quiet", "voice", "away", "awayquiet", "grant", "regrant", "regrantquiet", "fadeupd", "voiceupd", "enc", "tdma", "tdma1", "unit", "untick", "nocodec"];

    // "verb [arg] State [Watch [hz in MHz] | Follow | Stay]", steps divided by "|" (an action left out is not checked; Stay is
    // none); then the calls as "talkgroup:Outcome[+Flag...]", oldest first.
    [Theory]
    [InlineData("start Hunt Watch 851.0125 | quiet 0.7 Hunt Stay | quiet 0.2 Hunt Watch 851.5375 | quiet 0.8 Hunt Watch 851.0125", "")]
    [InlineData("start Hunt Watch 851.0125 | quiet 0.3 Hunt Stay | status Control Watch 851.0125 | quiet 1 Control Stay", "")]
    [InlineData("start Hunt Watch 851.0125 | bad Hunt Stay | quiet 0.8 Hunt Watch 851.5375", "")]
    [InlineData("start Hunt | other Hunt Watch 851.5375 | status Control Watch 851.5375", "")]
    [InlineData("start Hunt | status Control | quiet 2.9 Control Stay | quiet 0.2 Lost Watch 851.5375 | quiet 0.8 Lost Watch 851.0125 | status Control Watch 851.0125", "")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voice 0.5 Call | end Hang | quiet 0.9 Hang Stay | quiet 0.2 Control Watch 851.0125", "100:Heard")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | quiet 0.9 Call Stay | quiet 0.2 Hang | quiet 0.7 Hang Stay | quiet 0.4 Control Watch", "100:Lost")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voice 0.5 Call | end Hang | voice 0.5 Call | end Hang | quiet 1.1 Control Watch", "100:Heard")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voice 0.3 Call | skip Control Watch | quiet 0.5 Control", "100:Heard+Skipped")]
    [InlineData("start Hunt | status Control | grant 400 Control Stay | grant 500 Control | enc 200 Control | tdma 400 Control | unit 100 Control",
        "400:NotTicked 500:NotTicked 200:Encrypted+Encrypted 400:NotTicked 100:NotTicked+Unit")]
    // Phase 2: followed like Phase 1; the other slot of the same carrier is another call; a slot naming another talkgroup ends
    // the call (on Phase 1 that is not looked at); with no speech decoder the call is shown and logged so.
    [InlineData("start Hunt | status Control | tdma 300 Call Follow | voice 0.5 Call | end Hang | quiet 1.1 Control Watch", "300:Heard")]
    [InlineData("start Hunt | status Control | tdma 300 Call Follow | voice 0.2 Call | tdma1 200 Call Stay | voice 0.2 Call | end Hang | quiet 1.1 Control Watch",
        "200:Busy 300:Heard")]
    [InlineData("start Hunt | status Control | tdma 300 Call Follow | voice 0.3 Call | p2other Control Watch", "300:Heard")]
    [InlineData("start Hunt | status Control | tdma 300 Call Follow | p2other Control Watch", "300:Lost")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voice 0.3 Call | p2other Call Stay | voice 0.2 Call", "")]
    [InlineData("start Hunt | status Control | tdma 300 Call Follow | nocodec 0.5 Call | end Hang | quiet 1.1 Control Watch", "300:Phase2")]
    [InlineData("start Hunt | status Control | tdma 300 Call Follow | envoice Control Watch", "300:Encrypted+Encrypted")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voice 0.2 Call | grant 200 Call Stay | regrant 100 Call Stay | voice 0.2 Call | end Hang | quiet 1.1 Control Watch",
        "200:Busy 100:Heard")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voice 0.2 Call | end Hang | grant 200 Call Follow | voice 0.2 Call", "100:Heard")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | envoice Control Watch", "100:Encrypted+Encrypted")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | hop Call | away 2 Call Stay | awayquiet 0.1 Hang | awayquiet 1 Control Watch | quiet 2.9 Control Stay | quiet 0.2 Lost Watch",
        "100:Heard+Hop")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | hop Call | away 3 Call | skip Control Watch | grant 100 Control Stay | quiet 1 Control Stay | grant 100 Control Stay",
        "100:Heard+Hop+Skipped")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voice 0.2 Call | untick 100 Control Watch | grant 300 Call Follow", "100:Heard+Skipped")]
    // A conversation is one call. Voice gone without its end while the control channel repeats the grant: a fade, waited out up
    // to a second with no change of state (and nothing to re-follow); past that the hang time. Its end sent: the hang time at once,
    // repeats or not.
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voiceupd 0.5 Call | fadeupd 0.6 Call Stay | voice 0.3 Call Stay | over Hang | quiet 1.1 Control Watch",
        "100:Heard")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voiceupd 0.5 Call | fadeupd 0.9 Call Stay | fadeupd 0.2 Hang Stay | voice 0.2 Call Stay", "")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voiceupd 0.3 Call | fadeupd 0.2 Call | voice 0.2 Call | over Hang | quiet 1.1 Control", "100:Heard")]
    // Never heard at all: repeats or not, the voice is waited for a second only.
    [InlineData("start Hunt | status Control | grant 100 Call Follow | fadeupd 0.9 Call | fadeupd 0.2 Hang | quiet 1 Control Watch", "100:Lost")]
    // Granted again on its channel in the hang time (the next transmission): the call goes on, the voice channel untouched.
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voice 2.5 Call | over Hang | grant 100 Call Stay | voice 0.5 Call Stay | over Hang | quiet 1.1 Control Watch",
        "100:Heard")]
    // Granted on another channel in the hang time, or in a fade: followed there, still one call.
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voice 0.5 Call | over Hang | regrantquiet 100 Call Follow | voice 0.5 Call Stay | over Hang | quiet 1.1 Control Watch",
        "100:Heard")]
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voiceupd 0.5 Call | fadeupd 0.3 Call | regrantquiet 100 Call Follow | voice 0.2 Call | over Hang | quiet 1.1 Control",
        "100:Heard")]
    // Moved, and nothing comes on the new channel: the call is lost after a second, and logged once.
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voice 0.5 Call | over Hang | regrantquiet 100 Call Follow | quiet 0.9 Call | quiet 0.2 Hang | quiet 1 Control",
        "100:Heard")]
    // Another talkgroup granted in the hang time is still a new call.
    [InlineData("start Hunt | status Control | grant 100 Call Follow | voice 0.5 Call | over Hang | grant 200 Call Follow | voice 0.2 Call | over Hang | quiet 1.1 Control",
        "100:Heard 200:Heard")]
    // A grant is a message of the site too; it waits for its channel's identifier.
    [InlineData("start Hunt | grant 100 Control Watch | status Call Follow | voice 0.2 Call", "")]
    public void Transitions(string steps, string calls)
    {
        var rig = new Rig();
        foreach (string text in steps.Split('|', StringSplitOptions.TrimEntries))
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int at = 0;
            string verb = words[at++];
            double arg = TakesArg.Contains(verb) ? double.Parse(words[at++], CultureInfo.InvariantCulture) : 0;
            var want = Enum.Parse<TrunkState>(words[at++]);
            TrunkAction? wantAction = at < words.Length
                ? words[at++] switch
                {
                    "Watch" => TrunkAction.WatchControl, "Follow" => TrunkAction.Follow, "Stay" => TrunkAction.None,
                    var w => throw new ArgumentException(w),
                }
                : null;
            double? wantHz = at < words.Length ? double.Parse(words[at], CultureInfo.InvariantCulture) * 1e6 : null;

            var got = rig.Run(verb, arg);
            Assert.True(want == rig.C.State, $"after \"{text}\": {rig.C.State}");
            if (wantAction is not null) Assert.True(wantAction == got, $"after \"{text}\": {got}");
            if (wantHz is { } hz) Assert.Equal(hz, rig.C.ControlHz, 1);
            if (got == TrunkAction.Follow) Assert.NotNull(rig.C.Active);
        }
        string log = string.Join(" ", rig.C.Calls.Select(c =>
            $"{c.Talkgroup}:{c.Outcome}" + string.Concat(Enum.GetValues<TrunkCallFlags>()
                .Where(f => f is TrunkCallFlags.Skipped or TrunkCallFlags.Encrypted or TrunkCallFlags.Unit or TrunkCallFlags.Hop && c.Flags.HasFlag(f))
                .Select(f => $"+{f}"))));
        Assert.Equal(calls, log);
        Assert.Equal(Enumerable.Range(1, rig.C.Calls.Count).Select(n => (long)n), rig.C.Calls.Select(c => c.Seq));
    }

    [Fact]
    public void AFollowedCallSaysWhereAndHowLongAndIsLoggedWithItsSpeech()
    {
        var rig = new Rig();
        rig.Run("start", 0);
        rig.Run("status", 0);
        Assert.Equal(TrunkAction.Follow, rig.Run("grant", 100));
        var active = rig.C.Active!;
        Assert.Equal(100, active.Talkgroup);
        Assert.Equal(1100, active.Source);
        Assert.Equal(851_006_250 + 100 * 6250, active.Hz);
        Assert.Equal(1, active.Phase);
        Assert.Equal(TrunkOutcome.Following, active.Outcome);
        Assert.Equal(0, active.Seq);
        double started = rig.Now;
        rig.Run("voice", 1.5);
        Assert.Equal(1.5, rig.C.Active!.Seconds, 1);
        rig.Run("end", 0);
        rig.Run("quiet", 1.1);
        var call = Assert.Single(rig.C.Calls);
        Assert.Equal(TrunkOutcome.Heard, call.Outcome);
        Assert.True(call.Flags.HasFlag(TrunkCallFlags.Voice));
        Assert.Equal(1.5, call.Seconds, 1);
        Assert.Equal(new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc).AddSeconds(started), call.Start);
        Assert.Null(rig.C.Active);
    }

    [Fact]
    public void APhase2CallSaysItsPhaseAndSlot()
    {
        var rig = new Rig();
        rig.Run("start", 0);
        rig.Run("status", 0);
        Assert.Equal(TrunkAction.Follow, rig.Run("tdma1", 300));
        var active = rig.C.Active!;
        Assert.Equal((300, 2, 1), (active.Talkgroup, active.Phase, active.Slot));
        Assert.Equal(851_006_250 + 22 * 6250, active.Hz);
        rig.Run("voice", 0.5);
        rig.Run("end", 0);
        rig.Run("quiet", 1.1);
        var call = Assert.Single(rig.C.Calls);
        Assert.Equal((TrunkOutcome.Heard, 2, 1), (call.Outcome, call.Phase, call.Slot));
    }

    [Fact]
    public void ACallMovedToAnotherChannelIsFollowedThereAndLoggedThere()
    {
        var rig = new Rig();
        rig.Run("start", 0);
        rig.Run("status", 0);
        rig.Run("grant", 100);
        rig.Run("voice", 0.5);
        rig.Run("over", 0);
        Assert.Equal(TrunkAction.Follow, rig.Run("regrantquiet", 100));
        var active = rig.C.Active!;
        Assert.Equal(851_006_250 + 101 * 6250, active.Hz);
        Assert.Equal(TrunkState.Call, rig.C.State);
        rig.Run("voice", 0.5);
        Assert.Equal(1.0, rig.C.Active!.Seconds, 1);          // one call from the first grant
        rig.Run("over", 0);
        rig.Run("quiet", 1.1);
        var call = Assert.Single(rig.C.Calls);
        Assert.Equal((TrunkOutcome.Heard, 851_006_250 + 101 * 6250.0), (call.Outcome, call.Hz));
    }

    /// <summary>
    /// A long conversation on a weak signal, as seen on air: transmissions a second or two apart on one channel, the voice
    /// dropping out for a moment now and then, and the control channel repeating the grant all through. The state stays Call,
    /// apart from a hang time after each end; nothing is followed anew; and it is logged as one call.
    /// </summary>
    [Fact]
    public void AWeakConversationIsOneSteadyCall()
    {
        var rig = new Rig();
        rig.Run("start", 0);
        rig.Run("status", 0);
        rig.Run("grant", 100);
        var states = new List<TrunkState>();
        int follows = 0;
        for (int over = 0; over < 8; over++)
        {
            foreach (var (verb, arg) in new[] { ("voiceupd", 0.8), ("fadeupd", 0.4), ("voice", 0.6), ("fadeupd", 0.2), ("voice", 0.5), ("over", 0.0), ("fadeupd", 0.6) })
            {
                if (rig.Run(verb, arg) == TrunkAction.Follow) follows++;
                if (states.Count == 0 || states[^1] != rig.C.State) states.Add(rig.C.State);
            }
        }
        Assert.Equal(0, follows);
        // Call, then a hang time after each of the eight ends, each answered: Call Hang Call Hang ... (no flapping in the fades)
        Assert.Equal(16, states.Count);
        Assert.Empty(rig.C.Calls);
        rig.Run("quiet", 1.1);
        var call = Assert.Single(rig.C.Calls);
        Assert.Equal(TrunkOutcome.Heard, call.Outcome);
    }

    [Fact]
    public void TheLogKeepsTheLastCallsNumberedInOrder()
    {
        var rig = new Rig();
        rig.Run("start", 0);
        rig.Run("status", 0);
        // Each grant a talkgroup of its own, so that none is taken for a repeat of another.
        for (int k = 0; k < 30; k++)
        {
            rig.Run("quiet", 0.04);
            rig.Run("grant", 400 + 256 * k);
        }
        Assert.Equal(TrunkStatus.MostCalls, rig.C.Calls.Count);
        Assert.Equal(Enumerable.Range(7, TrunkStatus.MostCalls).Select(n => (long)n), rig.C.Calls.Select(c => c.Seq));
    }

    [Fact]
    public void ControlMessagesAreCountedASecond()
    {
        var rig = new Rig();
        rig.Run("start", 0);
        for (int k = 0; k < 100; k++) rig.Run("status", 0);      // three messages each, 50 steps a second
        Assert.InRange(rig.C.BlocksPerSecond, 140, 160);
        Assert.Equal(300, rig.C.Site.Messages);
    }
}
