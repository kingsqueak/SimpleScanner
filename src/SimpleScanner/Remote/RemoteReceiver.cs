// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SimpleScanner.Digital;
using SimpleScanner.Dsp;
using SimpleScanner.Radio;
using SimpleScanner.Trunk;

namespace SimpleScanner.Remote;

/// <summary>
/// A receiver that runs in a helper program of its own (see <see cref="ReceiverHost"/>), with its own
/// radio, processing and sound. What is read here is the helper's last report, at most a twentieth of a
/// second old; what is set here is sent to it, and shows here at once.
/// </summary>
public sealed class RemoteReceiver : IReceiver
{
    private Stream _to;
    private Action? _closed;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<WireMessage>> _waiting = new();
    private ManualResetEventSlim _first = new();
    private readonly RemoteSource _source;
    private ReceiverStatus _s = new();
    private long _id, _lastChange;
    private volatile bool _disposed, _gone;
    private int _gen;                        // which helper's reports count: those of the one started last

    // A new helper when the old one asks for it ("restart": its radio jammed, see SdrPlaySource.HandOverStalls),
    // when one that may change its radio is to open another, and when it has gone and is switched on again.
    // Everything sent is remembered, so that the new one is given it again.
    private readonly Func<string?, (Stream To, Stream From, Action? Closed)>? _launch;
    private string? _serial, _helperSerial;  // the radio chosen, and the one the helper now running was started for
    private readonly Action<string?> _replug;
    private readonly Dictionary<string, (object? Value, Type Type)> _sets = [];
    private ModeArg? _mode;
    private IReadOnlyList<ScanChannel>? _scan;
    private TrunkArg? _trunk;
    private bool _wantRunning;
    private volatile string? _restartWhy;
    private long _lastRestart;
    private const double RestartWindowSeconds = 60;

    /// <summary>The radio can be changed (<see cref="IIqSource.Serial"/>): a new helper is started for the one chosen.
    /// So it is for the window's own receiver; one started for a further radio keeps it.</summary>
    public bool CanChangeRadio { get; }

