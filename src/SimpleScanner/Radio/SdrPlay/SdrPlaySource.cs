using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SimpleScanner.Radio.SdrPlay;

/// <summary>
/// An SDRplay RSP through the vendor's API 3.x (written for the RSPdx and RSPdx-R2; the other
/// single-tuner models stream too, without antenna selection). The API and its background
/// service come from SDRplay's installer; this loads the library when first used.
///
/// Gain: both parts are ours. The LNA state is fixed at <see cref="GainStep"/>, or with
/// <see cref="AutoGain"/> moved by <see cref="AutoGainStep"/>. The IF gain reduction is set from the
/// level of the samples (see <see cref="NextIfGr"/>): the value found for each tuning is remembered and
/// sent with the frequency when the radio comes back to it, and while the radio stays on one tuning
/// it is trimmed ten times a second. The service's own IF AGC is left off: its stream of gain
/// changes holds up retunes (measured on an RSPdx on an outdoor antenna, 2026-11-24: 400 to 500 ms
/// for each retune and calls blocked for 1.5 s with it on, 13 ms with it off).
/// </summary>
public sealed unsafe class SdrPlaySource : IIqSource
{
    /// <summary>
    /// The radios plugged in and free now. One that another program (or another source in this
    /// one) has open is not listed. Throws if the API is not installed or its service is not answering.
    /// </summary>
    public static IReadOnlyList<RadioInfo> ListRadios()
    {
        EnsureApi();
        var devices = stackalloc DeviceT[16];
        uint count = 0;
        var found = new List<RadioInfo>();
        Native.LockDeviceApi();
        try
        {
            if (Native.GetDevices(devices, &count, 16) != Err.Success) return found;
            for (int k = 0; k < count; k++)
                if (devices[k].Valid != 0)
                    found.Add(new RadioInfo(ModelName(devices[k].HwVer), SerialOf(&devices[k])));
        }
        finally { Native.UnlockDeviceApi(); }
        return found;
    }

    /// <summary>
    /// The serial number of the radio to open at the next <see cref="Start"/>. With none, or when
    /// that radio is not there, the first RSPdx found is opened, or else the first radio of any model.
    /// </summary>
    public string? Serial { get; set; }

    /// <summary>
    /// With several radios in use at once: when the radio named by <see cref="Serial"/> is not there,
    /// <see cref="Start"/> fails; it does not open another, which may be meant for another receiver.
    /// </summary>
    public bool OnlyThisRadio { get; set; }

    public RadioInfo? Radio { get; private set; }

    public IReadOnlyList<RadioInfo> Radios()
    {
        var free = ListRadios();
        // The API leaves out a radio that is open, ours included.
        if (_running && Radio is { } mine && !free.Contains(mine)) return [mine, .. free];
        return free;
    }

    private static string SerialOf(DeviceT* d) => Marshal.PtrToStringUTF8((nint)d->SerNo) ?? "";

    private const int AgcSetPointDbfs = -30, AgcMode50Hz = 2, MaxIfGr = 59, MinIfGr = 20;
    private const double RetuneTimeoutMs = 500;
    private const double MaxRfHz = 2e9;             // the top of the tuning range the service accepts (the bottom is above 0)

    private static readonly object ApiLock = new();
    private static bool _apiOpen;

    private readonly object _gate = new();
    private readonly object _lifecycle = new();      // Start and Stop never overlap; callbacks take no lock
    private DeviceT _device;
    private DeviceParamsT* _params;
    private GCHandle _self;
    private Timer? _housekeeping;
    private readonly AutoGainStep _auto = new();
    private float[] _i = [], _q = [];
    private bool _running, _autoGain = true, _biasT, _fmNotch;
    private double _ppm;
    private int _gainStep, _activeStep, _ifGr = 40;
    private string _antenna = "Antenna A";
    private volatile bool _retunePending, _announceRetune, _overloadAckPending, _overloaded;
    private long _retuneStart;
    private long _lastStream;                       // when the stream callback last ran (Stopwatch ticks)
    private int _recovering;                        // 1 while a stalled radio is being restarted
    private long _lastRecovery;                     // when the last restart after a stall was made
    // For the log when the stream stalls: retunes since the start, how they ended, the slowest, the last.
    private long _started, _retunes, _timeouts, _refusals, _gainUpdates, _gainTrims, _lnaSteps;
    private double _slowestCallMs;
    private string _lastRetune = "none";
    private double _gainDb, _lastRetuneMs, _tuneCallMs;

    /// <summary>
    /// For measuring: called once for each retune with the centre asked for (Hz), how long the API's
    /// update call took (ms), how long until the radio confirmed it (ms) and how it ended ("ok",
    /// "TIMEOUT" when no confirmation came, or "refused ..." with the API's error). Called on the
    /// API's stream thread, a timer thread or the caller's: keep it short.
    /// </summary>
    public Action<double, double, double, string>? RetuneTrace { get; set; }

