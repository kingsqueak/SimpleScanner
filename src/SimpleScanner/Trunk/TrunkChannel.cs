// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleScanner.Digital;
using SimpleScanner.Dsp;

namespace SimpleScanner.Trunk;

/// <summary>
/// A trunked site's control channel taken out of the radio's span: a channel filter feeding P25's two front ends (the FM
/// discriminator, and the differential demodulator on the complex baseband that simulcast sites need; the one decoding
/// more is read), with no squelch and no sound. The receiver works it on its DSP thread under its lock, beside the slot that
/// holds the voice channel.
/// </summary>
internal sealed class TrunkChannel : IDisposable
{
    private static readonly P25Tsbk[] None = [];
    private float[] _left = [], _right = [];

    public TrunkChannel(double sampleRate)
    {
        Channel = new ChannelReceiver(sampleRate);
        Channel.Configure(Mode.Nfm, BandPlan.NfmNarrow);
        Decoder = P25FrontEnds.TryCreate();
        Channel.BasebandTap = (re, im) =>
        {
            Decoder?.FeedBaseband(re, im);
            RecordTap?.Invoke(re, im);
        };
        Channel.DiscriminatorTap = d => Decoder?.Feed(d, Channel.DiscriminatorHzPerUnit);
    }

    public ChannelReceiver Channel { get; }
    /// <summary>Null when the digital voice library could not be loaded: there is no trunking then.</summary>
    public P25FrontEnds? Decoder { get; }
    /// <summary>The control channel's complex 48 kHz samples as they are received (for recording it).</summary>
    public ComplexSamplesHandler? RecordTap { get; set; }
    public double FrequencyHz { get; private set; }
    public double OffsetHz { get; private set; }

    /// <summary>What the control channel says of itself, while P25 is arriving on it; otherwise null.</summary>
    public IDigitalStatus? Status => Decoder is { Active: true } d ? d.Status : null;

    /// <summary>Put it on a frequency, this far from the radio's centre. A new frequency starts the decoder afresh.</summary>
    public void Tune(double hz, double offsetHz)
    {
        if (hz != FrequencyHz)
        {
            Decoder?.SignalLost();
            Channel.ResetRoughness();
            FrequencyHz = hz;
        }
        OffsetHz = offsetHz;
        Channel.SetOffset(offsetHz);
    }

    /// <summary>One block of the radio's IQ.</summary>
    public void Process(ReadOnlySpan<float> i, ReadOnlySpan<float> q)
    {
        int max = Channel.MaxAudio(i.Length);
        if (_left.Length < max)
        {
            _left = new float[max];
            _right = new float[max];
        }
        Channel.Process(i, q, _left, _right);
    }

    /// <summary>The trunking messages decoded since the last call, oldest first.</summary>
    public IReadOnlyList<P25Tsbk> ReadTsbks() => Decoder is { HasTsbks: true } d ? d.ReadTsbks() : None;

    // The decoder belongs to the DSP thread: the receiver calls this only once that thread has ended.
    public void Dispose() => Decoder?.Dispose();
}
