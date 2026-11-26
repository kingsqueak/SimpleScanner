// rsp-bench: measurements on a real SDRplay RSP, without the window. Receive only.
//
//   rsp-bench                          sample throughput and retune times
//   rsp-bench list                     the radios that are plugged in and free
//   radio=<serial or model> anywhere   use that radio (otherwise the first RSPdx, or the first of any model)
//   ant=A|B|C, notch anywhere           RSPdx antenna socket (A when not given); the FM/MW notch on
//   rsp-bench listen <MHz> [seconds] [squelch dB] [file]
//                                      run the receiver on a frequency and print its level each second;
//                                      the file takes the audio as raw 48 kHz 32-bit float, stereo;
//                                      the word "auto" anywhere switches the auto (noise) squelch on
//   rsp-bench multi <from MHz> <to MHz> [seconds] [squelch dB] radio=<a> radio=<b> ... [rate=<MS/s>,<MS/s>...]
//                                      several radios at once in this one program, each scanning the range:
//                                      samples received against asked, blocks dropped, retune time and the
//                                      processor time used, each second. With no radio= all the free ones.
//                                      rate= gives each radio's sample rate in turn (7.68 when not given)
//   rsp-bench monitor <MHz,MHz,...|from-to> [seconds] [squelch dB] [file] [hang=<s>] [resume=<s>] [off] [play]
//                                      Multi Monitor: scan a list of frequencies (commas) or a range (a dash; the
//                                      band plan's raster) and print the four slots each second: frequency, mode,
//                                      level, squelch, digital signal; with the processor time used and the blocks
//                                      dropped. The file takes the mixed audio as raw 48 kHz 32-bit float, stereo.
//                                      hang= is the mode's hang time (8 s), resume= the longest stay in a window,
//                                      "off" scans with the mode off (to compare), "play" also plays the sound
//   rsp-bench stalltest [MHz] [seconds] [removed|failure]
//                                      the watchdog: after 5 s the samples are dropped as a jammed USB pipe
//                                      would (or, with "removed" or "failure", the API's event of that name is
//                                      acted out); the radio should be replugged in software and come back.
//                                      Samples a second are printed for the given seconds (30 when not given)
//   rsp-bench handover [MHz] [seconds] radio=<a>
//                                      the radio in a helper program; after 5 s it acts as if jammed, and the
//                                      helper should hand over to a new one that carries on (as in the app)
//   rsp-bench helpers [seconds] [squelch dB] radio=<a>:<from MHz>-<to MHz> radio=<b>:<from>-<to> ...
//                                      each radio in a helper program of its own, as the app runs a second radio
using System.Diagnostics;
using SimpleScanner;
using SimpleScanner.Dsp;
using SimpleScanner.Radio.SdrPlay;

bool autoSquelch = args.Contains("auto"), fast = args.Contains("fast"), vendorAgc = args.Contains("vendoragc");
string? lnaWord = args.FirstOrDefault(a => a.StartsWith("lna="))?[4..];
string[] radios = args.Where(a => a.StartsWith("radio=")).Select(a => a[6..]).ToArray();
string? radio = radios.FirstOrDefault();
string? rateWord = args.FirstOrDefault(a => a.StartsWith("rate="))?[5..];
string? antWord = args.FirstOrDefault(a => a.StartsWith("ant="))?[4..];      // A, B or C (RSPdx)
bool notch = args.Contains("notch");
args = args.Where(a => a != "auto" && a != "fast" && a != "vendoragc" && a != "notch" && !a.StartsWith("lna=") && !a.StartsWith("radio=")
    && !a.StartsWith("rate=") && !a.StartsWith("ant=")).ToArray();

if (args.Length >= 1 && args[0] == "list")
{
    foreach (var r in SdrPlaySource.ListRadios()) Console.WriteLine($"{r.Model}\t{r.Serial}");
    return;
}

// The radio asked for, by serial number or by model name.
SdrPlaySource NewSource()
{
    var s = new SdrPlaySource { IfAgc = vendorAgc, FmNotch = notch };
    if (antWord is not null) s.Antenna = $"Antenna {antWord.ToUpperInvariant()}";
    if (lnaWord is not null) { s.AutoGain = false; s.GainStep = int.Parse(lnaWord); }
    if (radio is null) return s;
    var match = SdrPlaySource.ListRadios().FirstOrDefault(r =>
        r.Serial == radio || r.Model.Equals(radio, StringComparison.OrdinalIgnoreCase));
    if (match is null) throw new InvalidOperationException($"No free radio matches \"{radio}\".");
    s.Serial = match.Serial;
    return s;
}

