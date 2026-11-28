// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleScanner.Dsp;

namespace SimpleScanner.Digital;

/// <summary>
/// Digital voice on one received channel: the discriminator output of narrow FM goes to P25, DMR,
/// NXDN, D-STAR and System Fusion (C4FM) decoders, and the channel's complex baseband to a second P25 front end
/// (<see cref="P25FrontEnds"/>: CQPSK and LSM, as simulcast sites send). When one finds its kind of signal, the analogue
/// audio (which would be a rasp) gives way to the decoded speech, and <see cref="Status"/> says what
/// the signal is. Encrypted calls are reported and stay silent.
///
/// A P25 Phase 2 (TDMA) voice channel is received only when it is expected (<see cref="Expect"/>: trunk mode, following a
/// TDMA grant): its system and slot cannot be found from the signal alone. Then the baseband goes to the Phase 2 decoder
/// alone, and the others are not fed.
///
/// Used from one thread (the receiver's DSP thread, under its lock), except <see cref="Status"/>,
/// <see cref="DigitalAvailable"/> and <see cref="DmrVoiceAvailable"/>, which may be read from any.
/// </summary>
public sealed class DigitalVoice : IDisposable
{
    private const int VoicePrime = ChannelReceiver.AudioRate * 220 / 1000;      // one voice superframe and a little
    private readonly P25FrontEnds? _p25 = P25FrontEnds.TryCreate();
    private readonly DmrDecoder? _dmr = DmrDecoder.TryCreate();
    private readonly NxdnDecoder? _nxdn = NxdnDecoder.TryCreate();
    private readonly DstarDecoder? _dstar = DstarDecoder.TryCreate();
    private readonly YsfDecoder? _ysf = YsfDecoder.TryCreate();
    private readonly Upsampler _voiceUp = new(8000, 6);
    private readonly FloatFifo _voice = new(ChannelReceiver.AudioRate * 3);
    private readonly float[] _voice8k = new float[2048], _voice48k = new float[2048 * 6];
    private bool _voicePlaying, _wasOpen;
    private volatile IDigitalStatus? _digital;
    // Made the first time a Phase 2 channel is expected.
    private P25Phase2Decoder? _p2;
    private P25Phase2Params? _expect;
    private Action<string>? _p2Trace;

    /// <summary>The digital signal on the channel now (P25, DMR, NXDN, D-STAR or System Fusion), or null for none.</summary>
    public IDigitalStatus? Status => _digital;
    /// <summary>False when the digital voice library could not be loaded.</summary>
    public bool DigitalAvailable => _p25 is not null && _dmr is not null && _nxdn is not null && _dstar is not null && _ysf is not null;
    /// <summary>False when the AMBE+2 speech decoder is not installed: DMR calls are shown, not heard.</summary>
    public bool DmrVoiceAvailable => _dmr is { VoiceAvailable: true };

    /// <summary>
    /// Nothing but decoded speech is let through: the analogue audio is silenced whatever is on the channel, signal or not.
    /// For a trunked voice channel, which carries nothing else worth hearing.
    /// </summary>
    public bool DigitalOnly { get; set; }

    /// <summary>
    /// Only P25 is looked for (a trunked P25 voice channel): the DMR, NXDN, D-STAR and System Fusion decoders are not fed, so
    /// the channel costs its two P25 front ends and no more. Set false: all of them again, from afresh (on the DSP thread,
    /// at the next block: this may be set from any thread).
    /// </summary>
    public bool P25Only
    {
        get => _p25Only;
        set
        {
            if (value == _p25Only) return;
            _p25Only = value;
            if (!value) _othersStale = true;
        }
    }
    private volatile bool _p25Only, _othersStale;

    // Back from P25 only: what the others had is from before it.
    private void CatchUp()
    {
        if (!_othersStale) return;
        _othersStale = false;
        _dmr?.SignalLost();
        _nxdn?.SignalLost();
        _dstar?.SignalLost();
        _ysf?.SignalLost();
    }

    /// <summary>Blocks the DMR, NXDN, D-STAR and System Fusion decoders have been fed. For tests.</summary>
    internal long OthersFed { get; private set; }

    /// <summary>Decoded speech is being played.</summary>
    public bool Speaking => _voicePlaying;

    /// <summary>The Phase 2 channel expected now, or null.</summary>
    public P25Phase2Params? Expected => _expect;

