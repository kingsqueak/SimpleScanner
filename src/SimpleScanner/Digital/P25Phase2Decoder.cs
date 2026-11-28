// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner.Digital;

/// <summary>
/// What a P25 Phase 2 voice channel is to be received with: the system's WACN, system id and NAC (its scrambling
/// sequence; from the control channel), the slot (0 or 1, the grant's), and the talkgroup granted there (0: any). Only that
/// talkgroup's speech is played: another on the slot is someone else's call.
/// </summary>
public sealed record P25Phase2Params(int Wacn, int SysId, int Nac, int Slot, int Talkgroup = 0);

/// <summary>
/// P25 Phase 2 (TDMA) voice on one slot of a channel: complex baseband (48 kHz) into the native receiver
/// (<see cref="P25Phase2Native"/>), its MAC PDUs read (<see cref="P25MacPdu"/>), and the slot's AMBE+2 frames to speech
/// through <see cref="AmbeStream"/>.
///
/// The receiver hands out every voice frame of the slot; what is played is decided here. A frame is played only while the
/// superframe timing holds and the MAC says a clear call of the right talkgroup is on the slot: from a PUSH-TO-TALK with the
/// clear algorithm, or, for a call joined late, from the group voice channel user message an ACTIVE PDU carries. It ends
/// with END-PUSH-TO-TALK, IDLE or HANGTIME, or when the timing is lost. A call that the PUSH-TO-TALK, the ESS or the service
/// options say is encrypted is shown and never played. Frames of a burst with many bit errors (as one received after the
/// signal has gone, before the timing is let go) go to the vocoder as lost ones: it repeats the last good frame for a
/// moment, then mutes, as for DMR.
///
/// One per channel; use from one thread, but <see cref="Status"/>, which may be read from any.
/// </summary>
public sealed unsafe class P25Phase2Decoder : IDisposable
{
    // A burst whose frames have more than this many bit errors corrected, on average, is taken for noise.
    private const int MostErrsPerFrame = 3;
    private const int Piece = 4800;              // samples fed at a time: the receiver's queues never fill between reads

    private readonly P25Phase2Native _rx;
    private readonly AmbeStream _speech;
    private P25Phase2Params? _params;
    private volatile P25Status _status = new() { Phase = 2 };
    private uint _voiceRead;                     // voice items read from the receiver since it started
    private double _burstsAge = 10;
    private bool _locked, _playing, _badBurst;
    // The call on the slot, as the MAC says.
    private bool _inCall, _encrypted, _emergency;
    private int _talkgroup, _source;
    private int _essAlg = P25MacPdu.Clear;       // the slot's last ESS: how its voice is sent
    // What the receiver gave at the last read; kept, so that reading it every block makes nothing.
    private readonly List<P25Phase2Native.Event> _events = [];
    private readonly List<byte[]> _items = [];

    private P25Phase2Decoder(P25Phase2Native rx, bool vocoder)
    {
        _rx = rx;
        _speech = new AmbeStream(vocoder);
    }

    /// <summary>Null when the native library is not present. <paramref name="vocoder"/> false: as if the speech decoder were missing (for tests).</summary>
    public static P25Phase2Decoder? TryCreate(bool vocoder = true) =>
        P25Phase2Native.TryCreate() is { } rx ? new P25Phase2Decoder(rx, vocoder) : null;

    /// <summary>Phase 2 bursts are arriving (within the last half second).</summary>
    public bool Active => _burstsAge < 0.5;
    /// <summary>What the slot has said; meaningful while <see cref="Active"/>.</summary>
    public P25Status Status => _status;
    /// <summary>False when the speech decoder library is missing: calls are shown, not heard.</summary>
    public bool VoiceAvailable => _speech.Available;
    public P25Phase2Params? Params => _params;
    /// <summary>The native receiver's counts. For the bench.</summary>
    internal P25Phase2Native.Stats Statistics => _rx.Statistics;

    /// <summary>Each voice frame given to the vocoder, the 49 bits then the error count (255: the end of a call). For tests.</summary>
    internal Action<byte[]>? FrameTap { get; set; }
    /// <summary>A line for each change of timing, each signalling burst of the slot and each MAC PDU and ESS read. For the bench.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>The channel and slot to receive. A different system or slot starts afresh.</summary>
    public void Configure(P25Phase2Params p)
    {
        var old = _params;
        if (old == p) return;
        if (old is null || old.Wacn != p.Wacn || old.SysId != p.SysId || old.Nac != p.Nac)
            _rx.SetSystem((uint)p.Wacn, (uint)p.SysId, (uint)p.Nac);
        bool slotChanged = _params?.Slot != p.Slot;
        _params = p;
        _rx.SetSlot(p.Slot);
        if (slotChanged) EndCall();
        Publish();
    }