if (args.Length >= 1 && args[0] == "handover")
{
    // rsp-bench handover [MHz] [seconds] radio=<a>: the radio in a helper program, as the app runs a second radio.
    // After 5 s the helper's radio acts as if jammed; the helper should hand over and a new one carry on.
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    double mhz = args.Length > 1 ? double.Parse(args[1], inv) : 162.4;
    int seconds = args.Length > 2 ? int.Parse(args[2]) : 40;
    string config = AppContext.BaseDirectory.Contains("/Debug/") ? "Debug" : "Release";
    string? root = AppContext.BaseDirectory;
    while (root is not null && !Directory.Exists(Path.Combine(root, "src", "SimpleScanner"))) root = Path.GetDirectoryName(root);
    string app = Path.Combine(root ?? ".", "src", "SimpleScanner", "bin", config, "net10.0", "SimpleScanner");
    var match = SdrPlaySource.ListRadios().FirstOrDefault(r => radio is null || r.Serial == radio || r.Model.Equals(radio, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"No free radio matches \"{radio}\".");
    using var rx = SimpleScanner.Remote.RemoteReceiver.Launch(match.Serial, SdrPlaySource.ListRadios, program: app);
    rx.Failed += why => Console.WriteLine($"  FAILED: {why}");
    var band = BandPlan.Find(mhz * 1e6);
    rx.SetMode(band.Mode, band.BandwidthHz);
    rx.FrequencyHz = mhz * 1e6;
    rx.SquelchDb = -200;
    rx.Volume = 0.3;
    rx.Start();
    Console.WriteLine($"{rx.Source.Description} in a helper: {mhz} MHz, {band.Mode}");
    for (int s = 1; s <= seconds; s++)
    {
        Thread.Sleep(1000);
        Console.WriteLine($"  {DateTime.Now:HH:mm:ss} {s,3} s  running {rx.IsRunning}  level {rx.LevelDb,7:F1} dB  centre {rx.Source.CenterHz / 1e6:F4}  dropped {rx.DroppedBlocks}");
        if (s == 5) { rx.SimulateStall(); Console.WriteLine("  -- stall simulated in the helper"); }
    }
    rx.Stop();
    return;
}

if (args.Length >= 1 && args[0] == "helpers")
{
    // rsp-bench helpers [seconds] [squelch dB] radio=<a>:<from MHz>-<to MHz> radio=<b>:<from>-<to> ...
    // Each radio in a helper program of its own (the app started with --radio-host), as the app runs a
    // second radio: each scans its range and plays what it stops on. Reports what the helpers say.
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    int seconds = args.Length > 1 ? int.Parse(args[1]) : 20;
    double squelch = args.Length > 2 ? double.Parse(args[2], inv) : -95;
    string config = AppContext.BaseDirectory.Contains("/Debug/") ? "Debug" : "Release";
    string? root = AppContext.BaseDirectory;
    while (root is not null && !Directory.Exists(Path.Combine(root, "src", "SimpleScanner"))) root = Path.GetDirectoryName(root);
    string app = Path.Combine(root ?? ".", "src", "SimpleScanner", "bin", config, "net10.0", "SimpleScanner");
    var free = SdrPlaySource.ListRadios();
    var rxs = new List<(SimpleScanner.Remote.RemoteReceiver Rx, List<ScanChannel> List, string Name)>();
    try
    {
        foreach (string word in radios)
        {
            string[] parts = word.Split(':');
            var match = free.FirstOrDefault(r => r.Serial == parts[0] || r.Model.Equals(parts[0], StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No free radio matches \"{parts[0]}\".");
            string[] range = (parts.Length > 1 ? parts[1] : "160-163").Split('-');
            double lo = double.Parse(range[0], inv) * 1e6, hi = double.Parse(range[1], inv) * 1e6;
            var list = new List<ScanChannel>();
            for (double f = lo; f <= hi + 1;)
            {
                var band = BandPlan.Find(f);
                double channel = Math.Ceiling(f / band.StepHz - 1e-6) * band.StepHz;
                if (channel > hi + 1) break;
                if (!BandPlan.Find(channel).Blocked) list.Add(new ScanChannel($"{channel / 1e6:F4}", Math.Round(channel / 10) * 10, Mode.Nfm, BandPlan.NfmNarrow));
                f = channel + band.StepHz;
            }
            var rx = SimpleScanner.Remote.RemoteReceiver.Launch(match.Serial, SdrPlaySource.ListRadios, program: app);
            rx.Failed += why => Console.WriteLine($"  {match.Model} FAILED: {why}");
            rx.SquelchDb = squelch;
            rx.Volume = 0.3;
            rx.ScanSkipSeconds = 5;
            rxs.Add((rx, list, match.Model));
        }
        foreach (var (rx, _, name) in rxs)
        {
            var sw1 = Stopwatch.StartNew();
            rx.Start();
            Console.WriteLine($"{name}: started in {sw1.ElapsedMilliseconds} ms: {rx.Source.Description}, antennas {string.Join("/", rx.Source.Antennas)}, bias T {(rx.Source.HasBiasT ? "yes" : "no")}");
        }
        Thread.Sleep(1500);
        foreach (var (rx, list, _) in rxs) rx.StartScan(list);
        var last = new long[rxs.Count];
        for (int s = 1; s <= seconds; s++)
        {
            Thread.Sleep(1000);
            var line = new System.Text.StringBuilder($"  {s,3} s");
            for (int k = 0; k < rxs.Count; k++)
            {
                var rx = rxs[k].Rx;
                long p = rx.ScanPasses;
                line.Append($"  [{rxs[k].Name}] {p - last[k],3} passes, {rx.DroppedBlocks} dropped, retune {rx.Source.LastRetuneMs:F0} ms, "
                    + (rx.ScanListening ? $"ON {rx.ScanChannel?.Name} {rx.LevelDb:F0} dB {rx.Digital}" : rx.Scanning ? "scanning" : "NOT SCANNING"));
                last[k] = p;
            }
            Console.WriteLine(line);
        }
        foreach (var (rx, _, name) in rxs)
        {
            rx.Stop();
            Console.WriteLine($"{name}: stopped, running {rx.IsRunning}");
        }
    }
    finally
    {
        foreach (var (rx, _, _) in rxs) rx.Dispose();
    }
    Thread.Sleep(500);
    Console.WriteLine($"helpers left: {Process.GetProcessesByName("SimpleScanner").Length}");
    return;
}

if (args.Length >= 3 && args[0] == "multi")
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    double lo = double.Parse(args[1], inv) * 1e6, hi = double.Parse(args[2], inv) * 1e6;
    int seconds = args.Length > 3 ? int.Parse(args[3]) : 20;
    double squelch = args.Length > 4 ? double.Parse(args[4], inv) : -95;
    double[] rates = rateWord?.Split(',').Select(r => double.Parse(r, inv) * 1e6).ToArray() ?? [];

    var free = SdrPlaySource.ListRadios();
    var chosen = radios.Length == 0 ? free.ToList() : radios.Select(want =>
        free.FirstOrDefault(r => r.Serial == want || r.Model.Equals(want, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"No free radio matches \"{want}\".")).ToList();
    if (chosen.Count == 0) throw new InvalidOperationException("No free radio.");

    var list = new List<ScanChannel>();
    for (double f = lo; f <= hi + 1;)
    {
        var band = BandPlan.Find(f);
        double channel = Math.Ceiling(f / band.StepHz - 1e-6) * band.StepHz;
        if (channel > hi + 1) break;
        if (!BandPlan.Find(channel).Blocked) list.Add(new ScanChannel("", Math.Round(channel / 10) * 10, Mode.Nfm, BandPlan.NfmNarrow));
        f = channel + band.StepHz;
    }

    // One receiver for each radio, all made and started on this thread, as the app will.
    var rxs = new List<Receiver>();
    var counts = new long[chosen.Count];
    for (int k = 0; k < chosen.Count; k++)
    {
        int at = k;
        var src = new SdrPlaySource { Serial = chosen[k].Serial, OnlyThisRadio = true };
        src.Samples += (i, q, retuned) => Interlocked.Add(ref counts[at], i.Length);
        var rx = new Receiver(src, playAudio: false, sampleRate: k < rates.Length ? rates[k] : Receiver.DefaultSampleRate)
            { ScanSkipSeconds = 2, ScanHangSeconds = 0.3 };
        rx.SquelchDb = squelch;
        rxs.Add(rx);
    }
    try
    {
        foreach (var rx in rxs) rx.Start();
        Thread.Sleep(1500);                  // let the gains settle
        foreach (var rx in rxs) rx.StartScan(list);
        Console.WriteLine($"{rxs.Count} radios, {list.Count} channels {lo / 1e6}-{hi / 1e6} MHz, squelch {squelch} dB, {Environment.ProcessorCount} processors");
        for (int k = 0; k < rxs.Count; k++)
            Console.WriteLine($"  [{k}] {rxs[k].Source.Description} at {rxs[k].SampleRate / 1e6} MS/s, {rxs[k].ScanWindows} windows");

        var me = Process.GetCurrentProcess();
        var sw = Stopwatch.StartNew();
        var lastCounts = counts.Select((_, k) => Interlocked.Read(ref counts[k])).ToArray();
        var lastPasses = new long[rxs.Count];
        var firstDrops = rxs.Select(r => r.DroppedBlocks).ToArray();
        var lastCpu = me.TotalProcessorTime;
        var cpuStart = lastCpu;
        double lastT = 0, worstShort = 0;
        for (int s = 1; s <= seconds; s++)
        {
            Thread.Sleep(Math.Max(0, (int)(s * 1000 - sw.ElapsedMilliseconds)));
            double t = sw.Elapsed.TotalSeconds, dt = t - lastT;
            me.Refresh();
            var cpu = me.TotalProcessorTime;
            var line = new System.Text.StringBuilder($"  {t,5:F1} s  cpu {(cpu - lastCpu).TotalSeconds / dt * 100,4:F0}%");
            for (int k = 0; k < rxs.Count; k++)
            {
                long c = Interlocked.Read(ref counts[k]), p = rxs[k].ScanPasses;
                double got = (c - lastCounts[k]) / dt / rxs[k].SampleRate * 100;
                if (s > 1) worstShort = Math.Max(worstShort, 100 - got);
                line.Append($"  [{k}] {got,5:F1}% of samples, {rxs[k].DroppedBlocks - firstDrops[k]} dropped, {p - lastPasses[k]} passes, retune {rxs[k].Source.LastRetuneMs:F0} ms{(rxs[k].ScanListening ? $", on {rxs[k].FrequencyHz / 1e6:F4}" : "")}");
                lastCounts[k] = c;
                lastPasses[k] = p;
            }
            Console.WriteLine(line);
            lastCpu = cpu;
            lastT = t;
        }
        me.Refresh();
        Console.WriteLine($"mean cpu {(me.TotalProcessorTime - cpuStart).TotalSeconds / sw.Elapsed.TotalSeconds * 100:F0}% of one processor; "
            + $"dropped blocks {string.Join(" / ", rxs.Select((r, k) => r.DroppedBlocks - firstDrops[k]))}; "
            + $"passes {string.Join(" / ", rxs.Select(r => r.ScanPasses))}; worst second {worstShort:F1}% short of samples");
    }
    finally
    {
        foreach (var rx in rxs) rx.Dispose();
    }
    return;
}

if (args.Length >= 2 && args[0] == "monitor")
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    bool modeOff = args.Contains("off"), play = args.Contains("play");
    string? hangWord = args.FirstOrDefault(a => a.StartsWith("hang="))?[5..], resumeWord = args.FirstOrDefault(a => a.StartsWith("resume="))?[7..];
    args = args.Where(a => a != "off" && a != "play" && !a.StartsWith("hang=") && !a.StartsWith("resume=")).ToArray();
    int seconds = args.Length > 2 ? int.Parse(args[2]) : 30;
    double squelch = args.Length > 3 ? double.Parse(args[3], inv) : -95;

    // A list (162.4,162.55) in each frequency's own mode by the band plan, or a range (154-156) on its raster.
    var list = new List<ScanChannel>();
    if (args[1].Contains(',') || !args[1].Contains('-'))
    {
        foreach (string word in args[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            double hz = Math.Round(double.Parse(word, inv) * 1e6 / 10) * 10;
            var band = BandPlan.Find(hz);
            list.Add(new ScanChannel($"{hz / 1e6:F4}", hz, band.Mode, band.BandwidthHz));
        }
    }
    else
    {
        string[] range = args[1].Split('-');
        double lo = double.Parse(range[0], inv) * 1e6, hi = double.Parse(range[1], inv) * 1e6;
        for (double f = lo; f <= hi + 1;)
        {
            var band = BandPlan.Find(f);
            double channel = Math.Ceiling(f / band.StepHz - 1e-6) * band.StepHz;
            if (channel > hi + 1) break;
            band = BandPlan.Find(channel);
            if (!band.Blocked) list.Add(new ScanChannel($"{channel / 1e6:F4}", Math.Round(channel / 10) * 10, band.Mode, band.BandwidthHz));
            f = channel + band.StepHz;
        }
    }

    using var rx = new Receiver(NewSource(), playAudio: play) { MultiMonitor = !modeOff };
    rx.SquelchDb = squelch;
    rx.AutoSquelch = autoSquelch;
    rx.Volume = 0.5;
    if (hangWord is not null) rx.MultiHangSeconds = double.Parse(hangWord, inv);
    if (resumeWord is not null) rx.ResumeSeconds = double.Parse(resumeWord, inv);
    using var capture = args.Length > 4 ? File.Create(args[4]) : null;
    if (capture is not null)
        rx.AudioOutput += a => capture.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a));
    rx.Start();
    Thread.Sleep(1500);                      // let the gain settle
    rx.StartScan(list);
    Console.WriteLine($"{rx.Source.Description} at {rx.SampleRate / 1e6} MS/s: {list.Count} channels in {rx.ScanWindows} windows, squelch {squelch} dB{(autoSquelch ? ", auto" : "")}, "
        + $"Multi Monitor {(rx.MultiMonitor ? $"on, hang {rx.MultiHangSeconds} s" : "off")}{(rx.ResumeSeconds is { } r ? $", resume {r} s" : "")}, digital {(rx.DigitalAvailable ? "available" : "MISSING")}");

    string[] seats = ["33%L", "33%R", "L   ", "R   "];
    var me = Process.GetCurrentProcess();
    var sw = Stopwatch.StartNew();
    var lastCpu = me.TotalProcessorTime;
    var cpuStart = lastCpu;
    double lastT = 0, cpuWithFour = 0, timeWithFour = 0, worstCpu = 0;
    long lastPasses = 0, firstDrops = rx.DroppedBlocks;
    int most = 0;
    var heldSeconds = new int[5];
    int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
    var pauseStart = GC.GetTotalPauseDuration();
    var everHeld = new SortedSet<double>();
    var tickCpu = lastCpu;
    double tickT = 0;
    int tickHeld = 0;
    for (int s = 1; s <= seconds; s++)
    {
        // Ten looks a second: the load with four slots held is taken over the tenths that began and ended with four.
        for (int tick = 9; tick >= 1; tick--)
        {
            Thread.Sleep(Math.Max(0, (int)(s * 1000 - tick * 100 - sw.ElapsedMilliseconds)));
            me.Refresh();
            var now = me.TotalProcessorTime;
            double at = sw.Elapsed.TotalSeconds;
            int holding = rx.Slots.Count(x => x.Holding);
            if (holding == 4 && tickHeld == 4)
            {
                cpuWithFour += (now - tickCpu).TotalSeconds;
                timeWithFour += at - tickT;
            }
            (tickCpu, tickT, tickHeld) = (now, at, holding);
        }
        Thread.Sleep(Math.Max(0, (int)(s * 1000 - sw.ElapsedMilliseconds)));
        double t = sw.Elapsed.TotalSeconds, dt = t - lastT;
        me.Refresh();
        var cpu = me.TotalProcessorTime;
        double load = (cpu - lastCpu).TotalSeconds / dt * 100;
        var slots = rx.Slots;
        int held = slots.Count(x => x.Holding);
        most = Math.Max(most, held);
        heldSeconds[held]++;
        worstCpu = Math.Max(worstCpu, load);
        if (held == 4 && tickHeld == 4)
        {
            cpuWithFour += (cpu - tickCpu).TotalSeconds;
            timeWithFour += t - tickT;
        }
        (tickCpu, tickT, tickHeld) = (cpu, t, held);
        long p = rx.ScanPasses;
        var src = rx.Source;
        Console.WriteLine($"  {t,5:F1} s  cpu {load,4:F0}%  dropped {rx.DroppedBlocks - firstDrops}  passes {p - lastPasses,3}  "
            + $"LNA {src.ActiveGainStep} gain {src.GainDb:F0} dB{(src.Overloaded ? " OVERLOAD" : "")}  level {rx.LevelDb,5:F0} dB  {(rx.ScanListening ? "holding" : rx.Scanning ? "scanning" : "NOT SCANNING")}");
        foreach (var x in slots)
        {
            if (!x.Holding) continue;
            everHeld.Add(x.FrequencyHz);
            Console.WriteLine($"           [{x.Seat}] {seats[x.Seat]}  {x.FrequencyHz / 1e6,9:F4} MHz  {x.Mode,-3} {x.BandwidthHz / 1000,5:F1} kHz  {x.LevelDb,5:F0} dB  {(x.SquelchOpen ? "open" : "shut")}"
                + $"{(x.Digital is { } d ? "  " + d : "")}{(x.Encrypted ? "  (encrypted)" : "")}{(x.NothingToHear ? "  (nothing to hear)" : "")}");
        }
        lastCpu = cpu;
        lastT = t;
        lastPasses = p;
    }
    me.Refresh();
    Console.WriteLine($"mean cpu {(me.TotalProcessorTime - cpuStart).TotalSeconds / sw.Elapsed.TotalSeconds * 100:F0}% of one processor, worst second {worstCpu:F0}%"
        + (timeWithFour > 0 ? $", with four slots held {cpuWithFour / timeWithFour * 100:F0}% (over {timeWithFour:F1} s)" : "")
        + $"; dropped blocks {rx.DroppedBlocks - firstDrops}; passes {rx.ScanPasses}");
    Console.WriteLine($"seconds with 0/1/2/3/4 slots held: {string.Join("/", heldSeconds)}; most at once {most}; "
        + $"frequencies held: {string.Join(" ", everHeld.Select(f => $"{f / 1e6:F4}"))}");
    Console.WriteLine($"garbage collections {GC.CollectionCount(0) - gen0} (of which full {GC.CollectionCount(2) - gen2}), paused {(GC.GetTotalPauseDuration() - pauseStart).TotalMilliseconds:F1} ms in all"
        + (play ? $"; audio underruns {rx.AudioUnderruns}" : ""));
    rx.Stop();
    return;
}

