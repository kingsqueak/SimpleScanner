// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleScanner.Digital;

namespace SimpleScanner.Trunk;

/// <summary>What the receiver is to do after a step of the trunk engine.</summary>
public enum TrunkAction
{
    None,
    /// <summary>
    /// Watch the control frequency <see cref="TrunkController.ControlHz"/> (while hunting: the one to try now). The voice channel,
    /// if one is held, is let go.
    /// </summary>
    WatchControl,
    /// <summary>Put the voice channel on the call now followed (<see cref="TrunkController.Active"/>).</summary>
    Follow,
}

/// <summary>
/// What the voice channel says, for one step: a P25 call is on it (its header or link control seen, its end not yet),
/// encrypted, the radio talking (0 until said), and decoded speech playing. For a Phase 2 call also the talkgroup its slot's
/// MAC names (0 until said), and that this copy has no speech decoder for it. <see cref="Ended"/>: the channel sent the end
/// of the transmission (a Phase 1 terminator; a Phase 2 END-PUSH-TO-TALK, IDLE or HANGTIME), as opposed to its signal being
/// lost.
/// </summary>
public readonly record struct TrunkVoice(bool InCall, bool Encrypted, int Source, bool Speaking, int Talkgroup = 0, bool NoVoiceCodec = false,
    bool Ended = false);

/// <summary>
/// The trunk engine, with no radio in it: fed the control channel's messages, what the voice channel says and the time, it
/// says where to listen. It hunts the site's control frequencies (<see cref="HuntDwellSeconds"/> on each) until one gives
/// messages of the site, then watches it for grants. A grant for a ticked talkgroup, clear, is followed, Phase 1 or Phase 2
/// (TDMA: one slot of a channel, the other slot another call); any other is logged with why it was not. The call ends when its
/// voice channel says so (a Phase 2 slot: END-PUSH-TO-TALK, IDLE or HANGTIME), when its voice has gone (or never came in
/// <see cref="VoiceLostSeconds"/>), when a Phase 2 slot names another talkgroup (the grant was stale), or on Skip; it is then
/// held <see cref="HangSeconds"/> for a reply before the control channel is watched again. A control channel with no messages
/// for <see cref="LostSeconds"/> is lost, and hunted for again. Use from one thread; the time is seconds from any origin,
/// always increasing.
///
/// A conversation is one call: each transmission of it is granted anew, on the same voice channel or another. A grant of the
/// talkgroup followed, in the call or its hang time, carries on the same call: on its channel nothing is touched; on another
/// channel (or slot) the voice channel moves there. Voice that goes without its end being sent, while the control channel
/// still repeats the grant (a fade of a weak signal), is waited for <see cref="VoiceLostSeconds"/> before the hang time
/// starts, so a brief fade neither ends the call nor shows as one.
/// </summary>
public sealed class TrunkController
{
    public const double HuntDwellSeconds = 0.8, LostSeconds = 3, VoiceLostSeconds = 1, HangSeconds = 1;

    private readonly TrunkSiteDef _def;
    private readonly DateTime _origin;
    private readonly List<TrunkCall> _calls = [];
    private TrunkFilter _filter;
    private long _seq;
    private int _huntIndex;
    private double _now, _dwellStart, _lastBlock;
    private double _rateStart;
    private int _rateCount;

    // The call followed.
    private TrunkGrant? _grant;
    private TrunkCallFlags _flags;
    private int _source;
    private double _callStart, _lastVoice, _hangStart;
    private bool _inCallSeen, _noVoiceCodec;
    // When the control channel last repeated the grant of the call followed (since it was followed); whether its voice was
    // in a call at the last step.
    private double _grantRepeat = double.NegativeInfinity;
    private bool _voiceLive;
    // The call last finished, and until when a grant of it is the same call. Back from a hop the control channel's repeats of a
    // call still going (one skipped, say) look new, because none were heard for a while: they are not followed again.
    private (int Talkgroup, double Hz, double Until) _last = (0, 0, double.NegativeInfinity);

    /// <param name="origin">The time of day the clock's zero stands for: what the calls' start times are counted from.</param>
    public TrunkController(TrunkSiteDef def, TrunkFilter filter, DateTime origin)
    {
        _def = def;
        _filter = filter;
        _origin = origin;
    }

