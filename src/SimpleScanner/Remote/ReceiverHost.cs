// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Text;
using System.Text.Json;

namespace SimpleScanner.Remote;

/// <summary>
/// The helper program's side: a real receiver, worked by the messages that arrive and reporting back.
/// </summary>
internal sealed class ReceiverHost
{
    private readonly IReceiver _rx;
    private readonly StreamReader _in;
    private readonly Stream _out;
    private readonly object _write = new();
    private long _seq;
    private volatile bool _open = true;

    private ReceiverHost(IReceiver rx, Stream input, Stream output)
    {
        _rx = rx;
        _in = new StreamReader(input, new UTF8Encoding(false));
        _out = output;
    }

    /// <summary>
    /// Serve the receiver until the other end closes the stream (the window has gone) or says "quit". The receiver is then stopped and disposed.
    /// </summary>
    /// <param name="handOver">Given the way to ask the window for a new helper ("restart", with why), for the radio to call when
    /// it wants a new process (see <see cref="Radio.SdrPlay.SdrPlaySource.HandOverStalls"/>).</param>
    /// <param name="exit">Ends this helper after it has asked for a new one; the whole process by default.</param>
    public static void Run(IReceiver rx, Stream input, Stream output, Action<Action<string>>? handOver = null, Action? exit = null)
    {
        var host = new ReceiverHost(rx, input, output);
        rx.Failed += why => host.Send(new WireMessage { Op = "failed", Error = why });
        handOver?.Invoke(why =>
        {
            host._open = false;
            host.Send(new WireMessage { Op = "restart", Error = why });
            // Nothing more from this process: the API in it is not to be trusted after a jammed radio.
            if (exit is not null) exit();
            else Environment.Exit(3);
        });
        var reports = new Thread(host.Report) { IsBackground = true, Name = "receiver status" };
        reports.Start();
        try
        {
            while (host._in.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                WireMessage? m;
                try { m = JsonSerializer.Deserialize<WireMessage>(line, WireMessage.Json); }
                catch (JsonException) { continue; }
                if (m is null) continue;
                if (m.Op == "quit") break;
                host.Handle(m);
                Interlocked.Exchange(ref host._seq, m.Id);
            }
        }
        catch (IOException) { }
        finally
        {
            host._open = false;
            reports.Join(500);
            rx.Dispose();
        }
    }

    private void Handle(WireMessage m)
    {
        bool answered = m.Op is "start" or "stop" or "avoid" or "avoidSlot";
        try
        {
            JsonElement? result = null;
            switch (m.Op)
            {
                case "start": _rx.Start(); break;
                case "stop": _rx.Stop(); break;
                case "avoid": result = JsonSerializer.SerializeToElement(_rx.AvoidChannel(), WireMessage.Json); break;
                case "set": Set(m.Name ?? "", m.Arg); break;
                case "setMode":
                    var mode = m.Arg!.Value.Deserialize<ModeArg>(WireMessage.Json)!;
                    _rx.SetMode(mode.Mode, mode.BandwidthHz);
                    break;
                case "startScan": _rx.StartScan(m.Arg!.Value.Deserialize<List<ScanChannel>>(WireMessage.Json)!); break;
                case "stopScan": _rx.StopScan(); break;
                case "skip": _rx.SkipChannel(); break;
                case "skipSlot": _rx.SkipSlot(m.Arg!.Value.GetInt32()); break;
                case "avoidSlot": result = JsonSerializer.SerializeToElement(_rx.AvoidSlot(m.Arg!.Value.GetInt32()), WireMessage.Json); break;
                case "clearAvoided": _rx.ClearAvoided(); break;
                case "startTrunk":
                    var trunk = m.Arg!.Value.Deserialize<TrunkArg>(WireMessage.Json)!;
                    _rx.StartTrunk(trunk.Site, trunk.Filter);
                    break;
                case "stopTrunk": _rx.StopTrunk(); break;
                case "setTrunkFilter": _rx.SetTrunkFilter(m.Arg!.Value.Deserialize<Trunk.TrunkFilter>(WireMessage.Json)!); break;
                case "simulateStall":            // for testing the hand-over to a new helper
                    if (_rx.Source is Radio.SdrPlay.SdrPlaySource sdr) sdr.SimulateStall = true;
                    break;
            }
            // An answer carries a report made after the work, so what is read next is already true.
            if (answered) Send(new WireMessage { Op = "reply", Id = m.Id, Arg = result, Status = Snapshot(_rx, m.Id) });
        }
        catch (Exception e)
        {
            var why = (e as System.Reflection.TargetInvocationException)?.InnerException ?? e;
            if (answered) Send(new WireMessage { Op = "reply", Id = m.Id, Error = why.Message });
            else Console.Error.WriteLine($"{m.Op} {m.Name}: {why.Message}");
        }
    }

