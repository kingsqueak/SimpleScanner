namespace SimpleScanner.Radio;

/// <summary>
/// Automatic front-end gain for a radio with a stepped LNA and its own IF AGC (the SDRplay
/// RSPs): as much front-end gain as the receiver will take, and no more.
///
/// Up (more attenuation) when the converter reports overload, or the IF AGC is against its
/// stop. Down (more gain) when neither holds and the IF AGC has room to take up the step, but
/// never below the operator's floor. Fast up, slow down, and a step that overloaded is left
/// alone for a while before it is tried again, longer each time it fails.
///
/// Ported from sdroxide's auto_lna.rs (KC2RGW, 2026-11-23). No hardware in here.
/// </summary>
public sealed class AutoGainStep
{
    private static readonly TimeSpan AttackOverload = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan AttackAgc = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Decay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HoldBase = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HoldMax = TimeSpan.FromSeconds(600);
    private static readonly TimeSpan Bounce = TimeSpan.FromSeconds(10);
    private const int AgcStopMarginDb = 1, AgcHeadroomDb = 20;

    private TimeSpan? _lastMove, _lastDown, _holdUntil;
    private TimeSpan _hold = HoldBase;

    /// <summary>Forget what was learned: the band or the antenna changed, or the operator moved the control.</summary>
    public void Reset()
    {
        _lastMove = _lastDown = _holdUntil = null;
        _hold = HoldBase;
    }

    /// <summary>
    /// The step to ask for, or null to stay. <paramref name="now"/> is any steadily rising clock.
    /// <paramref name="ifGr"/> and <paramref name="ifGrMax"/> are the IF AGC's present and greatest reduction in dB.
    /// </summary>
    public int? Step(TimeSpan now, bool overload, int step, int floor, int maxStep, int ifGr, int ifGrMax)
    {
        TimeSpan? moved = now - _lastMove;
        floor = Math.Min(floor, maxStep);
        if (step < floor)
        {
            _lastMove = now;
            return floor;
        }

        bool agcAtStop = ifGr >= ifGrMax - AgcStopMarginDb;
        if (overload || agcAtStop)
        {
            var attack = overload ? AttackOverload : AttackAgc;
            if (step >= maxStep || moved < attack) return null;
            // Driven back up by the step down just taken: the next try at it waits twice as long.
            if (now - _lastDown < Bounce) _hold = TimeSpan.FromTicks(Math.Min(_hold.Ticks * 2, HoldMax.Ticks));
            _holdUntil = now + _hold;
            _lastMove = now;
            return step + 1;
        }

        bool agcHasRoom = ifGr <= ifGrMax - AgcHeadroomDb;
        if (step > floor && agcHasRoom && !(moved < Decay) && !(now < _holdUntil))
        {
            if (moved >= HoldMax) _hold = HoldBase;
            _lastDown = now;
            _lastMove = now;
            return step - 1;
        }
        return null;
    }
}
