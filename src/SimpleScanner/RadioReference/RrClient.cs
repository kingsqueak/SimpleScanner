// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace SimpleScanner.RadioReference;

/// <summary>
/// The RadioReference database web service (https://api.radioreference.com/soap2/): countries,
/// states, counties, their categories and agencies, and the frequencies in them. Every call
/// but the list of countries needs the user's own RadioReference account (with a Premium
/// subscription) as well as this program's key. Answers are kept in <see cref="RrCache"/>.
/// </summary>
public sealed class RrClient : IDisposable
{
    public const string Address = "https://api.radioreference.com/soap2/index.php";
    /// <summary>How long a kept answer is used before the service is asked again.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly RrCache _cache;
    private readonly Dictionary<string, string> _modeNames = [];
    private readonly Dictionary<int, string> _tagNames = [];
    private readonly Dictionary<int, string> _typeNames = [], _flavorNames = [], _voiceNames = [];

    /// <summary>The account the calls are made with; null until the user has signed in.</summary>
    public RrAuth? Auth { get; set; }
    /// <summary>Counts the questions that went to the service (the rest were answered from the cache).</summary>
    public int Fetches { get; private set; }

    public RrClient(RrCache cache, HttpMessageHandler? handler = null)
    {
        _cache = cache;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(40);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleScanner");
    }

    // ---- the calls --------------------------------------------------------------------------

    /// <summary>Checks the account: who it is and when the subscription ends. Never from the cache.</summary>
    public async Task<RrUser> GetUserAsync(CancellationToken ct = default)
    {
        var r = await CallAsync("getUserData", [], needsAuth: true, cached: false, fresh: true, ct);
        return new RrUser(r.Str("username"), r.Str("subExpireDate"));
    }

    public async Task<List<RrCountry>> GetCountriesAsync(CancellationToken ct = default)
    {
        var r = await CallAsync("getCountryList", [], needsAuth: false, cached: true, fresh: false, ct);
        return r.Rows().Select(c => new RrCountry(c.Int("coid"), c.Str("countryName"), c.Str("countryCode"))).ToList();
    }

    public async Task<RrCountryInfo> GetCountryAsync(int coid, bool fresh = false, CancellationToken ct = default)
    {
        var r = await CallAsync("getCountryInfo", [("coid", coid)], true, true, fresh, ct);
        return new RrCountryInfo(r.Int("coid"), r.Str("countryName"),
            r.Rows("stateList").Select(s => new RrState(s.Int("stid"), s.Str("stateName"), s.Str("stateCode"))).ToList(),
            Agencies(r));
    }

    public async Task<RrStateInfo> GetStateAsync(int stid, bool fresh = false, CancellationToken ct = default)
    {
        var r = await CallAsync("getStateInfo", [("stid", stid)], true, true, fresh, ct);
        return new RrStateInfo(r.Int("stid"), r.Str("stateName"),
            r.Rows("countyList").Select(c => new RrCounty(c.Int("ctid"), c.Str("countyName"))).ToList(),
            Agencies(r), Systems(r));
    }

    public async Task<RrCountyInfo> GetCountyAsync(int ctid, bool fresh = false, CancellationToken ct = default)
    {
        var r = await CallAsync("getCountyInfo", [("ctid", ctid)], true, true, fresh, ct);
        return new RrCountyInfo(r.Int("ctid"), r.Str("countyName"), r.Str("countyHeader"), r.Int("stid"), r.Geo("lat"), r.Geo("lon"),
            Cats(r), Agencies(r), Systems(r));
    }

    public async Task<RrAgencyInfo> GetAgencyAsync(int aid, bool fresh = false, CancellationToken ct = default)
    {
        var r = await CallAsync("getAgencyInfo", [("aid", aid)], true, true, fresh, ct);
        return new RrAgencyInfo(r.Int("aid"), r.Str("agencyName"), Cats(r));
    }

    /// <summary>The frequencies of one subcategory, with the names of their modes and tags filled in.</summary>
    public async Task<List<RrFreq>> GetFreqsAsync(int scid, bool fresh = false, CancellationToken ct = default)
    {
        var r = await CallAsync("getSubcatFreqs", [("scid", scid)], true, true, fresh, ct);
        var list = new List<RrFreq>();
        foreach (var f in r.Rows())
        {
            var tags = new List<RrTag>();
            foreach (var t in f.Rows("tags"))
            {
                int id = t.Int("tagId");
                string descr = t.Str("tagDescr");
                tags.Add(new RrTag(id, descr.Length > 0 ? descr : await TagNameAsync(id, ct)));
            }
            list.Add(new RrFreq(f.Int("fid"), f.Dbl("out"), f.Dbl("in"), f.Str("callsign"), f.Str("descr"), f.Str("alpha"),
                f.Str("tone"), f.Str("colorCode"), f.Str("tg"), f.Str("slot"), await ModeNameAsync(f.Str("mode"), ct),
                f.Int("enc"), f.Str("class"), tags, f.Int("scid") is > 0 and var s ? s : scid));
        }
        return list;
    }

