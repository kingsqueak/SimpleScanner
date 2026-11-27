// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using Avalonia;
using SimpleScanner.Radio.SdrPlay;
using SimpleScanner.Remote;

namespace SimpleScanner;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // SimpleScanner --radio-host <serial> [--any] [--rate <samples a second>]: no window. Run one receiver
        // on that radio for the program that started this one, taking its messages on standard input and
        // answering on standard output (see Remote/Wire.cs). Every radio is run this way: the SDRplay
        // software lets one program open one radio only, and a radio whose USB link jams is restarted in
        // a fresh helper (calling the API again in the process it jammed in can crash it). With --any, or
        // an empty serial number, another radio is taken when that one is not there.
        int at = Array.IndexOf(args, "--radio-host");
        if (at >= 0 && at + 1 < args.Length)
        {
            RunHelper(args[at + 1], args);
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void RunHelper(string serial, string[] args)
    {
        var output = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);       // nothing else may write among the messages
        int at = Array.IndexOf(args, "--rate");
        double rate = at >= 0 && at + 1 < args.Length
            ? double.Parse(args[at + 1], System.Globalization.CultureInfo.InvariantCulture) : Receiver.DefaultSampleRate;
        // A stalled radio is restarted in a new helper (see SdrPlaySource.HandOverStalls).
        bool any = args.Contains("--any") || serial.Length == 0;
        var source = new SdrPlaySource { Serial = serial.Length > 0 ? serial : null, OnlyThisRadio = !any, HandOverStalls = true };
        ReceiverHost.Run(new Receiver(source, sampleRate: rate), Console.OpenStandardInput(), output,
            handOver: ask => source.HandOver += ask);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
