namespace SimpleScanner.Dsp;

/// <summary>
/// Carrier squelch: open while the channel level is above a threshold. It opens after the
/// level has been above for a moment and closes after it has been below (by a small margin)
/// for a little longer, so noise spikes and fades do not chatter it.
///
/// With <see cref="Auto"/> it is a noise squelch as well: the level must be there and the signal
/// must also be clean (see <see cref="ChannelReceiver.Roughness"/>), which noise is not however
/// strong it is. So one level setting serves a quiet band and a noisy one.
/// </summary>
public sealed class Squelch
{
    private const double HysteresisDb = 2, OpenAfter = 0.015, CloseAfter = 0.12;
    private double _above, _below;

    public double ThresholdDb { get; set; } = -100;
    /// <summary>With the squelch off, audio always passes.</summary>
    public bool Enabled { get; set; } = true;
    public bool IsOpen { get; private set; }
    /// <summary>Also keep shut on noise that is above the level.</summary>
    public bool Auto { get; set; }
    /// <summary>What was last measured is noise by the roughness test (whatever its level).</summary>
    public bool Noisy { get; private set; }
    // To open, the roughness must be this much of the limit or less; to close, over the limit.
    private const double CleanPart = 0.75;

    public void Reset()
    {
        IsOpen = Noisy = false;
        _above = _below = 0;
    }

    /// <summary>Give it the level measured over the last <paramref name="seconds"/>.</summary>
    /// <summary>
    /// The same, with the roughness measured and the most a signal may have (0: not measured in
    /// this mode, and the level alone decides).
    /// </summary>
    public bool Update(double levelDb, double seconds, double roughness = 0, double roughLimit = 0)
    {
        bool test = Auto && roughLimit > 0;
        Noisy = test && roughness > roughLimit;
        bool clean = !test || roughness <= roughLimit * CleanPart;
        if (!Enabled) return IsOpen = true;
        if (IsOpen)
        {
            _below = levelDb < ThresholdDb - HysteresisDb || Noisy ? _below + seconds : 0;
            if (_below >= CloseAfter)
            {
                IsOpen = false;
                _above = 0;
            }
        }
        else
        {
            _above = levelDb >= ThresholdDb && clean ? _above + seconds : 0;
            if (_above >= OpenAfter)
            {
                IsOpen = true;
                _below = 0;
            }
        }
        return IsOpen;
    }
}