    /// <summary>The channel's complex baseband, 48 kHz, after its channel filter.</summary>
    public void Feed(ReadOnlySpan<float> re, ReadOnlySpan<float> im)
    {
        int n = Math.Min(re.Length, im.Length);
        _burstsAge += n / 48000.0;
        for (int at = 0; at < n; at += Piece)
        {
            int k = Math.Min(Piece, n - at);
            _rx.Feed(re.Slice(at, k), im.Slice(at, k));
            Poll();
        }
    }

    // The receiver's events and voice items, in the order they came: each event before the voice items queued after it.
    private void Poll()
    {
        var events = _events;
        var voice = _items;
        _rx.ReadEvents(events);
        _rx.ReadVoice(voice);
        int e = 0;
        for (int v = 0; v <= voice.Count; v++)
        {
            while (e < events.Count && (v == voice.Count || events[e].VoiceAt <= _voiceRead)) OnEvent(events[e++]);
            if (v == voice.Count) break;
            OnVoice(voice[v]);
            _voiceRead++;
        }
        Publish();
    }

    private void OnEvent(in P25Phase2Native.Event ev)
    {
        int slot = _params?.Slot ?? -1;
        switch (ev.Kind)
        {
            case P25Phase2Native.EvSync:
                _locked = true;
                Trace?.Invoke("sync found");
                break;
            case P25Phase2Native.EvLost:
                _locked = false;
                EndCall();
                Trace?.Invoke("sync lost");
                break;
            case P25Phase2Native.EvBurst:
                _burstsAge = 0;
                if (ev.Slot != slot) break;
                if (ev.Duid is P25Phase2Native.Duid4V or P25Phase2Native.Duid2V)
                {
                    int frames = ev.Duid == P25Phase2Native.Duid4V ? 4 : 2;
                    _badBurst = ev.VoiceErrs > MostErrsPerFrame * frames;
                    break;
                }
                bool mac = ev.Duid is P25Phase2Native.DuidFacchS or P25Phase2Native.DuidFacchU or P25Phase2Native.DuidSacchS or P25Phase2Native.DuidSacchU;
                if (!mac) break;
                string what = ev.Duid is P25Phase2Native.DuidFacchS or P25Phase2Native.DuidFacchU ? "FACCH" : "SACCH";
                if (ev.Ok == 0)
                {
                    Trace?.Invoke($"slot {ev.Slot} ts {ev.Ts} {what}: {(ev.Readable == 0 ? "scrambled, no system" : ev.RsErrs < 0 ? "uncorrectable" : "CRC failed")}");
                    break;
                }
                if (P25MacPdu.Parse(ev.Bytes()) is { } pdu)
                {
                    Trace?.Invoke($"slot {ev.Slot} ts {ev.Ts} {what} {Describe(pdu)}");
                    OnMac(pdu);
                }
                break;
            case P25Phase2Native.EvEss:
                if (ev.Slot != slot || ev.Ok == 0) break;
                var (alg, key) = P25MacPdu.Ess(ev.Bytes());
                Trace?.Invoke($"slot {ev.Slot} ESS algorithm 0x{alg:X2} key 0x{key:X4}");
                // How the voice is sent: encrypted, whatever else said otherwise. Joining late it comes before the ACTIVE.
                _essAlg = alg;
                if (alg != P25MacPdu.Clear && _inCall) _encrypted = true;
                break;
        }
    }

    private void OnMac(P25MacPdu pdu)
    {
        switch (pdu.Kind)
        {
            case P25MacType.PushToTalk:
                // A new call
                StopPlaying();
                _inCall = true;
                _talkgroup = pdu.Talkgroup;
                _source = pdu.Source;
                _encrypted = pdu.Encrypted;
                _emergency = false;
                _essAlg = P25MacPdu.Clear;
                break;
            case P25MacType.EndPushToTalk:
            case P25MacType.Idle:
                EndCall();
                if (pdu.Kind == P25MacType.Idle) _talkgroup = _source = 0;
                break;
            case P25MacType.Hangtime:
                // The voice is over, the channel still the talkgroup's for a reply
                EndCall();
                if (pdu.GroupVoiceUser is { } h) _talkgroup = h.Talkgroup;
                break;
            case P25MacType.Active:
                if (pdu.GroupVoiceUser is not { } u) break;
                if (!_inCall || u.Talkgroup != _talkgroup)
                {
                    // Joined late (no PUSH-TO-TALK heard), or another talkgroup's call now
                    StopPlaying();
                    _inCall = true;
                    _talkgroup = u.Talkgroup;
                    _source = u.Source;
                    _encrypted = _essAlg != P25MacPdu.Clear;
                    _emergency = false;
                }
                if (_source == 0) _source = u.Source;
                if ((u.ServiceOptions & 0x40) != 0) _encrypted = true;
                if ((u.ServiceOptions & 0x80) != 0) _emergency = true;
                break;
        }
    }

