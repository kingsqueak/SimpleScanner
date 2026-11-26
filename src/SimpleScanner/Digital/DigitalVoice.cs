using SimpleScanner.Dsp;

namespace SimpleScanner.Digital;

/// <summary>
/// Digital voice on one received channel: the discriminator output of narrow FM goes to P25, DMR,
/// NXDN, D-STAR and System Fusion (C4FM) decoders. When one finds its kind of signal, the analogue
/// audio (which would be a rasp) gives way to the decoded speech, and <see cref="Status"/> says what
/// the signal is. Encrypted calls are reported and stay silent.
///
/// Used from one thread (the receiver's DSP thread, under its lock), except <see cref="Status"/>,
/// <see cref="DigitalAvailable"/> and <see cref="DmrVoiceAvailable"/>, which may be read from any.
/// </summary>
public sealed class DigitalVoice : IDisposable
{
    private const int VoicePrime = ChannelReceiver.AudioRate * 220 / 1000;      // one voice superframe and a little
    private readonly P25Decoder? _p25 = P25Decoder.TryCreate();
    private readonly DmrDecoder? _dmr = DmrDecoder.TryCreate();
    private readonly NxdnDecoder? _nxdn = NxdnDecoder.TryCreate();
    private readonly DstarDecoder? _dstar = DstarDecoder.TryCreate();
    private readonly YsfDecoder? _ysf = YsfDecoder.TryCreate();
    private readonly Upsampler _voiceUp = new(8000, 6);
    private readonly FloatFifo _voice = new(ChannelReceiver.AudioRate * 3);
    private readonly float[] _voice8k = new float[2048], _voice48k = new float[2048 * 6];
    private bool _voicePlaying, _wasOpen;
    private volatile IDigitalStatus? _digital;

    /// <summary>The digital signal on the channel now (P25, DMR, NXDN, D-STAR or System Fusion), or null for none.</summary>
    public IDigitalStatus? Status => _digital;
    /// <summary>False when the digital voice library could not be loaded.</summary>
    public bool DigitalAvailable => _p25 is not null && _dmr is not null && _nxdn is not null && _dstar is not null && _ysf is not null;
    /// <summary>False when the AMBE+2 speech decoder is not installed: DMR calls are shown, not heard.</summary>
    public bool DmrVoiceAvailable => _dmr is { VoiceAvailable: true };

    /// <summary>The channel's discriminator output (48 kHz) and its scale, for the decoders.</summary>
    public void Feed(ReadOnlySpan<float> d, double hzPerUnit)
    {
        _p25?.Feed(d, hzPerUnit);
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
        if (_p25 is null && _dmr is null && _nxdn is null && _dstar is null && _ysf is null) return;
        if (_wasOpen && !open && !settlingOrLooking) Reset();
        _wasOpen = open;
        if (mode != Mode.Nfm) return;

        int n;
        while (_p25 is not null && (n = _p25.ReadVoice(_voice8k)) > 0)
            _voice.Write(_voice48k.AsSpan(0, _voiceUp.Process(_voice8k.AsSpan(0, n), _voice48k)));
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
        bool active = _digital is not null;
        if (!active && !_voicePlaying)
        {
            _voice.Clear();
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
    }
}
