using System.Reflection;
using System.Runtime.InteropServices;

namespace SimpleScanner.Radio.SdrPlay;

// The SDRplay API 3.x (sdrplay_api.h and the headers it includes), as far as the scanner
// uses it. Structure layouts must match the C headers exactly: the library hands out
// pointers to its own copies and we write into them. tests/.../SdrPlayLayoutTests.cs checks
// the sizes and offsets against values printed by a C program built from the headers.
#pragma warning disable CS0649   // fields the library fills in

internal enum Err
{
    Success = 0, Fail = 1, InvalidParam = 2, OutOfRange = 3, GainUpdateError = 4, RfUpdateError = 5,
    FsUpdateError = 6, HwError = 7, AliasingError = 8, AlreadyInitialised = 9, NotInitialised = 10,
    NotEnabled = 11, HwVerError = 12, OutOfMemError = 13, ServiceNotResponding = 14, StartPending = 15,
    StopPending = 16, InvalidMode = 17, InvalidServiceVersion = 24,
}

[Flags]
internal enum Update : uint
{
    None = 0, DevFs = 0x1, TunerGr = 0x8000, TunerFrf = 0x20000, TunerBwType = 0x40000,
    CtrlAgc = 0x01000000, CtrlOverloadMsgAck = 0x04000000,
}

[Flags]
internal enum UpdateExt1 : uint { None = 0, RspDxAntennaControl = 0x4 }

internal enum EventId { GainChange = 0, PowerOverloadChange = 1, DeviceRemoved = 2, RspDuoModeChange = 3, DeviceFailure = 4 }

internal unsafe struct DeviceT
{
    public fixed byte SerNo[64];
    public byte HwVer;
    public int Tuner;
    public int RspDuoMode;
    public byte Valid;
    public double RspDuoSampleFreq;
    public nint Dev;
}

internal struct FsFreqT { public double FsHz; public byte SyncUpdate, ReCal; }
internal struct SyncUpdateT { public uint SampleNum, Period; }
internal struct ResetFlagsT { public byte ResetGainUpdate, ResetRfUpdate, ResetFsUpdate; }
internal struct Rsp1aParamsT { public byte RfNotchEnable, RfDabNotchEnable; }
internal struct Rsp2ParamsT { public byte ExtRefOutputEn; }
internal struct RspDuoParamsT { public int ExtRefOutputEn; }
internal struct RspDxParamsT { public byte HdrEnable, BiasTEnable; public int AntennaSel; public byte RfNotchEnable, RfDabNotchEnable; }

internal struct DevParamsT
{
    public double Ppm;
    public FsFreqT FsFreq;
    public SyncUpdateT SyncUpdate;
    public ResetFlagsT ResetFlags;
    public int Mode;
    public uint SamplesPerPkt;
    public Rsp1aParamsT Rsp1aParams;
    public Rsp2ParamsT Rsp2Params;
    public RspDuoParamsT RspDuoParams;
    public RspDxParamsT RspDxParams;
}

internal struct GainValuesT { public float Curr, Max, Min; }
internal struct GainT { public int GRdB; public byte LnaState, SyncUpdate; public int MinGr; public GainValuesT GainVals; }
internal struct RfFreqT { public double RfHz; public byte SyncUpdate; }
internal struct DcOffsetTunerT { public byte DcCal, SpeedUp; public int TrackTime, RefreshRateTime; }

internal struct TunerParamsT
{
    public int BwType, IfType, LoMode;
    public GainT Gain;
    public RfFreqT RfFreq;
    public DcOffsetTunerT DcOffsetTuner;
}

internal struct DcOffsetT { public byte DcEnable, IqEnable; }
internal struct DecimationT { public byte Enable, DecimationFactor, WideBandSignal; }
internal struct AgcT { public int Enable, SetPointDbfs; public ushort AttackMs, DecayMs, DecayDelayMs, DecayThresholdDb; public int SyncUpdate; }
internal struct ControlParamsT { public DcOffsetT DcOffset; public DecimationT Decimation; public AgcT Agc; public int AdsbMode; }
internal struct Rsp1aTunerParamsT { public byte BiasTEnable; }
internal struct Rsp2TunerParamsT { public byte BiasTEnable; public int AmPortSel, AntennaSel; public byte RfNotchEnable; }
internal struct RspDuoResetSlaveFlagsT { public byte ResetGainUpdate, ResetRfUpdate; }
internal struct RspDuoTunerParamsT
{
    public byte BiasTEnable; public int Tuner1AmPortSel;
    public byte Tuner1AmNotchEnable, RfNotchEnable, RfDabNotchEnable;
    public RspDuoResetSlaveFlagsT ResetSlaveFlags;
}
internal struct RspDxTunerParamsT { public int HdrBw; }

