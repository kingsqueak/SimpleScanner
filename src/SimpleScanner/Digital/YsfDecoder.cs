// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;
using System.Text;
using SimpleScanner.Dsp;

namespace SimpleScanner.Digital;

/// <summary>What a System Fusion transmission's traffic frames carry.</summary>
public enum YsfMode
{
    Unknown = -1,
    /// <summary>Voice and data, type 1: half-rate speech with a large data channel.</summary>
    VoiceData1 = 0,
    /// <summary>Data only.</summary>
    Data = 1,
    /// <summary>Voice and data, type 2: what the radios call DN, the usual mode.</summary>
    VoiceData2 = 2,
    /// <summary>Full-rate voice: what the radios call VW.</summary>
    VoiceFullRate = 3,
}

/// <summary>
/// What a Yaesu System Fusion ("C4FM") transmission has told us so far. It is an amateur radio
/// mode: stations are named by callsign, and a transmission says who sends it, who it is for and
/// which repeater it comes through.
/// </summary>
public sealed record YsfStatus : IDigitalStatus
{
    public bool InCall { get; init; }
    public YsfMode Mode { get; init; } = YsfMode.Unknown;
    /// <summary>The callsigns have arrived (in the header, or in the frames that follow for late comers).</summary>
    public bool HaveCallsigns { get; init; }
    /// <summary>The station transmitting.</summary>
    public string Source { get; init; } = "";
    /// <summary>Who is called: a callsign, or ALL for anybody.</summary>
    public string Destination { get; init; } = "";
    /// <summary>The repeater's callsign on its output, when the transmission comes through one.</summary>
    public string Downlink { get; init; } = "";
    /// <summary>The callsign on the repeater's input side.</summary>
    public string Uplink { get; init; } = "";
    /// <summary>The digital group ID (the mode's tone squelch, 1 to 126); 0 for none.</summary>
    public int GroupId { get; init; }
    /// <summary>The transmission reached the repeater over the network, not from a radio nearby.</summary>
    public bool Internet { get; init; }
    /// <summary>The start of the transmission was not heard.</summary>
    public bool Late { get; init; }
    /// <summary>System Fusion has no encryption.</summary>
    public bool Encrypted => false;
    /// <summary>False when the AMBE speech decoder library is not installed: DN transmissions are shown, not heard.</summary>
    public bool VoiceAvailable { get; init; } = true;
    public int Calls { get; init; }

    public bool NothingToHear => !InCall || Mode == YsfMode.Data;

    public override string ToString()
    {
        var s = new StringBuilder("C4FM");
        if (!InCall) return s.ToString();
        s.Append(Mode switch
        {
            YsfMode.VoiceData2 => " DN",
            YsfMode.VoiceFullRate => " VW",
            YsfMode.VoiceData1 => " V/D 1",
            YsfMode.Data => " data",
            _ => "",
        });
        if (HaveCallsigns)
        {
            s.Append($" · {Source}");
            if (Destination.Length > 0) s.Append($" to {Destination}");
        }
        if (Downlink.Length > 0 && Downlink != Source) s.Append($" · via {Downlink}");
        if (GroupId > 0) s.Append($" · group {GroupId}");
        if (Internet) s.Append(" · from the network");
        if (!VoiceAvailable && Mode is YsfMode.VoiceData1 or YsfMode.VoiceData2) s.Append(" · no voice codec");
        return s.ToString();
    }
}