    /// <summary>For measuring: each gain report from the radio, with the ms since the last retune was asked for,
    /// the IF reduction, the LNA reduction and the gain it gives. Called on the API's event thread.</summary>
    public Action<double, uint, uint, double>? GainTrace { get; set; }

    /// <summary>
    /// For measuring and the log: called when the stream has stalled, with what is being done about it
    /// ("restarting", "restarted", or "gave up: ..."). Called on a thread-pool thread.
    /// </summary>
    public Action<string>? StallTrace { get; set; }

    /// <summary>
    /// Use the service's IF AGC, not ours; read when the radio is started. For comparing the two:
    /// the service's makes retunes slow.
    /// </summary>
    public bool IfAgc { get; set; }

    // ---- IF gain ----------------------------------------------------------------------------
    private const double IfSetPointDbfs = AgcSetPointDbfs, IfDeadbandDb = 3, DwellMs = 250;
    private const int PowerEvery = 8, PowerEnough = 2000;
    private readonly object _powerLock = new();     // a leaf: nothing else is taken inside it
    private double _powerSum;
    private long _powerCount;
    private readonly Dictionary<long, int> _grFor = [];     // by centre frequency, in units of 100 kHz
    private int _grCmd = 40;                        // the IF gain reduction last sent
    private double _gainBase;                       // the gain with no IF reduction, from the radio's last report

    private static long GrKey(double centerHz) => (long)Math.Round(centerHz / 100e3);

    /// <summary>For measuring: no learning and no trimming, so the reductions given with <see cref="RememberIfGr"/> stay.</summary>
    internal bool FreezeIfGain { get; set; }
    /// <summary>For testing the watchdog: drop every block, as a radio whose USB pipe has jammed does.
    /// Cleared when the watchdog restarts the radio.</summary>
    internal bool SimulateStall { get => _simulateStall; set => _simulateStall = value; }
    private volatile bool _simulateStall;

    internal void RememberIfGr(double centerHz, int gr) { lock (_gate) _grFor[GrKey(centerHz)] = gr; }

    /// <summary>
    /// The IF gain reduction to use after the samples measured <paramref name="dbfs"/> (mean power
    /// against full scale) at a reduction of <paramref name="gr"/>: what brings them to the set point,
    /// within the radio's range; no change for a small error.
    /// </summary>
    internal static int NextIfGr(int gr, double dbfs)
    {
        double error = dbfs - IfSetPointDbfs;
        if (Math.Abs(error) < IfDeadbandDb) return gr;
        return Math.Clamp(gr + (int)Math.Round(error), MinIfGr, MaxIfGr);
    }

    // The mean power of the samples since this was last called, or null with too few to go by.
    private double? TakePower()
    {
        double sum;
        long count;
        lock (_powerLock)
        {
            (sum, count) = (_powerSum, _powerCount);
            _powerSum = 0;
            _powerCount = 0;
        }
        return count >= PowerEnough ? 10 * Math.Log10(sum / count + 1e-12) : null;
    }

    public string Description { get; private set; } = "SDRplay RSP";
    public double SampleRate { get; private set; }
    public double CenterHz { get; private set; }
    public bool IsRunning => _running;
    public double UsableBandwidthHz => BandwidthFor(SampleRate) * 1000.0 * 0.92;
    public event IqHandler? Samples;
    public event Action<string>? Failed;

    public IReadOnlyList<string> Antennas { get; private set; } = ["Antenna A", "Antenna B", "Antenna C"];
    // With our own IF gain the reduction just sent counts at once: the radio's report of it comes later.
    public double GainDb => IfAgc ? Volatile.Read(ref _gainDb) : Volatile.Read(ref _gainBase) - Volatile.Read(ref _grCmd);
    public bool Overloaded => _overloaded;
    public double LastRetuneMs => Volatile.Read(ref _lastRetuneMs);
    public int ActiveGainStep => _activeStep;
    public int MaxGainStep => MaxLnaState(CenterHz);
    /// <summary>The IF gain reduction the service's AGC has settled on, in dB.</summary>
    public int IfGainReductionDb => _ifGr;

    // ---- start and stop -------------------------------------------------------------------

    public void Start(double sampleRate, double centerHz)
    {
        lock (_lifecycle)
        {
            try { StartOnce(sampleRate, centerHz); }
            catch (InitFailedException first)
            {
                // A radio left jammed (an RSP1 after a USB babble error) is refused until it is replugged:
                // replug it in software and try once more. Not the API until macOS says it is free (see Recover).
                string? serial = Radio?.Serial;
                Log($"{first.Message} (Asked for {centerHz / 1e6:0.####} MHz.) Replugging it.");
                UsbReplug.WaitFor(serial, TimeSpan.FromSeconds(10), UsbReplug.State.Free, UsbReplug.State.Gone);
                var (replugged, how) = UsbReplug.Replug(serial, TimeSpan.FromSeconds(10));
                Log($"USB replug: {how}");
                if (!replugged) throw;
                UsbReplug.WaitFor(serial, TimeSpan.FromSeconds(10), UsbReplug.State.Free);
                Thread.Sleep(2000);
                string? wanted = Serial;
                bool only = OnlyThisRadio;
                try
                {
                    Serial = serial ?? wanted;
                    OnlyThisRadio = serial is not null || only;
                    StartOnce(sampleRate, centerHz);
                    Log("started after a replug");
                }
                finally
                {
                    Serial = wanted;
                    OnlyThisRadio = only;
                }
            }
        }
    }

