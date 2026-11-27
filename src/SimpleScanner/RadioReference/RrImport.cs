// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Globalization;

namespace SimpleScanner.RadioReference;

/// <summary>Turns frequencies fetched from RadioReference into stored channels.</summary>
public static class RrImport
{
    /// <summary>What marks a stored channel as one fetched from the service: "rr:" and the frequency's number there.</summary>
    public const string KeyPrefix = "rr:";

    /// <summary>
    /// A fetched frequency as a channel. <paramref name="agency"/> is the list it came from (the
    /// subcategory: a town's police, an airport), which is what a CSV download puts in that column.
    /// </summary>
    public static MemoryChannel ToChannel(RrFreq f, string agency, string? location = null)
    {
        double hz = Math.Round(f.OutMHz * 1e6 / 10) * 10, input = Math.Round(f.InMHz * 1e6 / 10) * 10;
        // An "e" after the mode is RadioReference's own mark for encryption: small for some of the traffic, capital for all.
        string format = f.Mode + (f.Mode.Length == 0 || f.Mode.EndsWith("e", StringComparison.OrdinalIgnoreCase) ? ""
            : f.Enc == 1 ? "e" : f.Enc >= 2 ? "E" : "");
        var (mode, width) = RadioReferenceCsv.Receive(format, hz);
        return new MemoryChannel
        {
            Name = f.Alpha.Length > 0 ? f.Alpha : f.Descr.Length > 0 ? f.Descr : agency,
            FrequencyHz = hz,
            Mode = mode,
            BandwidthHz = width,
            Agency = Opt(agency),
            Description = Opt(f.Descr),
            Tag = Opt(f.Tags.Select(t => t.Descr).FirstOrDefault(d => d.Length > 0)),
            Format = Opt(format),
            Tone = Opt(Code(f)),
            Callsign = Opt(f.Callsign),
            InputHz = input > 0 ? input : null,
            StationClass = Opt(f.Class),
            Location = Opt(location),
            ImportKey = KeyPrefix + f.Id.ToString(CultureInfo.InvariantCulture),
        };
    }

    // The tone, or for a digital channel its colour code, talkgroup and slot, as a CSV download words them.
    private static string Code(RrFreq f)
    {
        var parts = new List<string>();
        if (f.Tone.Length > 0) parts.Add(f.Tone.Replace("|", " "));
        if (f.ColorCode.Length > 0 && !f.Tone.Contains("CC", StringComparison.OrdinalIgnoreCase)) parts.Add("CC " + f.ColorCode);
        if (f.Tg.Length > 0 && f.Tg != "0") parts.Add("TG " + f.Tg);
        if (f.Slot.Length > 0 && f.Slot != "0") parts.Add("SL " + f.Slot);
        return string.Join(' ', parts);
    }

    private static string? Opt(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Add fetched channels to the store, in this group. One already stored is kept as it is (the
    /// user may have edited it) and joins the group: the same frequency fetched before, or the same
    /// row from a CSV download (same frequency, name and description).
    /// </summary>
    public static ImportResult Add(MemoryStore store, IEnumerable<MemoryChannel> channels, string group)
    {
        group = group.Trim();
        int added = 0, had = 0, skipped = 0, telemetry = 0;
        var imported = store.Channels.Where(c => c.ImportKey is not null).ToList();
        var byKey = imported.GroupBy(c => c.ImportKey!).ToDictionary(g => g.Key, g => g.First());
        var byRow = imported.GroupBy(Row).ToDictionary(g => g.Key, g => g.First());
        foreach (var c in channels)
        {
            if (c.FrequencyHz is < 100_000 or > 2_000_000_000 || BandPlan.IsBlocked(c.FrequencyHz, c.BandwidthHz ?? 0))
            {
                skipped++;
                continue;
            }
            if (byKey.TryGetValue(c.ImportKey ?? "", out var existing) || byRow.TryGetValue(Row(c), out existing))
            {
                had++;
                if (group.Length > 0 && !existing.Groups.Contains(group)) existing.Groups.Add(group);
                // Where it is was not known when it was stored (an earlier version, or a CSV file): it is now.
                existing.Location ??= c.Location;
                continue;
            }
            if (group.Length > 0) c.Groups.Add(group);
            // Telemetry is data, not speech: stored, and left out of the scan until the user ticks it.
            if (IsTelemetry(c))
            {
                c.Scan = false;
                telemetry++;
            }
            store.Channels.Add(c);
            if (c.ImportKey is not null) byKey[c.ImportKey] = c;
            byRow[Row(c)] = c;
            added++;
        }
        return new ImportResult(added, had, skipped, 0, telemetry);
    }

    /// <summary>True for a channel RadioReference lists as telemetry ("Telm").</summary>
    public static bool IsTelemetry(MemoryChannel c) => c.Format?.StartsWith("Telm", StringComparison.OrdinalIgnoreCase) == true;

    internal static (long, string, string) Row(MemoryChannel c) =>
        ((long)Math.Round(c.FrequencyHz / 10), c.Name.Trim(), (c.Description ?? "").Trim());
}
