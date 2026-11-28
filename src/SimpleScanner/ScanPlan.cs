// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

namespace SimpleScanner;

/// <summary>One tuning of the radio and the scan channels that can be watched from it.</summary>
public sealed record ScanWindow(double CenterHz, int[] Channels);

/// <summary>
/// Divides a scan list into as few tunings of the radio as its bandwidth allows: every channel
/// of a window lies inside the radio's flat passband and clear of the centre, where a zero-IF
/// receiver has a spur.
/// </summary>
public static class ScanPlan
{
    /// <summary>How far a channel's edge must be from the centre of the window.</summary>
    public const double DcGuardHz = 40_000;
    /// <summary>Where a channel on its own is put relative to the centre.</summary>
    public const double SingleOffsetHz = 300_000;
    /// <summary>The lowest and highest centre a radio is asked for. Outside them the SDRplay API refuses, and at a
    /// start says no more than "Fail" (2026-11-26: a centre of -200 kHz for a receiver left on 100 kHz).</summary>
    public const double MinCenterHz = 100_000, MaxCenterHz = 2_000_000_000;

    /// <summary>
    /// Where a channel on its own sits relative to the centre: <see cref="SingleOffsetHz"/> above it, or that far
    /// below it for a frequency so low that the centre would otherwise fall under <see cref="MinCenterHz"/>.
    /// </summary>
    public static double SingleOffsetFor(double frequencyHz) =>
        frequencyHz - SingleOffsetHz >= MinCenterHz ? SingleOffsetHz : -SingleOffsetHz;

    /// <summary>True when a channel this wide, this far from the centre, can be received in a passband this wide.</summary>
    public static bool Fits(double offsetHz, double bandwidthHz, double usableHz) =>
        Math.Abs(offsetHz) >= bandwidthHz / 2 + DcGuardHz && Math.Abs(offsetHz) + bandwidthHz / 2 <= usableHz / 2;

    /// <summary>
    /// The windows for these channels. <paramref name="channels"/> must be in frequency order;
    /// each window's indices refer to it.
    /// </summary>
    public static List<ScanWindow> Build(IReadOnlyList<ScanChannel> channels, double usableHz)
    {
        var windows = new List<ScanWindow>();
        int i = 0, n = channels.Count;
        while (i < n)
        {
            double lo = channels[i].FrequencyHz - channels[i].BandwidthHz / 2;
            int j = i;
            while (j + 1 < n && Top(channels, i, j + 1) - lo <= usableHz) j++;
            double? center = null;
            for (; j > i; j--)
            {
                center = FindCenter(channels, i, j, lo, usableHz);
                if (center is not null) break;
            }
            center ??= channels[i].FrequencyHz - SingleOffsetFor(channels[i].FrequencyHz);
            windows.Add(new ScanWindow(center.Value, Enumerable.Range(i, j - i + 1).ToArray()));
            i = j + 1;
        }
        return windows;
    }

    /// <summary>
    /// The centre for one channel that must be received and as many more as can be with it (a trunked site: its control
    /// channel and its voice channels), all of them <paramref name="bandwidthHz"/> wide: each one counted lies inside the
    /// passband and clear of the centre (<see cref="Fits"/>). Of the centres that hold the most, the middle of the longest
    /// run of them, which leaves the most room either side. <paramref name="held"/> is how many of
    /// <paramref name="wantHz"/> it holds.
    /// </summary>
    public static double CenterFor(double mustHz, IReadOnlyList<double> wantHz, double bandwidthHz, double usableHz, out int held)
    {
        const double step = 6250;
        int steps = (int)(usableHz / 2 / step);
        int best = -1, runStart = 0, runLength = 0, bestStart = 0, bestLength = 0;
        for (int k = -steps; k <= steps + 1; k++)
        {
            int count = -1;
            double center = mustHz + k * step;
            if (k <= steps && center >= MinCenterHz && center <= MaxCenterHz && Fits(mustHz - center, bandwidthHz, usableHz))
            {
                count = 0;
                foreach (double hz in wantHz)
                    if (Fits(hz - center, bandwidthHz, usableHz)) count++;
            }
            // Runs of centres holding the same number, one after another.
            if (count == best && count >= 0 && runLength > 0 && runStart + runLength == k)
            {
                runLength++;
                if (runLength > bestLength)
                {
                    bestStart = runStart;
                    bestLength = runLength;
                }
                continue;
            }
            if (count > best)
            {
                best = count;
                bestStart = runStart = k;
                bestLength = runLength = 1;
            }
            else if (count == best && count >= 0)
            {
                runStart = k;
                runLength = 1;
            }
            else runLength = 0;
        }
        held = Math.Max(best, 0);
        if (best < 0) return mustHz - SingleOffsetFor(mustHz);
        return mustHz + (bestStart + (bestLength - 1) / 2) * step;
    }

    private static double Top(IReadOnlyList<ScanChannel> c, int from, int to)
    {
        double top = 0;
        for (int k = from; k <= to; k++) top = Math.Max(top, c[k].FrequencyHz + c[k].BandwidthHz / 2);
        return top;
    }

    // The middle of the group if no channel sits on it; otherwise the nearest place to the
    // middle that still has every channel in the passband.
    private static double? FindCenter(IReadOnlyList<ScanChannel> c, int from, int to, double lo, double usableHz)
    {
        const double step = 12_500;
        double hi = Top(c, from, to), mid = Math.Round((lo + hi) / 2 / step) * step;
        for (double shift = 0; shift <= usableHz / 2; shift += step)
        {
            foreach (double center in shift == 0 ? [mid] : new[] { mid + shift, mid - shift })
            {
                if (center < MinCenterHz || center > MaxCenterHz) continue;      // not a tuning the radio has
                bool ok = true;
                for (int k = from; k <= to && ok; k++)
                    ok = Fits(c[k].FrequencyHz - center, c[k].BandwidthHz, usableHz);
                if (ok) return center;
            }
        }
        return null;
    }
}
