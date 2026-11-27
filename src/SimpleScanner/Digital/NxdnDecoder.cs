// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;
using System.Text;
using SimpleScanner.Dsp;

namespace SimpleScanner.Digital;

/// <summary>What an NXDN signal has told us so far.</summary>
public sealed record NxdnStatus : IDigitalStatus
{
    /// <summary>4800 bit/s (a 6.25 kHz channel) or 9600 bit/s (12.5 kHz).</summary>
    public int Rate { get; init; }
    /// <summary>Radio access number, 0 to 63: tells systems sharing a frequency apart. -1 until the signal has said.</summary>
    public int Ran { get; init; } = -1;
    public bool InCall { get; init; }
    /// <summary>The call's addresses have arrived.</summary>
    public bool HaveAddresses { get; init; }
    /// <summary>A talkgroup call; otherwise a call to one radio.</summary>
    public bool Group { get; init; }
    /// <summary>The talkgroup, or the radio called.</summary>
    public int Destination { get; init; }
    /// <summary>The radio transmitting.</summary>
    public int Source { get; init; }
    public bool Emergency { get; init; }
    public bool Encrypted { get; init; }
    /// <summary>The call uses NXDN's full-rate voice codec, which this scanner cannot decode.</summary>
    public bool FullRateVoice { get; init; }
    /// <summary>False when the speech decoder library is not installed: calls are shown, not heard.</summary>
    public bool VoiceAvailable { get; init; } = true;
    public int Calls { get; init; }

    public bool NothingToHear => !InCall || Encrypted || FullRateVoice;

    public override string ToString()
    {
        var s = new StringBuilder("NXDN");
        if (Rate != 0) s.Append($" {Rate}");
        if (Ran >= 0) s.Append($" · RAN {Ran}");
        if (!InCall) return s.ToString();
        if (!HaveAddresses) s.Append(" · voice");
        else s.Append(Group ? $" · TG {Destination}" : $" · to {Destination}").Append($" · ID {Source}");
        if (Emergency) s.Append(" · EMERGENCY");
        if (Encrypted) s.Append(" · encrypted");
        else if (FullRateVoice) s.Append(" · full-rate voice, not decoded");
        else if (!VoiceAvailable) s.Append(" · no voice codec");
        return s.ToString();
    }
}

