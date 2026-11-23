namespace SimpleScanner.Digital;

/// <summary>
/// What a digital voice signal has said about itself, whatever its kind. ToString gives the
/// line for the display.
/// </summary>
public interface IDigitalStatus
{
    /// <summary>Encrypted calls are shown and never played.</summary>
    bool Encrypted { get; }
    /// <summary>
    /// The signal is there but carries nothing to listen to now: a control or data channel, a
    /// repeater idling between calls, an encrypted call. A scan does not wait on it.
    /// </summary>
    bool NothingToHear { get; }
}
