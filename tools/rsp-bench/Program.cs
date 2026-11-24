// rsp-bench: measurements on a real SDRplay RSP, without the window. Receive only.
//
//   rsp-bench                          sample throughput and retune times
//   rsp-bench list                     the radios that are plugged in and free
//   radio=<serial or model> anywhere   use that radio (otherwise the first RSPdx, or the first of any model)
//   rsp-bench listen <MHz> [seconds] [squelch dB] [file]
//                                      run the receiver on a frequency and print its level each second;
//                                      the file takes the audio as raw 48 kHz 32-bit float, stereo;
//                                      the word "auto" anywhere switches the auto (noise) squelch on
//   rsp-bench multi <from MHz> <to MHz> [seconds] [squelch dB] radio=<a> radio=<b> ... [rate=<MS/s>,<MS/s>...]
//                                      several radios at once in this one program, each scanning the range:
//                                      samples received against asked, blocks dropped, retune time and the
//                                      processor time used, each second. With no radio= all the free ones.
//                                      rate= gives each radio's sample rate in turn (7.68 when not given)
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
args = args.Where(a => a != "auto" && a != "fast" && a != "vendoragc" && !a.StartsWith("lna=") && !a.StartsWith("radio=") && !a.StartsWith("rate=")).ToArray();

if (args.Length >= 1 && args[0] == "list")
{
    foreach (var r in SdrPlaySource.ListRadios()) Console.WriteLine($"{r.Model}\t{r.Serial}");
    return;
}

// The radio asked for, by serial number or by model name.
SdrPlaySource NewSource()
{
    var s = new SdrPlaySource { IfAgc = vendorAgc };
    if (lnaWord is not null) { s.AutoGain = false; s.GainStep = int.Parse(lnaWord); }
    if (radio is null) return s;
    var match = SdrPlaySource.ListRadios().FirstOrDefault(r =>
        r.Serial == radio || r.Model.Equals(radio, StringComparison.OrdinalIgnoreCase));
    if (match is null) throw new InvalidOperationException($"No free radio matches \"{radio}\".");
    s.Serial = match.Serial;
    return s;
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
        Console.WriteLine($"  level {rx.LevelDb,7:F1} dB  squelch {(rx.SquelchOpen ? "open  " : "closed")}  roughness {rx.Roughness,7:G4} (limit {rx.RoughnessLimit:G4})  LNA {src.ActiveGainStep}/{src.MaxGainStep}  gain {src.GainDb:F1} dB  overload {src.Overloaded}  underruns {rx.AudioUnderruns}{(rx.Stereo ? "  STEREO" : "")}");
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
    var rest = args.Skip(1).Where(a => a != "plan" && !a.StartsWith("db=") && !a.StartsWith("group=")).ToArray();
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
