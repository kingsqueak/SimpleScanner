// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;
using SimpleScanner.Dsp;

namespace SimpleScanner.Digital;

/// <summary>What a P25 signal has told us so far.</summary>
public sealed record P25Status : IDigitalStatus
{
    /// <summary>Network access code: which system or repeater this is.</summary>
    public int Nac { get; init; }
    /// <summary>A trunked system's control channel (data only, continuous).</summary>
    public bool ControlChannel { get; init; }
    public bool InCall { get; init; }
    /// <summary>
    /// The last call ended with its end sent (a Phase 1 terminator; for Phase 2, an end of the slot's MAC), not with the signal
    /// lost. Meaningful while not <see cref="InCall"/>.
    /// </summary>
    public bool Ended { get; init; }
    public int Talkgroup { get; init; }
    /// <summary>The radio transmitting; 0 until the signal has said.</summary>
    public int Source { get; init; }
    public bool Emergency { get; init; }
    /// <summary>Encrypted calls are shown and never played.</summary>
    public bool Encrypted { get; init; }
    public bool NothingToHear => ControlChannel || (InCall && Encrypted);
    public int SystemId { get; init; }
    public int Wacn { get; init; }
    public int Site { get; init; }
    /// <summary>1: FDMA (a channel to itself); 2: TDMA, one of the channel's two slots (<see cref="Slot"/>, 0 or 1).</summary>
    public int Phase { get; init; } = 1;
    public int Slot { get; init; }
    /// <summary>False when the speech decoder library is not installed (Phase 2 speech is AMBE+2): calls are shown, not heard.</summary>
    public bool VoiceAvailable { get; init; } = true;

    public override string ToString()
    {
        string nac = $"NAC {Nac:X3}";
        if (Phase == 2)
        {
            if (!InCall) return $"P25 Phase 2 · {nac}";
            return $"P25 Phase 2 · {nac} · TG {Talkgroup}" + (Source != 0 ? $" · ID {Source}" : "")
                + (Emergency ? " · EMERGENCY" : "") + (Encrypted ? " · encrypted" : !VoiceAvailable ? " · no voice codec" : "");
        }
        if (ControlChannel)
            return $"P25 control channel · {nac}" + (SystemId != 0 ? $" · system {SystemId:X3}" : "")
                + (Wacn != 0 ? $" · WACN {Wacn:X5}" : "") + (Site != 0 ? $" · site {Site}" : "");
        if (!InCall) return $"P25 · {nac}";
        return $"P25 · {nac} · TG {Talkgroup}" + (Source != 0 ? $" · ID {Source}" : "")
            + (Emergency ? " · EMERGENCY" : "") + (Encrypted ? " · encrypted" : "");
    }
}

/// <summary>What a trunking message is (RadioFoundry's P25_TSBK_* kinds, p25_tsbk.h).</summary>
public enum P25TsbkKind
{
    /// <summary>A standard message not read here (or one whose CRC failed): only its octets.</summary>
    Other,
    /// <summary>A manufacturer's own message: only its octets.</summary>
    Manufacturer,
    /// <summary>A voice grant or grant update.</summary>
    Grant,
    /// <summary>A channel identifier update: how channel numbers turn into frequencies.</summary>
    Identifier,
    Network,
    Rfss,
    Adjacent,
    /// <summary>The site's secondary control channels.</summary>
    Secondary,
    SystemService,
    RegistrationResponse,
    AffiliationResponse,
    Deny,
    Queued,
}

/// <summary>
/// One trunking message from a control channel, a single block of a TSDU or a multi-block packet, as numbers.
/// Fields that do not apply to its <see cref="Kind"/> are 0. Channels are as sent: the identifier in the top
/// four bits, the channel number in the low twelve (<see cref="Trunk.TrunkSiteState.TryResolve"/> makes them frequencies).
/// </summary>
public sealed record P25Tsbk
{
    public int Nac { get; init; }
    public int Opcode { get; init; }
    public int Mfid { get; init; }
    public P25TsbkKind Kind { get; init; }
    /// <summary>The block's CRC held (a multi-block message: the packet's). If not, only the octets are set.</summary>
    public bool CrcOk { get; init; }
    /// <summary>It came as a multi-block packet (alternate format).</summary>
    public bool MultiBlock { get; init; }
    public bool Last { get; init; }
    /// <summary>Its arguments are encrypted, so they were not read.</summary>
    public bool Protected { get; init; }
    /// <summary>The block as received, 12 octets; a multi-block message's header block.</summary>
    public byte[] Raw { get; init; } = [];
    /// <summary>A multi-block message's first data block, 12 octets; empty for a single block.</summary>
    public byte[] RawData { get; init; } = [];

