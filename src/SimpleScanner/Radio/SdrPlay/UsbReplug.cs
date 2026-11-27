// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Runtime.InteropServices;

namespace SimpleScanner.Radio.SdrPlay;

/// <summary>
/// A software unplug and replug of a radio (native/audio/ss_usb.c), for a radio whose USB data pipe
/// has jammed. Only works while no program is streaming from the radio: SDRplay's service holds it
/// for exclusive use until then. macOS only; elsewhere it does nothing and says so.
/// </summary>
internal static partial class UsbReplug
{
    public const int SdrPlayVendor = 0x1df7;

    [LibraryImport("ss_audio", EntryPoint = "ss_usb_reenumerate", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Reenumerate(int vendor, string? serial);

    [LibraryImport("ss_audio", EntryPoint = "ss_usb_state", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UsbState(int vendor, string? serial);

    public enum State { Unknown, Gone, Free, Held }

    /// <summary>Whether the radio is on USB and whether SDRplay's service holds it, read from macOS, not
    /// from the API (which must be left alone while the service recovers from a jammed radio).</summary>
    public static State StateOf(string? serial)
    {
        try
        {
            return UsbState(SdrPlayVendor, serial) switch { -1 => State.Gone, 0 => State.Free, 1 => State.Held, _ => State.Unknown };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return State.Unknown; }
    }

    /// <summary>Waits until the radio's state is one of those given, or the time is up; says the state it ended in.</summary>
    public static State WaitFor(string? serial, TimeSpan longest, params State[] wanted)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var now = StateOf(serial);
            if (now == State.Unknown || wanted.Contains(now) || clock.Elapsed >= longest) return now;
            Thread.Sleep(250);
        }
    }

    private const int ExclusiveAccess = unchecked((int)0xe00002c5);

    /// <summary>
    /// Replugs every SDRplay radio with this serial number; says how it went. While another program has the
    /// radio for its own (the service, still letting go of it) it is tried again each second, up to <paramref name="longest"/>.
    /// </summary>
    public static (bool Done, string Text) Replug(string? serial, TimeSpan longest = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int r, tries = 0;
        try
        {
            while (true)
            {
                r = Reenumerate(SdrPlayVendor, serial);
                tries++;
                if (r != ExclusiveAccess || clock.Elapsed >= longest) break;
                Thread.Sleep(1000);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return (false, "not available in this build");
        }
        string after = tries > 1 ? $" (try {tries})" : "";
        return r switch
        {
            0 => (true, "done" + after),
            1 => (false, "radio not found on USB"),
            -1 => (false, "not possible on this system"),
            ExclusiveAccess => (false, $"refused: still held by the SDRplay service{after}"),
            _ => (false, $"refused (0x{r:x8}){after}"),
        };
    }
}
