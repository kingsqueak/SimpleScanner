using SimpleScanner.Digital;
using SimpleScanner.Dsp;
using SimpleScanner.Radio;

namespace SimpleScanner;

/// <summary>
/// One receiver as the window sees it: a radio, tuned and demodulated, with its squelch, its sound and its
/// scan. <see cref="Receiver"/> is the real one, in this program. <see cref="Remote.RemoteReceiver"/> stands
/// for one running in a helper program of its own, which is how a second radio is run: the SDRplay
/// software lets one program open one radio only.
/// </summary>
public interface IReceiver : IDisposable
{
    IIqSource Source { get; }
    bool IsRunning { get; }
    /// <summary>The radio stopped by itself. May be raised on any thread.</summary>
    event Action<string>? Failed;

    void Start();
    void Stop();

    double FrequencyHz { get; set; }
    Mode Mode { get; }
    double BandwidthHz { get; }
    void SetMode(Mode mode, double bandwidthHz);

    double LevelDb { get; }
    double LevelOffsetDb { get; set; }
    bool SquelchOpen { get; }
    bool Blocked { get; }
    double SquelchDb { get; set; }
    bool SquelchEnabled { get; set; }
    bool AutoSquelch { get; set; }
    bool Stereo { get; }
    bool StereoEnabled { get; set; }
    IDigitalStatus? Digital { get; }

    string? AudioDevice { get; set; }
    double Volume { get; set; }
    /// <summary>Where this receiver's sound sits between the left speaker (-1) and the right (1); 0 is the middle.</summary>
    double Pan { get; set; }

    double ScanHangSeconds { get; set; }
    double ScanSkipSeconds { get; set; }
    double? ResumeSeconds { get; set; }
    TimeSpan? AvoidTime { get; set; }
    int AvoidedCount { get; }
    bool Scanning { get; }
    bool ScanListening { get; }
    long ScanPasses { get; }
    int ScanWindows { get; }
    ScanChannel? ScanChannel { get; }
    void StartScan(IReadOnlyList<ScanChannel> channels);
    void StopScan();
    void SkipChannel();
    ScanChannel? AvoidChannel();
    void ClearAvoided();

    /// <summary>Multi Monitor: a scan holds up to four active channels of a window at once, each in a slot of its own.</summary>
    bool MultiMonitor { get; set; }
    /// <summary>Multi Monitor: how long a slot keeps its channel after the signal has dropped.</summary>
    double MultiHangSeconds { get; set; }
    /// <summary>The four slots, always four entries: in a Multi Monitor scan each that holds a channel; otherwise the first is the one channel.</summary>
    IReadOnlyList<SlotStatus> Slots { get; }
    /// <summary>Let go the channel this slot (0 to 3) holds and pass it by for a while; the other slots carry on.</summary>
    void SkipSlot(int slot);
    /// <summary>Let go the channel this slot (0 to 3) holds and avoid it; the channel avoided, or null if the slot held none.</summary>
    ScanChannel? AvoidSlot(int slot);

    /// <summary>Blocks of samples thrown away because the processing had fallen behind (since the start).</summary>
    long DroppedBlocks { get; }
}
