// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;

namespace SimpleScanner.Digital;

/// <summary>
/// The P25 Phase 2 receiver in the ss_digital native library (native/digital/p25p2rx.c), as it is: complex baseband
/// in; superframe timing, burst types, corrected MAC PDU octets and the ESS as events; one logical channel's AMBE+2
/// frames out. A thin wrapper for the tests and for <see cref="P25Phase2Decoder"/>; nothing here reads the MAC messages
/// (<see cref="P25Phase2Mac"/> does).
/// One per channel; use from one thread.
/// </summary>
internal sealed unsafe partial class P25Phase2Native : IDisposable
{
    private const string Lib = "ss_digital";

    /// <summary>p25p2rx.c's p25p2rx_event, field for field (p25p2rx_abi says its size).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public int Kind, Slot, Ts, Duid, DuidErrs, Isch, Superframe, Scrambled, Readable, RsErrs, CrcOk, Ok, Len;
        public fixed byte Octets[24];
        public int VoiceErrs;
        public uint Symbol;
        /// <summary>Voice items queued before this event, counted from the start: a burst's own frames come after it.</summary>
        public uint VoiceAt;

        public readonly byte[] Bytes()
        {
            var b = new byte[Len];
            for (int i = 0; i < Len; i++) b[i] = Octets[i];
            return b;
        }
    }

    /// <summary>p25p2rx.c's p25p2rx_stats_t.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Stats
    {
        public int Locked, Syncs, Slots, Bursts, MacOk, MacBad, VoiceFrames, EssOk, HaveSystem;
        public float OffsetHz, ErrRad, Power;
        public int Shifts;
    }

    public const int EvSync = 1, EvLost = 2, EvBurst = 3, EvEss = 4;
    public const int Duid4V = 0, DuidSacchS = 3, Duid2V = 6, DuidFacchS = 9, DuidSacchU = 12, DuidFacchU = 15;
    public const int VoiceItem = 50, ErrsLost = 7, ErrsEnd = 255;
    public const int RsFacch = 0, RsSacch = 1, RsEss = 2;

    [LibraryImport(Lib)] private static partial nint p25p2rx_new(int rate);
    [LibraryImport(Lib)] private static partial void p25p2rx_free(nint r);
    [LibraryImport(Lib)] private static partial void p25p2rx_set_system(nint r, uint wacn, uint sysid, uint nac);
    [LibraryImport(Lib)] private static partial void p25p2rx_set_slot(nint r, int slot);
    [LibraryImport(Lib)] private static partial void p25p2rx_feed_iq(nint r, float* re, float* im, int n);
    [LibraryImport(Lib)] private static partial void p25p2rx_signal_lost(nint r);
    [LibraryImport(Lib)] private static partial int p25p2rx_locked(nint r);
    [LibraryImport(Lib)] private static partial int p25p2rx_read_events(nint r, Event* events, int max);
    [LibraryImport(Lib)] private static partial int p25p2rx_read_voice(nint r, byte* items, int max);
    [LibraryImport(Lib)] private static partial void p25p2rx_stats(nint r, Stats* stats);
    [LibraryImport(Lib)] private static partial int p25p2rx_abi();
    [LibraryImport(Lib)] private static partial int p25p2rx_stats_abi();

    [LibraryImport(Lib)] internal static partial void p25p2_test_scramble(uint wacn, uint sysid, uint nac, byte* seq);
    [LibraryImport(Lib)] internal static partial ulong p25p2_test_isch_encode(int v);
    [LibraryImport(Lib)] internal static partial int p25p2_test_isch_decode(ulong cw, int* errs);
    [LibraryImport(Lib)] internal static partial int p25p2_test_duid_encode(int v);
    [LibraryImport(Lib)] internal static partial int p25p2_test_duid_decode(int cw, int* errs);
    [LibraryImport(Lib)] internal static partial void p25p2_test_rs_encode(int code, byte* info, byte* sent);
    [LibraryImport(Lib)] internal static partial int p25p2_test_rs_decode(int code, byte* sent, byte* erased);
    [LibraryImport(Lib)] internal static partial int p25p2_test_crc12(byte* oct, int nbits);

    private nint _handle;

    /// <summary>The library's event and stats sizes are ours (a library from another p25p2rx.c is refused).</summary>
    public static bool AbiMatches => p25p2rx_abi() == sizeof(Event) && p25p2rx_stats_abi() == sizeof(Stats);

    /// <summary>Null when the native library is missing or of another version.</summary>
    public static P25Phase2Native? TryCreate(int rate = 48000)
    {
        try
        {
            if (!AbiMatches) return null;
            nint h = p25p2rx_new(rate);
            return h == 0 ? null : new P25Phase2Native(h);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private P25Phase2Native(nint handle) => _handle = handle;

    public void SetSystem(uint wacn, uint sysId, uint nac) => p25p2rx_set_system(_handle, wacn, sysId, nac);
    public void SetSlot(int slot) => p25p2rx_set_slot(_handle, slot);
    public void SignalLost() => p25p2rx_signal_lost(_handle);
    public bool Locked => p25p2rx_locked(_handle) != 0;

    public void Feed(ReadOnlySpan<float> re, ReadOnlySpan<float> im)
    {
        fixed (float* r = re, i = im) p25p2rx_feed_iq(_handle, r, i, Math.Min(re.Length, im.Length));
    }

    public List<Event> ReadEvents()
    {
        var list = new List<Event>();
        ReadEvents(list);
        return list;
    }

    /// <summary>The events waiting, in place of what <paramref name="list"/> held (it is reused: read every block, nothing is made).</summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    public void ReadEvents(List<Event> list)
    {
        list.Clear();
        Event* buf = stackalloc Event[32];
        int n;
        while ((n = p25p2rx_read_events(_handle, buf, 32)) > 0)
            for (int i = 0; i < n; i++) list.Add(buf[i]);
    }

    /// <summary>Voice items as dmrrx_read_voice gives them: the 49 bits one per byte, then the error count.</summary>
    public List<byte[]> ReadVoice()
    {
        var list = new List<byte[]>();
        ReadVoice(list);
        return list;
    }

    /// <summary>The voice items waiting, in place of what <paramref name="list"/> held.</summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    public void ReadVoice(List<byte[]> list)
    {
        list.Clear();
        byte* buf = stackalloc byte[16 * VoiceItem];
        int n;
        while ((n = p25p2rx_read_voice(_handle, buf, 16)) > 0)
            for (int i = 0; i < n; i++) list.Add(new ReadOnlySpan<byte>(buf + i * VoiceItem, VoiceItem).ToArray());
    }

    public Stats Statistics
    {
        get
        {
            Stats s;
            p25p2rx_stats(_handle, &s);
            return s;
        }
    }

    public void Dispose()
    {
        if (_handle != 0) p25p2rx_free(_handle);
        _handle = 0;
    }
}
