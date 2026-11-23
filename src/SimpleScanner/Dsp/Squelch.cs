namespace SimpleScanner.Dsp;

/// <summary>
/// Carrier squelch: open while the channel level is above a threshold. It opens after the
/// level has been above for a moment and closes after it has been below (by a small margin)
/// for a little longer, so noise spikes and fades do not chatter it.
/// </summary>
public sealed class Squelch
{
    private const double HysteresisDb = 2, OpenAfter = 0.015, CloseAfter = 0.12;
    private double _above, _below;

    public double ThresholdDb { get; set; } = -100;
    /// <summary>With the squelch off, audio always passes.</summary>
    public bool Enabled { get; set; } = true;
    public bool IsOpen { get; private set; }

    public void Reset()
    {
        IsOpen = false;
        _above = _below = 0;
    }

    /// <summary>Give it the level measured over the last <paramref name="seconds"/>.</summary>
    public bool Update(double levelDb, double seconds)
    {
        if (!Enabled) return IsOpen = true;
        if (IsOpen)
        {
            _below = levelDb < ThresholdDb - HysteresisDb ? _below + seconds : 0;
            if (_below >= CloseAfter)
            {
                IsOpen = false;
                _above = 0;
            }
        }
        else
        {
            _above = levelDb >= ThresholdDb ? _above + seconds : 0;
            if (_above >= OpenAfter)
            {
                IsOpen = true;
                _below = 0;
            }
        }
        return IsOpen;
    }
}