/// <summary>
/// NXDN conventional voice (both speeds) from an FM discriminator, through the ss_digital native
/// library (native/digital: RadioFoundry's soft modem, framer and NXDN layer) and, for the
/// speech, <see cref="AmbeVocoder"/>. Feed it the discriminator at 48 kHz; read the status and
/// the decoded speech (8 kHz). One per channel; use from one thread.
/// </summary>
public sealed unsafe partial class NxdnDecoder : IDisposable
{
    private const string Lib = "ss_digital";
    private const float UnitHz = 2500;
    private const int CallGroup = 1, CallBroadcast = 0, VoiceEfr = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public int Frames, Rate, Ran, InCall, HaveCall, Late, CallType, Dst, Src, Emergency, Cipher, VoiceMode, Calls;
    }

    [LibraryImport(Lib)] private static partial nint nxdnrx_new(int rate, float hzPerUnit);
    [LibraryImport(Lib)] private static partial void nxdnrx_free(nint r);
    [LibraryImport(Lib)] private static partial void nxdnrx_feed(nint r, float* disc, int n);
    [LibraryImport(Lib)] private static partial void nxdnrx_signal_lost(nint r);
    [LibraryImport(Lib)] private static partial void nxdnrx_status(nint r, Info* info);
    [LibraryImport(Lib)] private static partial int nxdnrx_read_voice(nint r, byte* items, int max);

    private nint _handle;
    private readonly AmbeStream _speech = new();
    // The modems work at 24 kHz. Noise above the signal is taken off first.
    private readonly FirDecimator _to24k = new(FirDesign.LowPass(48000, 6000, 4000), 2);
    private float[] _buf = [];
    private double _framesAge = 10;
    private int _lastFrames;
    private NxdnStatus _status = new();

    /// <summary>Null when the native library is not present: the scanner then has no NXDN.</summary>
    public static NxdnDecoder? TryCreate()
    {
        try
        {
            nint h = nxdnrx_new(24000, UnitHz);
            return h == 0 ? null : new NxdnDecoder(h);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private NxdnDecoder(nint handle) => _handle = handle;

    /// <summary>NXDN frames are arriving (within the last half second).</summary>
    public bool Active => _framesAge < 0.5;
    /// <summary>What the signal has said; meaningful while <see cref="Active"/>.</summary>
    public NxdnStatus Status => _status;
    /// <summary>Frames read since the start (or the last <see cref="SignalLost"/>).</summary>
    public int Frames => _lastFrames;
    /// <summary>False when the speech decoder library is missing.</summary>
    public bool VoiceAvailable => _speech.Available;

    /// <summary>
    /// Discriminator samples at 48 kHz, positive above the carrier, with no audio filtering.
    /// <paramref name="hzPerUnit"/> is the deviation that reads 1.0.
    /// </summary>
    public void Feed(ReadOnlySpan<float> disc48k, double hzPerUnit)
    {
        int max = _to24k.MaxOutput(disc48k.Length);
        if (_buf.Length < max) _buf = new float[max];
        int n = _to24k.Process(disc48k, _buf);
        float scale = (float)(hzPerUnit / UnitHz);
        if (scale != 1)
            for (int i = 0; i < n; i++) _buf[i] *= scale;
        _framesAge += disc48k.Length / 48000.0;
        Feed24k(_buf.AsSpan(0, n));
    }

    /// <summary>Samples already at 24 kHz, 1.0 = 2.5 kHz deviation. For tests (and the end of <see cref="Feed"/>).</summary>
    internal void Feed24k(ReadOnlySpan<float> disc)
    {
        // In pieces, so the speech is taken out as it comes and the queue in the library never overflows.
        for (int at = 0; at < disc.Length; at += 4800)
        {
            var piece = disc.Slice(at, Math.Min(4800, disc.Length - at));
            fixed (float* p = piece) nxdnrx_feed(_handle, p, piece.Length);
            Poll();
        }
    }

    private void Poll()
    {
        Info i;
        nxdnrx_status(_handle, &i);
        if (i.Frames != _lastFrames)
        {
            _lastFrames = i.Frames;
            _framesAge = 0;
        }
        _status = new NxdnStatus
        {
            Rate = i.Rate, Ran = i.Ran, InCall = i.InCall != 0, HaveAddresses = i.HaveCall != 0,
            Group = i.CallType is CallGroup or CallBroadcast, Destination = i.Dst, Source = i.Src,
            Emergency = i.Emergency != 0, Encrypted = i.Cipher != 0, FullRateVoice = i.HaveCall != 0 && i.VoiceMode == VoiceEfr,
            VoiceAvailable = _speech.Available, Calls = i.Calls,
        };
        byte* items = stackalloc byte[16 * AmbeStream.ItemBytes];
        int n;
        while ((n = nxdnrx_read_voice(_handle, items, 16)) > 0)
            for (int k = 0; k < n; k++) _speech.Push(items + k * AmbeStream.ItemBytes);
    }

    /// <summary>Decoded speech, 8 kHz, full scale +-1. Returns the samples written.</summary>
    public int ReadVoice(Span<float> voice) => _speech.Read(voice);

    /// <summary>The signal has gone or the channel has changed: end any call and start afresh.</summary>
    public void SignalLost()
    {
        nxdnrx_signal_lost(_handle);
        _speech.Clear();
        _to24k.Reset();
        _framesAge = 10;
        _lastFrames = 0;
        _status = new NxdnStatus();
    }

    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) nxdnrx_free(h);
        _speech.Dispose();
    }
}
