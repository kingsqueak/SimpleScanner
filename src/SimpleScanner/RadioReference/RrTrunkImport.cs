// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Globalization;
using SimpleScanner.Trunk;

namespace SimpleScanner.RadioReference;

/// <summary>Turns a trunked system fetched from RadioReference into a stored one.</summary>
public static class RrTrunkImport
{
    /// <summary>Whether a kind of system ("Project 25 Phase I", "Project 25 Phase II") is one the receiver can follow.</summary>
    public static bool IsP25(string typeName) => typeName.Contains("Project 25", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A system id, WACN or NAC, which the service writes in hex: "3A1", "0x3a1", "BEE00". Null when
    /// there is none (empty, 0, or not hex) or when it is too long for <paramref name="bits"/>.
    /// </summary>
    public static int? Hex(string? text, int bits)
    {
        string s = (text ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (s.StartsWith('$')) s = s[1..];
        if (s.EndsWith('h') || s.EndsWith('H')) s = s[..^1];
        if (s.Length == 0 || !int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v)) return null;
        return v > 0 && v < 1 << bits ? v : null;
    }

    // The values of a site frequency's "use" are not in the service's definition. "d" is taken as the
    // control channel and "a" as an alternate one, as RadioReference's site pages mark them; this
    // needs confirming against a live answer.
    public static TrunkFreqUse Use(string? use) => (use ?? "").Trim().ToLowerInvariant() switch
    {
        "d" => TrunkFreqUse.Control,
        "a" => TrunkFreqUse.Alternate,
        _ => TrunkFreqUse.Voice,
    };

    /// <summary>
    /// A talkgroup's mode as stored: "D" (Phase 1) or "T" (Phase 2, TDMA), followed by "E" when all of its
    /// traffic is encrypted or "e" when some is; and the encryption as a number (0 none, 1 some, 2 all).
    /// </summary>
    // The letters of tgMode are not in the service's definition either. A "T" (either case) is taken as
    // TDMA and an "E" as encryption, capital for all of the traffic and small for some, as with conventional
    // frequencies; the "enc" number counts as well. This needs confirming against a live answer.
    public static (string Mode, int Enc) Mode(string? tgMode, int enc)
    {
        string m = tgMode ?? "";
        int level = Math.Clamp(enc, 0, 2);
        if (m.Contains('E')) level = 2;
        else if (m.Contains('e')) level = Math.Max(level, 1);
        bool tdma = m.Contains('T', StringComparison.OrdinalIgnoreCase);
        return ((tdma ? "T" : "D") + (level >= 2 ? "E" : level == 1 ? "e" : ""), level);
    }

    /// <summary>A site's county: its name as given, else the name of its number among those known, else nothing.</summary>
    public static string County(RrTrsSite site, IReadOnlyDictionary<int, string>? counties) =>
        site.County.Trim().Length > 0 ? site.County.Trim()
        : site.CountyId > 0 && counties is not null && counties.TryGetValue(site.CountyId, out var name) ? name : "";

    /// <summary>
    /// A fetched system as it is stored. <paramref name="location"/> is where it is listed (a county, a state).
    /// Talkgroups whose traffic is all encrypted come unticked; the rest ticked. <paramref name="counties"/>: county names by
    /// RadioReference number (those of the state shown, already fetched), for sites that give only the number.
    /// </summary>
    public static (TrunkSystem System, List<TrunkSite> Sites, List<TrunkTalkgroup> Talkgroups) ToStore(RrTrsWhole whole, string location,
        DateTime imported, IReadOnlyDictionary<int, string>? counties = null)
    {
        var d = whole.Details;
        // The first system id that can be read; a system may list several (one per county or zone), or none.
        var ids = d.Sysids.Select(s => (Sysid: Hex(s.Sysid, 12), Wacn: Hex(s.Wacn, 20))).FirstOrDefault(s => s.Sysid is not null);
        var system = new TrunkSystem(0, d.Id, d.Name, d.TypeName, d.FlavorName, d.VoiceName, ids.Sysid, ids.Wacn, location, imported);

        var sites = whole.Sites.Select(s => new TrunkSite(0, 0, s.Id, s.Number, s.Rfss, s.Descr, County(s, counties), Hex(s.Nac, 12), s.Modulation,
            s.TdmaCc != 0, s.Lat, s.Lon,
            s.Freqs.Select(f => new TrunkSiteFreq(Math.Round(f.MHz * 1e6 / 10) * 10, f.Lcn > 0 ? f.Lcn : null, Use(f.Use)))
                .DistinctBy(f => f.Hz).OrderBy(f => f.Hz).ToList())).ToList();

        var cats = whole.Cats.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name);
        var talkgroups = whole.Talkgroups.Where(t => t.Dec > 0).DistinctBy(t => t.Dec).Select(t =>
        {
            var (mode, enc) = Mode(t.Mode, t.Enc);
            return new TrunkTalkgroup(0, t.Dec, t.Alpha.Trim(), t.Descr.Trim(), mode, enc,
                t.Tags.Select(g => g.Descr).FirstOrDefault(g => g.Length > 0) ?? "", cats.GetValueOrDefault(t.Cid, ""), Listen: enc < 2);
        }).ToList();
        return (system, sites, talkgroups);
    }
}
