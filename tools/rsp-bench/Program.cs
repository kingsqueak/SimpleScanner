// rsp-bench: measurements on a real SDRplay RSP, without the window. Receive only.
//
//   rsp-bench                          sample throughput and retune times
//   rsp-bench listen <MHz> [seconds] [squelch dB] [file]
//                                      run the receiver on a frequency and print its level each second;
//                                      the file takes the audio as raw 48 kHz 32-bit float, stereo
using System.Diagnostics;
using SimpleScanner;
using SimpleScanner.Dsp;
using SimpleScanner.Radio.SdrPlay;

if (args.Length >= 2 && args[0] == "listen")
{
    double mhz = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
    int seconds = args.Length > 2 ? int.Parse(args[2]) : 10;
    using var rx = new Receiver(new SdrPlaySource());
    var band = BandPlan.Find(mhz * 1e6);
    rx.SetMode(band.Mode, band.BandwidthHz);
    rx.FrequencyHz = mhz * 1e6;
    rx.SquelchDb = args.Length > 3 ? double.Parse(args[3]) : -200;
    rx.Volume = 1;
    // A fifth argument names a file to take the audio as raw 48 kHz 32-bit float.
    using var capture = args.Length > 4 ? File.Create(args[4]) : null;
    if (capture is not null)
        rx.AudioOutput += a => capture.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a));
    rx.Start();
    Console.WriteLine($"sound devices: {string.Join(" | ", SimpleScanner.Audio.AudioOut.Devices())}");
    Console.WriteLine($"{rx.Source.Description}: {mhz} MHz, {band.Name}, {band.Mode} {band.BandwidthHz / 1000} kHz");
    for (int s = 0; s < seconds; s++)
    {
        Thread.Sleep(1000);
        var src = rx.Source;
        Console.WriteLine($"  level {rx.LevelDb,7:F1} dB  squelch {(rx.SquelchOpen ? "open  " : "closed")}  LNA {src.ActiveGainStep}/{src.MaxGainStep}  gain {src.GainDb:F1} dB  overload {src.Overloaded}  underruns {rx.AudioUnderruns}{(rx.Stereo ? "  STEREO" : "")}");
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
    using var rx = new Receiver(new SdrPlaySource(), playAudio: false) { ScanSkipSeconds = 3600, ScanHangSeconds = 0.3 };
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
    Console.WriteLine($"hunting {lo / 1e6}-{hi / 1e6} MHz: {list.Count} channels in {rx.ScanWindows} windows, squelch {rx.SquelchDb} dB, P25 {(rx.DigitalAvailable ? "available" : "MISSING")}");
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
            if (what.Contains("system") || what.Contains("TG")) break;
        }
        if (rx.FrequencyHz == f)
        {
            if (!seen.ContainsKey(f) || (what.Length > 0 && seen[f].Length == 0))
                Console.WriteLine($"  {sw.Elapsed.TotalSeconds,5:F1} s  {f / 1e6,9:F4} MHz  {level,5:F0} dB  {(what.Length > 0 ? what : "no P25")}");
            seen[f] = what;
            rx.SkipChannel();
        }
    }
    Console.WriteLine($"{seen.Count} active frequencies, {seen.Values.Count(v => v.Length > 0)} with P25; {rx.ScanPasses} passes");
    rx.Stop();
    return;
}

if (args.Length >= 1 && args[0] == "scan")
{
    // rsp-bench scan [seconds] [squelch dB]: scan the ticked stored channels and report.
    int seconds = args.Length > 1 ? int.Parse(args[1]) : 10;
    using var rx = new Receiver(new SdrPlaySource(), playAudio: false);
    rx.SquelchDb = args.Length > 2 ? double.Parse(args[2]) : -95;
    var list = MemoryStore.Load().Channels.Where(c => c.Scan).Select(c =>
    {
        var band = BandPlan.Find(c.FrequencyHz);
        return new ScanChannel(c.Name, c.FrequencyHz, c.Mode ?? band.Mode, c.BandwidthHz ?? band.BandwidthHz);
    }).ToList();
    rx.Start();
    Thread.Sleep(1500);                      // let the gain settle
    rx.StartScan(list);
    Console.WriteLine($"{rx.Source.Description} at {rx.SampleRate / 1e6} MS/s: {list.Count} channels in {rx.ScanWindows} windows, squelch {rx.SquelchDb} dB");
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

using var source = new SdrPlaySource();
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