    /// <summary>
    /// A P25 Phase 2 voice channel is on this channel: its system (for the scrambling), slot and talkgroup. Until it is
    /// cleared (null) the channel's baseband goes to the Phase 2 decoder alone; the discriminator decoders and P25's
    /// differential front end are not fed. Null: everything as before. Call <see cref="Reset"/> as well when the channel moves.
    /// </summary>
    public void Expect(P25Phase2Params? p)
    {
        if (p is not null && _p2 is null)
        {
            _p2 = P25Phase2Decoder.TryCreate();
            if (_p2 is not null) _p2.Trace = _p2Trace;
        }
        if (p is null && _expect is not null) _p2?.SignalLost();
        _expect = p;
        if (p is not null) _p2?.Configure(p);
    }

    /// <summary>The Phase 2 decoder's lines (see <see cref="P25Phase2Decoder.Trace"/>). For the bench.</summary>
    internal Action<string>? Phase2Trace
    {
        get => _p2Trace;
        set
        {
            _p2Trace = value;
            if (_p2 is not null) _p2.Trace = value;
        }
    }

    /// <summary>The Phase 2 decoder, once one has been made. For tests and the bench.</summary>
    internal P25Phase2Decoder? Phase2 => _p2;

    /// <summary>P25 is being received through the differential (baseband) front end. For tests.</summary>
    internal bool P25UsingIq => _p25 is { UsingIq: true };
    /// <summary>The differential front end is running. For tests.</summary>
    internal bool P25IqRunning => _p25 is { IqRunning: true };

    /// <summary>
    /// The channel's complex baseband (48 kHz, after the channel filter), for P25's differential front end. Narrow FM
    /// only; give it before the same block's discriminator output. <paramref name="signal"/>: the squelch is open.
    ///
    /// The differential front end costs two thirds as much again as the five discriminator decoders together, so it runs
    /// only where P25 may be: on a trunked voice channel (<see cref="DigitalOnly"/>), while either P25 front end is
    /// receiving data units, and for the first seconds of a signal (the squelch open) in which neither has yet. Not while
    /// another kind of digital signal is decoded, nor on an analogue signal past those seconds.
    /// </summary>
    public void FeedBaseband(ReadOnlySpan<float> re, ReadOnlySpan<float> im, bool signal)
    {
        if (_expect is not null)
        {
            _p2?.Feed(re, im);
            return;
        }
        if (_p25 is null) return;
        CatchUp();
        bool other = !_p25Only && (_dmr is { Active: true } || _nxdn is { Active: true } || _dstar is { Active: true } || _ysf is { Active: true });
        bool p25 = _p25.AnyActive;
        if (!signal || p25) _iqTrying = 0;
        else _iqTrying += re.Length / 48000.0;
        _p25.IqEnabled = !other && (DigitalOnly || p25 || (signal && _iqTrying < IqTrySeconds));
        _p25.FeedBaseband(re, im);
    }

    // How long the differential front end looks at a signal neither P25 front end has found anything in.
    private const double IqTrySeconds = 3;
    private double _iqTrying;

    /// <summary>The channel's discriminator output (48 kHz) and its scale, for the decoders.</summary>
    public void Feed(ReadOnlySpan<float> d, double hzPerUnit)
    {
        if (_expect is not null) return;
        CatchUp();
        _p25?.Feed(d, hzPerUnit);
        if (_p25Only) return;
        OthersFed++;
        _dmr?.Feed(d, hzPerUnit);
        _nxdn?.Feed(d, hzPerUnit);
        _dstar?.Feed(d, hzPerUnit);
        _ysf?.Feed(d, hzPerUnit);
    }

    /// <summary>The channel changed or its signal went: whatever the decoder was following is over.</summary>
    public void Reset()
    {
        _p25?.SignalLost();
        _dmr?.SignalLost();
        _nxdn?.SignalLost();
        _dstar?.SignalLost();
        _ysf?.SignalLost();
        _p2?.SignalLost();
        _voiceUp.Reset();
        _voice.Clear();
        _voicePlaying = false;
        _digital = null;
    }

    /// <summary>The receiver stopped: no signal to report. Only the status; the decoders are left as they are.</summary>
    public void ClearStatus() => _digital = null;