    // Grants
    /// <summary>The voice channel granted; a status message's control channel; the first secondary control channel.</summary>
    public int Channel { get; init; }
    /// <summary>The update form's second grant; the second secondary control channel.</summary>
    public int Channel2 { get; init; }
    public int Talkgroup { get; init; }
    public int Talkgroup2 { get; init; }
    /// <summary>The radio that asked for the grant; 0 when the form has none.</summary>
    public int Source { get; init; }
    /// <summary>A unit to unit grant's called radio.</summary>
    public int Target { get; init; }
    public bool UnitToUnit { get; init; }
    /// <summary>The grant's service options, when <see cref="HasServiceOptions"/> (the update forms carry none).</summary>
    public int ServiceOptions { get; init; }
    public bool HasServiceOptions { get; init; }
    public bool Emergency => (ServiceOptions & 0x80) != 0;
    public bool Encrypted => (ServiceOptions & 0x40) != 0;

    // Channel identifiers
    public int IdenId { get; init; }
    /// <summary>The opcode it came in: 0x3D, 0x34 (VHF/UHF) or 0x33 (TDMA).</summary>
    public int IdenForm { get; init; }
    public long BaseHz { get; init; }
    public int SpacingHz { get; init; }
    /// <summary>Where the radios transmit, from the frequency they receive.</summary>
    public long TxOffsetHz { get; init; }
    /// <summary>0 when the form does not say (the TDMA form: its channel type does).</summary>
    public int BandwidthHz { get; init; }
    /// <summary>Channels (slots) per carrier: 1, or the TDMA channel type's.</summary>
    public int Slots { get; init; }
    /// <summary>RadioFoundry's reading of the TDMA form: any channel type but FDMA full rate (type 1).</summary>
    public bool Tdma { get; init; }
    /// <summary>The TDMA form's channel type, else 0.</summary>
    public int ChannelType { get; init; }

    // Status broadcasts and secondary control channels
    public int Wacn { get; init; }
    public int SystemId { get; init; }
    public int Rfss { get; init; }
    public int Site { get; init; }
    public int Lra { get; init; }
    /// <summary>The four bits ahead of the system ID (adjacent site: conventional, failure, valid, active network).</summary>
    public int SiteFlags { get; init; }
    public int ServiceClass { get; init; }
    public int ServiceClass2 { get; init; }

    // System service broadcast
    public int ServicesAvailable { get; init; }
    public int ServicesSupported { get; init; }
    public int RequestPriority { get; init; }
}

/// <summary>
/// P25 Phase 1 through the ss_digital native library (native/digital: RadioFoundry's soft modem, framer, P25 layer and
/// IMBE decoder). One made by <see cref="TryCreate"/> takes an FM discriminator at 48 kHz (<see cref="Feed"/>); one
/// made by <see cref="TryCreateIq"/> takes the channel's complex baseband at 48 kHz (<see cref="FeedBaseband"/>) through
/// a differential demodulator (native/digital/cqpsk.c), which receives the CQPSK (LSM) signals of simulcast sites as
/// well as C4FM. Read the status and the decoded speech (8 kHz). One per channel; use from one thread.
/// </summary>
public sealed unsafe partial class P25Decoder : IDisposable
{
    private const string Lib = "ss_digital";
    private const float UnitHz = 2500;

    // p25rx.c's p25rx_event, field for field. The library says its size (p25rx_abi): one of another size is refused.
    [StructLayout(LayoutKind.Sequential)]
    private struct Event
    {
        public int Kind, Nac, Duid, NidErrs, Late, HaveLc, HaveAlg, Lcf, Mfid, Emergency, Tgid, Src, Dst, Algid, Kid,
            TsbkOpcode, TsbkMfid, TsbkKind, Wacn, Sysid, Rfss, Site;
        public fixed byte Raw[12];
        public fixed byte RawData[12];
        public int CrcOk, Mbt, Last, Prot, Ch, Ch2, Tg, Tg2, Svc, HasSvc, Unit, IdenId, IdenForm;
        public uint IdenBase;
        public int IdenSpacing, IdenTxOff, IdenBw, IdenSlots, IdenTdma, IdenType, Lra, SiteFlags, Ssc, Ssc2,
            SvcAvail, SvcSupp, SvcPrio;
    }