    /// <summary>What the control channel has said.</summary>
    public TrunkSiteState Site { get; } = new();
    public TrunkSiteDef Def => _def;
    public TrunkFilter Filter => _filter;
    public TrunkState State { get; private set; }
    /// <summary>The control frequency watched, or while hunting the one being tried.</summary>
    public double ControlHz { get; private set; }
    /// <summary>Control channel messages (those whose CRC held) a second, over the last second or so.</summary>
    public double BlocksPerSecond { get; private set; }
    /// <summary>The last <see cref="TrunkStatus.MostCalls"/> finished calls, oldest first.</summary>
    public IReadOnlyList<TrunkCall> Calls => _calls;

    /// <summary>The call being followed (in a call or its hang time), as of the last step; null for none.</summary>
    public TrunkCall? Active => _grant is { } g
        ? new TrunkCall(0, g.Talkgroup, _source, g.Hz, g.Slot, g.Tdma ? 2 : 1, _flags, Time(_callStart), _now - _callStart, TrunkOutcome.Following)
        : null;

    /// <summary>Start on the site: everything heard before is forgotten, and the first control frequency is tried.</summary>
    public TrunkAction Start(double now)
    {
        _now = now;
        Site.Seed(_def);
        _grant = null;
        State = TrunkState.Hunt;
        _rateStart = now;
        _rateCount = 0;
        BlocksPerSecond = 0;
        return HuntAt(0, now);
    }

    /// <summary>Trunk mode is over. A call being followed is logged.</summary>
    public void Stop()
    {
        if (_grant is not null) FinishCall(_now, null);
        State = TrunkState.Off;
    }

    /// <summary>The ticks have changed. A call being followed whose talkgroup is no longer wanted is left.</summary>
    public TrunkAction SetFilter(TrunkFilter filter)
    {
        _filter = filter;
        if (_grant is { } g && State is TrunkState.Call or TrunkState.Hang && !filter.Wants(g.Talkgroup))
        {
            _flags |= TrunkCallFlags.Skipped;
            return EndCall(_now, null);
        }
        return TrunkAction.None;
    }

    /// <summary>Leave the call being followed, at once and without the hang time.</summary>
    public TrunkAction Skip()
    {
        if (State is not (TrunkState.Call or TrunkState.Hang)) return TrunkAction.None;
        _flags |= TrunkCallFlags.Skipped;
        return EndCall(_now, null);
    }

    /// <summary>
    /// The radio had to be retuned to reach the call's voice channel, which was outside its span: the call is marked so (and
    /// the control channel is not heard until it is over).
    /// </summary>
    public void MarkHop() => _flags |= TrunkCallFlags.Hop;

    /// <summary>
    /// One step: the control channel's messages since the last (empty when it was not heard: <paramref name="controlHeard"/>
    /// false while the radio is away on a voice channel outside its span) and what the voice channel says (meaningful in
    /// <see cref="TrunkState.Call"/> and <see cref="TrunkState.Hang"/>).
    /// </summary>
    public TrunkAction Step(double now, IReadOnlyList<P25Tsbk> tsbks, bool controlHeard, TrunkVoice voice)
    {
        _now = now;
        if (State == TrunkState.Off) return TrunkAction.None;
        int good = 0;
        for (int k = 0; k < tsbks.Count; k++)
        {
            var t = tsbks[k];
            if (!t.CrcOk) continue;
            Site.OnTsbk(t, now);
            good++;
            if (_grant is { } followed && Repeats(t, followed)) _grantRepeat = now;
        }
        _rateCount += good;
        if (now - _rateStart >= 1)
        {
            BlocksPerSecond = _rateCount / (now - _rateStart);
            _rateStart = now;
            _rateCount = 0;
        }
        if (good > 0) _lastBlock = now;

        var action = TrunkAction.None;
        switch (State)
        {
            case TrunkState.Hunt:
            case TrunkState.Lost:
                if (good > 0 && Site.MatchesSite == false)
                {
                    // Another site's control channel: forget what it said, and try the next.
                    Site.Seed(_def);
                    return HuntAt(_huntIndex + 1, now);
                }
                if (good > 0)
                {
                    // Found: the receiver now centres the radio for the site.
                    State = TrunkState.Control;
                    action = TrunkAction.WatchControl;
                    break;
                }
                return now - _dwellStart >= HuntDwellSeconds ? HuntAt(_huntIndex + 1, now) : TrunkAction.None;
            case TrunkState.Control:
                if (controlHeard && now - _lastBlock >= LostSeconds)
                {
                    State = TrunkState.Lost;
                    return HuntAt(_huntIndex + 1, now);
                }
                break;
            case TrunkState.Call:
                action = CallStep(now, voice);
                break;
            case TrunkState.Hang:
                action = HangStep(now, voice);
                break;
        }
        if (good > 0 && State is TrunkState.Control or TrunkState.Call or TrunkState.Hang)
            foreach (var g in Site.TakeGrants(now))
            {
                var a = Offer(g, now);
                if (a != TrunkAction.None) action = a;
            }
        return action;
    }