    public async Task<RrZip> GetZipAsync(int zip, CancellationToken ct = default)
    {
        var r = await CallAsync("getZipcodeInfo", [("zipcode", zip)], true, true, false, ct);
        return new RrZip(r.Int("zipCode"), r.Geo("lat"), r.Geo("lon"), r.Str("city"), r.Int("stid"), r.Int("ctid"));
    }

    // ---- trunked systems ----------------------------------------------------------------------

    /// <summary>A trunked system's details, with the names of its kind, flavour and voice filled in.</summary>
    public async Task<RrTrs> GetTrsAsync(int sid, bool fresh = false, CancellationToken ct = default)
    {
        var r = await CallAsync("getTrsDetails", [("sid", sid)], true, true, fresh, ct);
        int type = r.Int("sType"), flavor = r.Int("sFlavor"), voice = r.Int("sVoice");
        return new RrTrs(sid, r.Str("sName"), type, flavor, voice,
            await TrsTypeNameAsync(type, ct),
            await DefNameAsync(_flavorNames, "getTrsFlavor", flavor, "sFlavor", "sFlavorDescr", ct),
            await DefNameAsync(_voiceNames, "getTrsVoice", voice, "sVoice", "sVoiceDescr", ct),
            r.Str("sCity"),
            r.Rows("sysid").Select(x => new RrSysid(x.Str("sysid"), x.Str("wacn"))).Where(x => x.Sysid.Length > 0 || x.Wacn.Length > 0).ToList());
    }

    public async Task<List<RrTrsSite>> GetTrsSitesAsync(int sid, bool fresh = false, CancellationToken ct = default)
    {
        var r = await CallAsync("getTrsSites", [("sid", sid)], true, true, fresh, ct);
        return r.Rows().Select(s => new RrTrsSite(s.Int("siteId"), s.Int("siteNumber"), s.Str("siteDescr"), s.Int("rfss"), s.Str("nac"),
            s.Str("siteCt"), s.Str("siteModulation"), s.Int("tdma_cc"), s.Geo("lat"), s.Geo("lon"),
            s.Rows("siteFreqs").Select(f => new RrSiteFreq(f.Int("lcn"), f.Dbl("freq"), f.Str("use"))).Where(f => f.MHz > 0).ToList(),
            s.Int("siteCtid"))).ToList();
    }

    /// <summary>Every talkgroup of a system (no category, tag or number asked for), with the names of their tags filled in.</summary>
    public async Task<List<RrTalkgroup>> GetTalkgroupsAsync(int sid, bool fresh = false, CancellationToken ct = default)
    {
        var r = await CallAsync("getTrsTalkgroups", [("sid", sid), ("tgCid", 0), ("tgTag", 0), ("tgDec", 0)], true, true, fresh, ct);
        var list = new List<RrTalkgroup>();
        foreach (var t in r.Rows())
        {
            var tags = new List<RrTag>();
            foreach (var g in t.Rows("tags"))
            {
                int id = g.Int("tagId");
                string descr = g.Str("tagDescr");
                tags.Add(new RrTag(id, descr.Length > 0 ? descr : await TagNameAsync(id, ct)));
            }
            list.Add(new RrTalkgroup(t.Int("tgId"), t.Int("tgDec"), t.Str("tgDescr"), t.Str("tgAlpha"), t.Str("tgMode"), t.Int("enc"), tags, t.Int("tgCid")));
        }
        return list;
    }

    public async Task<List<RrTalkgroupCat>> GetTalkgroupCatsAsync(int sid, bool fresh = false, CancellationToken ct = default)
    {
        var r = await CallAsync("getTrsTalkgroupCats", [("sid", sid)], true, true, fresh, ct);
        return r.Rows().Select(c => new RrTalkgroupCat(c.Int("tgCid"), c.Str("tgCname"))).Where(c => c.Id > 0).ToList();
    }

    /// <summary>All of a system: its details, sites, talkgroups and their categories.</summary>
    public async Task<RrTrsWhole> GetTrsWholeAsync(int sid, bool fresh = false, CancellationToken ct = default) =>
        new(await GetTrsAsync(sid, fresh, ct), await GetTrsSitesAsync(sid, fresh, ct), await GetTalkgroupsAsync(sid, fresh, ct),
            await GetTalkgroupCatsAsync(sid, fresh, ct));