    // The API refused to start a radio it had opened.
    private sealed class InitFailedException(string message) : InvalidOperationException(message);

    private void StartOnce(double sampleRate, double centerHz)
    {
        lock (_lifecycle)
        lock (_gate)
        {
            if (_running) return;
            // Said here, not left to the API: its answer to a centre outside the range is a plain "Fail", which
            // reads as a jammed radio and gets it replugged (2026-11-26: -200 kHz, refused ten times in five starts).
            if (!(centerHz > 0 && centerHz <= MaxRfHz))
                throw new InvalidOperationException($"Could not start the radio: a centre frequency of {centerHz / 1e6:0.###} MHz is outside its range.");
            EnsureApi();

            var devices = stackalloc DeviceT[16];
            uint count = 0;
            Native.LockDeviceApi();
            try
            {
                Check(Native.GetDevices(devices, &count, 16), "list the radios");
                int pick = -1;
                bool dx = false, exact = false;
                for (int k = 0; k < count; k++)
                {
                    if (devices[k].Valid == 0) continue;
                    if (Serial is { Length: > 0 } wanted && SerialOf(&devices[k]) == wanted) { pick = k; exact = true; break; }
                    if (pick < 0) pick = k;
                    if (!dx && devices[k].HwVer is Native.HwRspDx or Native.HwRspDxR2) { pick = k; dx = true; }
                }
                if (OnlyThisRadio && Serial is { Length: > 0 } && !exact)
                    throw new InvalidOperationException($"Receiver {Serial} not found. Check that it is plugged in and that no other program is using it.");
                if (pick < 0)
                    throw new InvalidOperationException("No SDRplay radio found. Check that it is plugged in and that no other program is using it.");
                _device = devices[pick];
                _device.Tuner = Native.TunerA;
                fixed (DeviceT* d = &_device) Check(Native.SelectDevice(d), "open the radio");
            }
            finally { Native.UnlockDeviceApi(); }

            bool isDx = _device.HwVer is Native.HwRspDx or Native.HwRspDxR2;
            string serial;
            fixed (DeviceT* d = &_device) serial = SerialOf(d);
            Description = $"{ModelName(_device.HwVer)} {serial}";
            Radio = new RadioInfo(ModelName(_device.HwVer), serial);
            Antennas = isDx ? ["Antenna A", "Antenna B", "Antenna C"] : ["Antenna"];
            if (!Antennas.Contains(_antenna)) _antenna = Antennas[0];

            try
            {
                DeviceParamsT* p;
                Check(Native.GetDeviceParams(_device.Dev, &p), "read the radio's settings");
                _params = p;
                SampleRate = sampleRate;
                CenterHz = centerHz;
                _activeStep = Math.Min(_gainStep, MaxLnaState(centerHz));
                _grCmd = _ifGr = Math.Clamp(_ifGr, MinIfGr, MaxIfGr);
                TakePower();
                p->DevParams->FsFreq.FsHz = sampleRate;
                if (isDx) p->DevParams->RspDxParams.AntennaSel = Antennas.ToList().IndexOf(_antenna);
                WriteBiasT(p, out _, out _);
                WriteFmNotch(p, out _, out _);
                p->DevParams->Ppm = _ppm;
                var ch = p->RxChannelA;
                ch->TunerParams.BwType = BandwidthFor(sampleRate);
                ch->TunerParams.IfType = 0;                       // zero IF
                ch->TunerParams.RfFreq.RfHz = centerHz;
                ch->TunerParams.Gain.GRdB = _ifGr;
                ch->TunerParams.Gain.LnaState = (byte)_activeStep;
                ch->CtrlParams.Decimation.Enable = 0;
                ch->CtrlParams.DcOffset.DcEnable = 1;
                ch->CtrlParams.DcOffset.IqEnable = 1;
                ch->CtrlParams.Agc.Enable = IfAgc ? AgcMode50Hz : 0;
                ch->CtrlParams.Agc.SetPointDbfs = AgcSetPointDbfs;

                _self = GCHandle.Alloc(this);
                CallbackFnsT fns;
                fns.StreamA = &OnStream;
                fns.StreamB = &OnStreamB;
                fns.Event = &OnEvent;
                _retunePending = false;
                _announceRetune = true;
                _overloaded = false;
                _auto.Reset();
                var initErr = Native.Init(_device.Dev, &fns, (void*)GCHandle.ToIntPtr(_self));
                if (initErr != Err.Success)
                    throw new InitFailedException($"Could not start the radio: {Native.ErrorText(initErr)}.");
            }
            catch
            {
                if (_self.IsAllocated) _self.Free();
                Release();
                _params = null;
                throw;
            }
            _running = true;
            Volatile.Write(ref _lastStream, Stopwatch.GetTimestamp());
            _started = Stopwatch.GetTimestamp();
            _retunes = _timeouts = _refusals = _gainUpdates = _gainTrims = _lnaSteps = 0;
            _slowestCallMs = 0;
            _lastRetune = "none";
            Log($"started at {sampleRate / 1e6:0.##} MS/s, {centerHz / 1e6:0.####} MHz");
            _housekeeping = new Timer(_ => Housekeeping(), null, 100, 100);
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            Timer? timer;
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                timer = _housekeeping;
                _housekeeping = null;
            }
            timer?.Dispose();
            // Uninit waits for the stream callback to return, so it is called outside _gate,
            // which the callback's listeners may want.
            var err = Native.Uninit(_device.Dev);
            lock (_gate)
            {
                Release();
                _params = null;
                // If the API would not stop, a callback may still arrive: leave it a live handle
                // to find (a small leak) rather than a freed one.
                if (err == Err.Success && _self.IsAllocated) _self.Free();
            }
        }
    }

    // Under the API's own lock, as the opening is: another radio may be starting or stopping now.
    private void Release()
    {
        Native.LockDeviceApi();
        try { fixed (DeviceT* d = &_device) Native.ReleaseDevice(d); }
        finally { Native.UnlockDeviceApi(); }
    }

    public void Dispose() => Stop();

    private static void EnsureApi()
    {
        lock (ApiLock)
        {
            if (_apiOpen) return;
            Err err;
            try { err = Native.Open(); }
            catch (DllNotFoundException)
            {
                throw new InvalidOperationException(
                    "The SDRplay API is not installed. Download and install it from sdrplay.com/api, then start again.");
            }
            if (err != Err.Success)
                throw new InvalidOperationException(
                    $"The SDRplay API service is not answering ({Native.ErrorText(err)}). Restart the service or the computer.");
            float version = 0;
            Native.ApiVersion(&version);
            if (version < 3.07f)
            {
                Native.Close();
                throw new InvalidOperationException($"SDRplay API {version:F2} is too old; version 3.07 or later is needed.");
            }
            _apiOpen = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                if (!_skipCloseAtExit) Native.Close();      // not after a jammed radio: see HandOverStalls
            };
        }
    }

    // ---- controls -------------------------------------------------------------------------

    public void Tune(double centerHz)
    {
        lock (_gate)
        {
            if (!_running)
            {
                CenterHz = centerHz;
                return;
            }
            bool farMove = Math.Abs(centerHz - CenterHz) >= 1e6;
            var reason = Update.TunerFrf;
            if (!IfAgc)
            {
                // Remember what this tuning wants, and send what the next one wanted last time along
                // with the frequency: one update, not a gain change chasing the retune.
                if (TakePower() is double dbfs && !FreezeIfGain)
                {
                    if (_grFor.Count > 4096) _grFor.Clear();
                    _grFor[GrKey(CenterHz)] = NextIfGr(_grCmd, dbfs);
                }
                if (_grFor.TryGetValue(GrKey(centerHz), out int gr) && gr != _grCmd)
                {
                    _params->RxChannelA->TunerParams.Gain.GRdB = gr;
                    _grCmd = _ifGr = gr;
                    reason |= Update.TunerGr;
                }
            }
            CenterHz = centerHz;
            _params->RxChannelA->TunerParams.RfFreq.RfHz = centerHz;
            _retuneStart = Stopwatch.GetTimestamp();
            _retunePending = true;
            var err = Native.UpdateDevice(_device.Dev, Native.TunerA, reason, UpdateExt1.None);
            _tuneCallMs = Stopwatch.GetElapsedTime(_retuneStart).TotalMilliseconds;
            _retunes++;
            if ((reason & Update.TunerGr) != 0) _gainUpdates++;
            _slowestCallMs = Math.Max(_slowestCallMs, _tuneCallMs);
            _lastRetune = $"{centerHz / 1e6:0.###} MHz, call {_tuneCallMs:0} ms";
            if (err != Err.Success)
            {
                // Refused: say so anyway, or a listener waiting for the retune would wait for ever.
                _retunePending = false;
                _announceRetune = true;
                _refusals++;
                _lastRetune += $", refused {Native.ErrorText(err)}";
                RetuneTrace?.Invoke(centerHz, _tuneCallMs, _tuneCallMs, $"refused {Native.ErrorText(err)}");
            }
            if (farMove) _auto.Reset();
            // Each band has its own number of LNA states; one that does not exist here is refused.
            if (_activeStep > MaxLnaState(centerHz)) SetLna(MaxLnaState(centerHz));
        }
    }

    public string Antenna
    {
        get => _antenna;
        set
        {
            lock (_gate)
            {
                if (!Antennas.Contains(value) || value == _antenna) return;
                _antenna = value;
                if (!_running || Antennas.Count < 2) return;
                _params->DevParams->RspDxParams.AntennaSel = Antennas.ToList().IndexOf(value);
                Native.UpdateDevice(_device.Dev, Native.TunerA, Update.None, UpdateExt1.RspDxAntennaControl);
                _auto.Reset();
            }
        }
    }

    // No bias T on the RSP1; the RSPduo has it on tuner 2 only, which the scanner does not use.
    // Before the radio is found the model is not known (0), and the answer is yes.
    public bool HasBiasT => _device.HwVer is not (Native.HwRsp1 or Native.HwRspDuo);

    public bool BiasT
    {
        get => _biasT;
        set
        {
            lock (_gate)
            {
                if (value == _biasT) return;
                _biasT = value;
                if (!_running) return;
                WriteBiasT(_params, out Update reason, out UpdateExt1 ext);
                if (reason != Update.None || ext != UpdateExt1.None)
                    Native.UpdateDevice(_device.Dev, Native.TunerA, reason, ext);
            }
        }
    }

    /// <summary>Puts the bias T switch into the radio's settings, in the place this model keeps it,
    /// and says which update tells a running radio about it (none on a model without one).</summary>
    private void WriteBiasT(DeviceParamsT* p, out Update reason, out UpdateExt1 ext)
    {
        byte on = (byte)(_biasT ? 1 : 0);
        reason = Update.None;
        ext = UpdateExt1.None;
        switch (_device.HwVer)
        {
            case Native.HwRspDx or Native.HwRspDxR2:      // on Antenna B
                p->DevParams->RspDxParams.BiasTEnable = on;
                ext = UpdateExt1.RspDxBiasTControl;
                break;
            case Native.HwRsp1a or Native.HwRsp1b:
                p->RxChannelA->Rsp1aTunerParams.BiasTEnable = on;
                reason = Update.Rsp1aBiasTControl;
                break;
            case Native.HwRsp2:                           // on Antenna B
                p->RxChannelA->Rsp2TunerParams.BiasTEnable = on;
                reason = Update.Rsp2BiasTControl;
                break;
        }
    }

    // Same models as the bias T: the RSP1 has no notch, and the RSPduo is not catered for.
    public bool HasFmNotch => HasBiasT;

    public bool FmNotch
    {
        get => _fmNotch;
        set
        {
            lock (_gate)
            {
                if (value == _fmNotch) return;
                _fmNotch = value;
                if (!_running) return;
                WriteFmNotch(_params, out Update reason, out UpdateExt1 ext);
                if (reason != Update.None || ext != UpdateExt1.None)
                    Native.UpdateDevice(_device.Dev, Native.TunerA, reason, ext);
                _auto.Reset();      // what overloads the radio has changed
            }
        }
    }

    private void WriteFmNotch(DeviceParamsT* p, out Update reason, out UpdateExt1 ext)
    {
        byte on = (byte)(_fmNotch ? 1 : 0);
        reason = Update.None;
        ext = UpdateExt1.None;
        switch (_device.HwVer)
        {
            case Native.HwRspDx or Native.HwRspDxR2:
                p->DevParams->RspDxParams.RfNotchEnable = on;
                ext = UpdateExt1.RspDxRfNotchControl;
                break;
            case Native.HwRsp1a or Native.HwRsp1b:
                p->DevParams->Rsp1aParams.RfNotchEnable = on;
                reason = Update.Rsp1aRfNotchControl;
                break;
            case Native.HwRsp2:
                p->RxChannelA->Rsp2TunerParams.RfNotchEnable = on;
                reason = Update.Rsp2RfNotchControl;
                break;
        }
    }

    public double Ppm
    {
        get => _ppm;
        set
        {
            lock (_gate)
            {
                if (!double.IsFinite(value) || value == _ppm) return;
                _ppm = value;
                if (!_running) return;
                _params->DevParams->Ppm = value;
                Native.UpdateDevice(_device.Dev, Native.TunerA, Update.DevPpm, UpdateExt1.None);
            }
        }
    }

    public bool AutoGain
    {
        get => _autoGain;
        set
        {
            lock (_gate)
            {
                _autoGain = value;
                _auto.Reset();
                if (!value && _running) SetLna(Math.Min(_gainStep, MaxLnaState(CenterHz)));
            }
        }
    }

    public int GainStep
    {
        get => _gainStep;
        set
        {
            lock (_gate)
            {
                _gainStep = Math.Max(0, value);
                _auto.Reset();
                if (_running) SetLna(Math.Min(_gainStep, MaxLnaState(CenterHz)));
            }
        }
    }

    private void SetLna(int state)
    {
        if (state == _activeStep) return;
        _params->RxChannelA->TunerParams.Gain.LnaState = (byte)state;
        _lnaSteps++;
        if (Native.UpdateDevice(_device.Dev, Native.TunerA, Update.TunerGr, UpdateExt1.None) == Err.Success)
            _activeStep = state;
        else
            _params->RxChannelA->TunerParams.Gain.LnaState = (byte)_activeStep;
    }

    // Ten times a second: acknowledge overload reports (the API stops reporting until we do),
    // run the automatic LNA, and give up waiting for a retune the radio never confirmed.
    private void Housekeeping()
    {
        // Before the lock: a radio that has stopped answering may leave an update call stuck holding it.
        if (_running && Stopwatch.GetElapsedTime(Volatile.Read(ref _lastStream)).TotalSeconds > StallSeconds
            && Interlocked.CompareExchange(ref _recovering, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ => Recover(Trouble.Stalled));
            return;
        }
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (!_running) return;
            if (_overloadAckPending)
            {
                _overloadAckPending = false;
                Native.UpdateDevice(_device.Dev, Native.TunerA, Update.CtrlOverloadMsgAck, UpdateExt1.None);
            }
            if (_retunePending && Stopwatch.GetElapsedTime(_retuneStart).TotalMilliseconds > RetuneTimeoutMs)
            {
                _lastRetuneMs = RetuneTimeoutMs;
                _announceRetune = true;
                _retunePending = false;
                _timeouts++;
                _lastRetune += ", never confirmed";
                RetuneTrace?.Invoke(CenterHz, _tuneCallMs, RetuneTimeoutMs, "TIMEOUT");
            }
            // Staying on one tuning: trim the IF gain to the level of the samples.
            if (!IfAgc && !FreezeIfGain && !_retunePending && Stopwatch.GetElapsedTime(_retuneStart).TotalMilliseconds > DwellMs
                && TakePower() is double dbfs)
            {
                int gr = NextIfGr(_grCmd, dbfs);
                if (gr != _grCmd)
                {
                    _params->RxChannelA->TunerParams.Gain.GRdB = gr;
                    _gainTrims++;
                    if (Native.UpdateDevice(_device.Dev, Native.TunerA, Update.TunerGr, UpdateExt1.None) == Err.Success)
                        _grCmd = _ifGr = gr;
                    else
                        _params->RxChannelA->TunerParams.Gain.GRdB = _grCmd;
                }
            }
            // Not in the middle of a retune: a gain change sent then holds it up.
            if (_autoGain && !_retunePending)
            {
                var now = Stopwatch.GetElapsedTime(0);
                int? next = _auto.Step(now, _overloaded, _activeStep, _gainStep, MaxLnaState(CenterHz), _ifGr, MaxIfGr);
                if (next is int s) SetLna(s);
            }
        }
        finally { Monitor.Exit(_gate); }
    }

    // ---- a stream that stops by itself ---------------------------------------------------------

    // No samples for this long and the stream is taken as dead. The callback runs every millisecond
    // or so; retunes and gain changes do not pause it.
    private const double StallSeconds = 3, RecoveryWindowSeconds = 60;

    private enum Trouble { Stalled, Removed, Failure }

    // Three ways an RSP1 on macOS has been seen to die after half an hour to two hours (2026-11-25):
    // - Stalled: no word from the API, the samples just stop. One oversized USB packet ("babble error"),
    //   macOS halts the radio's data endpoint, the service logs "pipe is stalled" for ever; a plain
    //   restart is refused (initHw error 7) until the radio is replugged.
    // - Removed: the API says the radio was unplugged when it was not (a friend's Mac).
    // - Failure: the API says the radio failed.
    // In each case: stop, replug it in software (UsbReplug; it may also come back by itself), wait for
    // the API to list it again, start it where it was. If that fails, or it goes wrong again within a
    // minute of the last recovery, give up and say so.
    private void Recover(Trouble trouble)
    {
        string name = Radio?.Model ?? "radio";
        try
        {
            bool again = _lastRecovery != 0 && Stopwatch.GetElapsedTime(_lastRecovery).TotalSeconds < RecoveryWindowSeconds;
            string what = trouble switch
            {
                Trouble.Stalled => "stopped sending samples",
                Trouble.Removed => "reported unplugged by the API",
                _ => "reported failed by the API",
            };
            Log($"{what}{(again ? " again" : "")} after {Stopwatch.GetElapsedTime(_started).TotalMinutes:0.#} min: "
                + $"{_retunes} retunes ({_timeouts} never confirmed, {_refusals} refused, {_gainUpdates} with a gain change), "
                + $"{_gainTrims} IF gain trims, {_lnaSteps} LNA steps, slowest call {_slowestCallMs:0} ms; last retune {_lastRetune}");
            StallTrace?.Invoke(again ? $"gave up: {what} again" : $"{what}: restarting");
            double rate = SampleRate;
            string? serial = Serial;
            bool only = OnlyThisRadio;
            string? target = Radio?.Serial ?? serial;
            Stop();
            _simulateStall = false;
            string giveUp = trouble == Trouble.Stalled
                ? $"The {name} stopped sending samples"
                : trouble == Trouble.Removed ? $"The {name} dropped off USB" : $"The {name} reported a failure";
            if (again)
            {
                Failed?.Invoke($"{giveUp} again. Unplug it, plug it back in, then switch it on.");
                return;
            }
            if (trouble == Trouble.Stalled && HandingOver(what)) return;
            // Leave the API alone until the service has let go of the radio. Called while the service was
            // still sorting itself out after a jammed RSP1 (its stop timed out after 5 s), the API's
            // GetDevices crashed the program (2026-11-25 11:11). macOS says when the radio is free.
            var usb = UsbReplug.WaitFor(target, TimeSpan.FromSeconds(20), UsbReplug.State.Free, UsbReplug.State.Gone);
            Log($"USB after stopping: {usb}");
            try
            {
                Serial = target;                      // this radio, not whichever is free
                OnlyThisRadio = true;
                // Removed or failed: a plain stop and start has brought it back (switching it off and on
                // in the window did, on a friend's Mac), so try that first. A stalled radio is refused
                // until replugged, so for that go straight to the replug.
                if (trouble != Trouble.Stalled && usb == UsbReplug.State.Free)
                {
                    Thread.Sleep(1000);
                    try
                    {
                        // In a helper, no replug and second start here (Start does that on a refusal): a new process does it.
                        if (HandOverStalls) StartOnce(rate, CenterHz);
                        else Start(rate, CenterHz);
                        Restarted("restarted without a replug");
                        return;
                    }
                    catch (Exception e) { Log($"restart without a replug failed: {e.Message}"); }
                }
                // In a helper: the replug and the start are done in a new process.
                if (HandingOver(trouble == Trouble.Stalled ? what : $"{what}, and would not restart")) return;
                var (replugged, how) = UsbReplug.Replug(target, TimeSpan.FromSeconds(10));
                Log($"USB replug: {how}");
                // Back on USB and free, then a moment for the service to see it. Not on USB at all (a real
                // unplug, or a radio that dropped off): give it longer to come back.
                var back = UsbReplug.WaitFor(target, TimeSpan.FromSeconds(replugged ? 10 : 15), UsbReplug.State.Free);
                Thread.Sleep(2000);
                Exception? last = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        if (attempt > 0) Thread.Sleep(3000);
                        Start(rate, CenterHz);        // where the scan has got to meanwhile
                        Restarted(attempt == 0 ? "restarted" : "restarted at the second try");
                        return;
                    }
                    catch (Exception e)
                    {
                        last = e;
                        Log($"restart failed (USB {back}): {e.Message}");
                    }
                }
                StallTrace?.Invoke($"gave up: {last?.Message}");
                Failed?.Invoke(trouble == Trouble.Removed
                    ? $"{giveUp} and did not come back. Plug it back in (or unplug and replug it), then switch it on."
                    : $"{giveUp} and would not restart. Unplug it, plug it back in, then switch it on.");
            }
            finally
            {
                Serial = serial;
                OnlyThisRadio = only;
            }
        }
        catch { }
        finally { Volatile.Write(ref _recovering, 0); }
    }

    private void Restarted(string how)
    {
        _lastRecovery = Stopwatch.GetTimestamp();
        Log(how);
        StallTrace?.Invoke(how);
    }

    /// <summary>
    /// In a helper program: when the radio stalls (or will not restart after the API says it was removed
    /// or failed), stop it and raise <see cref="HandOver"/> rather than call the API again in this process.
    /// Twice (2026-11-25) a call to the API in the process whose stop had just timed out crashed it inside
    /// SDRplay's library, leaving the API's shared lock held and every program waiting on it until the
    /// service was restarted. A new process started the same jammed radio after a replug without trouble.
    /// </summary>
    public bool HandOverStalls { get; set; }

    /// <summary>With <see cref="HandOverStalls"/>: the radio is stopped and wants a new process; the text says why.
    /// The handler is expected to end this process (the API's close at exit is then left out).</summary>
    public event Action<string>? HandOver;

    private static volatile bool _skipCloseAtExit;

    private bool HandingOver(string why)
    {
        if (!HandOverStalls || HandOver is null) return false;
        Log($"handing over to a new process: {why}");
        StallTrace?.Invoke("handed over");
        _skipCloseAtExit = true;
        HandOver.Invoke(why);
        return true;
    }

    /// <summary>For testing the recovery: behave as when the API says the radio was unplugged (or failed).</summary>
    internal void SimulateApiEvent(bool removed)
    {
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) == 0)
            ThreadPool.QueueUserWorkItem(_ => Recover(removed ? Trouble.Removed : Trouble.Failure));
    }

    private void Log(string what) => RadioLog.Write($"{Description}: {what}");

    // ---- callbacks from the API's threads --------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStream(short* xi, short* xq, StreamCbParamsT* p, uint numSamples, uint reset, void* context)
    {
        try
        {
            if (GCHandle.FromIntPtr((nint)context).Target is SdrPlaySource self) self.Stream(xi, xq, p, (int)numSamples);
        }
        catch { /* an exception must not cross into the API's thread */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStreamB(short* xi, short* xq, StreamCbParamsT* p, uint numSamples, uint reset, void* context) { }

    private void Stream(short* xi, short* xq, StreamCbParamsT* p, int n)
    {
        if (_simulateStall) return;
        Volatile.Write(ref _lastStream, Stopwatch.GetTimestamp());
        if (_retunePending)
        {
            if (p->RfChanged == 0) return;          // still the old frequency
            _lastRetuneMs = Stopwatch.GetElapsedTime(_retuneStart).TotalMilliseconds;
            _retunePending = false;
            _announceRetune = true;
            RetuneTrace?.Invoke(CenterHz, _tuneCallMs, _lastRetuneMs, "ok");
        }
        if (_i.Length < n)
        {
            _i = new float[n];
            _q = new float[n];
        }
        const float scale = 1f / 32768;
        for (int k = 0; k < n; k++)
        {
            _i[k] = xi[k] * scale;
            _q[k] = xq[k] * scale;
        }
        if (!IfAgc)
        {
            double sum = 0;
            int m = 0;
            for (int k = 0; k < n; k += PowerEvery, m++) sum += _i[k] * _i[k] + _q[k] * _q[k];
            lock (_powerLock)
            {
                _powerSum += sum;
                _powerCount += m;
            }
        }
        // In one step: another thread may set it between a read and a clear, and it would be lost.
        bool retuned = Interlocked.Exchange(ref _announceRetune, false);
        Samples?.Invoke(_i.AsSpan(0, n), _q.AsSpan(0, n), retuned);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEvent(EventId id, int tuner, void* eventParams, void* context)
    {
        try
        {
            if (GCHandle.FromIntPtr((nint)context).Target is not SdrPlaySource self) return;
            switch (id)
            {
                case EventId.GainChange:
                    var g = (GainCbParamT*)eventParams;
                    self.GainTrace?.Invoke(Stopwatch.GetElapsedTime(self._retuneStart).TotalMilliseconds, g->GRdB, g->LnaGRdB, g->CurrGain);
                    if (self.IfAgc) self._ifGr = (int)g->GRdB;
                    Volatile.Write(ref self._gainDb, g->CurrGain);
                    Volatile.Write(ref self._gainBase, g->CurrGain + g->GRdB);
                    break;
                case EventId.PowerOverloadChange:
                    self._overloaded = *(int*)eventParams == 0;      // 0 detected, 1 corrected
                    self._overloadAckPending = true;
                    break;
                case EventId.DeviceRemoved:
                case EventId.DeviceFailure:
                    // Recover from another thread: Uninit cannot be called from inside a callback.
                    // Already recovering: this is the radio going away during it.
                    self.SimulateApiEvent(id == EventId.DeviceRemoved);
                    break;
            }
        }
        catch { }
    }

    // ---- tables ---------------------------------------------------------------------------

    private void Check(Err err, string doing)
    {
        if (err != Err.Success)
            throw new InvalidOperationException($"Could not {doing}: {Native.ErrorText(err)}.");
    }

    private static string ModelName(byte hwVer) => hwVer switch
    {
        1 => "RSP1", 2 => "RSP2", 3 => "RSPduo", 4 => "RSPdx", 6 => "RSP1B", 7 => "RSPdx-R2", 255 => "RSP1A", _ => "RSP",
    };

    /// <summary>The widest IF filter that fits inside the sample rate, in kHz (the API's own codes).</summary>
    internal static int BandwidthFor(double sampleRate)
    {
        int best = 200;
        foreach (int khz in new[] { 200, 300, 600, 1536, 5000, 6000, 7000, 8000 })
            if (khz * 1000.0 <= sampleRate) best = khz;
        return best;
    }

    /// <summary>The highest LNA state at a frequency, from the gain tables of the API's specification
    /// (RSPdx as on Antenna A or B). A state the model does not have is refused by the API.</summary>
    private int MaxLnaState(double hz)
    {
        if (_device.HwVer == Native.HwRsp1) return 3;
        if (_device.HwVer == Native.HwRsp2) return hz < 420e6 ? 8 : 5;
        if (_device.HwVer is not (Native.HwRspDx or Native.HwRspDxR2))
            return hz < 60e6 ? 6 : hz < 1000e6 ? 9 : 8;
        return hz < 12e6 ? 18 : hz < 50e6 ? 19 : hz < 60e6 ? 24 : hz < 250e6 ? 26 : hz < 420e6 ? 27 : hz < 1000e6 ? 20 : 18;
    }
}