if (args.Length >= 1 && args[0] == "stalltest")
{
    string? acted = args.FirstOrDefault(a => a is "removed" or "failure");
    args = args.Where(a => a is not ("removed" or "failure")).ToArray();
    double mhz = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 162.4;
    int seconds = args.Length > 2 ? int.Parse(args[2]) : 30;
    var src = NewSource();
    long count = 0;
    string? failed = null;
    src.Samples += (i, q, retuned) => Interlocked.Add(ref count, i.Length);
    src.StallTrace = what => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] watchdog: {what}");
    src.Failed += why => failed = why;
    src.Start(Receiver.DefaultSampleRate, mhz * 1e6);
    Console.WriteLine($"{src.Description}: {mhz} MHz, {Receiver.DefaultSampleRate / 1e6} MS/s");
    for (int s = 1; s <= seconds && failed is null; s++)
    {
        Thread.Sleep(1000);
        long got = Interlocked.Exchange(ref count, 0);
        Console.WriteLine($"  {DateTime.Now:HH:mm:ss} {s,3} s  {got / 1e6,6:F2} MS  running {src.IsRunning}{(src.SimulateStall ? "  (stall simulated)" : "")}");
        if (s == 5)
        {
            if (acted is null) src.SimulateStall = true;
            else src.SimulateApiEvent(acted == "removed");
        }
    }
    if (failed is not null) Console.WriteLine($"  FAILED: {failed}");
    src.Stop();
    return;
}

