// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner.Digital;

/// <summary>
/// P25 Phase 1 on one channel through two front ends at once: the FM discriminator (C4FM transmitters) and the
/// differential demodulator on the complex baseband (CQPSK and LSM, the simulcast sites' modulation, and C4FM too). The
/// one decoding more is the one heard and read: its status, speech and trunking messages; the other's are thrown away
/// as they come, so the speech is never a mix of the two. It changes over when the other has decoded clearly more data
/// units and trunking messages over the last second, or at once when the one in use has gone quiet and the other has
/// something. Use from one thread.
/// </summary>
public sealed class P25FrontEnds : IDisposable
{
    private const double Window = 1.0;          // seconds over which the two are compared
    private const double Quiet = 0.5;           // nothing for this long (P25Decoder.Active): the other may take over at once

    private readonly P25Decoder _fm;
    private readonly P25Decoder? _iq;
    private P25Decoder _selected;
    private double _clock, _windowStart;
    private long _fmAtStart, _iqAtStart, _fmLast, _iqLast;
    private bool _iqEnabled = true, _iqFed;
    private readonly float[] _drain = new float[1024];

    /// <summary>Null when the native library is not present (no P25 then). Without the differential front end, FM only.</summary>
    public static P25FrontEnds? TryCreate()
    {
        var fm = P25Decoder.TryCreate();
        return fm is null ? null : new P25FrontEnds(fm, P25Decoder.TryCreateIq());
    }

    private P25FrontEnds(P25Decoder fm, P25Decoder? iq)
    {
        _fm = fm;
        _iq = iq;
        _selected = fm;
    }

    /// <summary>The front end being heard and read now.</summary>
    public P25Decoder Selected => _selected;
    /// <summary>The differential front end is the one in use.</summary>
    public bool UsingIq => _iq is not null && _selected == _iq;
    public P25Decoder Fm => _fm;
    public P25Decoder? Iq => _iq;

    /// <summary>P25 data units are arriving at the front end in use.</summary>
    public bool Active => _selected.Active;
    /// <summary>P25 data units are arriving at either front end.</summary>
    public bool AnyActive => _fm.Active || (_iqFed && _iq!.Active);
    public P25Status Status => _selected.Status;

    /// <summary>
    /// Whether the differential front end runs (it costs about as much again as the FM one). Turned off, it is reset and
    /// the FM one is used.
    /// </summary>
    public bool IqEnabled
    {
        get => _iqEnabled && _iq is not null;
        set
        {
            if (value == _iqEnabled) return;
            _iqEnabled = value;
            if (!value && _iq is not null)
            {
                if (_iqFed) _iq.SignalLost();
                _iqFed = false;
                _iq.ClearTsbks();
                if (_selected == _iq) Select(_fm);
            }
        }
    }

    /// <summary>The differential front end has been fed since it was last reset. For tests.</summary>
    internal bool IqRunning => _iqFed;

    /// <summary>The channel's discriminator output at 48 kHz (see <see cref="P25Decoder.Feed"/>). Feed this after the baseband of the same block.</summary>
    public void Feed(ReadOnlySpan<float> disc48k, double hzPerUnit)
    {
        _fm.Feed(disc48k, hzPerUnit);
        _clock += disc48k.Length / 48000.0;
        Choose();
    }

    /// <summary>The channel's complex baseband at 48 kHz (see <see cref="P25Decoder.FeedBaseband"/>), for the differential front end.</summary>
    public void FeedBaseband(ReadOnlySpan<float> re, ReadOnlySpan<float> im)
    {
        if (!IqEnabled) return;
        _iq!.FeedBaseband(re, im);
        _iqFed = true;
    }

    private static long Score(P25Decoder d) => d.Units + d.GoodTsbks;

    private void Choose()
    {
        var other = _selected == _fm ? _iq : _fm;
        if (other is not null && IqEnabled)
        {
            long sf = Score(_fm), si = Score(_iq!);
            long selNow = _selected == _fm ? sf : si, otherNow = _selected == _fm ? si : sf;
            long otherLast = _selected == _fm ? _iqLast : _fmLast;
            if (!_selected.Active && other.Active && otherNow > otherLast)
                Select(other);
            else if (_clock - _windowStart >= Window)
            {
                long dSel = selNow - (_selected == _fm ? _fmAtStart : _iqAtStart);
                long dOther = otherNow - (_selected == _fm ? _iqAtStart : _fmAtStart);
                // Clearly more: two units or messages, and a tenth
                if (dOther >= dSel + Math.Max(2, dSel / 10)) Select(other);
                _windowStart = _clock;
                _fmAtStart = sf;
                _iqAtStart = si;
            }
            _fmLast = sf;
            _iqLast = si;
        }
        // What the one not in use found is not wanted
        if (_selected != _fm) _fm.ClearTsbks();
        else _iq?.ClearTsbks();
    }

    private void Select(P25Decoder d)
    {
        _selected = d;
        _windowStart = _clock;
        _fmAtStart = Score(_fm);
        _iqAtStart = _iq is null ? 0 : Score(_iq);
    }

    /// <summary>Decoded speech of the front end in use (see <see cref="P25Decoder.ReadVoice"/>); the other's is thrown away.</summary>
    public int ReadVoice(Span<float> voice)
    {
        var other = _selected == _fm ? _iq : _fm;
        if (other is not null)
            while (other.ReadVoice(_drain) > 0) { }
        return _selected.ReadVoice(voice);
    }

    /// <summary>Trunking messages of the front end in use are waiting.</summary>
    public bool HasTsbks => _selected.HasTsbks;

    /// <summary>The trunking messages of the front end in use since the last call (see <see cref="P25Decoder.ReadTsbks"/>).</summary>
    public List<P25Tsbk> ReadTsbks() => _selected.ReadTsbks();

    /// <summary>The signal has gone or the channel has changed: both start afresh, and the FM one is used until the other decodes more.</summary>
    public void SignalLost()
    {
        _fm.SignalLost();
        if (_iqFed) _iq!.SignalLost();
        _iqFed = false;
        _selected = _fm;
        _windowStart = _clock;
        _fmAtStart = _fmLast = Score(_fm);
        _iqAtStart = _iqLast = _iq is null ? 0 : Score(_iq);
    }

    public void Dispose()
    {
        _fm.Dispose();
        _iq?.Dispose();
    }
}