    private TrunkAction HuntAt(int index, double now)
    {
        var list = Site.ControlHz;
        _dwellStart = now;
        _lastBlock = now;
        if (list.Count == 0) return TrunkAction.None;
        _huntIndex = index % list.Count;
        ControlHz = list[_huntIndex];
        return TrunkAction.WatchControl;
    }

    private TrunkAction CallStep(double now, TrunkVoice voice)
    {
        Heard(voice);
        if (Stale(voice)) return EndCall(now, null);
        _voiceLive = voice.InCall;
        if (voice.InCall)
        {
            if (voice.Encrypted && _filter.SkipEncrypted) return EndCall(now, TrunkOutcome.Encrypted);
            _lastVoice = now;
            _inCallSeen = true;
            return TrunkAction.None;
        }
        // Gone without its end, while the control channel still says the call is on that channel: a fade, waited out.
        if (_inCallSeen && !voice.Ended && Vouched(now) && now - _lastVoice < VoiceLostSeconds) return TrunkAction.None;
        // Its end was sent, or its voice has gone (or never came): a moment for the reply.
        if (_inCallSeen || now - _lastVoice >= VoiceLostSeconds)
        {
            State = TrunkState.Hang;
            _hangStart = now;
        }
        return TrunkAction.None;
    }

    private TrunkAction HangStep(double now, TrunkVoice voice)
    {
        Heard(voice);
        if (Stale(voice)) return EndCall(now, null);
        _voiceLive = voice.InCall;
        if (voice.InCall)
        {
            // The reply, on the same channel.
            if (voice.Encrypted && _filter.SkipEncrypted) return EndCall(now, TrunkOutcome.Encrypted);
            State = TrunkState.Call;
            _lastVoice = now;
            _inCallSeen = true;
            return TrunkAction.None;
        }
        return now - _hangStart >= HangSeconds ? EndCall(now, null) : TrunkAction.None;
    }

    // The control channel has repeated the grant of the call followed within the last moment (a grant update, say): the call
    // is still on that channel.
    private bool Vouched(double now) => now - _grantRepeat <= TrunkSiteState.RepeatSeconds;

    // A message granting the call followed again, on its channel (either call of an update's two).
    private static bool Repeats(P25Tsbk t, TrunkGrant g) =>
        t.Kind == P25TsbkKind.Grant && !t.UnitToUnit && !g.Unit
        && ((t.Channel == g.Channel && t.Talkgroup == g.Talkgroup) || (t.Opcode == 0x02 && t.Channel2 == g.Channel && t.Talkgroup2 == g.Talkgroup));

    // A Phase 2 slot carrying another talkgroup than the one granted: someone else's call now.
    private bool Stale(TrunkVoice voice) => _grant is { Tdma: true } g && voice.Talkgroup != 0 && voice.Talkgroup != g.Talkgroup;

    private void Heard(TrunkVoice voice)
    {
        if (voice.Speaking) _flags |= TrunkCallFlags.Voice;
        if (voice.InCall && voice.NoVoiceCodec) _noVoiceCodec = true;
        if (voice.InCall && voice.Encrypted) _flags |= TrunkCallFlags.Encrypted;
        if (_source == 0 && voice.Source != 0) _source = voice.Source;
    }