if (args.Length >= 2 && args[0] == "listen")
{
    double mhz = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    int seconds = args.Length > 2 ? int.Parse(args[2]) : 10;
    using var rx = new Receiver(NewSource());
    var band = BandPlan.Find(mhz * 1e6);
    rx.SetMode(band.Mode, band.BandwidthHz);
    rx.FrequencyHz = mhz * 1e6;
    rx.SquelchDb = args.Length > 3 ? double.Parse(args[3]) : -200;
    rx.AutoSquelch = autoSquelch;
    rx.Volume = 1;
    // A fifth argument names a file to take the audio as raw 48 kHz 32-bit float.
    using var capture = args.Length > 4 ? File.Create(args[4]) : null;
    if (capture is not null)
        rx.AudioOutput += a => capture.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a));
    rx.Start();
    Console.WriteLine($"sound devices: {string.Join(" | ", SimpleScanner.Audio.AudioOut.Devices())}");
    Console.WriteLine($"{rx.Source.Description}: {mhz} MHz, {band.Name}, {band.Mode} {band.BandwidthHz / 1000} kHz");
    // "fast": ten reports a second, with how many times the squelch shut in between.
    for (int s = 0; s < seconds * (fast ? 10 : 1); s++)
    {
        Thread.Sleep(fast ? 100 : 1000);
        var src = rx.Source;
        Console.WriteLine($"  level {rx.LevelDb,7:F1} dB  squelch {(rx.SquelchOpen ? "open  " : "closed")}  roughness {rx.Roughness,7:G4} (limit {rx.RoughnessLimit:G4})  LNA {src.ActiveGainStep}/{src.MaxGainStep}  IF GR {(src as SdrPlaySource)?.IfGainReductionDb}  gain {src.GainDb:F1} dB  overload {src.Overloaded}  underruns {rx.AudioUnderruns}{(rx.Stereo ? "  STEREO" : "")}");
    }
    rx.Stop();
    return;
}