    private const int EvUnit = 1, EvCallStart = 2, EvCallUpdate = 3, EvCallEnd = 4, EvTsbk = 5;
    private const int DuidTdu = 3, DuidTdulc = 0xF, DuidTsdu = 7, AlgidClear = 0x80, TsbkNet = 4, TsbkRfss = 5, IdenUp = 0x3D;
    // Trunking messages kept for ReadTsbks; past this the oldest go.
    private const int TsbkQueue = 512;

    [LibraryImport(Lib)] private static partial nint p25rx_new(int rate, float hzPerUnit);
    [LibraryImport(Lib)] private static partial void p25rx_free(nint r);
    [LibraryImport(Lib)] private static partial void p25rx_feed(nint r, float* disc, int n);
    [LibraryImport(Lib)] private static partial void p25rx_signal_lost(nint r);
    [LibraryImport(Lib)] private static partial int p25rx_locked(nint r);
    [LibraryImport(Lib)] private static partial int p25rx_read_pcm(nint r, short* pcm, int max);
    [LibraryImport(Lib)] private static partial int p25rx_read_events(nint r, Event* events, int max);
    [LibraryImport(Lib)] private static partial int p25rx_abi();
    [LibraryImport(Lib)] private static partial nint p25rx_new_iq(int rate);
    [LibraryImport(Lib)] private static partial void p25rx_feed_iq(nint r, float* re, float* im, int n);
    [LibraryImport(Lib)] private static partial void p25rx_iq_stats(nint r, float* offsetHz, float* errRad, float* power);

    private nint _handle;
    // The modem works at 24 kHz, five samples to a symbol. Noise above the signal is taken off first.
    private readonly FirDecimator _to24k = new(FirDesign.LowPass(48000, 6000, 4000), 2);
    private float[] _buf = [];
    private double _unitsAge = 10;
    private P25Status _status = new();
    private readonly Queue<P25Tsbk> _tsbks = new();
    // The data unit last received: a call's end that follows a terminator was sent, one that follows anything else is the
    // signal lost.
    private int _lastDuid = -1;

    /// <summary>Null when the native library is not present: the scanner then has no P25.</summary>
    public static P25Decoder? TryCreate()
    {
        try
        {
            // A library built from another version of p25rx.c would hand over events of another shape.
            if (!AbiMatches) return null;
            nint h = p25rx_new(24000, UnitHz);
            return h == 0 ? null : new P25Decoder(h);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>The size of the native library's event, and of ours: they must be the same.</summary>
    internal static int NativeEventSize => p25rx_abi();
    internal static int EventSize => sizeof(Event);
    private static bool AbiMatches => NativeEventSize == EventSize;

    /// <summary>
    /// One for complex baseband instead (<see cref="FeedBaseband"/>), 48 kHz, centred on the channel. Null when the
    /// native library is not present.
    /// </summary>
    public static P25Decoder? TryCreateIq()
    {
        try
        {
            if (!AbiMatches) return null;
            nint h = p25rx_new_iq(IqRate);
            return h == 0 ? null : new P25Decoder(h) { IsIq = true };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private const int IqRate = 48000;

    private P25Decoder(nint handle) => _handle = handle;

    /// <summary>Made by <see cref="TryCreateIq"/>: it takes complex baseband, not the discriminator.</summary>
    public bool IsIq { get; private init; }

    /// <summary>P25 data units are arriving (within the last half second).</summary>
    public bool Active => _unitsAge < 0.5;
    /// <summary>What the signal has said; meaningful while <see cref="Active"/>.</summary>
    public P25Status Status => _status;
    /// <summary>Data units received since the start.</summary>
    public long Units { get; private set; }
    /// <summary>Trunking messages received with a good CRC since the start.</summary>
    public long GoodTsbks { get; private set; }

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

    /// <summary>
    /// The channel's complex samples at 48 kHz (real and imaginary parts), centred on it, at any level, for one made by
    /// <see cref="TryCreateIq"/>.
    /// </summary>
    public void FeedBaseband(ReadOnlySpan<float> re, ReadOnlySpan<float> im)
    {
        int n = Math.Min(re.Length, im.Length);
        fixed (float* r = re)
        fixed (float* i = im)
            p25rx_feed_iq(_handle, r, i, n);
        _unitsAge += n / (double)IqRate;
        ReadEvents();
    }

    /// <summary>
    /// What the differential front end measures (zeros for a discriminator one): the carrier's offset from the channel
    /// in Hz, the mean distance of the symbols' phase steps from the nearest of the four in radians (near 0 for a clean
    /// signal, about 0.35 for noise), and the filtered signal's mean power.
    /// </summary>
    public (double OffsetHz, double PhaseError, double Power) IqStats
    {
        get
        {
            float o, e, p;
            p25rx_iq_stats(_handle, &o, &e, &p);
            return (o, e, p);
        }
    }

    /// <summary>Samples already at 24 kHz, 1.0 = 2.5 kHz deviation. For tests.</summary>
    internal void Feed24k(ReadOnlySpan<float> disc)
    {
        fixed (float* p = disc) p25rx_feed(_handle, p, disc.Length);
    }

    // The events are written by the library before they are read: the buffer need not be cleared first.
    [System.Runtime.CompilerServices.SkipLocalsInit]
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
                        _lastDuid = e.Duid;
                        // A different system on the channel: forget the last one's details.
                        if (e.Nac != _status.Nac) _status = new P25Status { Nac = e.Nac };
                        if (e.Duid == DuidTsdu && !_status.ControlChannel) _status = _status with { ControlChannel = true, InCall = false };
                        break;
                    case EvCallStart:
                    case EvCallUpdate:
                        _status = _status with
                        {
                            Nac = e.Nac, InCall = true, Ended = false, ControlChannel = false, Talkgroup = e.Tgid,
                            Source = e.HaveLc != 0 ? e.Src : _status.InCall ? _status.Source : 0,
                            Emergency = e.Emergency != 0, Encrypted = e.HaveAlg != 0 && e.Algid != AlgidClear,
                        };
                        break;
                    case EvCallEnd:
                        _status = _status with { InCall = false, Ended = _lastDuid is DuidTdu or DuidTdulc };
                        break;
                    case EvTsbk:
                        if (e.CrcOk != 0) GoodTsbks++;
                        if (_tsbks.Count == TsbkQueue) _tsbks.Dequeue();
                        _tsbks.Enqueue(ToTsbk(ref e));
                        if (e.TsbkKind == TsbkNet) _status = _status with { ControlChannel = true, Wacn = e.Wacn, SystemId = e.Sysid };
                        else if (e.TsbkKind == TsbkRfss) _status = _status with { ControlChannel = true, SystemId = e.Sysid, Site = e.Site };
                        break;
                }
            }
        }
    }

