// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Globalization;
using System.Text.RegularExpressions;

namespace SimpleScanner.RadioReference;

/// <summary>
/// Where a RadioReference list is used, as the words kept in a channel's Location: the town
/// when the county lists its users by town, otherwise the county, the state or the country
/// the list was found under.
/// </summary>
public static partial class RrPlaces
{
    /// <summary>True for a county category whose lists are towns ("Municipalities", "Cities and Towns", "Townships").</summary>
    public static bool IsTownCategory(string category) => Towns().IsMatch(category);

    [GeneratedRegex(@"\b(municipal\w*|cit(y|ies)|towns?|townships?|villages?|boroughs?|communit(y|ies))\b", RegexOptions.IgnoreCase)]
    private static partial Regex Towns();

    public static string Town(string town, string stateCode) => stateCode.Length > 0 ? $"{town}, {stateCode}" : town;

    /// <summary>"Hillsborough County, NH"; the header is the county's kind in RadioReference's words (County, Parish, Borough).</summary>
    public static string County(string name, string header, string stateCode)
    {
        string full = header.Length > 0 && !name.Contains(header, StringComparison.CurrentCultureIgnoreCase) ? $"{name} {header}" : name;
        return stateCode.Length > 0 ? $"{full}, {stateCode}" : full;
    }

    public static string State(string name) => $"{name} statewide";
    public static string Country(string name) => $"{name} nationwide";

    /// <summary>The location of a list in a county's own category.</summary>
    public static string ForCountyList(RrCat cat, RrSubcat list, string county, string header, string stateCode) =>
        IsTownCategory(cat.Name) ? Town(list.Name, stateCode) : County(county, header, stateCode);

    /// <summary>
    /// The location of every list that can be worked out from what has been fetched and kept: by the
    /// list's number (scid). Used to give a location to channels stored before locations were kept.
    /// </summary>
    public static Dictionary<int, string> FromCache(RrCache cache)
    {
        var lists = new Dictionary<int, string>();
        var agencies = new Dictionary<int, string>();
        var stateCodes = new Dictionary<int, string>();

        foreach (var r in Answers(cache, "getCountryInfo("))
        {
            foreach (var s in r.Rows("stateList")) stateCodes[s.Int("stid")] = s.Str("stateCode");
            foreach (var a in RrClient.Agencies(r)) agencies.TryAdd(a.Id, Country(r.Str("countryName")));
        }
        // A state's agencies are named before the country's would be, and a county's before the state's.
        foreach (var r in Answers(cache, "getStateInfo("))
            foreach (var a in RrClient.Agencies(r)) agencies[a.Id] = State(r.Str("stateName"));
        foreach (var r in Answers(cache, "getCountyInfo("))
        {
            string code = stateCodes.GetValueOrDefault(r.Int("stid"), ""), name = r.Str("countyName"), header = r.Str("countyHeader");
            foreach (var a in RrClient.Agencies(r)) agencies[a.Id] = County(name, header, code);
            foreach (var cat in RrClient.Cats(r))
                foreach (var list in cat.Subcats) lists[list.Id] = ForCountyList(cat, list, name, header, code);
        }
        foreach (var r in Answers(cache, "getAgencyInfo("))
        {
            if (!agencies.TryGetValue(r.Int("aid"), out string? where)) continue;
            foreach (var cat in RrClient.Cats(r))
                foreach (var list in cat.Subcats) lists.TryAdd(list.Id, where);
        }
        return lists;
    }

    /// <summary>
    /// Give a location to stored channels that have none and came from a list that was fetched
    /// (the same frequency number, or the same frequency, name and description as a CSV row has).
    /// </summary>
    /// <returns>How many channels were given one.</returns>
    public static int FillFromCache(MemoryStore store, RrCache cache)
    {
        var missing = store.Channels.Where(c => c.Location is null && c.ImportKey is not null).ToList();
        if (missing.Count == 0) return 0;
        var byKey = missing.ToLookup(c => c.ImportKey!);
        var byRow = missing.ToLookup(RrImport.Row);
        var places = FromCache(cache);
        int filled = 0;
        foreach (var (question, xml) in cache.All("getSubcatFreqs("))
        {
            if (!int.TryParse(question["getSubcatFreqs(".Length..].TrimEnd(')'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scid)
                || !places.TryGetValue(scid, out string? where)) continue;
            System.Xml.Linq.XElement r;
            try { r = RrSoap.Return(xml); }
            catch (RrException) { continue; }
            foreach (var f in r.Rows())
            {
                long hz10 = (long)Math.Round(f.Dbl("out") * 1e6 / 10);
                string alpha = f.Str("alpha"), descr = f.Str("descr");
                var same = byKey[RrImport.KeyPrefix + f.Int("fid").ToString(CultureInfo.InvariantCulture)]
                    .Concat(byRow[(hz10, alpha.Length > 0 ? alpha : descr, descr)]);
                foreach (var c in same.Where(c => c.Location is null))
                {
                    c.Location = where;
                    filled++;
                }
            }
        }
        return filled;
    }

    private static IEnumerable<System.Xml.Linq.XElement> Answers(RrCache cache, string call)
    {
        foreach (var (_, xml) in cache.All(call))
        {
            System.Xml.Linq.XElement r;
            try { r = RrSoap.Return(xml); }
            catch (RrException) { continue; }
            yield return r;
        }
    }
}