if (args.Length >= 3 && args[0] == "hunt")
{
    // rsp-bench hunt <from MHz> <to MHz> [seconds] [squelch dB]: search a range on the band plan's
    // raster; on each signal found wait a moment for it to identify itself, report, and move on.
    double lo = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) * 1e6;
    double hi = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) * 1e6;
    int seconds = args.Length > 3 ? int.Parse(args[3]) : 30;
    using var rx = new Receiver(NewSource(), playAudio: false) { ScanSkipSeconds = 3600, ScanHangSeconds = 0.3 };
    rx.SquelchDb = args.Length > 4 ? double.Parse(args[4]) : -100;
    var list = new List<ScanChannel>();
    for (double f = lo; f <= hi + 1;)
    {
        var band = BandPlan.Find(f);
        double channel = Math.Ceiling(f / band.StepHz - 1e-6) * band.StepHz;     // the raster counts from zero
        if (channel > hi + 1) break;
        if (!BandPlan.Find(channel).Blocked) list.Add(new ScanChannel("", Math.Round(channel / 10) * 10, Mode.Nfm, BandPlan.NfmNarrow));
        f = channel + band.StepHz;
    }
    rx.Start();
    Thread.Sleep(1500);
    rx.StartScan(list);
    Console.WriteLine($"hunting {lo / 1e6}-{hi / 1e6} MHz: {list.Count} channels in {rx.ScanWindows} windows, squelch {rx.SquelchDb} dB, digital {(rx.DigitalAvailable ? "available" : "MISSING")}");
    var sw = Stopwatch.StartNew();
    var seen = new Dictionary<double, string>();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        Thread.Sleep(10);
        if (!rx.ScanListening) continue;
        double f = rx.FrequencyHz, level = rx.LevelDb;
        string what = "";
        var wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 1500 && rx.ScanListening && rx.FrequencyHz == f)
        {
            Thread.Sleep(20);
            level = Math.Max(level, rx.LevelDb);
            if (rx.Digital is { } d) what = d.ToString();
            if (what.Contains("system") || what.Contains("TG") || what.Contains(" · to ")) break;
        }
        if (rx.FrequencyHz == f)
        {
            if (!seen.ContainsKey(f) || (what.Length > 0 && seen[f].Length == 0))
                Console.WriteLine($"  {sw.Elapsed.TotalSeconds,5:F1} s  {f / 1e6,9:F4} MHz  {level,5:F0} dB  {(what.Length > 0 ? what : "analogue or unknown")}");
            seen[f] = what;
            rx.SkipChannel();
        }
    }
    Console.WriteLine($"{seen.Count} active frequencies, {seen.Values.Count(v => v.Length > 0)} digital; {rx.ScanPasses} passes");
    rx.Stop();
    return;
}

