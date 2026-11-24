namespace SimpleScanner;

/// <summary>
/// Shares a scan list among radios. A channel that only one of the radios is to scan goes to that
/// radio. The rest, in frequency order, are cut into one run for each radio, so that a radio
/// keeps to its own part of the spectrum, with the cuts placed so that no radio has more tunings to go
/// round (its own channels included) than it must.
/// </summary>
public static class ScanSplit
{
    /// <summary>
    /// Each radio's share of the list.
    /// </summary>
    /// <param name="channels">The scan list, in frequency order.</param>
    /// <param name="wanted">For each channel, the radio it is given to (its place among the radios), or -1 for any.</param>
    /// <param name="usableHz">For each radio that is on, the width it can watch from one tuning.</param>
    /// <returns>For each radio, its channels in frequency order. A radio may get none.</returns>
    public static List<ScanChannel>[] Split(IReadOnlyList<ScanChannel> channels, IReadOnlyList<int> wanted, IReadOnlyList<double> usableHz)
    {
        int radios = usableHz.Count;
        var named = new List<ScanChannel>[radios];
        for (int r = 0; r < radios; r++) named[r] = [];
        if (radios == 0) return named;
        var any = new List<ScanChannel>();
        for (int i = 0; i < channels.Count; i++)
        {
            int r = i < wanted.Count ? wanted[i] : -1;
            (r >= 0 && r < radios ? named[r] : any).Add(channels[i]);
        }
        if (radios == 1)
        {
            named[0] = Merge(named[0], any, 0, any.Count);
            return named;
        }

        // The least number of tunings that the busiest radio can be kept to, found by halving; then the
        // cuts that keep every radio to it.
        int[] own = new int[radios];
        int low = 1, high = 1;
        for (int r = 0; r < radios; r++)
        {
            own[r] = Windows(named[r], usableHz[r]);
            low = Math.Max(low, own[r]);
            high = Math.Max(high, Windows(Merge(named[r], any, 0, any.Count), usableHz[r]));
        }
        int[]? cuts = null;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (Cuts(named, any, usableHz, middle) is { } found)
            {
                cuts = found;
                high = middle;
            }
            else low = middle + 1;
        }
        cuts ??= Cuts(named, any, usableHz, low) ?? LastTakesAll(radios, any.Count);
        var shares = new List<ScanChannel>[radios];
        for (int r = 0; r < radios; r++) shares[r] = Merge(named[r], any, cuts[r], cuts[r + 1]);
        return shares;
    }

    /// <summary>How many tunings of a radio these channels take.</summary>
    public static int Windows(IReadOnlyList<ScanChannel> channels, double usableHz) =>
        channels.Count == 0 ? 0 : ScanPlan.Build(channels, usableHz).Count;

    // Where to cut the channels that any radio may take so that no radio has more than `most` tunings:
    // each radio in turn takes as long a run as it may. Null when that leaves some over.
    private static int[]? Cuts(List<ScanChannel>[] named, List<ScanChannel> any, IReadOnlyList<double> usableHz, int most)
    {
        int radios = named.Length;
        int[] cuts = new int[radios + 1];
        int at = 0;
        for (int r = 0; r < radios; r++)
        {
            if (Windows(named[r], usableHz[r]) > most) return null;
            // The longest run from here that keeps this radio within the limit.
            int lo = at, hi = any.Count;
            while (lo < hi)
            {
                int middle = (lo + hi + 1) / 2;
                if (Windows(Merge(named[r], any, at, middle), usableHz[r]) <= most) lo = middle;
                else hi = middle - 1;
            }
            at = lo;
            cuts[r + 1] = at;
        }
        return at == any.Count ? Even(cuts, named, any, usableHz, most) : null;
    }

    // The cuts found give the first radios all they may take and the last what is over. Move each cut back
    // while that brings a radio nearer to the one that has the run before its own (radios between them
    // with no run are passed over), so that none is left idle that could help. A cut is moved only to
    // where one of the earlier radio's tunings begins: channels that one tuning can watch together are
    // never divided between two radios.
    private static int[] Even(int[] cuts, List<ScanChannel>[] named, List<ScanChannel> any, IReadOnlyList<double> usableHz, int most)
    {
        bool moved = true;
        for (int pass = 0; moved && pass < named.Length; pass++)
        {
            moved = false;
            for (int r = named.Length - 1; r >= 1; r--)
            {
                int q = r - 1;
                while (q > 0 && cuts[q + 1] == cuts[q]) q--;
                int from = cuts[q], count = cuts[r] - from;
                if (count == 0) continue;
                var starts = ScanPlan.Build(any.GetRange(from, count), usableHz[q]).Select(w => from + w.Channels[0]).ToList();
                int mine = Windows(Merge(named[r], any, cuts[r], cuts[r + 1]), usableHz[r]);
                int theirs = Windows(Merge(named[q], any, from, cuts[r]), usableHz[q]);
                for (int k = starts.Count - 1; k >= 0; k--)
                {
                    int mineThen = Windows(Merge(named[r], any, starts[k], cuts[r + 1]), usableHz[r]);
                    int theirsThen = Windows(Merge(named[q], any, from, starts[k]), usableHz[q]);
                    if (mineThen > most || mineThen > theirs || mine >= theirsThen) break;
                    for (int j = q + 1; j <= r; j++) cuts[j] = starts[k];
                    mine = mineThen;
                    theirs = theirsThen;
                    moved = true;
                }
            }
        }
        return cuts;
    }

    private static int[] LastTakesAll(int radios, int count)
    {
        int[] cuts = new int[radios + 1];
        cuts[radios] = count;
        return cuts;
    }

    // A radio's own channels and a run of the others, in frequency order.
    private static List<ScanChannel> Merge(List<ScanChannel> own, List<ScanChannel> any, int from, int to)
    {
        var list = new List<ScanChannel>(own.Count + to - from);
        int a = 0, b = from;
        while (a < own.Count || b < to)
        {
            if (b >= to || (a < own.Count && own[a].FrequencyHz <= any[b].FrequencyHz)) list.Add(own[a++]);
            else list.Add(any[b++]);
        }
        return list;
    }
}