    private bool Playable => _locked && _inCall && !_encrypted && _talkgroup != 0
        && (_params is not { Talkgroup: not 0 } p || p.Talkgroup == _talkgroup);

    private void OnVoice(byte[] item)
    {
        if (item[AmbeVocoder.ParameterBits] == P25Phase2Native.ErrsEnd)
        {
            StopPlaying();
            return;
        }
        if (!Playable)
        {
            StopPlaying();
            return;
        }
        if (_badBurst) item[AmbeVocoder.ParameterBits] = P25Phase2Native.ErrsLost;
        _playing = true;
        Push(item);
    }

    private void Push(byte[] item)
    {
        FrameTap?.Invoke(item);
        fixed (byte* p = item) _speech.Push(p);
    }

    // Whatever the vocoder had of the call goes with it.
    private void StopPlaying()
    {
        if (!_playing) return;
        _playing = false;
        var end = new byte[AmbeStream.ItemBytes];
        end[AmbeVocoder.ParameterBits] = P25Phase2Native.ErrsEnd;
        Push(end);
    }

    private void EndCall()
    {
        StopPlaying();
        _inCall = _encrypted = _emergency = false;
        _essAlg = P25MacPdu.Clear;
    }

    // A new status only when something in it has changed: it is published every block.
    private void Publish()
    {
        var s = _status;
        int slot = _params?.Slot ?? 0, nac = _params?.Nac ?? 0, sysId = _params?.SysId ?? 0, wacn = _params?.Wacn ?? 0;
        bool encrypted = _inCall && _encrypted, emergency = _inCall && _emergency;
        if (s.Slot == slot && s.Nac == nac && s.SystemId == sysId && s.Wacn == wacn && s.InCall == _inCall && s.Talkgroup == _talkgroup
            && s.Source == _source && s.Encrypted == encrypted && s.Emergency == emergency && s.VoiceAvailable == _speech.Available)
            return;
        _status = new P25Status
        {
            Phase = 2, Slot = slot, Nac = nac, SystemId = sysId, Wacn = wacn, InCall = _inCall, Talkgroup = _talkgroup, Source = _source,
            Encrypted = encrypted, Emergency = emergency, VoiceAvailable = _speech.Available,
        };
    }

    private static string Describe(P25MacPdu p) => p.Kind switch
    {
        P25MacType.PushToTalk => $"PUSH-TO-TALK TG {p.Talkgroup} ID {p.Source} algorithm 0x{p.AlgId:X2} key 0x{p.KeyId:X4}",
        P25MacType.EndPushToTalk => $"END-PUSH-TO-TALK TG {p.Talkgroup} ID {p.Source} NAC {p.ColorCode:X3}",
        _ => $"{p.Kind.ToString().ToUpperInvariant()}" + string.Concat(p.Messages.Select(m => m.IsGroupVoiceUser
            ? $" [group voice user TG {m.Talkgroup} ID {m.Source} options 0x{m.ServiceOptions:X2}]" : $" [0x{m.Opcode:X2}]")),
    };

    /// <summary>Decoded speech, 8 kHz, full scale +-1. Returns the samples written.</summary>
    public int ReadVoice(Span<float> voice) => _speech.Read(voice);

    /// <summary>The signal has gone or the channel has changed: end any call and start afresh (on the same system and slot).</summary>
    public void SignalLost()
    {
        _rx.SignalLost();
        _rx.ReadEvents();
        _voiceRead += (uint)_rx.ReadVoice().Count;
        _locked = _playing = _badBurst = false;
        _inCall = _encrypted = _emergency = false;
        _talkgroup = _source = 0;
        _essAlg = P25MacPdu.Clear;
        _burstsAge = 10;
        _speech.Clear();
        Publish();
    }

    public void Dispose()
    {
        _rx.Dispose();
        _speech.Dispose();
    }
}