if (args.Length >= 1 && args[0] == "grtest")
{
    // rsp-bench grtest [MHz a] [MHz b] [GR a] [GR b]: hop between two tunings that are given different IF gain
    // reductions, sent with the frequency, and show the level of the samples in 5 ms steps after each retune,
    // with the radio's gain reports: is the new reduction in force when the retune is confirmed?
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    double a = (args.Length > 1 ? double.Parse(args[1], inv) : 146.275) * 1e6, b = (args.Length > 2 ? double.Parse(args[2], inv) : 156.95) * 1e6;
    int grA = args.Length > 3 ? int.Parse(args[3]) : 25, grB = args.Length > 4 ? int.Parse(args[4]) : 55;
    var src = NewSource();
    src.AutoGain = false;
    src.FreezeIfGain = true;
    src.RememberIfGr(a, grA);
    src.RememberIfGr(b, grB);
    var gate = new object();
    var sw = Stopwatch.StartNew();
    double tuneAt = 0;
    var bins = new (double Sum, long N)[40];
    var notes = new List<string>();
    src.Samples += (i, q, retuned) =>
    {
        double sum = 0;
        for (int k = 0; k < i.Length; k++) sum += i[k] * i[k] + q[k] * q[k];
        lock (gate)
        {
            int bin = (int)((sw.Elapsed.TotalMilliseconds - tuneAt) / 5);
            if (retuned) notes.Add($"confirmed +{sw.Elapsed.TotalMilliseconds - tuneAt:F1} ms");
            if (bin >= 0 && bin < bins.Length) bins[bin] = (bins[bin].Sum + sum, bins[bin].N + i.Length);
        }
    };
    src.GainTrace = (ms, gr, lna, gain) => { lock (gate) notes.Add($"report +{sw.Elapsed.TotalMilliseconds - tuneAt:F1} ms GR {gr} gain {gain:F1}"); };
    src.Start(Receiver.DefaultSampleRate, a);
    Thread.Sleep(1000);
    Console.WriteLine($"{src.Description}: {a / 1e6} MHz at GR {grA} <-> {b / 1e6} MHz at GR {grB}; level of the samples (dBFS) in 5 ms steps from the retune");
    for (int hop = 0; hop < 12; hop++)
    {
        double to = hop % 2 == 0 ? b : a;
        lock (gate) { Array.Clear(bins); notes.Clear(); tuneAt = sw.Elapsed.TotalMilliseconds; }
        src.Tune(to);
        Thread.Sleep(200);
        lock (gate)
            Console.WriteLine($"  to {to / 1e6,8:F3} (GR {(hop % 2 == 0 ? grB : grA)}): "
                + string.Join(" ", bins.Take(30).Select(x => x.N == 0 ? "  .." : $"{10 * Math.Log10(x.Sum / x.N + 1e-12),4:F0}"))
                + "\n      " + string.Join("; ", notes));
    }
    src.Stop();
    return;
}