    /// <summary>
    /// Start this program again as a helper for the radio with that serial number, and connect to it.
    /// With <paramref name="changeable"/> the helper takes another radio when that one is not there (none given: the
    /// first it finds), and a new helper is started when another is chosen. Throws when the helper does not come up.
    /// </summary>
    public static RemoteReceiver Launch(string? serial, Func<IReadOnlyList<RadioInfo>> freeRadios, double? sampleRate = null,
        string? program = null, bool changeable = false)
    {
        string path = program ?? Environment.ProcessPath ?? throw new InvalidOperationException("This program's own file is not known.");
        (Stream, Stream, Action?) Connect(string? radio)
        {
            var start = new ProcessStartInfo(path)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("--radio-host");
            start.ArgumentList.Add(radio ?? "");
            if (changeable) start.ArgumentList.Add("--any");
            if (sampleRate is { } rate)
            {
                start.ArgumentList.Add("--rate");
                start.ArgumentList.Add(rate.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            var process = Process.Start(start) ?? throw new InvalidOperationException("The receiver's helper program could not be started.");
            return (process.StandardInput.BaseStream, process.StandardOutput.BaseStream, () =>
            {
                // Closing its input tells it to stop; if it has not gone soon, it is ended, so the radio is free again.
                try
                {
                    if (!process.WaitForExit(3000)) process.Kill();
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
            });
        }
        var rx = new RemoteReceiver(Connect, freeRadios, serial, changeable);
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
        _closed = closed;
        _replug = UsbReplugStep;
        _source = new RemoteSource(this, freeRadios ?? (() => []));
        StartReading(fromHelper, _first);
    }

    /// <param name="launch">Starts a helper and gives its streams, and what to do once they are closed; called again for a new helper.</param>
    /// <param name="replug">Between two helpers: replug the radio with that serial number (by default in software, on USB).</param>
    internal RemoteReceiver(Func<(Stream To, Stream From, Action? Closed)> launch, Func<IReadOnlyList<RadioInfo>>? freeRadios = null,
        Action<string?>? replug = null)
        : this(_ => launch(), freeRadios, null, false, replug) { }

    /// <param name="launch">Starts a helper for the radio with that serial number (null: any) and gives its streams.</param>
    internal RemoteReceiver(Func<string?, (Stream To, Stream From, Action? Closed)> launch, Func<IReadOnlyList<RadioInfo>>? freeRadios,
        string? serial, bool changeable, Action<string?>? replug = null)
    {
        _launch = launch;
        _replug = replug ?? UsbReplugStep;
        _serial = _helperSerial = serial;
        CanChangeRadio = changeable;
        _source = new RemoteSource(this, freeRadios ?? (() => []));
        var (to, from, closed) = launch(serial);
        _to = to;
        _closed = closed;
        StartReading(from, _first);
    }

    private void StartReading(Stream from, ManualResetEventSlim first)
    {
        var reader = new StreamReader(from, new UTF8Encoding(false));
        int gen = Interlocked.Increment(ref _gen);
        new Thread(() => Read(reader, first, gen)) { IsBackground = true, Name = "receiver helper" }.Start();
    }

    /// <summary>Wait for the helper's first report (so that what is read is the helper's, not blanks).</summary>
    public bool WaitForFirstReport(int milliseconds) => _first.Wait(milliseconds);

    public event Action<string>? Failed;
    public IIqSource Source => _source;
    internal ReceiverStatus Status => Volatile.Read(ref _s);

    // ---- the helper's reports ----------------------------------------------------------------

    private void Read(StreamReader from, ManualResetEventSlim first, int gen)
    {
        try
        {
            while (from.ReadLine() is { } line)
            {
                WireMessage? m;
                try { m = JsonSerializer.Deserialize<WireMessage>(line, WireMessage.Json); }
                catch (JsonException) { continue; }
                switch (m?.Op)
                {
                    case "status" when m.Status is { } status:
                        lock (_gate)
                        {
                            // A report made before the helper had our last change would undo it here for a moment;
                            // one from a helper being replaced is not this receiver's any more.
                            if (status.Seq >= _lastChange && gen == _gen) Volatile.Write(ref _s, status);
                        }
                        first.Set();
                        break;
                    case "reply":
                        if (_waiting.TryRemove(m.Id, out var waiter)) waiter.TrySetResult(m);
                        break;
                    case "failed":
                        Failed?.Invoke(m.Error ?? "The radio stopped.");
                        break;
                    case "restart":
                        _restartWhy = m.Error ?? "asked for";
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        // The helper has gone. If it was replaced, that is all. If it asked for a new one, start that;
        // otherwise nothing is running any more: say so if it was not our doing.
        if (gen != Volatile.Read(ref _gen)) return;
        _gone = true;
        if (_restartWhy is { } why && !_disposed && _launch is not null)
        {
            _restartWhy = null;
            new Thread(() => Restart(why)) { IsBackground = true, Name = "receiver helper restart" }.Start();
            return;
        }
        bool wasRunning;
        lock (_gate)
        {
            wasRunning = _s.IsRunning;
            Volatile.Write(ref _s, _s with { IsRunning = false, Scanning = false, ScanListening = false, ScanChannel = null, SquelchOpen = false, LevelDb = -140, DigitalText = null, Slots = ReceiverStatus.EmptySlots, Trunk = null });
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

    private void Set<T>(string name, T value, Func<ReceiverStatus, ReceiverStatus> change)
    {
        lock (_gate)
        {
            _sets.Remove(name);              // kept in the order last set
            _sets[name] = (value, typeof(T));
        }
        Send("set", name, value, typeof(T), change);
    }

    // Send and wait for the helper's answer. Its failure comes back as the exception a real receiver would throw.
    private WireMessage Ask(string op, int milliseconds, object? arg = null, Type? argType = null)
    {
        if (_gone) throw new InvalidOperationException("The receiver's helper program is not running.");
        var waiter = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (_gate)
        {
            id = _id + 1;
            _waiting[id] = waiter;
            Send(op, arg: arg, argType: argType, change: s => s);    // counted as a change: reports from before it are stale
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
        // A helper that has gone (it failed, or its radio would not restart), or one started for another radio
        // than the one chosen now, is replaced first.
        string? want;
        lock (_gate) want = _serial;
        if (_launch is not null && !_disposed && (_gone || CanChangeRadio && want != _helperSerial && !IsRunning))
        {
            RadioLog.Write($"{want ?? "radio"}: a new helper{(_gone ? "" : $" for another radio (was {_helperSerial ?? "any"})")}");
            Replace(want);
        }
        Ask("start", 20000);
        lock (_gate) _wantRunning = true;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _wantRunning = false;
            _trunk = null;                   // a receiver that stops leaves trunk mode
        }
        if (_gone) return;
        Ask("stop", 10000);
    }

    public double FrequencyHz
    {
        get => Status.FrequencyHz;
        set
        {
            // Tuning by hand ends trunk mode (tuning to where it is already does nothing).
            lock (_gate)
                if (value != _s.FrequencyHz) _trunk = null;
            Set(nameof(FrequencyHz), value, s => s with { FrequencyHz = value, Trunk = value != s.FrequencyHz ? null : s.Trunk });
        }
    }

    public Mode Mode => Status.Mode;
    public double BandwidthHz => Status.BandwidthHz;

    public void SetMode(Mode mode, double bandwidthHz)
    {
        lock (_gate)
        {
            _mode = new ModeArg(mode, bandwidthHz);
            if (mode != _s.Mode || bandwidthHz != _s.BandwidthHz) _trunk = null;      // a new mode ends trunk mode
        }
        Send("setMode", arg: new ModeArg(mode, bandwidthHz), argType: typeof(ModeArg), change: s => s with
        {
            Mode = mode, BandwidthHz = bandwidthHz, Trunk = mode != s.Mode || bandwidthHz != s.BandwidthHz ? null : s.Trunk,
        });
    }

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

    public double Pan
    {
        get => Status.Pan;
        set => Set(nameof(Pan), value, s => s with { Pan = Math.Clamp(value, -1, 1) });
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

    public void StartScan(IReadOnlyList<ScanChannel> channels)
    {
        lock (_gate)
        {
            _scan = channels.Count > 0 ? channels : null;
            _trunk = null;
        }
        // The receiver leaves out the channels it refuses to receive: with none left it does not scan, and stays on its channel.
        bool any = channels.Any(c => !BandPlan.IsBlocked(c.FrequencyHz, c.BandwidthHz));
        Send("startScan", arg: channels, argType: typeof(IReadOnlyList<ScanChannel>),
            change: s => s with
            {
                Scanning = channels.Count > 0 && s.IsRunning, ScanChannel = null, ScanListening = false, SquelchEnabled = true, Trunk = null,
                Slots = OneChannel(s, none: s.MultiMonitor && any && s.IsRunning),
            });
    }

    public void StopScan()
    {
        lock (_gate) _scan = null;
        Send("stopScan", change: s => s with { Scanning = false, ScanListening = false, ScanChannel = null, Slots = OneChannel(s) });
    }

    // The slots as a receiver has them on one channel, which is where it was (the lowest slot that held one): the
    // first holds it, the rest nothing. With none, no slot holds anything (a Multi Monitor scan just started).
    private static SlotStatus[] OneChannel(ReceiverStatus s, bool none = false)
    {
        var list = (SlotStatus[])ReceiverStatus.EmptySlots.Clone();
        if (!none)
            list[0] = new SlotStatus(0, true, null, s.FrequencyHz, s.Mode, s.BandwidthHz, s.LevelDb, s.SquelchOpen,
                s.DigitalText, s.DigitalEncrypted, s.DigitalNothingToHear);
        return list;
    }

    public bool MultiMonitor
    {
        get => Status.MultiMonitor;
        set => Set(nameof(MultiMonitor), value, s => s with { MultiMonitor = value });
    }

    public double MultiHangSeconds
    {
        get => Status.MultiHangSeconds;
        set => Set(nameof(MultiHangSeconds), value, s => s with { MultiHangSeconds = value });
    }

    /// <summary>The last report's four slots (four empty ones before any report, or for a report without them).</summary>
    public IReadOnlyList<SlotStatus> Slots
    {
        get
        {
            var slots = Status.Slots;
            if (slots is { Length: SlotMixer.Seats } && Array.TrueForAll(slots, x => x is not null)) return slots;
            var list = (SlotStatus[])ReceiverStatus.EmptySlots.Clone();
            for (int k = 0; k < list.Length && slots is not null && k < slots.Length; k++)
                if (slots[k] is { } slot) list[k] = slot with { Seat = k };
            return list;
        }
    }

    public void SkipSlot(int slot) => Send("skipSlot", arg: slot, argType: typeof(int));

    public TrunkStatus? Trunk => Status.Trunk;

    public void StartTrunk(TrunkSiteDef site, TrunkFilter filter)
    {
        var arg = new TrunkArg(site, filter);
        lock (_gate)
        {
            _trunk = arg;
            _scan = null;
        }
        Send("startTrunk", arg: arg, argType: typeof(TrunkArg), change: s => s with
        {
            Scanning = false, ScanListening = false, ScanChannel = null, Slots = OneChannel(s),
            Trunk = s.IsRunning && site.ControlHz.Count > 0 ? TrunkStatus.Starting(site) : null,
        });
    }

    public void StopTrunk()
    {
        lock (_gate) _trunk = null;
        Send("stopTrunk", change: s => s with { Trunk = null });
    }

    public void SetTrunkFilter(TrunkFilter filter)
    {
        lock (_gate)
            if (_trunk is not null) _trunk = _trunk with { Filter = filter };
        Send("setTrunkFilter", arg: filter, argType: typeof(TrunkFilter));
    }

    public ScanChannel? AvoidSlot(int slot)
    {
        if (_gone) return null;
        var reply = Ask("avoidSlot", 3000, slot, typeof(int));
        return reply.Arg is { ValueKind: not JsonValueKind.Null } a ? a.Deserialize<ScanChannel>(WireMessage.Json) : null;
    }
    public void SkipChannel() => Send("skip");

    /// <summary>For testing: the helper's radio acts as if jammed (its samples are dropped), so its watchdog fires.</summary>
    internal void SimulateStall() => Send("simulateStall");
    public void ClearAvoided() => Send("clearAvoided", change: s => s with { AvoidedCount = 0 });

    public ScanChannel? AvoidChannel()
    {
        if (_gone) return null;
        var reply = Ask("avoid", 3000);
        return reply.Arg is { ValueKind: not JsonValueKind.Null } a ? a.Deserialize<ScanChannel>(WireMessage.Json) : null;
    }

    // ---- a new helper for a jammed radio ---------------------------------------------------------

    private static void UsbReplugStep(string? serial)
    {
        var usb = Radio.SdrPlay.UsbReplug.WaitFor(serial, TimeSpan.FromSeconds(20), Radio.SdrPlay.UsbReplug.State.Free, Radio.SdrPlay.UsbReplug.State.Gone);
        var (done, how) = Radio.SdrPlay.UsbReplug.Replug(serial, TimeSpan.FromSeconds(10));
        RadioLog.Write($"{serial}: USB {usb} after the helper went; replug: {how}");
        if (done) Radio.SdrPlay.UsbReplug.WaitFor(serial, TimeSpan.FromSeconds(10), Radio.SdrPlay.UsbReplug.State.Free);
        Thread.Sleep(2000);                  // for the service to see it again
    }

    // The old helper has gone, asking for a new one. Replug the radio from here (no SDRplay call in this
    // process), start a helper and give it everything the old one had been given, then start and scan.
    private void Restart(string why)
    {
        string? serial = Status.RadioSerial;
        string name = Status.RadioModel ?? "radio";
        bool unplugged = why.Contains("unplugged");
        bool connected = false;
        try
        {
            RadioLog.Write($"{name} {serial}: helper asked for a new one ({why})");
            _closed?.Invoke();
            bool again = _lastRestart != 0 && Stopwatch.GetElapsedTime(_lastRestart).TotalSeconds < RestartWindowSeconds;
            if (again || _disposed)
            {
                if (!_disposed)
                {
                    RadioLog.Write($"{name} {serial}: stopped again within a minute; given up");
                    Failed?.Invoke($"The {name} stopped sending samples again. Unplug it, plug it back in, then switch it on.");
                }
                return;
            }
            _replug(serial);
            if (!Connect(serial ?? _helperSerial)) return;
            connected = true;
            IReadOnlyList<ScanChannel>? scan;
            TrunkArg? trunk;
            bool running;
            lock (_gate) (scan, trunk, running) = (_scan, _trunk, _wantRunning);
            if (running)
            {
                Ask("start", 20000);
                if (trunk is not null) Send("startTrunk", arg: trunk, argType: typeof(TrunkArg));
                else if (scan is not null) Send("startScan", arg: scan, argType: typeof(IReadOnlyList<ScanChannel>));
            }
            _lastRestart = Stopwatch.GetTimestamp();
            RadioLog.Write($"{name} {serial}: running again in a new helper{(trunk is not null ? ", trunking" : scan is not null ? ", scanning" : "")}");
        }
        catch (Exception e)
        {
            RadioLog.Write($"{name} {serial}: a new helper failed: {e.Message}");
            // A helper that is up keeps serving: switching the radio on again later goes to it.
            _gone = !connected;
            if (!_disposed)
                Failed?.Invoke(unplugged
                    ? $"The {name} was unplugged or dropped off USB. Plug it back in, then switch it on."
                    : $"The {name} stopped sending samples and would not restart ({e.Message}). Unplug it, plug it back in, then switch it on.");
        }
    }

    // Start a helper for that radio (null: any) and give it everything the one before had been given, in
    // the order it was last given. False when this receiver was closed meanwhile. Throws when it does not answer.
    private bool Connect(string? serial)
    {
        // A scan moves the receiver without anything being sent from here, so the frequency and mode last sent
        // may be hours old, and are not what the display shows. Where the helper before was, by its last report
        // (with anything sent since), is where the new one is put. Taken now: the new one's reports replace it.
        // (2026-11-26: a stray click had sent 100 kHz, the scan went on, and every new helper was given 100 kHz.)
        lock (_gate)
        {
            if (_s.FrequencyHz > 0)
            {
                _sets.Remove(nameof(FrequencyHz));
                _sets[nameof(FrequencyHz)] = (_s.FrequencyHz, typeof(double));
                if (_s.BandwidthHz > 0) _mode = new ModeArg(_s.Mode, _s.BandwidthHz);
            }
        }
        var first = new ManualResetEventSlim();
        var (to, from, closed) = _launch!(serial);
        lock (_gate)
        {
            _to = to;
            _closed = closed;
            _first = first;
            _helperSerial = serial;
        }
        StartReading(from, first);
        if (_disposed)
        {
            Dispose(closing: true);
            return false;
        }
        if (!first.Wait(10000)) throw new InvalidOperationException("The new helper program did not answer.");
        _gone = false;
        List<(string Name, object? Value, Type Type)> sets;
        ModeArg? mode;
        lock (_gate)
        {
            sets = [.. _sets.Select(kv => (kv.Key, kv.Value.Value, kv.Value.Type))];
            mode = _mode;
        }
        foreach (var (setName, value, type) in sets) Send("set", setName, value, type);
        if (mode is not null) Send("setMode", arg: mode, argType: typeof(ModeArg));
        return true;
    }

    // End the helper running now (its radio is stopped and let go) and start one for that radio in its place.
    private void Replace(string? serial)
    {
        Action? closed;
        lock (_gate)
        {
            Interlocked.Increment(ref _gen);     // the old helper's going is not news
            closed = _closed;
            _closed = null;
        }
        try { _to.Dispose(); }
        catch (IOException) { }
        closed?.Invoke();                    // waits for it to go, so that the radio is free
        if (!Connect(serial)) throw new InvalidOperationException("The receiver was closed.");
    }

    /// <summary>The radio chosen: the one the next start opens. (<see cref="CanChangeRadio"/>: it may be changed.)</summary>
    internal string? ChosenSerial
    {
        get
        {
            lock (_gate) return CanChangeRadio ? _serial ?? Status.RadioSerial : Status.RadioSerial;
        }
        set
        {
            if (!CanChangeRadio)
            {
                if (value != Status.RadioSerial) throw new NotSupportedException("A helper's radio cannot be changed; start another helper.");
                return;
            }
            lock (_gate) _serial = value;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Dispose(closing: true);
    }

    private void Dispose(bool closing)
    {
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

        // A helper keeps the radio it was started for; another radio, if it may change, gets a new helper at the next start.
        public string? Serial
        {
            get => rx.ChosenSerial;
            set => rx.ChosenSerial = value;
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
        public int MostGainSteps => S.MostGainSteps;
        public int ActiveGainStep => S.ActiveGainStep;
        public double GainDb => S.GainDb;
        public bool Overloaded => S.Overloaded;
        public double LastRetuneMs => S.LastRetuneMs;
        public void Dispose() { }
    }
}