    /// <summary>
    /// After the channel has been demodulated: is there P25, DMR, NXDN, D-STAR or System Fusion on it, and
    /// if so put its decoded speech in place of the analogue audio in the first <paramref name="count"/>
    /// samples of <paramref name="left"/> and <paramref name="right"/>. <paramref name="settlingOrLooking"/>
    /// is true while a scan is settling on or looking at a channel. Does nothing without any decoder.
    /// </summary>
    public void Process(bool open, bool settlingOrLooking, Mode mode, Span<float> left, Span<float> right, int count)
    {
        if (DigitalOnly && (mode != Mode.Nfm || !DigitalAvailable))
        {
            left[..count].Clear();
            right[..count].Clear();
        }
        if (_p25 is null && _dmr is null && _nxdn is null && _dstar is null && _ysf is null) return;
        if (_wasOpen && !open && !settlingOrLooking) Reset();
        _wasOpen = open;
        if (mode != Mode.Nfm) return;

        int n;
        if (_expect is not null)
        {
            while (_p2 is not null && (n = _p2.ReadVoice(_voice8k)) > 0)
                _voice.Write(_voice48k.AsSpan(0, _voiceUp.Process(_voice8k.AsSpan(0, n), _voice48k)));
            _digital = _p2 is { Active: true } ? _p2.Status : null;
        }
        else
        {
            while (_p25 is not null && (n = _p25.ReadVoice(_voice8k)) > 0)
                _voice.Write(_voice48k.AsSpan(0, _voiceUp.Process(_voice8k.AsSpan(0, n), _voice48k)));
            if (_p25Only) _digital = _p25 is { Active: true } ? _p25.Status : null;
            else ReadOthers();
        }
        Play(left, right, count);
    }

    // The speech of the decoders other than P25, and which of them all has a signal.
    private void ReadOthers()
    {
        int n;
        while (_dmr is not null && (n = _dmr.ReadVoice(_voice8k)) > 0)
            _voice.Write(_voice48k.AsSpan(0, _voiceUp.Process(_voice8k.AsSpan(0, n), _voice48k)));
        while (_nxdn is not null && (n = _nxdn.ReadVoice(_voice8k)) > 0)
            _voice.Write(_voice48k.AsSpan(0, _voiceUp.Process(_voice8k.AsSpan(0, n), _voice48k)));
        while (_dstar is not null && (n = _dstar.ReadVoice(_voice8k)) > 0)
            _voice.Write(_voice48k.AsSpan(0, _voiceUp.Process(_voice8k.AsSpan(0, n), _voice48k)));
        while (_ysf is not null && (n = _ysf.ReadVoice(_voice8k)) > 0)
            _voice.Write(_voice48k.AsSpan(0, _voiceUp.Process(_voice8k.AsSpan(0, n), _voice48k)));
        // System Fusion before NXDN: its frames are proved by a 40-bit sync and a CRC, NXDN's by little, and an
        // NXDN frame or two can turn up by chance in a System Fusion signal.
        _digital = _p25 is { Active: true } ? _p25.Status : _dmr is { Active: true } ? _dmr.Status
            : _ysf is { Active: true } ? _ysf.Status : _nxdn is { Active: true } ? _nxdn.Status
            : _dstar is { Active: true } ? _dstar.Status : null;
    }

    private void Play(Span<float> left, Span<float> right, int count)
    {
        bool active = _digital is not null;
        if (!active && !_voicePlaying)
        {
            _voice.Clear();
            if (DigitalOnly)
            {
                left[..count].Clear();
                right[..count].Clear();
            }
            return;
        }
        // P25 speech arrives nine frames at a time, every 180 ms (DMR's three at a time): hold some before starting to play.
        if (!_voicePlaying && _voice.Count >= VoicePrime) _voicePlaying = true;
        var l = left[..count];
        l.Clear();
        if (_voicePlaying && _voice.Read(l) < count) _voicePlaying = false;
        l.CopyTo(right);
    }

    /// <summary>
    /// Gives back the decoders' memory. They belong to the thread that feeds them: call this only once
    /// that thread has ended.
    /// </summary>
    public void Dispose()
    {
        _p25?.Dispose();
        _dmr?.Dispose();
        _nxdn?.Dispose();
        _dstar?.Dispose();
        _ysf?.Dispose();
        _p2?.Dispose();
    }
}