/// <summary>
/// Yaesu System Fusion digital voice from an FM discriminator, through the ss_digital native
/// library (native/digital/ysfrx.c on RadioFoundry's soft modem). DN speech goes through
/// <see cref="AmbeVocoder"/>; full-rate (VW) speech is decoded in the library. Feed it the
/// discriminator at 48 kHz; read the status and the decoded speech (8 kHz). One per channel; use
/// from one thread.
/// </summary>
public sealed unsafe partial class YsfDecoder : IDisposable
{
    private const string Lib = "ss_digital";
    private const float UnitHz = 2500;

    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public int Frames, InCall, Calls, Mode, HaveCalls, Late, GroupId, Internet, Narrow;
        // dest[11], src[11], down[11], up[11]
        public fixed byte Chars[44];
    }

    [LibraryImport(Lib)] private static partial nint ysfrx_new(int rate, float hzPerUnit);
    [LibraryImport(Lib)] private static partial void ysfrx_free(nint r);
    [LibraryImport(Lib)] private static partial void ysfrx_feed(nint r, float* disc, int n);
    [LibraryImport(Lib)] private static partial void ysfrx_signal_lost(nint r);
    [LibraryImport(Lib)] private static partial void ysfrx_status(nint r, Info* info);
    [LibraryImport(Lib)] private static partial int ysfrx_read_voice(nint r, byte* items, int max);
    [LibraryImport(Lib)] private static partial int ysfrx_read_pcm(nint r, short* pcm, int max);

    private nint _handle;
    private readonly AmbeStream _speech = new();
    private readonly FloatFifo _fullRate = new(8000 * 3);
    // The modem works at 24 kHz, five samples to a symbol. Noise above the signal is taken off first.
    private readonly FirDecimator _to24k = new(FirDesign.LowPass(48000, 6000, 4000), 2);
    private float[] _buf = [];
    private double _framesAge = 10;
    private int _lastFrames;
    private YsfStatus _status = new();

    /// <summary>Null when the native library is not present: the scanner then has no System Fusion.</summary>
    public static YsfDecoder? TryCreate()
    {
        try
        {
            nint h = ysfrx_new(24000, UnitHz);
            return h == 0 ? null : new YsfDecoder(h);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private YsfDecoder(nint handle) => _handle = handle;

    /// <summary>System Fusion frames are arriving (within the last half second).</summary>
    public bool Active => _framesAge < 0.5;
    /// <summary>What the transmission has said; meaningful while <see cref="Active"/>.</summary>
    public YsfStatus Status => _status;
    /// <summary>Frames received since the start (or the last <see cref="SignalLost"/>): ten a second.</summary>
    public int Frames => _lastFrames;
    /// <summary>False when the AMBE speech decoder library is missing.</summary>
    public bool VoiceAvailable => _speech.Available;

    /// <summary>
    /// Discriminator samples at 48 kHz with no audio filtering. <paramref name="hzPerUnit"/> is
    /// the deviation that reads 1.0. Either sense of deviation is taken.
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
        // In pieces, so the speech is taken out as it comes and the queues in the library never overflow.
        for (int at = 0; at < disc.Length; at += 4800)
        {
            var piece = disc.Slice(at, Math.Min(4800, disc.Length - at));
            fixed (float* p = piece) ysfrx_feed(_handle, p, piece.Length);
            Poll();
        }
    }

    private static string Field(byte* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.ASCII.GetString(p, n).Trim();
    }

    private void Poll()
    {
        Info i;
        ysfrx_status(_handle, &i);
        if (i.Frames != _lastFrames)
        {
            _lastFrames = i.Frames;
            _framesAge = 0;
        }
        byte* c = i.Chars;
        _status = new YsfStatus
        {
            InCall = i.InCall != 0, Mode = (YsfMode)i.Mode, HaveCallsigns = i.HaveCalls != 0,
            Destination = Field(c, 10), Source = Field(c + 11, 10), Downlink = Field(c + 22, 10), Uplink = Field(c + 33, 10),
            GroupId = i.GroupId, Internet = i.Internet != 0, Late = i.Late != 0,
            VoiceAvailable = _speech.Available, Calls = i.Calls,
        };

        byte* items = stackalloc byte[8 * AmbeStream.ItemBytes];
        int n;
        while ((n = ysfrx_read_voice(_handle, items, 8)) > 0)
            for (int k = 0; k < n; k++) _speech.Push(items + k * AmbeStream.ItemBytes);

        const int chunk = 800;
        short* pcm = stackalloc short[chunk];
        Span<float> f = stackalloc float[chunk];
        while ((n = ysfrx_read_pcm(_handle, pcm, chunk)) > 0)
        {
            for (int k = 0; k < n; k++) f[k] = pcm[k] / 32768f;
            _fullRate.Write(f[..n]);
        }
    }

    /// <summary>Decoded speech, 8 kHz, full scale +-1. Returns the samples written.</summary>
    public int ReadVoice(Span<float> voice)
    {
        // A transmission is in one voice mode or the other, so only one of the two has anything.
        int n = _speech.Read(voice);
        return n > 0 ? n : _fullRate.Read(voice);
    }

    /// <summary>The signal has gone or the channel has changed: end any transmission and start afresh.</summary>
    public void SignalLost()
    {
        ysfrx_signal_lost(_handle);
        _speech.Clear();
        _fullRate.Clear();
        _to24k.Reset();
        _framesAge = 10;
        _lastFrames = 0;
        _status = new YsfStatus();
    }

    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) ysfrx_free(h);
        _speech.Dispose();
    }
}