if (args.Length >= 1 && args[0] == "stored")
{
    // rsp-bench stored [seconds] [squelch dB] [plan] [db=<file>] group=<name> group=<name> ...
    // Scan the stored channels of these groups, as the app does for a radio with those groups ticked,
    // and time every retune. "plan" only prints the windows and does not open a radio. db= reads
    // another database (a copy). The word "auto" switches the auto squelch on, "vendoragc" uses the
    // service's IF AGC in place of ours, and lna=<n> fixes the LNA state.
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    bool planOnly = args.Contains("plan");
    string? dbFile = args.FirstOrDefault(a => a.StartsWith("db="))?[3..];
    var groups = args.Where(a => a.StartsWith("group=")).Select(a => a[6..]).ToList();
    var rest = args.Skip(1).Where(a => a != "plan" && a != "gains" && !a.StartsWith("add=") && !a.StartsWith("db=") && !a.StartsWith("group=")).ToArray();
    int seconds = rest.Length > 0 ? int.Parse(rest[0]) : 30;
    double squelch = rest.Length > 1 ? double.Parse(rest[1], inv) : -100;
    var list = MemoryStore.Load(dbFile).Channels
        .Where(c => c.Scan && (groups.Count == 0 || groups.Any(g => MemoryStore.InGroup(c, g))))
        .GroupBy(c => Math.Round(c.FrequencyHz / 100)).OrderBy(g => g.First().FrequencyHz)
        .Select(g =>
        {
            var c = g.First();
            var band = BandPlan.Find(c.FrequencyHz);
            var mode = c.Mode ?? band.Mode;
            double width = c.BandwidthHz ?? (mode == band.Mode ? band.BandwidthHz
                : mode switch { Mode.Am => BandPlan.AmAir, Mode.Wfm => BandPlan.Wfm, _ => BandPlan.NfmWide });
            return new ScanChannel(c.Name, c.FrequencyHz, mode, width);
        })
        .Where(c => !BandPlan.IsBlocked(c.FrequencyHz, c.BandwidthHz)).ToList();
    // add=<MHz> puts one more frequency in the list, whether or not it is stored or ticked.
    foreach (string word in args.Where(a => a.StartsWith("add=")))
    {
        double hz = double.Parse(word[4..], inv) * 1e6;
        var band = BandPlan.Find(hz);
        list.Add(new ScanChannel($"added {hz / 1e6:F4}", hz, band.Mode, band.BandwidthHz));
    }
    list = list.OrderBy(c => c.FrequencyHz).ToList();
    double usable = SdrPlaySource.BandwidthFor(Receiver.DefaultSampleRate) * 1000.0 * 0.92;
    var plan = ScanPlan.Build(list, usable);
    Console.WriteLine($"{list.Count} channels in {plan.Count} windows (usable {usable / 1e6:F2} MHz)");
    foreach (var w in plan)
        Console.WriteLine($"  centre {w.CenterHz / 1e6,9:F4} MHz  {w.Channels.Length,3} channels  {list[w.Channels[0]].FrequencyHz / 1e6:F4} - {list[w.Channels[^1]].FrequencyHz / 1e6:F4}  {list[w.Channels[0]].Mode}");
    if (planOnly) return;

    var src = NewSource();
    var gate = new object();
    var trace = new List<(double At, double Hz, double CallMs, double Ms, string How)>();
    var clock = Stopwatch.StartNew();
    src.RetuneTrace = (hz, callMs, ms, how) => { lock (gate) trace.Add((clock.Elapsed.TotalSeconds, hz, callMs, ms, how)); };
    // The word "gains" prints every gain report: when it came after the retune, and what it said.
    if (args.Contains("gains"))
        src.GainTrace = (ms, gr, lna, gain) => Console.WriteLine($"      gain report {clock.Elapsed.TotalSeconds,6:F3} s  centre {src.CenterHz / 1e6,9:F4}  +{ms,6:F1} ms after retune  IF GR {gr,2}  LNA GR {lna,2}  gain {gain,5:F1}  (sent GR {src.IfGainReductionDb}, LNA {src.ActiveGainStep})");
    using var rx = new Receiver(src, playAudio: false) { ResumeSeconds = 15 };
    rx.SquelchDb = squelch;
    rx.AutoSquelch = autoSquelch;
    rx.Start();
    Thread.Sleep(1500);
    lock (gate) trace.Clear();
    clock.Restart();
    rx.StartScan(list);
    Console.WriteLine($"{src.Description}: squelch {squelch} dB{(autoSquelch ? ", auto" : "")}");
    long lastP = 0;
    int seenTo = 0;
    string was = "";
    double next = 1;
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        Thread.Sleep(20);
        string now = rx.ScanListening ? $"{rx.ScanChannel?.Name} ({rx.FrequencyHz / 1e6:F4} MHz)" : "";
        if (now != was && now.Length > 0) Console.WriteLine($"  {clock.Elapsed.TotalSeconds,5:F1} s  stopped on {now} at {rx.LevelDb:F0} dB {rx.Digital}");
        was = now;
        if (clock.Elapsed.TotalSeconds < next) continue;
        next += 1;
        List<(double At, double Hz, double CallMs, double Ms, string How)> part;
        lock (gate) { part = trace.Skip(seenTo).ToList(); seenTo = trace.Count; }
        long p = rx.ScanPasses;
        var ok = part.Where(t => t.How == "ok").Select(t => t.Ms).OrderBy(m => m).ToList();
        Console.WriteLine($"  {clock.Elapsed.TotalSeconds,5:F1} s  {p - lastP,2} passes  {part.Count,3} retunes: ok {ok.Count} (median {(ok.Count > 0 ? ok[ok.Count / 2] : 0):F0} ms, max {(ok.Count > 0 ? ok[^1] : 0):F0} ms), "
            + $"timeouts {part.Count(t => t.How == "TIMEOUT")}, refused {part.Count(t => t.How.StartsWith("refused"))}, call max {(part.Count > 0 ? part.Max(t => t.CallMs) : 0):F0} ms  "
            + $"LNA {src.ActiveGainStep}/{src.MaxGainStep} IF GR {src.IfGainReductionDb} gain {src.GainDb:F0} dB{(src.Overloaded ? " OVERLOAD" : "")}  dropped {rx.DroppedBlocks}");
        lastP = p;
    }
    rx.Stop();
    Console.WriteLine("by window (the centre retuned to):");
    List<(double At, double Hz, double CallMs, double Ms, string How)> all;
    lock (gate) all = trace.ToList();
    foreach (var g in all.GroupBy(t => t.Hz).OrderBy(g => g.Key))
    {
        var ok = g.Where(t => t.How == "ok").Select(t => t.Ms).OrderBy(m => m).ToList();
        Console.WriteLine($"  {g.Key / 1e6,9:F4} MHz  {g.Count(),4} retunes  ok median {(ok.Count > 0 ? ok[ok.Count / 2] : 0),5:F1} ms  max {(ok.Count > 0 ? ok[^1] : 0),5:F1} ms  call max {g.Max(t => t.CallMs),5:F1} ms  timeouts {g.Count(t => t.How == "TIMEOUT"),3}  refused {g.Count(t => t.How.StartsWith("refused")),3} {g.FirstOrDefault(t => t.How.StartsWith("refused")).How}");
    }
    Console.WriteLine($"{all.Count} retunes, {all.Count(t => t.How == "TIMEOUT")} timeouts, {all.Count(t => t.How.StartsWith("refused"))} refused, {rx.ScanPasses} passes in {seconds} s");
    return;
}

