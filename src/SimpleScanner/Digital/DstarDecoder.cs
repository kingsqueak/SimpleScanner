using System.Runtime.InteropServices;
using System.Text;
using SimpleScanner.Dsp;

namespace SimpleScanner.Digital;

/// <summary>
/// What a D-STAR transmission has told us so far. D-STAR is an amateur radio mode: stations are
/// named by callsign, and a transmission says who sends it, who it is for and which repeater it
/// goes through.
/// </summary>
public sealed record DstarStatus : IDigitalStatus
{
    public bool InCall { get; init; }
    /// <summary>The callsigns have arrived (at the start, or repeated in the transmission for late comers).</summary>
    public bool HaveCallsigns { get; init; }
    /// <summary>The station transmitting.</summary>
    public string My { get; init; } = "";
    /// <summary>The four characters a station adds to its callsign (often the radio's model).</summary>
    public string Suffix { get; init; } = "";
    /// <summary>Who is called: a callsign, or CQCQCQ for anybody.</summary>
    public string Your { get; init; } = "";
    /// <summary>The repeater the station sends through; DIRECT when none.</summary>
    public string Repeater { get; init; } = "";
    /// <summary>Where that repeater passes the transmission on (its gateway, or another band's module).</summary>
    public string Repeater2 { get; init; } = "";
    /// <summary>The short message a station sends with every transmission.</summary>
    public string Text { get; init; } = "";
    public bool Emergency { get; init; }
    /// <summary>A data transmission, not speech.</summary>
    public bool Data { get; init; }
    /// <summary>D-STAR has no encryption of its own.</summary>
    public bool Encrypted => false;
    /// <summary>False when the speech decoder library is not installed: transmissions are shown, not heard.</summary>
    public bool VoiceAvailable { get; init; } = true;
    public int Calls { get; init; }

    public bool NothingToHear => !InCall || Data;

    public override string ToString()
    {
        var s = new StringBuilder("D-STAR");
        if (!InCall) return s.ToString();
        if (!HaveCallsigns) s.Append(" · voice");
        else
        {
            s.Append($" · {My}");
            if (Suffix.Length > 0) s.Append($"/{Suffix}");
            if (Your.Length > 0) s.Append($" to {Your}");
            if (Repeater.Length > 0 && Repeater != "DIRECT") s.Append($" · via {Repeater}");
        }
        if (Text.Length > 0) s.Append($" · {Text}");
        if (Emergency) s.Append(" · EMERGENCY");
        if (Data) s.Append(" · data");
        else if (!VoiceAvailable) s.Append(" · no voice codec");
        return s.ToString();
    }
}

/// <summary>
/// D-STAR digital voice from an FM discriminator, through the ss_digital native library
/// (native/digital/dstarrx.c on RadioFoundry's soft modem) and, for the speech,
/// <see cref="AmbeVocoder"/>. Feed it the discriminator at 48 kHz; read the status and the
/// decoded speech (8 kHz). One per channel; use from one thread.
/// </summary>
public sealed unsafe partial class DstarDecoder : IDisposable
{
    private const string Lib = "ss_digital";
    private const float UnitHz = 2500;

    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public int Frames, InCall, HaveHeader, Late, Data, Emergency, Calls;
        // my[9], suffix[5], your[9], rpt1[9], rpt2[9], text[21], and two bytes to fill the struct
        public fixed byte Chars[64];
    }

    [LibraryImport(Lib)] private static partial nint dstarrx_new(int rate, float hzPerUnit);
    [LibraryImport(Lib)] private static partial void dstarrx_free(nint r);
    [LibraryImport(Lib)] private static partial void dstarrx_feed(nint r, float* disc, int n);
    [LibraryImport(Lib)] private static partial void dstarrx_signal_lost(nint r);
    [LibraryImport(Lib)] private static partial void dstarrx_status(nint r, Info* info);
    [LibraryImport(Lib)] private static partial int dstarrx_read_voice(nint r, byte* items, int max);

    private nint _handle;
    private readonly AmbeStream _speech = new();
    // The modem works at 24 kHz, five samples to a bit. Noise above the signal is taken off first.
    private readonly FirDecimator _to24k = new(FirDesign.LowPass(48000, 6000, 4000), 2);
    private float[] _buf = [];
    private double _framesAge = 10;
    private int _lastFrames;
    private DstarStatus _status = new();

    /// <summary>Null when the native library is not present: the scanner then has no D-STAR.</summary>
    public static DstarDecoder? TryCreate()
    {
        try
        {
            nint h = dstarrx_new(24000, UnitHz);
            return h == 0 ? null : new DstarDecoder(h);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private DstarDecoder(nint handle) => _handle = handle;

    /// <summary>D-STAR voice frames are arriving (within the last half second).</summary>
    public bool Active => _framesAge < 0.5;
    /// <summary>What the transmission has said; meaningful while <see cref="Active"/>.</summary>
    public DstarStatus Status => _status;
    /// <summary>Voice frames received since the start (or the last <see cref="SignalLost"/>).</summary>
    public int Frames => _lastFrames;
    /// <summary>False when the speech decoder library is missing.</summary>
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
        // In pieces, so the speech is taken out as it comes and the queue in the library never overflows.
        for (int at = 0; at < disc.Length; at += 4800)
        {
            var piece = disc.Slice(at, Math.Min(4800, disc.Length - at));
            fixed (float* p = piece) dstarrx_feed(_handle, p, piece.Length);
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
        dstarrx_status(_handle, &i);
        if (i.Frames != _lastFrames)
        {
            _lastFrames = i.Frames;
            _framesAge = 0;
        }
        byte* c = i.Chars;
        _status = new DstarStatus
        {
            InCall = i.InCall != 0, HaveCallsigns = i.HaveHeader != 0, My = Field(c, 8), Suffix = Field(c + 9, 4),
            Your = Field(c + 14, 8), Repeater = Field(c + 23, 8), Repeater2 = Field(c + 32, 8), Text = Field(c + 41, 20),
            Emergency = i.Emergency != 0, Data = i.Data != 0, VoiceAvailable = _speech.Available, Calls = i.Calls,
        };
        byte* items = stackalloc byte[8 * AmbeStream.DstarItemBytes];
        int n;
        while ((n = dstarrx_read_voice(_handle, items, 8)) > 0)
            for (int k = 0; k < n; k++) _speech.PushDstar(items + k * AmbeStream.DstarItemBytes);
    }

    /// <summary>Decoded speech, 8 kHz, full scale +-1. Returns the samples written.</summary>
    public int ReadVoice(Span<float> voice) => _speech.Read(voice);

    /// <summary>The signal has gone or the channel has changed: end any transmission and start afresh.</summary>
    public void SignalLost()
    {
        dstarrx_signal_lost(_handle);
        _speech.Clear();
        _to24k.Reset();
        _framesAge = 10;
        _lastFrames = 0;
        _status = new DstarStatus();
    }

    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) dstarrx_free(h);
        _speech.Dispose();
    }
}
