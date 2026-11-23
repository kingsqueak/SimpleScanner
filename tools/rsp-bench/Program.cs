// rsp-bench: measurements on a real SDRplay RSP, without the window. Receive only.
//
//   rsp-bench                          sample throughput and retune times
//   rsp-bench listen <MHz> [seconds] [squelch dB] [file]
//                                      run the receiver on a frequency and print its level each second;
//                                      the file takes the audio as raw 48 kHz 32-bit float, stereo
using System.Diagnostics;
using SimpleScanner;
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

using var source = new SdrPlaySource();
long samples = 0;
int retunes = 0;
source.Samples += (i, q, retuned) =>
{
    Interlocked.Add(ref samples, i.Length);
    if (retuned) Interlocked.Increment(ref retunes);
};

foreach (double rate in new[] { 2.4e6, 6.0e6, 9.6e6 })
{
    Interlocked.Exchange(ref samples, 0);
    source.Start(rate, 155e6);
    Thread.Sleep(500);                       // let the stream settle
    long before = Interlocked.Read(ref samples);
    var sw = Stopwatch.StartNew();
    Thread.Sleep(3000);
    double got = (Interlocked.Read(ref samples) - before) / sw.Elapsed.TotalSeconds;
    Console.WriteLine($"{source.Description}: asked {rate / 1e6:F1} MS/s, received {got / 1e6:F3} MS/s");
    if (rate != 9.6e6) source.Stop();
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

foreach (double rate in new[] { 9.6e6, 2.4e6 })
{
    if (rate != 9.6e6)
    {
        source.Stop();
        source.Start(rate, 155e6);
        Thread.Sleep(500);
    }
    Console.WriteLine($"-- retune at {rate / 1e6:F1} MS/s");
    Series("155 <-> 156 MHz (same band)", 155e6, 156e6, 40);
    Series("152 <-> 172 MHz (same band)", 152e6, 172e6, 40);
    Series("121 <-> 155 MHz (airband/VHF)", 121e6, 155e6, 40);
    Series("155 <-> 460 MHz (VHF/UHF)", 155e6, 460e6, 40);
    Series("460 <-> 855 MHz (UHF/800)", 460e6, 855e6, 40);
    Series("46 <-> 855 MHz (low band/800)", 46e6, 855e6, 40);
}
source.Stop();
