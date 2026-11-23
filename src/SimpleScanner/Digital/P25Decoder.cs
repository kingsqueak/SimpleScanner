using System.Runtime.InteropServices;
using SimpleScanner.Dsp;

namespace SimpleScanner.Digital;

/// <summary>What a P25 signal has told us so far.</summary>
public sealed record P25Status
{
    /// <summary>Network access code: which system or repeater this is.</summary>
    public int Nac { get; init; }
    /// <summary>A trunked system's control channel (data only, continuous).</summary>
    public bool ControlChannel { get; init; }
    public bool InCall { get; init; }
    public int Talkgroup { get; init; }
    /// <summary>The radio transmitting; 0 until the signal has said.</summary>
    public int Source { get; init; }
    public bool Emergency { get; init; }
    /// <summary>Encrypted calls are shown and never played.</summary>
    public bool Encrypted { get; init; }
    public int SystemId { get; init; }
    public int Wacn { get; init; }
    public int Site { get; init; }

    public override string ToString()
    {
        string nac = $"NAC {Nac:X3}";
        if (ControlChannel)
            return $"P25 control channel · {nac}" + (SystemId != 0 ? $" · system {SystemId:X3}" : "")
                + (Wacn != 0 ? $" · WACN {Wacn:X5}" : "") + (Site != 0 ? $" · site {Site}" : "");
        if (!InCall) return $"P25 · {nac}";
        return $"P25 · {nac} · TG {Talkgroup}" + (Source != 0 ? $" · ID {Source}" : "")
            + (Emergency ? " · EMERGENCY" : "") + (Encrypted ? " · encrypted" : "");
    }
}

/// <summary>
/// P25 Phase 1 from an FM discriminator, through the ss_digital native library
/// (native/digital: RadioFoundry's soft modem, framer, P25 layer and IMBE decoder).
/// Feed it the discriminator at 48 kHz; read the status and the decoded speech (8 kHz).
/// One per channel; use from one thread.
/// </summary>
public sealed unsafe partial class P25Decoder : IDisposable
{
    private const string Lib = "ss_digital";
    private const float UnitHz = 2500;

    [StructLayout(LayoutKind.Sequential)]
    private struct Event
    {
        public int Kind, Nac, Duid, NidErrs, Late, HaveLc, HaveAlg, Lcf, Mfid, Emergency, Tgid, Src, Dst, Algid, Kid,
            TsbkOpcode, TsbkMfid, TsbkKind, Wacn, Sysid, Rfss, Site;
    }

    private const int EvUnit = 1, EvCallStart = 2, EvCallUpdate = 3, EvCallEnd = 4, EvTsbk = 5;
    private const int DuidTsdu = 7, AlgidClear = 0x80, TsbkNet = 4, TsbkRfss = 5;

    [LibraryImport(Lib)] private static partial nint p25rx_new(int rate, float hzPerUnit);
    [LibraryImport(Lib)] private static partial void p25rx_free(nint r);
    [LibraryImport(Lib)] private static partial void p25rx_feed(nint r, float* disc, int n);
    [LibraryImport(Lib)] private static partial void p25rx_signal_lost(nint r);
    [LibraryImport(Lib)] private static partial int p25rx_locked(nint r);
    [LibraryImport(Lib)] private static partial int p25rx_read_pcm(nint r, short* pcm, int max);
    [LibraryImport(Lib)] private static partial int p25rx_read_events(nint r, Event* events, int max);

    private nint _handle;
    // The modem works at 24 kHz, five samples to a symbol. Noise above the signal is taken off first.
    private readonly FirDecimator _to24k = new(FirDesign.LowPass(48000, 6000, 4000), 2);
    private float[] _buf = [];
    private double _unitsAge = 10;
    private P25Status _status = new();

    /// <summary>Null when the native library is not present: the scanner then has no P25.</summary>
    public static P25Decoder? TryCreate()
    {
        try
        {
            nint h = p25rx_new(24000, UnitHz);
            return h == 0 ? null : new P25Decoder(h);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private P25Decoder(nint handle) => _handle = handle;

    /// <summary>P25 data units are arriving (within the last half second).</summary>
    public bool Active => _unitsAge < 0.5;
    /// <summary>What the signal has said; meaningful while <see cref="Active"/>.</summary>
    public P25Status Status => _status;
    /// <summary>Data units received since the start.</summary>
    public long Units { get; private set; }

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
        Feed24k(_buf.AsSpan(0, n));
        _unitsAge += disc48k.Length / 48000.0;
        ReadEvents();
    }

    /// <summary>Samples already at 24 kHz, 1.0 = 2.5 kHz deviation. For tests.</summary>
    internal void Feed24k(ReadOnlySpan<float> disc)
    {
        fixed (float* p = disc) p25rx_feed(_handle, p, disc.Length);
    }

    internal void ReadEvents()
    {
        var events = stackalloc Event[64];
        int n;
        while ((n = p25rx_read_events(_handle, events, 64)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                ref Event e = ref events[i];
                switch (e.Kind)
                {
                    case EvUnit:
                        Units++;
                        _unitsAge = 0;
                        // A different system on the channel: forget the last one's details.
                        if (e.Nac != _status.Nac) _status = new P25Status { Nac = e.Nac };
                        if (e.Duid == DuidTsdu && !_status.ControlChannel) _status = _status with { ControlChannel = true, InCall = false };
                        break;
                    case EvCallStart:
                    case EvCallUpdate:
                        _status = _status with
                        {
                            Nac = e.Nac, InCall = true, ControlChannel = false, Talkgroup = e.Tgid,
                            Source = e.HaveLc != 0 ? e.Src : _status.InCall ? _status.Source : 0,
                            Emergency = e.Emergency != 0, Encrypted = e.HaveAlg != 0 && e.Algid != AlgidClear,
                        };
                        break;
                    case EvCallEnd:
                        _status = _status with { InCall = false };
                        break;
                    case EvTsbk:
                        if (e.TsbkKind == TsbkNet) _status = _status with { ControlChannel = true, Wacn = e.Wacn, SystemId = e.Sysid };
                        else if (e.TsbkKind == TsbkRfss) _status = _status with { ControlChannel = true, SystemId = e.Sysid, Site = e.Site };
                        break;
                }
            }
        }
    }

    /// <summary>Decoded speech, 8 kHz, full scale +-1. Returns the samples written.</summary>
    public int ReadVoice(Span<float> voice)
    {
        const int chunk = 1024;
        short* pcm = stackalloc short[chunk];
        int total = 0;
        while (total < voice.Length)
        {
            int n = p25rx_read_pcm(_handle, pcm, Math.Min(chunk, voice.Length - total));
            if (n == 0) break;
            for (int i = 0; i < n; i++) voice[total + i] = pcm[i] / 32768f;
            total += n;
        }
        return total;
    }

    /// <summary>The signal has gone or the channel has changed: end any call and start afresh.</summary>
    public void SignalLost()
    {
        p25rx_signal_lost(_handle);
        _to24k.Reset();
        _unitsAge = 10;
        _status = new P25Status();
        short* pcm = stackalloc short[1024];
        while (p25rx_read_pcm(_handle, pcm, 1024) > 0) { }
    }

    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) p25rx_free(h);
    }
}