    // A grant from the control channel: followed, or logged with why not.
    private TrunkAction Offer(TrunkGrant g, double now)
    {
        var flags = (g.Emergency ? TrunkCallFlags.Emergency : 0) | (g.Encrypted ? TrunkCallFlags.Encrypted : 0) | (g.Unit ? TrunkCallFlags.Unit : 0);
        TrunkOutcome? why = g.Unit || !_filter.Wants(g.Talkgroup) ? TrunkOutcome.NotTicked
            : g.Encrypted && _filter.SkipEncrypted ? TrunkOutcome.Encrypted
            : null;
        if (why is null && State is TrunkState.Call or TrunkState.Hang && _grant is { } followed && followed.Talkgroup == g.Talkgroup
            && followed.Unit == g.Unit)
            return Again(g, flags, now);
        if (why is null && State == TrunkState.Call) why = TrunkOutcome.Busy;      // another talkgroup's: a call missed
        if (why is null && g.Talkgroup == _last.Talkgroup && g.Hz == _last.Hz && now < _last.Until) return TrunkAction.None;
        if (why is { } outcome)
        {
            Log(new TrunkCall(0, g.Talkgroup, g.Source, g.Hz, g.Slot, g.Tdma ? 2 : 1, flags, Time(now), 0, outcome));
            return TrunkAction.None;
        }
        // In the hang time of the last call: that one is over.
        if (_grant is not null) FinishCall(now, null);
        _grant = g;
        _flags = flags;
        _source = g.Source;
        _callStart = _lastVoice = now;
        _inCallSeen = _noVoiceCodec = _voiceLive = false;
        _grantRepeat = double.NegativeInfinity;
        State = TrunkState.Call;
        return TrunkAction.Follow;
    }

    // The talkgroup followed granted again, in the call or its hang time: the same conversation, its next transmission.
    private TrunkAction Again(TrunkGrant g, TrunkCallFlags flags, double now)
    {
        bool here = g.Channel == _grant!.Channel;
        // On another channel while its voice is still heard here: as before, it stays (its end will come).
        if (!here && State == TrunkState.Call && _voiceLive) return TrunkAction.None;
        _flags |= flags;
        if (g.Source != 0) _source = g.Source;
        if (here)
        {
            // On its channel: the voice channel is left as it is (a reset would clip the start of what is said).
            _grantRepeat = now;
            if (State == TrunkState.Hang)
            {
                State = TrunkState.Call;
                _lastVoice = now;
                _inCallSeen = false;
            }
            return TrunkAction.None;
        }
        // Moved to another channel or slot: followed there, still the one call.
        _grant = g;
        _grantRepeat = double.NegativeInfinity;
        _lastVoice = now;
        _inCallSeen = _voiceLive = false;
        State = TrunkState.Call;
        return TrunkAction.Follow;
    }

    private TrunkAction EndCall(double now, TrunkOutcome? outcome)
    {
        FinishCall(now, outcome);
        State = TrunkState.Control;
        _lastBlock = now;          // the control channel may not have been heard while away: it has its full time again
        return TrunkAction.WatchControl;
    }

    private void FinishCall(double now, TrunkOutcome? outcome)
    {
        if (_grant is not { } g) return;
        double end = (_flags & TrunkCallFlags.Skipped) != 0 ? now : _lastVoice;
        Log(new TrunkCall(0, g.Talkgroup, _source, g.Hz, g.Slot, g.Tdma ? 2 : 1, _flags, Time(_callStart), Math.Max(0, end - _callStart),
            outcome ?? ((_flags & TrunkCallFlags.Voice) != 0 ? TrunkOutcome.Heard : _noVoiceCodec ? TrunkOutcome.Phase2 : TrunkOutcome.Lost)));
        _last = (g.Talkgroup, g.Hz, now + TrunkSiteState.RepeatSeconds);
        _grant = null;
    }

    private void Log(TrunkCall call)
    {
        if (_calls.Count == TrunkStatus.MostCalls) _calls.RemoveAt(0);
        _calls.Add(call with { Seq = ++_seq });
    }

    private DateTime Time(double seconds) => _origin.AddSeconds(seconds);
}
