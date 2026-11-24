using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SimpleScanner.Digital;
using SimpleScanner.Dsp;
using SimpleScanner.Radio;

namespace SimpleScanner.Remote;

/// <summary>
/// A receiver that runs in a helper program of its own (see <see cref="ReceiverHost"/>), with its own
/// radio, processing and sound. What is read here is the helper's last report, at most a twentieth of a
/// second old; what is set here is sent to it, and shows here at once.
/// </summary>
public sealed class RemoteReceiver : IReceiver
{
    private readonly Stream _to;
    private readonly StreamReader _from;
    private readonly Action? _closed;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<WireMessage>> _waiting = new();
    private readonly ManualResetEventSlim _first = new();
    private readonly RemoteSource _source;
    private ReceiverStatus _s = new();
    private long _id, _lastChange;
    private volatile bool _disposed, _gone;

    /// <summary>
    /// Start this program again as a helper for the radio with that serial number, and connect to it.
    /// Throws when the helper does not come up.
    /// </summary>
    public static RemoteReceiver Launch(string serial, Func<IReadOnlyList<RadioInfo>> freeRadios, double? sampleRate = null, string? program = null)
    {
        var start = new ProcessStartInfo(program ?? Environment.ProcessPath ?? throw new InvalidOperationException("This program's own file is not known."))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--radio-host");
        start.ArgumentList.Add(serial);
        if (sampleRate is { } rate)
        {
            start.ArgumentList.Add("--rate");
            start.ArgumentList.Add(rate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException("The receiver's helper program could not be started.");
        var rx = new RemoteReceiver(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, freeRadios, () =>
        {
            // Closing its input tells it to stop; if it has not gone soon, it is ended, so the radio is free again.
            try
            {
                if (!process.WaitForExit(3000)) process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.Dispose();
        });
        if (!rx._first.Wait(10000))
        {
            rx.Dispose();
            throw new InvalidOperationException("The receiver's helper program did not answer.");
        }
        return rx;
    }

    /// <param name="toHelper">Messages to the helper.</param>
    /// <param name="fromHelper">Its messages back.</param>
    /// <param name="freeRadios">The radios free to open now, for <see cref="IIqSource.Radios"/>.</param>
    /// <param name="closed">Called once, after the streams are closed.</param>
    public RemoteReceiver(Stream toHelper, Stream fromHelper, Func<IReadOnlyList<RadioInfo>>? freeRadios = null, Action? closed = null)
    {
        _to = toHelper;
        _from = new StreamReader(fromHelper, new UTF8Encoding(false));
        _closed = closed;
        _source = new RemoteSource(this, freeRadios ?? (() => []));
        new Thread(Read) { IsBackground = true, Name = "receiver helper" }.Start();
    }

    /// <summary>Wait for the helper's first report (so that what is read is the helper's, not blanks).</summary>
    public bool WaitForFirstReport(int milliseconds) => _first.Wait(milliseconds);

    public event Action<string>? Failed;
    public IIqSource Source => _source;
    internal ReceiverStatus Status => Volatile.Read(ref _s);

    // ---- the helper's reports ----------------------------------------------------------------

    private void Read()
    {
        try
        {
            while (_from.ReadLine() is { } line)
            {
                WireMessage? m;
                try { m = JsonSerializer.Deserialize<WireMessage>(line, WireMessage.Json); }
                catch (JsonException) { continue; }
                switch (m?.Op)
                {
                    case "status" when m.Status is { } status:
                        lock (_gate)
                        {
                            // A report made before the helper had our last change would undo it here for a moment.
                            if (status.Seq >= _lastChange) Volatile.Write(ref _s, status);
                        }
                        _first.Set();
                        break;
                    case "reply":
                        if (_waiting.TryRemove(m.Id, out var waiter)) waiter.TrySetResult(m);
                        break;
                    case "failed":
                        Failed?.Invoke(m.Error ?? "The radio stopped.");
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        // The helper has gone. Nothing is running any more; say so if it was not our doing.
        _gone = true;
        bool wasRunning;
        lock (_gate)
        {
            wasRunning = _s.IsRunning;
            Volatile.Write(ref _s, _s with { IsRunning = false, Scanning = false, ScanListening = false, ScanChannel = null, SquelchOpen = false, LevelDb = -140, DigitalText = null });
        }
        foreach (var waiter in _waiting.Values) waiter.TrySetException(new InvalidOperationException("The receiver's helper program stopped."));
        if (!_disposed && wasRunning) Failed?.Invoke("The receiver's helper program stopped.");
    }

    // ---- sending ------------------------------------------------------------------------------

    private long Send(string op, string? name = null, object? arg = null, Type? argType = null, Func<ReceiverStatus, ReceiverStatus>? change = null)
    {
        lock (_gate)
        {
            long id = ++_id;
            if (change is not null)
            {
                _lastChange = id;
                Volatile.Write(ref _s, change(_s));
            }
            var m = new WireMessage { Id = id, Op = op, Name = name };
            if (argType is not null) m.Arg = JsonSerializer.SerializeToElement(arg, argType, WireMessage.Json);
            try
            {
                _to.Write(JsonSerializer.SerializeToUtf8Bytes(m, WireMessage.Json));
                _to.WriteByte((byte)'\n');
                _to.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            return id;
        }
    }

    private void Set<T>(string name, T value, Func<ReceiverStatus, ReceiverStatus> change) =>
        Send("set", name, value, typeof(T), change);

    // Send and wait for the helper's answer. Its failure comes back as the exception a real receiver would throw.
    private WireMessage Ask(string op, int milliseconds)
    {
        if (_gone) throw new InvalidOperationException("The receiver's helper program is not running.");
        var waiter = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (_gate)
        {
            id = _id + 1;
            _waiting[id] = waiter;
            Send(op, change: s => s);        // counted as a change: reports from before it are stale
        }
        if (!waiter.Task.Wait(milliseconds))
        {
            _waiting.TryRemove(id, out _);
            throw new InvalidOperationException("The receiver's helper program did not answer.");
        }
        var reply = waiter.Task.Result;
        if (reply.Error is { } error) throw new InvalidOperationException(error);
        if (reply.Status is { } status)
        {
            lock (_gate)
            {
                if (status.Seq >= _lastChange) Volatile.Write(ref _s, status);
            }
        }
        return reply;
    }

    // ---- IReceiver -----------------------------------------------------------------------------

    public bool IsRunning => Status.IsRunning;

    public void Start()
    {
        Ask("start", 20000);
    }

    public void Stop()
    {
        if (_gone) return;
        Ask("stop", 10000);
    }

    public double FrequencyHz
    {
        get => Status.FrequencyHz;
        set => Set(nameof(FrequencyHz), value, s => s with { FrequencyHz = value });
    }

    public Mode Mode => Status.Mode;
    public double BandwidthHz => Status.BandwidthHz;

    public void SetMode(Mode mode, double bandwidthHz) =>
        Send("setMode", arg: new ModeArg(mode, bandwidthHz), argType: typeof(ModeArg), change: s => s with { Mode = mode, BandwidthHz = bandwidthHz });

    public double LevelDb => Status.LevelDb;
    public bool SquelchOpen => Status.SquelchOpen;
    public bool Blocked => Status.Blocked;
    public bool Stereo => Status.Stereo;

    public double LevelOffsetDb
    {
        get => Status.LevelOffsetDb;
        set => Set(nameof(LevelOffsetDb), value, s => s with { LevelOffsetDb = value });
    }

    public double SquelchDb
    {
        get => Status.SquelchDb;
        set => Set(nameof(SquelchDb), value, s => s with { SquelchDb = value });
    }

    public bool SquelchEnabled
    {
        get => Status.SquelchEnabled;
        set => Set(nameof(SquelchEnabled), value, s => s with { SquelchEnabled = value });
    }

    public bool AutoSquelch
    {
        get => Status.AutoSquelch;
        set => Set(nameof(AutoSquelch), value, s => s with { AutoSquelch = value });
    }

    public bool StereoEnabled
    {
        get => Status.StereoEnabled;
        set => Set(nameof(StereoEnabled), value, s => s with { StereoEnabled = value });
    }

    public IDigitalStatus? Digital => Status is { DigitalText: { } text } s ? new DigitalReport(text, s.DigitalEncrypted, s.DigitalNothingToHear) : null;

    public string? AudioDevice
    {
        get => Status.AudioDevice;
        set => Set(nameof(AudioDevice), value, s => s with { AudioDevice = value });
    }

    public double Volume
    {
        get => Status.Volume;
        set => Set(nameof(Volume), value, s => s with { Volume = value });
    }

    public double ScanHangSeconds
    {
        get => Status.ScanHangSeconds;
        set => Set(nameof(ScanHangSeconds), value, s => s with { ScanHangSeconds = value });
    }

    public double ScanSkipSeconds
    {
        get => Status.ScanSkipSeconds;
        set => Set(nameof(ScanSkipSeconds), value, s => s with { ScanSkipSeconds = value });
    }

    public double? ResumeSeconds
    {
        get => Status.ResumeSeconds;
        set => Set(nameof(ResumeSeconds), value, s => s with { ResumeSeconds = value });
    }

    public TimeSpan? AvoidTime
    {
        get => Status.AvoidTime;
        set => Set(nameof(AvoidTime), value, s => s with { AvoidTime = value });
    }

    public int AvoidedCount => Status.AvoidedCount;
    public bool Scanning => Status.Scanning;
    public bool ScanListening => Status.ScanListening;
    public long ScanPasses => Status.ScanPasses;
    public int ScanWindows => Status.ScanWindows;
    public ScanChannel? ScanChannel => Status.ScanChannel;
    public long DroppedBlocks => Status.DroppedBlocks;

    public void StartScan(IReadOnlyList<ScanChannel> channels) =>
        Send("startScan", arg: channels, argType: typeof(IReadOnlyList<ScanChannel>),
            change: s => s with { Scanning = channels.Count > 0 && s.IsRunning, ScanChannel = null, ScanListening = false, SquelchEnabled = true });

    public void StopScan() => Send("stopScan", change: s => s with { Scanning = false, ScanListening = false, ScanChannel = null });
    public void SkipChannel() => Send("skip");
    public void ClearAvoided() => Send("clearAvoided", change: s => s with { AvoidedCount = 0 });

    public ScanChannel? AvoidChannel()
    {
        if (_gone) return null;
        var reply = Ask("avoid", 3000);
        return reply.Arg is { ValueKind: not JsonValueKind.Null } a ? a.Deserialize<ScanChannel>(WireMessage.Json) : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _to.Dispose(); }               // the helper reads the end of its input, stops its radio and goes
        catch (IOException) { }
        _closed?.Invoke();
    }

    private sealed record DigitalReport(string Text, bool Encrypted, bool NothingToHear) : IDigitalStatus
    {
        public override string ToString() => Text;
    }

    /// <summary>The helper's radio, read from its reports and set by message. It cannot be started, stopped or tuned from here: the helper's receiver does that.</summary>
    private sealed class RemoteSource(RemoteReceiver rx, Func<IReadOnlyList<RadioInfo>> freeRadios) : IIqSource
    {
        private ReceiverStatus S => rx.Status;

        public string Description => S.Description;
        public RadioInfo? Radio => S is { RadioModel: { } model, RadioSerial: { } serial } ? new RadioInfo(model, serial) : null;

        public IReadOnlyList<RadioInfo> Radios()
        {
            var list = freeRadios().ToList();
            if (Radio is { } mine && !list.Any(r => r.Serial == mine.Serial)) list.Insert(0, mine);
            return list;
        }

        // The helper was started for one radio and keeps it.
        public string? Serial
        {
            get => S.RadioSerial;
            set
            {
                if (value != S.RadioSerial) throw new NotSupportedException("A helper's radio cannot be changed; start another helper.");
            }
        }

        public double SampleRate => S.SampleRate;
        public double CenterHz => S.CenterHz;
        public double UsableBandwidthHz => S.UsableBandwidthHz;
        public bool IsRunning => S.IsRunning;
#pragma warning disable CS0067   // the samples stay in the helper
        public event IqHandler? Samples;
        public event Action<string>? Failed;
#pragma warning restore CS0067
        public void Start(double sampleRate, double centerHz) => throw new NotSupportedException();
        public void Stop() => throw new NotSupportedException();
        public void Tune(double centerHz) => throw new NotSupportedException();

        public IReadOnlyList<string> Antennas => S.Antennas;

        public string Antenna
        {
            get => S.Antenna;
            set => rx.Set("Source." + nameof(Antenna), value, s => s with { Antenna = value });
        }

        public bool HasBiasT => S.HasBiasT;

        public bool BiasT
        {
            get => S.BiasT;
            set => rx.Set("Source." + nameof(BiasT), value, s => s with { BiasT = value });
        }

        public bool HasFmNotch => S.HasFmNotch;

        public bool FmNotch
        {
            get => S.FmNotch;
            set => rx.Set("Source." + nameof(FmNotch), value, s => s with { FmNotch = value });
        }

        public double Ppm
        {
            get => S.Ppm;
            set => rx.Set("Source." + nameof(Ppm), value, s => s with { Ppm = value });
        }

        public bool AutoGain
        {
            get => S.AutoGain;
            set => rx.Set("Source." + nameof(AutoGain), value, s => s with { AutoGain = value });
        }

        public int GainStep
        {
            get => S.GainStep;
            set => rx.Set("Source." + nameof(GainStep), value, s => s with { GainStep = value });
        }

        public int MaxGainStep => S.MaxGainStep;
        public int ActiveGainStep => S.ActiveGainStep;
        public double GainDb => S.GainDb;
        public bool Overloaded => S.Overloaded;
        public double LastRetuneMs => S.LastRetuneMs;
        public void Dispose() { }
    }
}