    // "Volume" is the receiver's; "Source.Antenna" its radio's.
    private void Set(string name, JsonElement? arg)
    {
        object target = _rx;
        var type = typeof(IReceiver);
        if (name.StartsWith("Source."))
        {
            target = _rx.Source;
            type = typeof(Radio.IIqSource);
            name = name[7..];
        }
        var property = type.GetProperty(name);
        if (property is null || !property.CanWrite) throw new InvalidOperationException("no such setting");
        property.SetValue(target, arg is { } a ? a.Deserialize(property.PropertyType, WireMessage.Json) : null);
    }

    private void Report()
    {
        while (_open)
        {
            ReceiverStatus status;
            try { status = Snapshot(_rx, Interlocked.Read(ref _seq)); }
            catch (Exception e)
            {
                Console.Error.WriteLine($"status: {e.Message}");
                Thread.Sleep(200);
                continue;
            }
            if (!Send(new WireMessage { Op = "status", Status = status })) return;
            Thread.Sleep(50);
        }
    }

    private bool Send(WireMessage m)
    {
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(m, WireMessage.Json);
        lock (_write)
        {
            try
            {
                _out.Write(line);
                _out.WriteByte((byte)'\n');
                _out.Flush();
                return true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { return false; }
        }
    }

    internal static ReceiverStatus Snapshot(IReceiver rx, long seq)
    {
        var src = rx.Source;
        var radio = src.Radio;
        // What the processing changes together is one reading, taken under the receiver's lock; read one by one, a
        // report could say the scan is listening with the squelch still shut. Another receiver is read property by property.
        var now = rx is Receiver real ? real.ReadStatus()
            : new ReceiverReading(rx.FrequencyHz, rx.Mode, rx.BandwidthHz, rx.LevelDb, rx.SquelchOpen, rx.Blocked, rx.Stereo,
                rx.Digital, rx.Scanning, rx.ScanListening, rx.ScanChannel, rx.ScanPasses, rx.ScanWindows, rx.AvoidedCount,
                rx.MultiMonitor, rx.Slots, rx.Trunk);
        var digital = now.Digital;
        var p25 = digital as Digital.P25Status;
        return new ReceiverStatus
        {
            Seq = seq,
            IsRunning = rx.IsRunning,
            FrequencyHz = now.FrequencyHz,
            Mode = now.Mode,
            BandwidthHz = now.BandwidthHz,
            LevelDb = now.LevelDb,
            LevelOffsetDb = rx.LevelOffsetDb,
            SquelchOpen = now.SquelchOpen,
            Blocked = now.Blocked,
            SquelchDb = rx.SquelchDb,
            SquelchEnabled = rx.SquelchEnabled,
            AutoSquelch = rx.AutoSquelch,
            Stereo = now.Stereo,
            StereoEnabled = rx.StereoEnabled,
            DigitalText = digital?.ToString(),
            DigitalEncrypted = digital is { Encrypted: true },
            DigitalNothingToHear = digital is { NothingToHear: true },
            DigitalNac = p25?.Nac,
            DigitalTalkgroup = p25 is { InCall: true } ? p25.Talkgroup : null,
            DigitalSource = p25 is { InCall: true, Source: not 0 } ? p25.Source : null,
            AudioDevice = rx.AudioDevice,
            Volume = rx.Volume,
            Pan = rx.Pan,
            ScanHangSeconds = rx.ScanHangSeconds,
            ScanSkipSeconds = rx.ScanSkipSeconds,
            ResumeSeconds = rx.ResumeSeconds,
            AvoidTime = rx.AvoidTime,
            AvoidedCount = now.AvoidedCount,
            Scanning = now.Scanning,
            ScanListening = now.ScanListening,
            ScanPasses = now.ScanPasses,
            ScanWindows = now.ScanWindows,
            ScanChannel = now.ScanChannel,
            MultiMonitor = now.MultiMonitor,
            MultiHangSeconds = rx.MultiHangSeconds,
            Slots = [.. now.Slots],
            Trunk = now.Trunk,
            DroppedBlocks = rx.DroppedBlocks,
            Description = src.Description,
            RadioModel = radio?.Model,
            RadioSerial = radio?.Serial ?? src.Serial,
            SampleRate = src.SampleRate,
            CenterHz = src.CenterHz,
            UsableBandwidthHz = src.UsableBandwidthHz,
            Antennas = [.. src.Antennas],
            Antenna = src.Antenna,
            HasBiasT = src.HasBiasT,
            BiasT = src.BiasT,
            HasFmNotch = src.HasFmNotch,
            FmNotch = src.FmNotch,
            Ppm = src.Ppm,
            AutoGain = src.AutoGain,
            GainStep = src.GainStep,
            MaxGainStep = src.MaxGainStep,
            MostGainSteps = src.MostGainSteps,
            ActiveGainStep = src.ActiveGainStep,
            GainDb = src.GainDb,
            Overloaded = src.Overloaded,
            LastRetuneMs = src.LastRetuneMs,
        };
    }
}
