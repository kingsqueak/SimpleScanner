// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleScanner.Digital;
using SimpleScanner.Dsp;
using SimpleScanner.Trunk;

namespace SimpleScanner;

/// <summary>
/// Trunk mode: the receiver follows one trunked P25 site. Its control channel is received on a channel of its own
/// (<see cref="TrunkChannel"/>) and read by the trunk engine (<see cref="TrunkController"/>); the calls it follows are received
/// in the first slot, so the sound, the meter, the display and Skip work as on any channel. The voice channel plays decoded
/// speech only (<see cref="DigitalVoice.DigitalOnly"/>), never the rasp of the digital signal.
///
/// When the control channel is found the radio is centred to hold it and as many of the site's voice frequencies as fit
/// (<see cref="ScanPlan.CenterFor"/>): a call on one of those needs no retune, and the control channel is read on through it.
/// A call outside the span is reached by retuning to it (a hop), and the radio comes back after the call; the control
/// channel is not heard meanwhile. A Phase 2 (TDMA) call is received on its slot with the system's scrambling, from what the
/// control channel said of the site (<see cref="DigitalVoice.Expect"/>).
///
/// Everything here runs on the DSP thread under the receiver's lock, or under that lock from the caller's thread.
/// <see cref="StartScan"/>, tuning by hand and a new mode end trunk mode; starting it ends a scan.
/// </summary>
public sealed partial class Receiver
{
    private TrunkController? _trunk;
    private TrunkChannel? _trunkChannel;
    private ComplexSamplesHandler? _trunkTap;
    private double _trunkClock, _trunkLevel = -140;
    // The first slot holds the call's voice channel; the radio has been retuned away from the site to reach it.
    private bool _trunkVoice, _trunkAway;
    private int _trunkHops;
    // The site's centre for a control frequency, worked out once (for that frequency and span).
    private double _trunkCenterFor = double.NaN, _trunkCenterSpan, _trunkCenter;

    /// <summary>The trunk engine as it stands, or null outside trunk mode.</summary>
    public TrunkStatus? Trunk
    {
        get
        {
            lock (_cfg) return TrunkStatusLocked();
        }
    }

    /// <summary>The filter trunk mode follows now (null outside it). For tests.</summary>
    internal TrunkFilter? TrunkFilterNow
    {
        get
        {
            lock (_cfg) return _trunk?.Filter;
        }
    }

    /// <summary>The control channel is being read through P25's differential (baseband) front end. For tests.</summary>
    internal bool TrunkControlUsingIq
    {
        get
        {
            lock (_cfg) return _trunkChannel?.Decoder is { UsingIq: true };
        }
    }

    /// <summary>The control channel's complex 48 kHz samples as they are received, on the DSP thread (for recording it).</summary>
    public ComplexSamplesHandler? TrunkControlTap
    {
        get => _trunkTap;
        set
        {
            lock (_cfg)
            {
                _trunkTap = value;
                if (_trunkChannel is not null) _trunkChannel.RecordTap = value;
            }
        }
    }

    /// <summary>
    /// Follow this site: find its control channel and follow the calls the filter wants. The radio must be running, and the
    /// digital voice library there. Ends a scan, and trunk mode on another site.
    /// </summary>
    public void StartTrunk(TrunkSiteDef site, TrunkFilter filter)
    {
        // The control channel's receiver and decoder are made the first time, outside the lock.
        var made = _trunkChannel is null && !_disposed ? new TrunkChannel(_rate) : null;
        lock (_cfg)
        {
            if (made is not null && _trunkChannel is null && !_disposed)
            {
                _trunkChannel = made;
                _trunkChannel.RecordTap = _trunkTap;
                made = null;
            }
            EndScanLocked();
            EndTrunkLocked();
            if (IsRunning && !_disposed && _trunkChannel is { Decoder: not null } && site.ControlHz.Count > 0)
            {
                var slot = _slots[0];
                ConfigureLocked(slot, Mode.Nfm, BandPlan.NfmNarrow);
                slot.DigitalVoice.DigitalOnly = true;
                slot.DigitalVoice.P25Only = true;
                _trunk = new TrunkController(site, filter, DateTime.UtcNow);
                _trunkClock = 0;
                _trunkHops = 0;
                _trunkLevel = -140;
                _trunkVoice = _trunkAway = false;
                _trunkCenterFor = double.NaN;
                ApplyTrunkLocked(_trunk.Start(0));
            }
        }
        made?.Dispose();
    }