    /// <summary>Trunking messages are waiting to be read.</summary>
    public bool HasTsbks => _tsbks.Count > 0;

    /// <summary>Forget the trunking messages waiting to be read.</summary>
    public void ClearTsbks() => _tsbks.Clear();

    /// <summary>The trunking messages received since the last call, oldest first (at most the last 512).</summary>
    public List<P25Tsbk> ReadTsbks()
    {
        var list = new List<P25Tsbk>(_tsbks);
        _tsbks.Clear();
        return list;
    }

    private static P25Tsbk ToTsbk(ref Event e)
    {
        var raw = new byte[12];
        for (int i = 0; i < 12; i++) raw[i] = e.Raw[i];
        byte[] data = [];
        if (e.Mbt != 0)
        {
            data = new byte[12];
            for (int i = 0; i < 12; i++) data[i] = e.RawData[i];
        }
        // The 0x3D form's transmit offset is in 250 kHz steps, the other two forms' in channel spacings.
        int spacing = e.IdenSpacing * 125;
        return new P25Tsbk
        {
            Nac = e.Nac, Opcode = e.TsbkOpcode, Mfid = e.TsbkMfid, Kind = (P25TsbkKind)e.TsbkKind, CrcOk = e.CrcOk != 0,
            MultiBlock = e.Mbt != 0, Last = e.Last != 0, Protected = e.Prot != 0, Raw = raw, RawData = data,
            Channel = e.Ch, Channel2 = e.Ch2, Talkgroup = e.Tg, Talkgroup2 = e.Tg2, Source = e.Src, Target = e.Dst,
            UnitToUnit = e.Unit != 0, ServiceOptions = e.Svc, HasServiceOptions = e.HasSvc != 0,
            IdenId = e.IdenId, IdenForm = e.IdenForm, BaseHz = e.IdenBase * 5L, SpacingHz = spacing,
            TxOffsetHz = (long)e.IdenTxOff * (e.IdenForm == IdenUp ? 250_000 : spacing), BandwidthHz = e.IdenBw * 125,
            Slots = e.IdenSlots, Tdma = e.IdenTdma != 0, ChannelType = e.IdenType,
            Wacn = e.Wacn, SystemId = e.Sysid, Rfss = e.Rfss, Site = e.Site, Lra = e.Lra, SiteFlags = e.SiteFlags,
            ServiceClass = e.Ssc, ServiceClass2 = e.Ssc2,
            ServicesAvailable = e.SvcAvail, ServicesSupported = e.SvcSupp, RequestPriority = e.SvcPrio,
        };
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
        _lastDuid = -1;
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