internal struct RxChannelParamsT
{
    public TunerParamsT TunerParams;
    public ControlParamsT CtrlParams;
    public Rsp1aTunerParamsT Rsp1aTunerParams;
    public Rsp2TunerParamsT Rsp2TunerParams;
    public RspDuoTunerParamsT RspDuoTunerParams;
    public RspDxTunerParamsT RspDxTunerParams;
}

internal unsafe struct DeviceParamsT
{
    public DevParamsT* DevParams;
    public RxChannelParamsT* RxChannelA;
    public RxChannelParamsT* RxChannelB;
}

internal struct StreamCbParamsT { public uint FirstSampleNum; public int GrChanged, RfChanged, FsChanged; public uint NumSamples; }
internal struct GainCbParamT { public uint GRdB, LnaGRdB; public double CurrGain; }

internal unsafe struct CallbackFnsT
{
    public delegate* unmanaged[Cdecl]<short*, short*, StreamCbParamsT*, uint, uint, void*, void> StreamA;
    public delegate* unmanaged[Cdecl]<short*, short*, StreamCbParamsT*, uint, uint, void*, void> StreamB;
    public delegate* unmanaged[Cdecl]<EventId, int, void*, void*, void> Event;
}

internal static unsafe partial class Native
{
    private const string Lib = "sdrplay_api";
    public const int TunerA = 1;
    public const int HwRspDx = 4, HwRspDxR2 = 7;

    static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);

    // The API is installed by SDRplay's own installer, outside the places .NET looks.
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return 0;
        string[] candidates;
        if (OperatingSystem.IsWindows())
        {
            string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            candidates =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SDRplay", "API", arch, "sdrplay_api.dll"),
                "sdrplay_api.dll",
            ];
        }
        else if (OperatingSystem.IsMacOS())
            candidates = ["/usr/local/lib/libsdrplay_api.dylib", "/usr/local/lib/libsdrplay_api.so", "libsdrplay_api.dylib"];
        else
            candidates = ["libsdrplay_api.so.3", "/usr/local/lib/libsdrplay_api.so.3", "/usr/local/lib/libsdrplay_api.so", "libsdrplay_api.so"];
        foreach (string c in candidates)
            if (NativeLibrary.TryLoad(c, out nint handle)) return handle;
        return 0;
    }

    [LibraryImport(Lib, EntryPoint = "sdrplay_api_Open")] public static partial Err Open();
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_Close")] public static partial Err Close();
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_ApiVersion")] public static partial Err ApiVersion(float* version);
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_LockDeviceApi")] public static partial Err LockDeviceApi();
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_UnlockDeviceApi")] public static partial Err UnlockDeviceApi();
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_GetDevices")] public static partial Err GetDevices(DeviceT* devices, uint* count, uint max);
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_SelectDevice")] public static partial Err SelectDevice(DeviceT* device);
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_ReleaseDevice")] public static partial Err ReleaseDevice(DeviceT* device);
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_GetErrorString")] public static partial byte* GetErrorString(Err err);
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_GetDeviceParams")] public static partial Err GetDeviceParams(nint dev, DeviceParamsT** deviceParams);
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_Init")] public static partial Err Init(nint dev, CallbackFnsT* callbacks, void* context);
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_Uninit")] public static partial Err Uninit(nint dev);
    [LibraryImport(Lib, EntryPoint = "sdrplay_api_Update")] public static partial Err UpdateDevice(nint dev, int tuner, Update reason, UpdateExt1 ext1);

    public static string ErrorText(Err err)
    {
        byte* s = GetErrorString(err);
        return s == null ? err.ToString() : Marshal.PtrToStringUTF8((nint)s) ?? err.ToString();
    }
}