    /// <summary>The name of a kind of trunked system ("Project 25 Phase I"); empty when it is not known.</summary>
    public Task<string> TrsTypeNameAsync(int type, CancellationToken ct = default) =>
        DefNameAsync(_typeNames, "getTrsType", type, "sType", "sTypeDescr", ct);

    // The kinds, flavours and voices of systems come as numbers; these calls give their names (the one asked for, or all).
    private async Task<string> DefNameAsync(Dictionary<int, string> names, string call, int id, string idField, string nameField, CancellationToken ct)
    {
        if (id <= 0) return "";
        if (names.TryGetValue(id, out string? name)) return name;
        try
        {
            var r = await CallAsync(call, [("id", id)], true, true, false, ct);
            foreach (var d in r.Rows())
                if (d.Str(nameField) is { Length: > 0 } n) names[d.Int(idField)] = n;
        }
        catch (RrException) { }
        return names.TryGetValue(id, out name) ? name : names[id] = "";
    }

    // A frequency's mode may come as a number; getMode gives the names (all of them, or the one asked for).
    private async Task<string> ModeNameAsync(string mode, CancellationToken ct)
    {
        if (mode.Length == 0 || !int.TryParse(mode, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) return mode;
        if (_modeNames.TryGetValue(mode, out string? name)) return name;
        try
        {
            var r = await CallAsync("getMode", [("mode", id)], true, true, false, ct);
            foreach (var m in r.Rows())
                if (m.Str("modeName") is { Length: > 0 } n) _modeNames[m.Str("mode")] = n;
        }
        catch (RrException) { }
        return _modeNames.TryGetValue(mode, out name) ? name : _modeNames[mode] = mode;
    }

    private async Task<string> TagNameAsync(int id, CancellationToken ct)
    {
        if (id <= 0) return "";
        if (_tagNames.TryGetValue(id, out string? name)) return name;
        try
        {
            var r = await CallAsync("getTag", [("id", id)], true, true, false, ct);
            foreach (var t in r.Rows())
                if (t.Str("tagDescr") is { Length: > 0 } n) _tagNames[t.Int("tagId")] = n;
        }
        catch (RrException) { }
        return _tagNames.TryGetValue(id, out name) ? name : _tagNames[id] = "";
    }

    internal static List<RrAgency> Agencies(XElement r) =>
        r.Rows("agencyList").Select(a => new RrAgency(a.Int("aid"), a.Str("aName"))).Where(a => a.Id > 0).ToList();

    private static List<RrSystem> Systems(XElement r) =>
        r.Rows("trsList").Select(s => new RrSystem(s.Int("sid"), s.Str("sName"), s.Str("sCity"), s.Int("sType"))).Where(s => s.Id > 0).ToList();

    internal static List<RrCat> Cats(XElement r) =>
        r.Rows("cats").Select(c => new RrCat(c.Int("cid"), c.Str("cName"),
            c.Rows("subcats").Select(s => new RrSubcat(s.Int("scid"), s.Str("scName"), s.Geo("lat"), s.Geo("lon"), s.Geo("range")))
                .Where(s => s.Id > 0).ToList())).ToList();

    // ---- asking -----------------------------------------------------------------------------

    private async Task<XElement> CallAsync(string call, (string Name, object Value)[] args, bool needsAuth, bool cached, bool fresh,
        CancellationToken ct)
    {
        string question = call + "(" + string.Join(",", args.Select(a => Convert.ToString(a.Value, CultureInfo.InvariantCulture))) + ")";
        if (cached && !fresh && _cache.Get(question, KeepFor) is { } kept)
        {
            // A kept answer that can no longer be read (the service's format changed) is fetched again.
            try { return RrSoap.Return(kept.Xml); }
            catch (RrException) { _cache.Forget(question); }
        }
        if (needsAuth && Auth is null) throw new RrException("Sign in to RadioReference first");

        string xml;
        try
        {
            using var content = new StringContent(RrSoap.Request(call, args, needsAuth ? Auth : null), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
            using var request = new HttpRequestMessage(HttpMethod.Post, Address) { Content = content };
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{RrSoap.Namespace}#{call}\"");
            Fetches++;
            // A refusal comes as HTTP 500 with the reason in the body, so the body is read whatever the status.
            using var response = await _http.SendAsync(request, ct);
            xml = await response.Content.ReadAsStringAsync(ct);
            if (xml.Length == 0) throw new RrException($"RadioReference did not answer (HTTP {(int)response.StatusCode})");
        }
        catch (HttpRequestException e) { throw new RrException("RadioReference could not be reached: " + e.Message, e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new RrException("RadioReference took too long to answer", e); }

        var result = RrSoap.Return(xml);
        if (cached) _cache.Put(question, xml);
        return result;
    }

    public void Dispose() => _http.Dispose();
}