if (args.Length >= 1 && args[0] == "scan")
{
    // rsp-bench scan [seconds] [squelch dB] [from MHz] [to MHz]: scan the ticked stored channels
    // (those in the range, if one is given) and report.
    double scanLo = args.Length > 4 ? double.Parse(args[3]) * 1e6 : 0, scanHi = args.Length > 4 ? double.Parse(args[4]) * 1e6 : double.MaxValue;
    int seconds = args.Length > 1 ? int.Parse(args[1]) : 10;
    using var rx = new Receiver(NewSource(), playAudio: false);
    rx.SquelchDb = args.Length > 2 ? double.Parse(args[2]) : -95;
    rx.AutoSquelch = autoSquelch;
    var list = MemoryStore.Load().Channels.Where(c => c.Scan && c.FrequencyHz >= scanLo && c.FrequencyHz <= scanHi).Select(c =>
    {
        var band = BandPlan.Find(c.FrequencyHz);
        return new ScanChannel(c.Name, c.FrequencyHz, c.Mode ?? band.Mode, c.BandwidthHz ?? band.BandwidthHz);
    }).ToList();
    rx.Start();
    Thread.Sleep(1500);                      // let the gain settle
    rx.StartScan(list);
    Console.WriteLine($"{rx.Source.Description} at {rx.SampleRate / 1e6} MS/s: {list.Count} channels in {rx.ScanWindows} windows, squelch {rx.SquelchDb} dB{(autoSquelch ? ", auto" : "")}");
    var sw = Stopwatch.StartNew();
    string last = "";
    long lastPasses = 0;
    double nextReport = 1;
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        Thread.Sleep(20);
        string now = rx.ScanListening ? rx.ScanChannel?.Name ?? "" : "";
        if (now != last && now.Length > 0) Console.WriteLine($"  {sw.Elapsed.TotalSeconds,5:F1} s  stopped on {now} ({rx.FrequencyHz / 1e6:F4} MHz) at {rx.LevelDb:F0} dB");
        last = now;
        if (sw.Elapsed.TotalSeconds >= nextReport)
        {
            long p = rx.ScanPasses;
            Console.WriteLine($"  {sw.Elapsed.TotalSeconds,5:F1} s  {p - lastPasses} passes/s  LNA {rx.Source.ActiveGainStep}  gain {rx.Source.GainDb:F0} dB  retune {rx.Source.LastRetuneMs:F0} ms  best level {rx.LevelDb:F0} dB");
            lastPasses = p;
            nextReport += 1;
        }
    }
    rx.Stop();
    return;
}

using var source = NewSource();
long samples = 0;
int retunes = 0;
source.Samples += (i, q, retuned) =>
{
    Interlocked.Add(ref samples, i.Length);
    if (retuned) Interlocked.Increment(ref retunes);
};

foreach (double rate in new[] { 2.4e6, 6.0e6, 7.68e6 })
{
    Interlocked.Exchange(ref samples, 0);
    source.Start(rate, 155e6);
    Thread.Sleep(500);                       // let the stream settle
    long before = Interlocked.Read(ref samples);
    var sw = Stopwatch.StartNew();
    Thread.Sleep(3000);
    double got = (Interlocked.Read(ref samples) - before) / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"{source.Description}: asked {rate / 1e6:F1} MS/s, received {got / 1e6:F3} MS/s");
    if (rate != 7.68e6) source.Stop();
}

// Retunes at the last rate. Each waits for the radio to confirm before the next.
double Hop(double hz)
{
    int seen = Volatile.Read(ref retunes);
    source.Tune(hz);
    var sw = Stopwatch.StartNew();
    while (Volatile.Read(ref retunes) == seen && sw.ElapsedMilliseconds < 1000) Thread.SpinWait(200);
    return source.LastRetuneMs;
}

void Series(string name, double a, double b, int count)
{
    var times = new List<double>();
    var sw = Stopwatch.StartNew();
    for (int k = 0; k < count; k++) times.Add(Hop(k % 2 == 0 ? b : a));
    double total = sw.Elapsed.TotalSeconds;
    times.Sort();
    Console.WriteLine($"{name,-34} median {times[count / 2],6:F1} ms  max {times[^1],6:F1} ms  {count / total,6:F1} hops/s");
}

foreach (double rate in new[] { 7.68e6 })
{
    if (rate != 7.68e6)
    {
        source.Stop();
        source.Start(rate, 155e6);
        Thread.Sleep(500);
    }
    Console.WriteLine($"-- retune at {rate / 1e6:F1} MS/s");
    Series("155 <-> 156 MHz (same band)", 155e6, 156e6, 40);
    Series("121 <-> 155 MHz (airband/VHF)", 121e6, 155e6, 40);
    Series("155 <-> 460 MHz (VHF/UHF)", 155e6, 460e6, 40);
    Series("460 <-> 855 MHz (UHF/800)", 460e6, 855e6, 40);
}
source.Stop();
