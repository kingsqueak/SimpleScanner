// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;
using System.Text;
using SimpleScanner.Dsp;

namespace SimpleScanner.Digital;

/// <summary>Who is sending a DMR signal: it decides what the signal says about its time slots.</summary>
public enum DmrSource { Unknown, Repeater, Mobile, Direct }

/// <summary>What one of DMR's two time slots is carrying.</summary>
public enum DmrActivity { Idle, Voice, Data, Control }

/// <summary>
/// What a DMR signal has told us so far. A DMR channel carries two conversations at once in
/// alternating 30 ms time slots; this describes the slot being listened to (the first with a
/// call that can be played), and says when the other one is busy too.
/// </summary>
public sealed record DmrStatus : IDigitalStatus
{
    public DmrSource From { get; init; }
    /// <summary>Color code, 0 to 15: tells co-channel systems apart. -1 until the signal has said.</summary>
    public int ColorCode { get; init; } = -1;
    /// <summary>Time slot 1 or 2; 0 when the signal does not number its slots (a mobile heard directly).</summary>
    public int Slot { get; init; }
    public DmrActivity Activity { get; init; }
    public bool InCall => Activity == DmrActivity.Voice;
    /// <summary>The call's addresses have arrived (they are repeated through the call).</summary>
    public bool HaveAddresses { get; init; }
    /// <summary>A talkgroup call; otherwise a call to one radio.</summary>
    public bool Group { get; init; }
    /// <summary>The talkgroup, or the radio called.</summary>
    public int Destination { get; init; }
    /// <summary>The radio transmitting.</summary>
    public int Source { get; init; }
    public bool Emergency { get; init; }
    public bool Encrypted { get; init; }
    public bool NothingToHear => !InCall || Encrypted;
    /// <summary>The name the transmitting radio sends with its call, when it sends one.</summary>
    public string Alias { get; init; } = "";
    /// <summary>The other time slot has a voice call as well.</summary>
    public bool OtherSlotBusy { get; init; }
    /// <summary>False when the speech decoder library is not installed: calls are shown, not heard.</summary>
    public bool VoiceAvailable { get; init; } = true;
    /// <summary>Voice calls seen on this slot since the signal was found.</summary>
    public int Calls { get; init; }

    public override string ToString()
    {
        var s = new StringBuilder("DMR");
        if (ColorCode >= 0) s.Append($" · CC {ColorCode}");
        if (Slot != 0 && Activity != DmrActivity.Idle) s.Append($" · TS{Slot}");
        switch (Activity)
        {
            case DmrActivity.Idle: s.Append(" · idle"); break;
            case DmrActivity.Data: s.Append(" · data"); break;
            case DmrActivity.Control: s.Append(" · control"); break;
            default:
                if (!HaveAddresses) s.Append(" · voice");
                else s.Append(Group ? $" · TG {Destination}" : $" · to {Destination}").Append($" · ID {Source}");
                if (Alias.Length > 0) s.Append($" · {Alias}");
                if (Emergency) s.Append(" · EMERGENCY");
                if (Encrypted) s.Append(" · encrypted");
                else if (!VoiceAvailable) s.Append(" · no voice codec");
                if (OtherSlotBusy) s.Append(Slot != 0 ? $" · TS{3 - Slot} busy" : " · other slot busy");
                break;
        }
        return s.ToString();
    }
}

/// <summary>
/// DMR (Tier II conventional) from an FM discriminator, through the ss_digital native library
/// (native/digital: RadioFoundry's soft modem, framer and DMR error correction, with the slot
/// and call logic in dmrrx.c) and, for the speech, <see cref="AmbeVocoder"/>.
/// Feed it the discriminator at 48 kHz; read the status and the decoded speech (8 kHz).
/// One per channel; use from one thread.
/// </summary>
public sealed unsafe partial class DmrDecoder : IDisposable
{
    private const string Lib = "ss_digital";
    private const float UnitHz = 2500;
    private const int VoiceItem = 50, ErrsEnd = 255;

    [StructLayout(LayoutKind.Sequential)]
    private struct SlotInfo
    {
        public int Slot, State, Cc, HaveLc, Group, Dst, Src, Emergency, Encrypted, Late, Calls;
        public fixed byte Alias[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public int Bursts, Source, Play;
        public SlotInfo S0, S1;
    }

    [LibraryImport(Lib)] private static partial nint dmrrx_new(int rate, float hzPerUnit);
    [LibraryImport(Lib)] private static partial void dmrrx_free(nint r);
    [LibraryImport(Lib)] private static partial void dmrrx_feed(nint r, float* disc, int n);
    [LibraryImport(Lib)] private static partial void dmrrx_signal_lost(nint r);
    [LibraryImport(Lib)] private static partial void dmrrx_status(nint r, Info* info);
    [LibraryImport(Lib)] private static partial int dmrrx_read_voice(nint r, byte* items, int max);

    private nint _handle;
    private readonly AmbeVocoder? _vocoder = AmbeVocoder.TryCreate();
    // The modem works at 24 kHz, five samples to a symbol. Noise above the signal is taken off first.
    private readonly FirDecimator _to24k = new(FirDesign.LowPass(48000, 6000, 4000), 2);
    private float[] _buf = [];
    private readonly short[] _pcm = new short[8000 * 3];
    private int _pcmHead, _pcmCount;
    private double _burstsAge = 10;
    private int _lastBursts;
    private DmrStatus _status = new();