    /// <summary>Leave trunk mode. The receiver stays where it is: on the control channel, or on the call it was following.</summary>
    public void StopTrunk()
    {
        lock (_cfg) EndTrunkLocked();
    }

    /// <summary>Which calls trunk mode follows; may be changed at any time.</summary>
    public void SetTrunkFilter(TrunkFilter filter)
    {
        lock (_cfg)
            if (_trunk is not null) ApplyTrunkLocked(_trunk.SetFilter(filter));
    }

    private void EndTrunkLocked()
    {
        if (_trunk is null) return;
        _trunk.Stop();
        _trunk = null;
        _trunkVoice = _trunkAway = false;
        var slot = _slots[0];
        slot.DigitalVoice.DigitalOnly = false;
        slot.DigitalVoice.P25Only = false;
        slot.DigitalVoice.Expect(null);
        slot.State = SlotState.Free;
        slot.ResetSquelch();
        slot.ResetDigital();
        TakeCentreLocked(slot);
    }

    // Skip in trunk mode: leave the call being followed.
    private void TrunkSkipLocked()
    {
        if (_trunk is not null) ApplyTrunkLocked(_trunk.Skip());
    }

    // What the display shows: in trunk mode between calls, what the control channel says of itself.
    private IDigitalStatus? ShownDigital => _trunk is not null && !_trunkVoice ? _trunkChannel?.Status : Main.DigitalVoice.Status;

    private TrunkStatus? TrunkStatusLocked()
    {
        if (_trunk is not { } t) return null;
        var s = t.Site;
        return new TrunkStatus(t.State, t.Def.SystemId, t.Def.SiteId, t.ControlHz, _trunkLevel, s.Nac, s.SystemId, s.Wacn, s.Rfss, s.Site,
            s.MatchesSite, t.BlocksPerSecond, s.Messages, _trunkHops, t.Active, [.. t.Calls], s.Neighbours(TrunkStatus.MostNeighbours));
    }

    // Once a block in trunk mode, before the slots: the control channel (when the radio holds it), then a step of the engine.
    private void TrunkStepLocked(ReadOnlySpan<float> i, ReadOnlySpan<float> q, double seconds)
    {
        var trunk = _trunk!;
        var control = _trunkChannel!;
        _trunkClock += seconds;
        bool heard = !_trunkAway;
        IReadOnlyList<P25Tsbk> tsbks = [];
        if (heard)
        {
            control.Process(i, q);
            tsbks = control.ReadTsbks();
            double level = control.Channel.LevelDbfs - _source.GainDb + LevelOffsetDb;
            _trunkLevel = level > _trunkLevel ? level : _trunkLevel + 0.1 * (level - _trunkLevel);
            // Between calls the meter shows the control channel.
            if (!_trunkVoice) Volatile.Write(ref _level, _trunkLevel);
        }
        var voice = default(TrunkVoice);
        if (_trunkVoice)
        {
            var slot = _slots[0];
            var p25 = slot.DigitalVoice.Status as P25Status;
            voice = new TrunkVoice(p25 is { InCall: true, ControlChannel: false }, p25 is { Encrypted: true }, p25?.Source ?? 0,
                slot.DigitalVoice.Speaking,
                // Only a call on the slot names its talkgroup here: the last HANGTIME of the one before may still be on the air
                p25 is { Phase: 2, InCall: true } ? p25.Talkgroup : 0, p25 is { Phase: 2, VoiceAvailable: false },
                // Its end sent: a Phase 1 terminator; on a Phase 2 slot (heard, so its MAC said so) any end of the call
                p25 is { InCall: false, ControlChannel: false } && (p25.Phase == 2 || p25.Ended));
        }
        ApplyTrunkLocked(trunk.Step(_trunkClock, tsbks, heard, voice));
    }

    private void ApplyTrunkLocked(TrunkAction action)
    {
        switch (action)
        {
            case TrunkAction.WatchControl:
                WatchControlLocked();
                break;
            case TrunkAction.Follow:
                FollowLocked();
                break;
        }
    }

