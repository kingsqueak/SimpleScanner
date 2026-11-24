using System.Runtime.InteropServices;
using SimpleScanner.Radio;
using SimpleScanner.Radio.SdrPlay;

namespace SimpleScanner.Tests;

/// <summary>
/// The C# copies of the SDRplay API structures against the layout a C compiler gives the real
/// headers (API 3.15.1, 64-bit; printed by a small C program using sizeof and offsetof).
/// </summary>
public unsafe class SdrPlayLayoutTests
{
    [Fact]
    public void StructuresMatchTheCHeaders()
    {
        Assert.Equal(96, sizeof(DeviceT));
        Assert.Equal(64, sizeof(DevParamsT));
        Assert.Equal(144, sizeof(RxChannelParamsT));
        Assert.Equal(20, sizeof(StreamCbParamsT));
        Assert.Equal(16, sizeof(GainCbParamT));
        Assert.Equal(64, (int)Marshal.OffsetOf<DeviceT>("HwVer"));
        Assert.Equal(68, (int)Marshal.OffsetOf<DeviceT>("Tuner"));
        Assert.Equal(76, (int)Marshal.OffsetOf<DeviceT>("Valid"));
        Assert.Equal(80, (int)Marshal.OffsetOf<DeviceT>("RspDuoSampleFreq"));
        Assert.Equal(88, (int)Marshal.OffsetOf<DeviceT>("Dev"));
        Assert.Equal(8, (int)Marshal.OffsetOf<DevParamsT>("FsFreq"));
        Assert.Equal(24, (int)Marshal.OffsetOf<DevParamsT>("SyncUpdate"));
        Assert.Equal(32, (int)Marshal.OffsetOf<DevParamsT>("ResetFlags"));
        Assert.Equal(36, (int)Marshal.OffsetOf<DevParamsT>("Mode"));
        Assert.Equal(40, (int)Marshal.OffsetOf<DevParamsT>("SamplesPerPkt"));
        Assert.Equal(44, (int)Marshal.OffsetOf<DevParamsT>("Rsp1aParams"));
        Assert.Equal(46, (int)Marshal.OffsetOf<DevParamsT>("Rsp2Params"));
        Assert.Equal(48, (int)Marshal.OffsetOf<DevParamsT>("RspDuoParams"));
        Assert.Equal(52, (int)Marshal.OffsetOf<DevParamsT>("RspDxParams"));
        Assert.Equal(1, (int)Marshal.OffsetOf<RspDxParamsT>("BiasTEnable"));
        Assert.Equal(4, (int)Marshal.OffsetOf<RspDxParamsT>("AntennaSel"));
        Assert.Equal(8, (int)Marshal.OffsetOf<RspDxParamsT>("RfNotchEnable"));
        Assert.Equal(12, (int)Marshal.OffsetOf<TunerParamsT>("Gain"));
        Assert.Equal(40, (int)Marshal.OffsetOf<TunerParamsT>("RfFreq"));
        Assert.Equal(56, (int)Marshal.OffsetOf<TunerParamsT>("DcOffsetTuner"));
        Assert.Equal(4, (int)Marshal.OffsetOf<GainT>("LnaState"));
        Assert.Equal(8, (int)Marshal.OffsetOf<GainT>("MinGr"));
        Assert.Equal(12, (int)Marshal.OffsetOf<GainT>("GainVals"));
        Assert.Equal(72, (int)Marshal.OffsetOf<RxChannelParamsT>("CtrlParams"));
        Assert.Equal(104, (int)Marshal.OffsetOf<RxChannelParamsT>("Rsp1aTunerParams"));
        Assert.Equal(108, (int)Marshal.OffsetOf<RxChannelParamsT>("Rsp2TunerParams"));
        Assert.Equal(124, (int)Marshal.OffsetOf<RxChannelParamsT>("RspDuoTunerParams"));
        Assert.Equal(140, (int)Marshal.OffsetOf<RxChannelParamsT>("RspDxTunerParams"));
        Assert.Equal(2, (int)Marshal.OffsetOf<ControlParamsT>("Decimation"));
        Assert.Equal(8, (int)Marshal.OffsetOf<ControlParamsT>("Agc"));
        Assert.Equal(28, (int)Marshal.OffsetOf<ControlParamsT>("AdsbMode"));
        Assert.Equal(4, (int)Marshal.OffsetOf<AgcT>("SetPointDbfs"));
        Assert.Equal(8, (int)Marshal.OffsetOf<AgcT>("AttackMs"));
        Assert.Equal(16, (int)Marshal.OffsetOf<AgcT>("SyncUpdate"));
    }
}

public class AutoGainStepTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void OverloadStepsUpQuicklyAndQuietStepsBackSlowly()
    {
        var a = new AutoGainStep();
        Assert.Equal(1, a.Step(S(0), true, 0, 0, 18, 30, 59));
        Assert.Null(a.Step(S(0.1), true, 1, 0, 18, 30, 59));          // too soon after the last step
        Assert.Equal(2, a.Step(S(0.4), true, 1, 0, 18, 30, 59));
        Assert.Null(a.Step(S(5), false, 2, 0, 18, 30, 59));           // held away from the step that overloaded
        Assert.Equal(1, a.Step(S(21), false, 2, 0, 18, 30, 59));
        Assert.Null(a.Step(S(22), false, 1, 0, 18, 30, 59));
        Assert.Equal(0, a.Step(S(23.5), false, 1, 0, 18, 30, 59));
    }

    [Fact]
    public void TheAgcAtItsStopStepsUpAndATightAgcBlocksTheWayDown()
    {
        var a = new AutoGainStep();
        Assert.Equal(1, a.Step(S(0), false, 0, 0, 18, 59, 59));
        Assert.Null(a.Step(S(0.5), false, 1, 0, 18, 59, 59));
        Assert.Equal(2, a.Step(S(1.1), false, 1, 0, 18, 59, 59));
        Assert.Null(a.Step(S(100), false, 2, 0, 18, 45, 59));         // under 20 dB in hand
        Assert.Equal(1, a.Step(S(100), false, 2, 0, 18, 39, 59));
    }

    [Fact]
    public void NeverGoesBelowTheFloorOrAboveTheTop()
    {
        var a = new AutoGainStep();
        Assert.Equal(5, a.Step(S(0), false, 2, 5, 18, 30, 59));
        Assert.Null(a.Step(S(100), false, 5, 5, 18, 30, 59));
        Assert.Null(a.Step(S(200), true, 18, 5, 18, 30, 59));
    }
}