    /// <summary>Null when the native library is not present: the scanner then has no DMR.</summary>
    public static DmrDecoder? TryCreate()
    {
        try
        {
            nint h = dmrrx_new(24000, UnitHz);
            return h == 0 ? null : new DmrDecoder(h);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private DmrDecoder(nint handle) => _handle = handle;

    /// <summary>DMR bursts are arriving (within the last half second).</summary>
    public bool Active => _burstsAge < 0.5;
    /// <summary>What the signal has said; meaningful while <see cref="Active"/>.</summary>
    public DmrStatus Status => _status;
    /// <summary>Bursts read since the start (or the last <see cref="SignalLost"/>).</summary>
    public int Bursts => _lastBursts;
    /// <summary>False when the speech decoder library is missing.</summary>
    public bool VoiceAvailable => _vocoder is not null;

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
        _burstsAge += disc48k.Length / 48000.0;
        Feed24k(_buf.AsSpan(0, n));
    }

    /// <summary>Samples already at 24 kHz, 1.0 = 2.5 kHz deviation. For tests (and the end of <see cref="Feed"/>).</summary>
    internal void Feed24k(ReadOnlySpan<float> disc)
    {
        // In pieces, so the speech is taken out as it comes and the queue in the library never overflows.
        for (int at = 0; at < disc.Length; at += 4800)
        {
            var piece = disc.Slice(at, Math.Min(4800, disc.Length - at));
            fixed (float* p = piece) dmrrx_feed(_handle, p, piece.Length);
            Poll();
        }
    }

    private void Poll()
    {
        Info info;
        dmrrx_status(_handle, &info);
        if (info.Bursts != _lastBursts)
        {
            _lastBursts = info.Bursts;
            _burstsAge = 0;
        }
        // The slot to describe: the one being played, else one with a call, else one doing anything.
        SlotInfo* slots = &info.S0;
        int show = info.Play;
        if (show < 0) show = slots[0].State == 1 ? 0 : slots[1].State == 1 ? 1 : slots[0].State != 0 ? 0 : slots[1].State != 0 ? 1 : slots[0].Cc >= 0 ? 0 : 1;
        SlotInfo* s = &slots[show];
        int len = 0;
        while (len < 31 && s->Alias[len] != 0) len++;
        string alias = len == 0 ? "" : len == _status.Alias.Length && Matches(_status.Alias, s->Alias) ? _status.Alias : Encoding.ASCII.GetString(s->Alias, len).Trim();
        _status = new DmrStatus
        {
            From = (DmrSource)info.Source, ColorCode = s->Cc >= 0 ? s->Cc : slots[show ^ 1].Cc, Slot = s->Slot, Activity = (DmrActivity)s->State,
            HaveAddresses = s->HaveLc != 0, Group = s->Group != 0, Destination = s->Dst, Source = s->Src,
            Emergency = s->Emergency != 0, Encrypted = s->Encrypted != 0, Alias = alias,
            OtherSlotBusy = s->State == 1 && slots[show ^ 1].State == 1, VoiceAvailable = _vocoder is not null, Calls = s->Calls,
        };

        byte* items = stackalloc byte[16 * VoiceItem];
        Span<short> frame = stackalloc short[AmbeVocoder.FrameSamples];
        int n;
        while ((n = dmrrx_read_voice(_handle, items, 16)) > 0)
        {
            if (_vocoder is null) continue;
            for (int i = 0; i < n; i++)
            {
                byte* item = items + i * VoiceItem;
                if (item[AmbeVocoder.ParameterBits] == ErrsEnd)
                {
                    _vocoder.Reset();
                    continue;
                }
                _vocoder.Decode(new ReadOnlySpan<byte>(item, AmbeVocoder.ParameterBits), item[AmbeVocoder.ParameterBits], frame);
                foreach (short v in frame)
                {
                    if (_pcmCount == _pcm.Length)               // full: the oldest goes
                    {
                        _pcmHead = (_pcmHead + 1) % _pcm.Length;
                        _pcmCount--;
                    }
                    _pcm[(_pcmHead + _pcmCount++) % _pcm.Length] = v;
                }
            }
        }
    }

    private static bool Matches(string have, byte* ascii)
    {
        for (int i = 0; i < have.Length; i++)
            if (have[i] != ascii[i]) return false;
        return true;
    }

    /// <summary>Decoded speech, 8 kHz, full scale +-1. Returns the samples written.</summary>
    public int ReadVoice(Span<float> voice)
    {
        int n = Math.Min(voice.Length, _pcmCount);
        for (int i = 0; i < n; i++)
        {
            voice[i] = _pcm[_pcmHead] / 32768f;
            _pcmHead = (_pcmHead + 1) % _pcm.Length;
        }
        _pcmCount -= n;
        return n;
    }

    /// <summary>The signal has gone or the channel has changed: end any call and start afresh.</summary>
    public void SignalLost()
    {
        dmrrx_signal_lost(_handle);
        _vocoder?.Reset();
        _to24k.Reset();
        _burstsAge = 10;
        _lastBursts = 0;
        _pcmHead = _pcmCount = 0;
        _status = new DmrStatus();
    }

    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) dmrrx_free(h);
        _vocoder?.Dispose();
    }
}