    // The control channel, to try or to watch. While hunting the radio moves only when the frequency is outside its span; on
    // the control channel (found, or back after a call) it is centred for the site.
    private void WatchControlLocked()
    {
        var trunk = _trunk!;
        var slot = _slots[0];
        double hz = trunk.ControlHz, bw = slot.Channel.BandwidthHz, usable = _source.UsableBandwidthHz, center = _source.CenterHz;
        if (_trunkVoice)
        {
            _trunkVoice = false;
            slot.SquelchOpen = false;
            slot.ResetDigital();
            slot.DigitalVoice.Expect(null);
        }
        bool hunting = trunk.State is TrunkState.Hunt or TrunkState.Lost;
        if (_trunkAway || !ScanPlan.Fits(hz - center, bw, usable) || !hunting)
        {
            if (hz != _trunkCenterFor || usable != _trunkCenterSpan)
            {
                _trunkCenter = ScanPlan.CenterFor(hz, trunk.Def.VoiceHz, bw, usable, out _);
                _trunkCenterFor = hz;
                _trunkCenterSpan = usable;
            }
            if (Math.Abs(_trunkCenter - center) > 1)
            {
                _flushUntilRetuned = true;
                _source.Tune(_trunkCenter);
                center = _trunkCenter;
            }
        }
        // Back from a hop: what the decoder had of the control channel is from before it.
        if (_trunkAway) _trunkChannel!.Decoder?.SignalLost();
        _trunkAway = false;
        _trunkChannel!.Tune(hz, hz - center);
        // The first slot waits on the control frequency, unworked, so that the display shows it.
        slot.FrequencyHz = hz;
        slot.OffsetHz = hz - center;
        slot.Channel.SetOffset(slot.OffsetHz);
        slot.Blocked = BandPlan.IsBlocked(hz, bw);
    }

    // The call the engine follows now, in the first slot: in the span, only its filter moves; outside it, the radio hops there.
    private void FollowLocked()
    {
        var trunk = _trunk!;
        if (trunk.Active is not { } call) return;
        var slot = _slots[0];
        double hz = call.Hz, bw = slot.Channel.BandwidthHz, offset = hz - _source.CenterHz;
        if (!ScanPlan.Fits(offset, bw, _source.UsableBandwidthHz))
        {
            offset = ScanPlan.SingleOffsetFor(hz);
            _flushUntilRetuned = true;
            _source.Tune(hz - offset);
            _trunkAway = true;
            _trunkHops++;
            trunk.MarkHop();
        }
        slot.FrequencyHz = hz;
        slot.OffsetHz = offset;
        slot.Channel.SetOffset(offset);
        slot.Blocked = BandPlan.IsBlocked(hz, bw);
        slot.ResetSquelch();
        slot.ResetDigital();
        slot.DigitalVoice.Expect(call.Phase == 2 ? Phase2Params(trunk, call) : null);
        _trunkVoice = true;
    }

    // A Phase 2 call's slot and talkgroup, and the system's identity for its scrambling: as the control channel said it, else
    // as the site was given.
    private static P25Phase2Params Phase2Params(TrunkController trunk, TrunkCall call)
    {
        var site = trunk.Site;
        return new P25Phase2Params(site.Wacn ?? trunk.Def.Wacn ?? 0, site.SystemId ?? trunk.Def.SysId ?? 0, site.Nac ?? trunk.Def.Nac ?? 0,
            call.Slot, call.Flags.HasFlag(TrunkCallFlags.Unit) ? 0 : call.Talkgroup);
    }

    /// <summary>
    /// For the bench: receive a known P25 Phase 2 voice channel on the frequency tuned (outside trunk mode), only its decoded
    /// speech heard; null goes back to the usual decoders. <paramref name="trace"/> gets the Phase 2 decoder's lines.
    /// </summary>
    internal void ListenPhase2(P25Phase2Params? p, Action<string>? trace = null)
    {
        lock (_cfg)
        {
            var voice = _slots[0].DigitalVoice;
            voice.Phase2Trace = trace;
            voice.DigitalOnly = p is not null;
            _slots[0].ResetDigital();
            voice.Expect(p);
        }
    }

    /// <summary>Blocks the first slot has fed to its decoders other than P25 (DMR, NXDN, D-STAR, System Fusion). For tests.</summary>
    internal long OtherDecodersFed
    {
        get
        {
            lock (_cfg) return _slots[0].DigitalVoice.OthersFed;
        }
    }

    /// <summary>The first slot's Phase 2 decoder, once made. For tests and the bench.</summary>
    internal P25Phase2Decoder? Phase2Decoder
    {
        get
        {
            lock (_cfg) return _slots[0].DigitalVoice.Phase2;
        }
    }
}
