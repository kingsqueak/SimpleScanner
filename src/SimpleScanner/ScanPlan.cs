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
            center ??= channels[i].FrequencyHz - SingleOffsetHz;
            windows.Add(new ScanWindow(center.Value, Enumerable.Range(i, j - i + 1).ToArray()));
            i = j + 1;
        }
        return windows;
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
                bool ok = true;
                for (int k = from; k <= to && ok; k++)
                    ok = Fits(c[k].FrequencyHz - center, c[k].BandwidthHz, usableHz);
                if (ok) return center;
            }
        }
        return null;
    }
}
