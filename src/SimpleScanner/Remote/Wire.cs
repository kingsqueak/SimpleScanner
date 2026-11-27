// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleScanner.Dsp;

namespace SimpleScanner.Remote;

// What passes between the window and a receiver in a helper program: one line of JSON for each message,
// down the helper's standard input and back up its standard output.
//
// To the helper:   start, stop, avoid, avoidSlot (each answered by a "reply" with the same Id; avoid's and
//                  avoidSlot's Arg is the channel avoided, or null; avoidSlot's Arg in the message is the slot, 0 to 3)
//                  set (Name is a property of the receiver, or "Source.<property>" of its radio; Arg the value:
//                  MultiMonitor and MultiHangSeconds among them)
//                  setMode, startScan, stopScan, skip, skipSlot (Arg the slot, 0 to 3), clearAvoided,
//                  simulateStall (testing) (not answered)
// From the helper: reply (Error is the reason when it failed; Arg any result; Status a report made after the work)
//                  status (twenty times a second: everything the window shows, Multi Monitor's four slots among it;
//                  what the receiver's processing changes together is one reading, see Receiver.ReadStatus)
//                  failed (the radio stopped by itself; Error says why)
//                  restart (the radio is stopped and wants a new helper: this one is going; Error says why)
internal sealed class WireMessage
{
    public long Id { get; set; }
    public string Op { get; set; } = "";
    public string? Name { get; set; }
    public JsonElement? Arg { get; set; }
    public string? Error { get; set; }
    public ReceiverStatus? Status { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}

internal sealed record ModeArg(Mode Mode, double BandwidthHz);

/// <summary>Everything about a receiver and its radio that can be read, as it stood when <see cref="Seq"/> was the last message handled.</summary>
internal sealed record ReceiverStatus
{
    public long Seq { get; init; }
    public bool IsRunning { get; init; }
    public double FrequencyHz { get; init; }
    public Mode Mode { get; init; }
    public double BandwidthHz { get; init; }
    public double LevelDb { get; init; } = -140;
    public double LevelOffsetDb { get; init; }
    public bool SquelchOpen { get; init; }
    public bool Blocked { get; init; }
    public double SquelchDb { get; init; }
    public bool SquelchEnabled { get; init; }
    public bool AutoSquelch { get; init; }
    public bool Stereo { get; init; }
    public bool StereoEnabled { get; init; }
    public string? DigitalText { get; init; }
    public bool DigitalEncrypted { get; init; }
    public bool DigitalNothingToHear { get; init; }
    public string? AudioDevice { get; init; }
    public double Volume { get; init; }
    public double Pan { get; init; }
    public double ScanHangSeconds { get; init; }
    public double ScanSkipSeconds { get; init; }
    public double? ResumeSeconds { get; init; }
    public TimeSpan? AvoidTime { get; init; }
    public int AvoidedCount { get; init; }
    public bool Scanning { get; init; }
    public bool ScanListening { get; init; }
    public long ScanPasses { get; init; }
    public int ScanWindows { get; init; }
    public ScanChannel? ScanChannel { get; init; }
    public bool MultiMonitor { get; init; }
    public double MultiHangSeconds { get; init; }
    /// <summary>Multi Monitor's slots, four of them (see <see cref="IReceiver.Slots"/>); four empty ones until a report says otherwise.</summary>
    public SlotStatus[] Slots { get; init; } = EmptySlots;
    public long DroppedBlocks { get; init; }

    // The radio
    public string Description { get; init; } = "";
    public string? RadioModel { get; init; }
    public string? RadioSerial { get; init; }
    public double SampleRate { get; init; }
    public double CenterHz { get; init; }
    public double UsableBandwidthHz { get; init; }
    public string[] Antennas { get; init; } = [];
    public string Antenna { get; init; } = "";
    public bool HasBiasT { get; init; }
    public bool BiasT { get; init; }
    public bool HasFmNotch { get; init; }
    public bool FmNotch { get; init; }
    public double Ppm { get; init; }
    public bool AutoGain { get; init; }
    public int GainStep { get; init; }
    public int MaxGainStep { get; init; }
    public int MostGainSteps { get; init; }
    public int ActiveGainStep { get; init; }
    public double GainDb { get; init; }
    public bool Overloaded { get; init; }
    public double LastRetuneMs { get; init; }

    public static readonly SlotStatus[] EmptySlots = [.. Enumerable.Range(0, SlotMixer.Seats).Select(SlotStatus.Empty)];
}
