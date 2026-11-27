// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleScanner.Dsp;

namespace SimpleScanner;

/// <summary>What is normally found on a stretch of spectrum, and how to receive it.</summary>
public sealed record Band(double LowHz, double HighHz, string Name, Mode Mode, double BandwidthHz, double StepHz,
    bool Blocked = false);

/// <summary>
/// A United States band plan for the frequencies a scanner covers: the label to show, and the
/// mode, filter width and tuning step to use unless the operator chooses otherwise.
/// </summary>
public static class BandPlan
{
    // Channel filter widths. FM under 13 kHz means 2.5 kHz deviation (see ChannelReceiver).
    public const double NfmNarrow = 11000, NfmWide = 16000, AmAir = 10000, AmAir833 = 6600, AmWide = 10000, Wfm = 200000;

    private static readonly Band Default = new(0, double.MaxValue, "", Mode.Nfm, NfmNarrow, 12500);

    // In order; the first band that contains the frequency wins, so narrower entries go first.
    private static readonly Band[] Bands =
    [
        new(530e3, 1710e3, "AM broadcast", Mode.Am, AmWide, 10000),
        new(1710e3, 26.965e6, "Shortwave", Mode.Am, AmWide, 5000),
        new(26.965e6, 27.405e6, "CB", Mode.Am, AmWide, 10000),
        new(28.0e6, 29.7e6, "10 m amateur", Mode.Nfm, NfmWide, 5000),
        new(29.7e6, 50e6, "VHF low band", Mode.Nfm, NfmWide, 20000),
        new(50e6, 54e6, "6 m amateur", Mode.Nfm, NfmWide, 5000),
        new(87.5e6, 108e6, "FM broadcast", Mode.Wfm, Wfm, 100000),
        new(108e6, 118e6, "Air navigation", Mode.Am, AmAir, 50000),
        new(118e6, 137e6, "Airband", Mode.Am, AmAir, 25000),
        new(137e6, 138e6, "Weather satellites", Mode.Nfm, NfmWide, 5000),
        new(138e6, 144e6, "Federal / military", Mode.Nfm, NfmNarrow, 12500),
        new(144e6, 148e6, "2 m amateur", Mode.Nfm, NfmWide, 5000),
        new(148e6, 150.8e6, "Federal / military", Mode.Nfm, NfmNarrow, 12500),
        new(156.0e6, 157.45e6, "Marine", Mode.Nfm, NfmWide, 25000),
        new(160.6e6, 162.025e6, "Marine / railroad", Mode.Nfm, NfmWide, 7500),
        new(162.4e6, 162.5625e6, "NOAA weather", Mode.Nfm, NfmWide, 25000),
        new(150.8e6, 174e6, "VHF public service", Mode.Nfm, NfmNarrow, 7500),
        new(216e6, 222e6, "VHF 220", Mode.Nfm, NfmNarrow, 12500),
        new(222e6, 225e6, "1.25 m amateur", Mode.Nfm, NfmWide, 5000),
        new(225e6, 400e6, "Military airband", Mode.Am, AmAir, 25000),
        new(406e6, 420e6, "Federal UHF", Mode.Nfm, NfmNarrow, 12500),
        new(420e6, 450e6, "70 cm amateur", Mode.Nfm, NfmWide, 5000),
        new(462.5e6, 462.75e6, "FRS / GMRS", Mode.Nfm, NfmWide, 12500),
        new(467.5e6, 467.75e6, "FRS / GMRS", Mode.Nfm, NfmWide, 12500),
        new(450e6, 470e6, "UHF public service", Mode.Nfm, NfmNarrow, 12500),
        new(470e6, 512e6, "UHF-T", Mode.Nfm, NfmNarrow, 12500),
        new(764e6, 776e6, "700 MHz public safety", Mode.Nfm, NfmNarrow, 12500),
        new(794e6, 806e6, "700 MHz public safety", Mode.Nfm, NfmNarrow, 12500),
        new(806e6, 824e6, "800 MHz", Mode.Nfm, NfmNarrow, 12500),
        // Cellular telephone bands: a scanner must not receive these (47 CFR 15.121).
        new(824e6, 849e6, "Cellular - blocked", Mode.Nfm, NfmNarrow, 12500, Blocked: true),
        new(851e6, 869e6, "800 MHz", Mode.Nfm, NfmNarrow, 12500),
        new(869e6, 894e6, "Cellular - blocked", Mode.Nfm, NfmNarrow, 12500, Blocked: true),
        new(896e6, 902e6, "900 MHz", Mode.Nfm, NfmNarrow, 12500),
        new(902e6, 928e6, "33 cm amateur", Mode.Nfm, NfmWide, 12500),
        new(935e6, 941e6, "900 MHz", Mode.Nfm, NfmNarrow, 12500),
        new(1240e6, 1300e6, "23 cm amateur", Mode.Nfm, NfmWide, 25000),
    ];

    public static Band Find(double hz)
    {
        foreach (var b in Bands)
            if (hz >= b.LowHz && hz < b.HighHz) return b;
        return Default;
    }

    /// <summary>True when any part of a channel this wide at this frequency is in a blocked band.</summary>
    public static bool IsBlocked(double hz, double bandwidthHz = 0) =>
        Find(hz - bandwidthHz / 2).Blocked || Find(hz + bandwidthHz / 2).Blocked || Find(hz).Blocked;
}
