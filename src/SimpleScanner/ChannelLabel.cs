// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Globalization;

namespace SimpleScanner;

/// <summary>
/// What the display says about the frequency it is on, from the stored channels there: the
/// name, a line saying whose it is, and a line of particulars. One frequency is often shared
/// (two towns' fire departments on the same channel), so several channels may contribute.
/// </summary>
public sealed record ChannelLabel(string? Title, string Detail, string Info)
{
    public static readonly ChannelLabel None = new(null, "", "");

    public static ChannelLabel For(IReadOnlyList<MemoryChannel> channels)
    {
        if (channels.Count == 0) return None;
        string title = Join(channels.Select(c => c.Name), " / ", 3);
        // Whose it is: each agency once, with what its channels here are for.
        string detail = Join(channels.GroupBy(c => c.Agency ?? "", StringComparer.CurrentCultureIgnoreCase).Select(g =>
        {
            string uses = Join(g.Select(c => c.Description), " / ", 3);
            return g.Key.Length > 0 && uses.Length > 0 ? $"{g.Key}: {uses}" : g.Key + uses;
        }), "  ·  ", 3);

        var info = new List<string>();
        void Add(string text)
        {
            if (text.Length > 0) info.Add(text);
        }
        Add(Join(channels.Select(c => c.Tag), " / ", 3));
        // Plain FM and AM are already on the display as the receive mode.
        Add(Join(channels.Select(c => c.Format).Where(f => f is not null && f.ToUpperInvariant() is not ("FM" or "FMN" or "AM")), " / ", 3));
        Add(Join(channels.Select(c => c.Tone), " / ", 4));
        Add(Join(channels.Select(c => StationKind(c.StationClass)), " / ", 2));
        Add(Join(channels.Select(c => c.InputHz is double hz ? "input " + (hz / 1e6).ToString("0.0000##", CultureInfo.InvariantCulture) : null), " / ", 2));
        Add(Join(channels.Select(c => c.Callsign), " / ", 3));
        return new ChannelLabel(title.Length > 0 ? title : null, detail, string.Join("  ·  ", info));
    }

    /// <summary>The FCC station class in words.</summary>
    private static string? StationKind(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        null or "" => null,
        "RM" or "R" => "Repeater",
        "BM" => "Base and mobiles",
        "M" => "Mobiles",
        "B" => "Base",
        "F" => "Fixed",
        var other => other,
    };

    // The different values, in order, with a count of those left out when there are too many.
    private static string Join(IEnumerable<string?> values, string separator, int most)
    {
        var distinct = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        return distinct.Count <= most
            ? string.Join(separator, distinct)
            : string.Join(separator, distinct.Take(most)) + $" +{distinct.Count - most} more";
    }
}
