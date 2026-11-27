// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner.Radio;

/// <summary>
/// A block of IQ samples, full scale at +-1. <paramref name="retuned"/> is true on the first
/// block after a change of centre frequency has taken effect.
/// </summary>
public delegate void IqHandler(ReadOnlySpan<float> i, ReadOnlySpan<float> q, bool retuned);

/// <summary>A radio that is plugged in and free to open: its model and serial number.</summary>
public sealed record RadioInfo(string Model, string Serial)
{
    public string Label => $"{Model}  {Serial}";
}

/// <summary>
/// A radio as the scanner sees it: a stream of IQ samples round a centre frequency, with a
/// gain control and a choice of antenna. Each kind of hardware is one implementation.
/// </summary>
public interface IIqSource : IDisposable
{
    /// <summary>Model and serial number, once started.</summary>
    string Description { get; }
    /// <summary>The radios that can be chosen now: those that are free, and the one this source has open.
    /// Throws <see cref="InvalidOperationException"/> when the radio's software is missing or not answering.</summary>
    IReadOnlyList<RadioInfo> Radios();
    /// <summary>The radio open now, or the one last open; null before the first start.</summary>
    RadioInfo? Radio { get; }
    /// <summary>The serial number of the radio to open at the next <see cref="Start"/>. With none, or when
    /// that radio is not there, the source picks one itself.</summary>
    string? Serial { get; set; }
    double SampleRate { get; }
    double CenterHz { get; }
    /// <summary>The width round the centre over which the radio's response is flat, at the present sample rate.</summary>
    double UsableBandwidthHz { get; }
    bool IsRunning { get; }

    /// <summary>Raised on the radio's own thread. Samples sent while a retune is in flight are dropped.</summary>
    event IqHandler? Samples;
    /// <summary>The radio stopped by itself (unplugged, driver failure).</summary>
    event Action<string>? Failed;

    void Start(double sampleRate, double centerHz);
    void Stop();
    void Tune(double centerHz);

    IReadOnlyList<string> Antennas { get; }
    string Antenna { get; set; }

    /// <summary>The radio can put DC on an antenna socket to power a preamplifier there (known once started).</summary>
    bool HasBiasT { get; }
    /// <summary>DC on the antenna socket that has it. Off unless asked for; may be set before the radio is started.</summary>
    bool BiasT { get; set; }
    /// <summary>The radio has a notch filter for the FM broadcast band (known once started).</summary>
    bool HasFmNotch { get; }
    /// <summary>Cut the FM broadcast band ahead of the tuner, so strong stations there cannot overload it.</summary>
    bool FmNotch { get; set; }
    /// <summary>Correction for the error of the radio's reference oscillator, in parts per million.</summary>
    double Ppm { get; set; }

    /// <summary>Let the radio choose its front-end gain. <see cref="GainStep"/> is then the most gain it may use.</summary>
    bool AutoGain { get; set; }
    /// <summary>Front-end attenuation step: 0 is the most gain.</summary>
    int GainStep { get; set; }
    /// <summary>The highest step at the present frequency.</summary>
    int MaxGainStep { get; }
    /// <summary>The step in use now (differs from <see cref="GainStep"/> under automatic gain).</summary>
    int ActiveGainStep { get; }
    /// <summary>Total gain ahead of the samples, in dB, for turning sample levels into signal levels.</summary>
    double GainDb { get; }
    bool Overloaded { get; }
    /// <summary>How long the last retune took, in milliseconds (0 before the first).</summary>
    double LastRetuneMs { get; }
}
