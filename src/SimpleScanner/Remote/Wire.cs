using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleScanner.Dsp;

namespace SimpleScanner.Remote;

// What passes between the window and a receiver in a helper program: one line of JSON for each message,
// down the helper's standard input and back up its standard output.
//
// To the helper:   start, stop, avoid (each answered by a "reply" with the same Id)
//                  set (Name is a property of the receiver, or "Source.<property>" of its radio; Arg the value)
//                  setMode, startScan, stopScan, skip, clearAvoided (not answered)
// From the helper: reply (Error is the reason when it failed; Arg any result; Status a report made after the work)
//                  status (twenty times a second: everything the window shows)
//                  failed (the radio stopped by itself; Error says why)
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
    public int ActiveGainStep { get; init; }
    public double GainDb { get; init; }
    public bool Overloaded { get; init; }
    public double LastRetuneMs { get; init; }
}
