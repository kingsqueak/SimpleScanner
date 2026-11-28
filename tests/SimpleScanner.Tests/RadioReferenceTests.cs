// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Net;
using System.Text;
using SimpleScanner.Dsp;
using SimpleScanner.RadioReference;
using SimpleScanner.Trunk;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Tests;

/// <summary>
/// The RadioReference web service client and browser, against a pretend service that answers in
/// the real one's format (the envelope and the list of countries are as the real service sent
/// them; the calls that need an account are written from its published definition).
/// </summary>
public class RadioReferenceTests
{
    // ---- a pretend service ------------------------------------------------------------------

    internal sealed class Service : HttpMessageHandler
    {
        public List<(string Call, string Body)> Asked { get; } = [];
        public Func<string, string, string>? Answer { get; set; }
        public HttpStatusCode Code { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string action = request.Headers.GetValues("SOAPAction").First().Trim('"');
            string call = action[(action.IndexOf('#') + 1)..];
            string body = await request.Content!.ReadAsStringAsync(ct);
            Asked.Add((call, body));
            return new HttpResponseMessage(Code) { Content = new StringContent(Answer!(call, body), Encoding.UTF8, "text/xml") };
        }

        public int Count(string call) => Asked.Count(a => a.Call == call);
    }

    private static string Envelope(string call, string inner) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><SOAP-ENV:Envelope SOAP-ENV:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\" " +
        "xmlns:SOAP-ENV=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" " +
        "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:SOAP-ENC=\"http://schemas.xmlsoap.org/soap/encoding/\" " +
        "xmlns:tns=\"http://api.radioreference.com/soap2\"><SOAP-ENV:Body>" +
        $"<ns1:{call}Response xmlns:ns1=\"http://api.radioreference.com/soap2\">{inner}</ns1:{call}Response></SOAP-ENV:Body></SOAP-ENV:Envelope>";

    private static string Fault(string why) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://schemas.xmlsoap.org/soap/envelope/\"><SOAP-ENV:Body>" +
        $"<SOAP-ENV:Fault><faultcode>AUTH</faultcode><faultstring>{why}</faultstring></SOAP-ENV:Fault></SOAP-ENV:Body></SOAP-ENV:Envelope>";

    // A struct or a row of a list: fields as name, value pairs; a value starting with '<' is put in as it is.
    private static string Item(string tag, params (string Name, string Value)[] fields) =>
        $"<{tag}>" + string.Concat(fields.Select(f => f.Value.StartsWith('<') ? f.Value : $"<{f.Name} xsi:type=\"xsd:string\">{f.Value}</{f.Name}>")) + $"</{tag}>";

    private static string List(string name, params string[] items) =>
        $"<{name} xsi:type=\"SOAP-ENC:Array\">{string.Concat(items)}</{name}>";

    // As the real service sent it on 2026-11-24 (three of its 236 rows).
    private const string Countries =
        "<return xsi:type=\"SOAP-ENC:Array\" SOAP-ENC:arrayType=\"tns:Country[3]\">" +
        "<item xsi:type=\"tns:Country\"><coid xsi:type=\"xsd:int\">5</coid><countryName xsi:type=\"xsd:string\">Afghanistan</countryName><countryCode xsi:type=\"xsd:string\">AF</countryCode></item>" +
        "<item xsi:type=\"tns:Country\"><coid xsi:type=\"xsd:int\">2</coid><countryName xsi:type=\"xsd:string\">Canada</countryName><countryCode xsi:type=\"xsd:string\">CA</countryCode></item>" +
        "<item xsi:type=\"tns:Country\"><coid xsi:type=\"xsd:int\">1</coid><countryName xsi:type=\"xsd:string\">United States</countryName><countryCode xsi:type=\"xsd:string\">US</countryCode></item>" +
        "</return>";

    private static string Subcat(int id, string name, string lat = "0", string lon = "0") =>
        Item("item", ("scid", $"{id}"), ("scName", name), ("lat", lat), ("lon", lon), ("range", "5"));

    private static string Freq(int fid, string mhz, string alpha, string descr, string mode, string tone = "", string tag = "Law Dispatch",
        string input = "0", string cc = "", string tg = "", string slot = "", int enc = 0) =>
        Item("item", ("fid", $"{fid}"), ("out", mhz), ("in", input), ("callsign", "KCA123"), ("descr", descr), ("alpha", alpha),
            ("tone", tone), ("colorCode", cc), ("tg", tg), ("slot", slot), ("mode", mode), ("enc", $"{enc}"), ("class", "RM"),
            ("tags", List("tags", Item("item", ("tagId", "2"), ("tagDescr", tag)))), ("scid", "0"));

    // ---- trunked systems, written from the service's definition (no live answer seen yet) ---

    // The talkgroups of the P25 system; a test may change them to stand for RadioReference's being edited.
    internal static List<string> Talkgroups() =>
    [
        Talkgroup(101, "Milford PD", "Police dispatch", "D", 0, "Law Dispatch", 1),
        Talkgroup(102, "Milford FD", "Fire dispatch", "T", 0, "Fire Dispatch", 2),
        Talkgroup(103, "PD Tac", "Police tactical", "DE", 2, "Law Tac", 1),
        Talkgroup(104, "PD Ops", "Police operations", "De", 1, "Law Talk", 1),
    ];

    internal static string Talkgroup(int dec, string alpha, string descr, string mode, int enc, string tag, int cid) =>
        Item("item", ("tgId", $"{dec + 90000}"), ("tgDec", $"{dec}"), ("tgSubfleet", ""), ("tgLtr", "false"), ("tgSlot", ""),
            ("tgDescr", descr), ("tgAlpha", alpha), ("tgMode", mode), ("enc", $"{enc}"),
            ("tags", List("tags", Item("item", ("tagId", "7"), ("tagDescr", tag)))), ("tgCid", $"{cid}"), ("tgSort", "0"));

    private static string SiteFreq(int lcn, string mhz, string use) =>
        Item("item", ("lcn", $"{lcn}"), ("freq", mhz), ("use", use), ("colorCode", ""), ("ch_id", ""));

    // As RadioReference sends them, a site's county may be its number only (siteCtid, with siteCt empty).
    private static string Site(int id, int number, string descr, string nac, string county, string freqs, int tdma = 0, int ctid = 0) =>
        Item("item", ("siteId", $"{id}"), ("sid", "6000"), ("siteNumber", $"{number}"), ("siteDescr", descr), ("zoneNumber", "0"),
            ("rfss", "1"), ("nac", nac), ("siteCt", county), ("siteCtid", $"{ctid}"), ("siteModulation", "C4FM"), ("lat", "42.83"),
            ("lon", "-71.65"), ("tdma_cc", $"{tdma}"), ("siteFreqs", freqs));

    // As rpc/encoded may send them: a value written once at the end of the body and pointed to with href.
    private static string WithRefs(string envelope, params (string Id, string Xml)[] refs) =>
        envelope.Replace("</SOAP-ENV:Body>", string.Concat(refs.Select(r => r.Xml.Replace("<multiRef>", $"<multiRef id=\"{r.Id}\" xsi:type=\"SOAP-ENC:Array\">"))) + "</SOAP-ENV:Body>");

    private List<string> _talkgroups = Talkgroups();

    private string TrunkAnswer(string call, string body, Func<string, string> arg) => call switch
    {
        "getTrsType" => Envelope(call, List("return", Item("item", ("sType", arg("id")), ("sTypeDescr", arg("id") switch
        {
            "2" => "Motorola Type II Smartnet",
            "3" => "EDACS Standard",
            "8" => "Project 25 Phase I",
            "12" => "Project 25 Phase II",
            _ => "",
        })))),
        "getTrsFlavor" => Envelope(call, List("return", Item("item", ("sType", "8"), ("sFlavor", arg("id")), ("sFlavorDescr", "Standard")))),
        "getTrsVoice" => Envelope(call, List("return", Item("item", ("sType", "8"), ("sVoice", arg("id")), ("sVoiceDescr", "Analog and Digital")))),
        "getTrsDetails" => arg("sid") == "6000"
            ? WithRefs(Envelope(call, Item("return", ("sName", "Souhegan Valley"), ("sType", "8"), ("sFlavor", "1"), ("sVoice", "4"),
                    ("sCity", "Milford"), ("sysid", "<sysid href=\"#ref1\"/>"))),
                ("ref1", "<multiRef>" + Item("item", ("sysid", "3A1"), ("ct", "1766"), ("wacn", "BEE00"), ("model", "")) + "</multiRef>"))
            // A system with no system id listed.
            : Envelope(call, Item("return", ("sName", "NH State Interop"), ("sType", "12"), ("sFlavor", "1"), ("sVoice", "4"),
                ("sCity", ""), ("sysid", List("sysid")))),
        "getTrsSites" => WithRefs(Envelope(call, List("return",
                Site(61, 1, "Milford", "293", "Hillsborough", List("siteFreqs",
                    SiteFreq(3, "852.5375", ""), SiteFreq(1, "851.0125", "d"), SiteFreq(2, "851.2625", "a"))),
                Site(62, 2, "Amherst", "0x2a1", "", "<siteFreqs href=\"#ref2\"/>", tdma: 1, ctid: 1766))),
            ("ref2", "<multiRef>" + SiteFreq(1, "853.1125", "D") + SiteFreq(2, "853.6125", "") + "</multiRef>")),
        "getTrsTalkgroupCats" => Envelope(call, List("return",
            Item("item", ("tgCid", "1"), ("sid", "6000"), ("tgCname", "Police")),
            Item("item", ("tgCid", "2"), ("sid", "6000"), ("tgCname", "Fire")))),
        "getTrsTalkgroups" => Envelope(call, List("return", [.. _talkgroups])),
        _ => Fault("No such call in the test: " + call),
    };

    /// <summary>A small New Hampshire: one county with two categories, a county agency, a state agency and trunked systems.</summary>
    private string Answer(string call, string body)
    {
        string Arg(string name)
        {
            int at = body.IndexOf($"<{name} ", StringComparison.Ordinal);
            int from = body.IndexOf('>', at) + 1;
            return body[from..body.IndexOf('<', from)];
        }
        if (call != "getCountryList" && !body.Contains("<password xsi:type=\"xsd:string\">right</password>"))
            return Fault("Invalid username or password");
        return call switch
        {
            "getCountryList" => Envelope(call, Countries),
            "getUserData" => Envelope(call, Item("return", ("username", "listener1"), ("subExpireDate", "2030-01-01"))),
            "getCountryInfo" => Envelope(call, Item("return", ("coid", "1"), ("countryName", "United States"), ("countryCode", "US"),
                ("agencyList", List("agencyList", Item("item", ("aid", "900"), ("aName", "Federal Aviation Administration"), ("aType", "1")))),
                ("stateList", List("stateList",
                    Item("item", ("stid", "33"), ("stateName", "New Hampshire"), ("stateCode", "NH")),
                    Item("item", ("stid", "23"), ("stateName", "Maine"), ("stateCode", "ME")))))),
            "getStateInfo" => Envelope(call, Item("return", ("stid", "33"), ("stateName", "New Hampshire"),
                ("trsList", List("trsList",
                    Item("item", ("sid", "5000"), ("sName", "Nashua Public Safety"), ("sType", "2"), ("sCity", "Nashua")),
                    Item("item", ("sid", "7000"), ("sName", "NH State Interop"), ("sType", "12"), ("sCity", "")),
                    Item("item", ("sid", "7100"), ("sName", "Coastal EDACS"), ("sType", "3"), ("sCity", "Portsmouth")))),
                ("agencyList", List("agencyList", Item("item", ("aid", "700"), ("aName", "State Police"), ("aType", "2")))),
                ("countyList", List("countyList",
                    Item("item", ("ctid", "1767"), ("countyName", "Merrimack"), ("countyHeader", "")),
                    Item("item", ("ctid", "1766"), ("countyName", "Hillsborough"), ("countyHeader", "")))))),
            "getZipcodeInfo" => Envelope(call, Arg("zipcode") == "3101"   // a whole number on the wire: the leading zero is gone
                ? Item("return", ("zipCode", "3101"), ("lat", "42.99"), ("lon", "-71.46"), ("city", "Manchester"), ("stid", "33"), ("ctid", "1766"))
                : Item("return", ("zipCode", "0"), ("lat", ""), ("lon", ""), ("city", ""), ("stid", "0"), ("ctid", "0"))),
            "getCountyInfo" => Envelope(call, Item("return", ("ctid", "1766"), ("countyName", "Hillsborough"), ("countyHeader", "County"), ("stid", "33"),
                ("lat", "42.9"), ("lon", "-71.7"), ("range", "20"),
                ("cats", List("cats",
                    Item("item", ("cid", "10"), ("cName", "Municipalities"), ("subcats", List("subcats",
                        Subcat(101, "Manchester", "42.99", "-71.46"), Subcat(102, "Nashua", "42.76", "-71.46")))),
                    Item("item", ("cid", "11"), ("cName", "Airports"), ("subcats", List("subcats", Subcat(103, "Manchester Airport (MHT)")))))),
                ("trsList", List("trsList", Item("item", ("sid", "5000"), ("sName", "Nashua Public Safety"), ("sType", "2"), ("sCity", "Nashua")),
                    Item("item", ("sid", "6000"), ("sName", "Souhegan Valley"), ("sType", "8"), ("sCity", "Milford")))),
                ("agencyList", List("agencyList", Item("item", ("aid", "800"), ("aName", "County Sheriff"), ("aType", "3")))))),
            "getAgencyInfo" => Envelope(call, Item("return", ("aid", Arg("aid")), ("agencyName", "State Police"),
                ("cats", List("cats", Item("item", ("cid", "20"), ("cName", "Troops"), ("subcats", List("subcats", Subcat(201, "Troop A"), Subcat(202, "Troop B")))))))),
            "getSubcatFreqs" => Envelope(call, List("return", Arg("scid") switch
            {
                "101" => [Freq(1, "154.815", "Manch PD 1", "Dispatch", "2", "117 NAC"), Freq(2, "460.100", "Manch PD 2", "Tactical", "FMN", "136.5 PL", "Law Tac", "465.100")],
                "102" => [Freq(3, "464.925", "Nashua PD", "Dispatch", "DMR", "", "Law Dispatch", "469.925", "7", "1100", "1", enc: 2)],
                "103" => [Freq(4, "121.300", "MHT Tower", "Tower", "AM", "", "Aircraft"), Freq(5, "836.500", "Cell", "In the cellular band", "FMN"),
                    Freq(8, "173.2625", "MHT Telemetry", "Runway lights", "Telm", "", "Data")],
                "201" => [Freq(6, "44.94", "NHSP A", "Troop A dispatch", "FM", "", "Law Dispatch")],
                "202" => [Freq(7, "45.06", "NHSP B", "Troop B dispatch", "FM", "", "Law Dispatch")],
                _ => [],
            })),
            "getMode" => Envelope(call, List("return", Item("item", ("mode", Arg("mode")), ("modeName", "P25")))),
            _ => TrunkAnswer(call, body, Arg),
        };
    }

    private (RrClient Client, Service Service) Signed(string password = "right")
    {
        var service = new Service { Answer = Answer };
        var client = new RrClient(new RrCache(":memory:"), service) { Auth = new RrAuth("listener1", password, "the-key") };
        return (client, service);
    }

    private static MemoryStore Store() => new(Path.Combine(Path.GetTempPath(), $"ss-rr-{Guid.NewGuid():N}.json"));

    // ---- the client -------------------------------------------------------------------------

    [Fact]
    public async Task The_list_of_countries_is_read_without_an_account()
    {
        var service = new Service { Answer = Answer };
        using var client = new RrClient(new RrCache(":memory:"), service);
        var list = await client.GetCountriesAsync();
        Assert.Equal(3, list.Count);
        Assert.Equal(new RrCountry(1, "United States", "US"), list[2]);
        Assert.DoesNotContain("authInfo", service.Asked[0].Body);
    }

    [Fact]
    public async Task A_call_carries_its_arguments_and_the_account_with_awkward_characters_made_safe()
    {
        var service = new Service { Answer = (_, _) => Fault("no") };
        using var client = new RrClient(new RrCache(":memory:"), service) { Auth = new RrAuth("me", "p<&>\"w", "key-1") };
        await Assert.ThrowsAsync<RrException>(() => client.GetCountyAsync(1766));
        var (call, body) = service.Asked.Single();
        Assert.Equal("getCountyInfo", call);
        Assert.Contains("<ns1:getCountyInfo><ctid xsi:type=\"xsd:int\">1766</ctid><authInfo", body);
        Assert.Contains("<username xsi:type=\"xsd:string\">me</username>", body);
        Assert.Contains("<password xsi:type=\"xsd:string\">p&lt;&amp;&gt;&quot;w</password>", body);
        Assert.Contains("<appKey xsi:type=\"xsd:string\">key-1</appKey>", body);
        System.Xml.Linq.XDocument.Parse(body);                    // and it is well-formed
    }

    [Fact]
    public async Task A_refusal_is_reported_in_the_services_own_words()
    {
        var (client, service) = Signed("wrong");
        service.Code = HttpStatusCode.InternalServerError;        // a fault comes with HTTP 500
        var e = await Assert.ThrowsAsync<RrException>(() => client.GetUserAsync());
        Assert.Equal("RadioReference says: Invalid username or password", e.Message);

        service.Answer = (_, _) => "<html><body>Bad gateway</body></html>";
        e = await Assert.ThrowsAsync<RrException>(() => client.GetStateAsync(33));
        Assert.StartsWith("RadioReference sent an answer with nothing in it", e.Message);
    }

    [Fact]
    public async Task Without_an_account_nothing_is_asked()
    {
        var service = new Service { Answer = Answer };
        using var client = new RrClient(new RrCache(":memory:"), service);
        var e = await Assert.ThrowsAsync<RrException>(() => client.GetCountyAsync(1766));
        Assert.Equal("Sign in to RadioReference first", e.Message);
        Assert.Empty(service.Asked);
    }

    [Fact]
    public async Task A_county_is_read_with_its_categories_agencies_and_systems()
    {
        var (client, _) = Signed();
        var county = await client.GetCountyAsync(1766);
        Assert.Equal(("Hillsborough", 33, 42.9, -71.7), (county.Name, county.StateId, county.Lat, county.Lon));
        Assert.Equal(["Municipalities", "Airports"], county.Cats.Select(c => c.Name));
        Assert.Equal("County", county.Header);
        Assert.Equal(new RrSubcat(102, "Nashua", 42.76, -71.46, 5), county.Cats[0].Subcats[1]);
        Assert.Null(county.Cats[1].Subcats[0].Lat);                // 0 is "no position"
        Assert.Equal(new RrAgency(800, "County Sheriff"), county.Agencies.Single());
        Assert.Equal([new RrSystem(5000, "Nashua Public Safety", "Nashua", 2), new RrSystem(6000, "Souhegan Valley", "Milford", 8)], county.Systems);
    }

    [Fact]
    public async Task An_answer_is_kept_and_not_asked_for_twice_unless_asked_afresh()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ss-rr-{Guid.NewGuid():N}.db");
        try
        {
            var service = new Service { Answer = Answer };
            using (var cache = new RrCache(path))
            using (var client = new RrClient(cache, service) { Auth = new RrAuth("listener1", "right", "k") })
            {
                await client.GetCountyAsync(1766);
                await client.GetCountyAsync(1766);
                Assert.Equal(1, service.Count("getCountyInfo"));
                await client.GetCountyAsync(1766, fresh: true);
                Assert.Equal(2, service.Count("getCountyInfo"));
                await client.GetUserAsync();
                await client.GetUserAsync();
                Assert.Equal(2, service.Count("getUserData"));     // the account is always checked with the service
            }
            // Another run of the program: the file answers, with nobody signed in. The password is not in it.
            using (var cache = new RrCache(path))
            using (var client = new RrClient(cache, service))
            {
                Assert.Equal("Hillsborough", (await client.GetCountyAsync(1766)).Name);
                Assert.Equal(2, service.Count("getCountyInfo"));
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Assert.DoesNotContain("right", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_refusal_is_not_kept()
    {
        var (client, service) = Signed("wrong");
        await Assert.ThrowsAsync<RrException>(() => client.GetCountyAsync(1766));
        client.Auth = new RrAuth("listener1", "right", "k");
        Assert.Equal("Hillsborough", (await client.GetCountyAsync(1766)).Name);
        Assert.Equal(2, service.Count("getCountyInfo"));
    }

    [Fact]
    public void A_value_sent_once_and_pointed_to_is_found()
    {
        string xml = Envelope("getCountyInfo",
            "<return><ctid>1766</ctid><countyName href=\"#ref1\"/></return>").Replace("</SOAP-ENV:Body>",
            "<multiRef id=\"ref1\" xsi:type=\"xsd:string\">Hillsborough</multiRef></SOAP-ENV:Body>");
        Assert.Equal("Hillsborough", RrSoap.Return(xml).Str("countyName"));
    }

    // ---- frequencies as channels ------------------------------------------------------------

    [Fact]
    public async Task Fetched_frequencies_become_channels_field_by_field()
    {
        var (client, service) = Signed();
        var p25 = RrImport.ToChannel((await client.GetFreqsAsync(101))[0], "Manchester");
        Assert.Equal(("Manch PD 1", 154.815e6, "Manchester", "Dispatch", "Law Dispatch"),
            (p25.Name, p25.FrequencyHz, p25.Agency, p25.Description, p25.Tag));
        Assert.Equal("P25", p25.Format);                           // the mode came as the number 2 and was looked up
        Assert.Equal(1, service.Count("getMode"));
        Assert.Equal((Mode.Nfm, BandPlan.NfmNarrow), (p25.Mode, p25.BandwidthHz));
        Assert.Equal(("117 NAC", "KCA123", "RM", "rr:1"), (p25.Tone, p25.Callsign, p25.StationClass, p25.ImportKey));
        Assert.Null(p25.InputHz);

        var fm = RrImport.ToChannel((await client.GetFreqsAsync(101))[1], "Manchester");
        Assert.Equal((465.1e6, "136.5 PL", "Law Tac"), (fm.InputHz, fm.Tone, fm.Tag));

        var dmr = RrImport.ToChannel((await client.GetFreqsAsync(102))[0], "Nashua");
        Assert.Equal(("DMRE", "CC 7 TG 1100 SL 1"), (dmr.Format, dmr.Tone));   // encrypted throughout
        Assert.Equal((Mode.Nfm, BandPlan.NfmNarrow), (dmr.Mode, dmr.BandwidthHz));

        // The service's own name for 4800 bit/s NXDN, as seen in real answers (2026-11-24); some of its traffic encrypted.
        var nxdn = RrImport.ToChannel(new RrFreq(9, 160.215, 0, "", "Channel 307", "AAR 307", "", "", "", "", "NXDN48", 1, "BM", [], 1), "Railroads");
        Assert.Equal(("NXDN48e", Mode.Nfm, BandPlan.NfmNarrow), (nxdn.Format, nxdn.Mode, nxdn.BandwidthHz));

        var air = RrImport.ToChannel((await client.GetFreqsAsync(103))[0], "Manchester Airport (MHT)");
        Assert.Equal((Mode.Am, BandPlan.AmAir), (air.Mode, air.BandwidthHz));
    }

    [Fact]
    public async Task Loading_twice_or_after_a_csv_import_of_the_same_rows_adds_nothing()
    {
        var (client, _) = Signed();
        var store = Store();
        // The same Manchester row as a CSV download has it, imported earlier.
        RadioReferenceCsv.Import(store, RadioReferenceCsv.Parse(
            "\"Frequency Output\",\"Frequency Input\",\"FCC Callsign\",Agency/Category,Description,\"Alpha Tag\",\"PL Output Tone\",\"PL Input Tone\",Mode,\"Class Station Code\",Tag\n" +
            "154.815000,0.00000,KCA123,Manchester,Dispatch,\"Manch PD 1\",\"117 NAC\",,P25,RM,\"Law Dispatch\"\n"), "Old county file");

        var fetched = (await client.GetFreqsAsync(101)).Concat(await client.GetFreqsAsync(103))
            .Select(f => RrImport.ToChannel(f, "x")).ToList();
        var first = RrImport.Add(store, fetched, "Hillsborough, NH");
        Assert.Equal((3, 1, 1, 1), (first.Added, first.AlreadyStored, first.Skipped, first.Telemetry));   // the cellular one is left out
        Assert.Equal(4, store.Channels.Count);
        Assert.Equal(["Old county file", "Hillsborough, NH"], store.Channels[0].Groups);

        var again = RrImport.Add(store, (await client.GetFreqsAsync(101)).Select(f => RrImport.ToChannel(f, "x")), "Hillsborough, NH");
        Assert.Equal((0, 2), (again.Added, again.AlreadyStored));
        Assert.Equal(4, store.Channels.Count);
    }

    // ---- the browser ------------------------------------------------------------------------

    internal async Task<(RadioReferenceViewModel Vm, Service Service, MemoryStore Store, Settings.RrPlace Place)> Browser(TrunkStore? trunks = null)
    {
        var service = new Service { Answer = Answer };
        var store = Store();
        var place = new Settings.RrPlace();
        var vm = new RadioReferenceViewModel(store, new RrClient(new RrCache(":memory:"), service), place, () => "the-key", trunks: trunks);
        await vm.OpenAsync();
        vm.Username = "listener1";
        vm.Password = "right";
        await vm.SignInCommand.ExecuteAsync(null);
        return (vm, service, store, place);
    }

    [Fact]
    public async Task A_wrong_password_does_not_sign_in_and_says_why()
    {
        var service = new Service { Answer = Answer };
        var vm = new RadioReferenceViewModel(Store(), new RrClient(new RrCache(":memory:"), service), null, () => "the-key");
        await vm.OpenAsync();
        Assert.Equal("United States", vm.SelectedCountry?.Name);
        vm.Username = "listener1";
        vm.Password = "wrong";
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.False(vm.SignedIn);
        Assert.Equal("RadioReference says: Invalid username or password", vm.Status);
    }

    [Fact]
    public async Task A_build_with_no_key_says_so_and_asks_nothing()
    {
        var service = new Service { Answer = Answer };
        var vm = new RadioReferenceViewModel(Store(), new RrClient(new RrCache(":memory:"), service), null, () => null);
        await vm.OpenAsync();
        Assert.Contains("built without RadioReference's application key", vm.Status);
        Assert.Empty(service.Asked);
    }

    [Fact]
    public void The_key_comes_from_the_environment_before_the_one_built_in()
    {
        string? before = Environment.GetEnvironmentVariable("RADIOREFERENCE_KEY");
        try
        {
            Environment.SetEnvironmentVariable("RADIOREFERENCE_KEY", "  from-environment \n");
            Assert.Equal("from-environment", RrKey.Find());
            Environment.SetEnvironmentVariable("RADIOREFERENCE_KEY", null);
            Assert.Equal(RrKey.BuiltIn, RrKey.Find());
        }
        finally { Environment.SetEnvironmentVariable("RADIOREFERENCE_KEY", before); }
    }

    [Fact]
    public async Task A_remembered_login_signs_in_by_itself_next_time()
    {
        var logins = new MemoryLogins();
        var place = new Settings.RrPlace();
        var service = new Service { Answer = Answer };
        var vm = new RadioReferenceViewModel(Store(), new RrClient(new RrCache(":memory:"), service), place, () => "the-key", logins: logins);
        await vm.OpenAsync();
        Assert.True(vm.Remember);   // ticked unless the user unticks it
        vm.Username = "listener1";
        vm.Password = "right";
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.True(vm.SignedIn);
        Assert.Equal("listener1", place.Username);
        Assert.Equal("right", logins.Read("listener1"));

        // The next run: the window opens and signs in with what was kept.
        var client = new RrClient(new RrCache(":memory:"), service);
        var again = new RadioReferenceViewModel(Store(), client, place, () => "the-key", logins: logins);
        await again.OpenAsync();
        Assert.True(again.SignedIn);
        Assert.Equal(new RrAuth("listener1", "right", "the-key"), client.Auth);

        // Signing out forgets the password.
        again.SignOutCommand.Execute(null);
        Assert.Null(logins.Read("listener1"));
    }

    [Fact]
    public async Task Unticked_nothing_is_kept()
    {
        var logins = new MemoryLogins();
        logins.Keep("listener1", "old");
        var place = new Settings.RrPlace { Username = "listener1" };
        var vm = new RadioReferenceViewModel(Store(), new RrClient(new RrCache(":memory:"), new Service { Answer = Answer }), place,
            () => "the-key", logins: logins);
        await vm.OpenAsync();
        vm.SignOutCommand.Execute(null);
        vm.Remember = false;
        vm.Password = "right";
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.True(vm.SignedIn);
        Assert.Null(place.Username);
        Assert.Null(logins.Read("listener1"));
    }

    [Fact]
    public void The_login_file_is_readable_only_by_its_owner_and_only_for_its_user()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"ss-rr-login-{Guid.NewGuid():N}");
        try
        {
            var file = new FileLogins(folder);
            Assert.Null(file.Read("listener1"));
            Assert.True(file.Keep("listener1", "pass word"));
            Assert.Equal("pass word", file.Read("listener1"));
            Assert.Null(file.Read("someone-else"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, "radioreference.login")));
            file.Forget("someone-else");
            Assert.Equal("pass word", file.Read("listener1"));
            file.Forget("listener1");
            Assert.False(File.Exists(Path.Combine(folder, "radioreference.login")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task A_zip_code_shows_its_county_as_a_tree_with_distances()
    {
        var (vm, _, _, place) = await Browser();
        Assert.True(vm.SignedIn);
        Assert.Equal("Signed in as listener1 · Premium until 1 Jan 2030", vm.AccountText);
        Assert.Equal(["Maine", "New Hampshire"], vm.States.Select(s => s.Name));

        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        Assert.Equal(("New Hampshire", "Hillsborough"), (vm.SelectedState?.Name, vm.SelectedCounty?.Name));
        Assert.Equal("Hillsborough, NH", vm.LoadGroup);
        Assert.Equal((1, 33, 1766, "03101"), (place.Country, place.State, place.County, place.Zip));

        Assert.Equal(["Hillsborough", "Agencies in Hillsborough", "New Hampshire statewide agencies", "United States nationwide agencies", "Trunked systems",
            "Other systems in New Hampshire"], vm.Roots.Select(r => r.Title));
        var police = vm.Roots[0].Children[0];
        Assert.Equal(["Manchester", "Nashua"], police.Children.Select(c => c.Title));
        Assert.Equal(["0 mi", "16 mi"], police.Children.Select(c => c.Detail));        // from the ZIP code's own position
        Assert.Equal("", vm.Roots[0].Children[1].Children[0].Detail);                  // a list with no position
        Assert.False(vm.Roots[4].Children[0].CanCheck);                                // a system other than P25 cannot be ticked
        Assert.False(vm.CanLoad);
    }

    [Fact]
    public async Task Ticks_run_down_to_the_lists_and_up_to_the_headings()
    {
        var (vm, _, _, _) = await Browser();
        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        var top = vm.Roots[0];
        var police = top.Children[0];

        police.Children[0].IsChecked = true;
        Assert.Null(police.IsChecked);                    // half ticked
        Assert.Null(top.IsChecked);
        Assert.Equal("1 list ticked", vm.TickedText);

        police.IsChecked = true;
        Assert.All(police.Children, c => Assert.True(c.IsChecked));
        Assert.Equal("2 lists ticked", vm.TickedText);

        top.IsChecked = true;
        Assert.Equal("3 lists ticked", vm.TickedText);
        top.IsChecked = false;
        Assert.Equal("Nothing ticked", vm.TickedText);
        Assert.False(vm.CanLoad);
    }

    [Fact]
    public async Task Ticked_lists_are_loaded_into_the_group_and_saved()
    {
        var (vm, _, store, _) = await Browser();
        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        int told = 0;
        vm.Loaded += () => told++;

        vm.Roots[0].Children[0].IsChecked = true;                 // Police: Manchester and Nashua
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(1, told);
        Assert.Equal(["Manch PD 1", "Manch PD 2", "Nashua PD"], store.Channels.Select(c => c.Name));
        Assert.All(store.Channels, c => Assert.Equal(["Hillsborough, NH"], c.Groups));
        Assert.Equal(["Manchester", "Manchester", "Nashua"], store.Channels.Select(c => c.Agency));
        Assert.StartsWith("2 lists into \"Hillsborough, NH\": 3 channels added", vm.Status);
        Assert.Equal(3, MemoryStore.Load(StorePath(store)).Channels.Count);            // and it is on disk

        // Where each is used: the town, since this county lists its users by town; and each location is a group.
        Assert.Equal(["Manchester, NH", "Manchester, NH", "Nashua, NH"], store.Channels.Select(c => c.Location));
        Assert.Equal(["Location: Manchester, NH", "Location: Nashua, NH"], store.PlaceGroupNames());
        Assert.Equal(2, store.Channels.Count(c => MemoryStore.InGroup(c, "Location: manchester NH")));
    }

    [Fact]
    public async Task Lists_not_tied_to_a_town_are_located_by_county_state_or_country_and_telemetry_is_not_ticked()
    {
        var (vm, _, store, _) = await Browser();
        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        vm.Roots[0].Children[1].IsChecked = true;                 // Airports: not a category of towns
        vm.Roots[2].Children[0].IsChecked = true;                 // a statewide agency
        await vm.LoadCommand.ExecuteAsync(null);

        var tower = store.Channels.Single(c => c.Name == "MHT Tower");
        Assert.Equal("Hillsborough County, NH", tower.Location);
        Assert.Equal("New Hampshire statewide", store.Channels.Single(c => c.Name == "NHSP A").Location);
        var telemetry = store.Channels.Single(c => c.Name == "MHT Telemetry");
        Assert.False(telemetry.Scan);
        Assert.True(tower.Scan);
        Assert.Contains("1 telemetry (data) frequency is stored but not ticked for scanning", vm.Status);
    }

    [Fact]
    public void Categories_of_towns_are_told_from_the_rest()
    {
        foreach (string towns in new[] { "Municipalities", "Cities and Towns", "Townships", "Villages", "City of Nashua", "Municipal" })
            Assert.True(RrPlaces.IsTownCategory(towns), towns);
        foreach (string other in new[] { "Federal", "Aeronautical", "Hillsborough County", "Education", "Businesses", "Capacity planning" })
            Assert.False(RrPlaces.IsTownCategory(other), other);
        Assert.Equal("Orleans Parish, LA", RrPlaces.County("Orleans", "Parish", "LA"));
        Assert.Equal("Hillsborough County, NH", RrPlaces.County("Hillsborough County", "County", "NH"));
    }

    [Fact]
    public async Task Channels_stored_without_a_location_get_one_from_what_was_fetched()
    {
        var service = new Service { Answer = Answer };
        using var cache = new RrCache(":memory:");
        var client = new RrClient(cache, service) { Auth = new RrAuth("listener1", "right", "k") };
        // What a session of browsing leaves in the cache.
        await client.GetCountryAsync(1);
        await client.GetStateAsync(33);
        await client.GetCountyAsync(1766);
        await client.GetAgencyAsync(700);
        var store = Store();
        foreach (int list in new[] { 101, 102, 103, 201 })
            RrImport.Add(store, (await client.GetFreqsAsync(list)).Select(f => RrImport.ToChannel(f, "x")), "Old load");
        // And one from a CSV file, with the row's own key, and one typed in.
        store.Channels.Single(c => c.Name == "Nashua PD").ImportKey = "464.925|Nashua|Dispatch|Nashua PD";
        store.Channels.Add(new MemoryChannel { Name = "Mine", FrequencyHz = 146.52e6 });
        Assert.All(store.Channels, c => Assert.Null(c.Location));

        Assert.Equal(6, RrPlaces.FillFromCache(store, cache));
        string? Where(string name) => store.Channels.Single(c => c.Name == name).Location;
        Assert.Equal("Manchester, NH", Where("Manch PD 1"));
        Assert.Equal("Nashua, NH", Where("Nashua PD"));
        Assert.Equal("Hillsborough County, NH", Where("MHT Tower"));
        Assert.Equal("New Hampshire statewide", Where("NHSP A"));
        Assert.Null(Where("Mine"));

        store.Channels[0].Location = "Somewhere else";            // one the user has changed is left alone
        Assert.Equal(0, RrPlaces.FillFromCache(store, cache));
        Assert.Equal("Somewhere else", store.Channels[0].Location);
    }

    private static string StorePath(MemoryStore store) =>
        (string)typeof(MemoryStore).GetField("_path", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;

    [Fact]
    public async Task An_agency_is_fetched_when_opened_and_a_ticked_one_is_loaded_whole()
    {
        var (vm, service, store, _) = await Browser();
        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        var police = vm.Roots[2].Children[0];
        Assert.Equal(("State Police", RrNodeKind.Agency), (police.Title, police.Kind));
        Assert.Equal(0, service.Count("getAgencyInfo"));          // nothing fetched until it is wanted

        police.IsChecked = true;                                  // ticked without being opened
        Assert.Equal("1 whole agency ticked", vm.TickedText);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(1, service.Count("getAgencyInfo"));
        Assert.Equal(["NHSP A", "NHSP B"], store.Channels.Select(c => c.Name));
        Assert.Equal("State Police - Troop A", store.Channels[0].Agency);
        Assert.Equal((Mode.Nfm, BandPlan.NfmWide), (store.Channels[0].Mode, store.Channels[0].BandwidthHz));
        Assert.Equal(["Troop A", "Troop B"], police.Children[0].Children.Select(c => c.Title));
    }

    [Fact]
    public async Task Selecting_a_list_shows_its_frequencies()
    {
        var (vm, _, store, _) = await Browser();
        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        vm.SelectedNode = vm.Roots[0].Children[0].Children[0];
        await Task.Yield();
        Assert.Equal("Manchester: 2 frequencies · location Manchester, NH", vm.PreviewTitle);
        Assert.Equal(new RrFreqRow("154.8150", "Manch PD 1", "Dispatch", "Law Dispatch", "P25", "117 NAC"), vm.Preview[0]);
        Assert.Empty(store.Channels);                             // looking is not loading
    }

    [Fact]
    public async Task An_unknown_zip_code_and_a_bad_one_are_said()
    {
        var (vm, service, _, _) = await Browser();
        vm.Zip = "1234";
        await vm.FindZipCommand.ExecuteAsync(null);
        Assert.Equal("A ZIP code is five digits", vm.Status);
        Assert.Equal(0, service.Count("getZipcodeInfo"));
        vm.Zip = "99999";
        await vm.FindZipCommand.ExecuteAsync(null);
        Assert.Equal("RadioReference does not know the ZIP code 99999", vm.Status);
    }

    // ---- trunked systems --------------------------------------------------------------------

    [Fact]
    public async Task A_trunked_system_is_read_with_its_sites_talkgroups_and_names()
    {
        var (client, service) = Signed();
        var trs = await client.GetTrsAsync(6000);
        Assert.Equal(("Souhegan Valley", "Project 25 Phase I", "Standard", "Analog and Digital", "Milford"),
            (trs.Name, trs.TypeName, trs.FlavorName, trs.VoiceName, trs.City));
        Assert.Equal(new RrSysid("3A1", "BEE00"), trs.Sysids.Single());                 // sent once and pointed to
        Assert.Contains("<ns1:getTrsDetails><sid xsi:type=\"xsd:int\">6000</sid><authInfo", service.Asked.First(a => a.Call == "getTrsDetails").Body);
        Assert.Empty((await client.GetTrsAsync(7000)).Sysids);                          // none listed
        Assert.Equal(1, service.Count("getTrsFlavor"));                                 // a name is asked for once

        var sites = await client.GetTrsSitesAsync(6000);
        Assert.Equal(["Milford", "Amherst"], sites.Select(s => s.Descr));
        Assert.Equal((61, 1, 1, "293", "Hillsborough", "C4FM", 0), (sites[0].Id, sites[0].Number, sites[0].Rfss, sites[0].Nac, sites[0].County,
            sites[0].Modulation, sites[0].TdmaCc));
        Assert.Equal(new RrSiteFreq(1, 851.0125, "d"), sites[0].Freqs[1]);
        Assert.Equal([853.1125, 853.6125], sites[1].Freqs.Select(f => f.MHz));          // a list pointed to
        Assert.Equal(1, sites[1].TdmaCc);
        Assert.Equal(("", 1766), (sites[1].County, sites[1].CountyId));                 // the county by its number only

        var talkgroups = await client.GetTalkgroupsAsync(6000);
        Assert.Equal(new RrTalkgroup(90101, 101, "Police dispatch", "Milford PD", "D", 0, [new RrTag(7, "Law Dispatch")], 1) with { Tags = talkgroups[0].Tags },
            talkgroups[0]);
        Assert.Equal("Law Dispatch", talkgroups[0].Tags.Single().Descr);
        string asked = service.Asked.First(a => a.Call == "getTrsTalkgroups").Body;
        Assert.Contains("<sid xsi:type=\"xsd:int\">6000</sid><tgCid xsi:type=\"xsd:int\">0</tgCid><tgTag xsi:type=\"xsd:int\">0</tgTag><tgDec xsi:type=\"xsd:int\">0</tgDec>", asked);
        Assert.Equal([new RrTalkgroupCat(1, "Police"), new RrTalkgroupCat(2, "Fire")], await client.GetTalkgroupCatsAsync(6000));
    }

    [Fact]
    public void Ids_modes_and_uses_are_read_whatever_way_they_are_written()
    {
        Assert.Equal(0x3A1, RrTrunkImport.Hex("3A1", 12));
        Assert.Equal(0x3A1, RrTrunkImport.Hex(" 0x3a1 ", 12));
        Assert.Equal(0xBEE00, RrTrunkImport.Hex("BEE00", 20));
        Assert.Equal(0x293, RrTrunkImport.Hex("$293", 12));
        Assert.Null(RrTrunkImport.Hex("", 12));
        Assert.Null(RrTrunkImport.Hex("0", 12));
        Assert.Null(RrTrunkImport.Hex("none", 12));
        Assert.Null(RrTrunkImport.Hex("BEE00", 12));                                    // too long for a system id

        Assert.Equal(TrunkFreqUse.Control, RrTrunkImport.Use("d"));
        Assert.Equal(TrunkFreqUse.Control, RrTrunkImport.Use(" D "));
        Assert.Equal(TrunkFreqUse.Alternate, RrTrunkImport.Use("a"));
        Assert.Equal(TrunkFreqUse.Voice, RrTrunkImport.Use(""));
        Assert.Equal(TrunkFreqUse.Voice, RrTrunkImport.Use(null));

        Assert.Equal(("D", 0), RrTrunkImport.Mode("D", 0));
        Assert.Equal(("T", 0), RrTrunkImport.Mode("t", 0));
        Assert.Equal(("DE", 2), RrTrunkImport.Mode("DE", 0));
        Assert.Equal(("TE", 2), RrTrunkImport.Mode("TE", 2));
        Assert.Equal(("De", 1), RrTrunkImport.Mode("De", 0));
        Assert.Equal(("De", 1), RrTrunkImport.Mode("D", 1));                             // the number says so though the letters do not
        Assert.Equal(("DE", 2), RrTrunkImport.Mode("", 2));
        Assert.True(RrTrunkImport.IsP25("Project 25 Phase II"));
        Assert.False(RrTrunkImport.IsP25("Motorola Type II Smartnet"));
        Assert.False(RrTrunkImport.IsP25(""));
    }

    private static async Task Until(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++) await Task.Delay(5);
        Assert.True(done());
    }

    private static TrunkStore Trunks() => new(Path.Combine(Path.GetTempPath(), $"ss-trunk-{Guid.NewGuid():N}.db"));

    [Fact]
    public async Task P25_systems_can_be_ticked_others_are_dimmed_and_the_states_other_systems_open_when_wanted()
    {
        var (vm, service, _, _) = await Browser(Trunks());
        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        var systems = vm.Roots[4];
        Assert.Equal(["Nashua Public Safety", "Souhegan Valley"], systems.Children.Select(c => c.Title));
        var (smartnet, p25) = (systems.Children[0], systems.Children[1]);
        Assert.Equal((false, true, "Nashua · not a P25 system"), (smartnet.CanCheck, smartnet.Dim, smartnet.Detail));
        Assert.Equal((true, false, "Milford · Project 25 Phase I"), (p25.CanCheck, p25.Dim, p25.Detail));
        Assert.Equal("Hillsborough County, NH", p25.Location);

        // The state's other systems: not the county's again, and their kinds not asked for until the heading is opened.
        var others = vm.Roots[5];
        Assert.Equal("2", others.Detail);
        Assert.Equal(0, service.Asked.Count(a => a.Call == "getTrsType" && (a.Body.Contains(">12<") || a.Body.Contains(">3<"))));
        others.IsExpanded = true;
        await Until(() => others.Children.Count == 2 && others.Children[0].Kind == RrNodeKind.System);
        Assert.Equal(["Coastal EDACS", "NH State Interop"], others.Children.Select(c => c.Title));
        Assert.Equal((false, true), (others.Children[0].CanCheck, others.Children[1].CanCheck));
        Assert.Equal("New Hampshire statewide", others.Children[1].Location);

        p25.IsChecked = true;
        others.Children[1].IsChecked = true;
        Assert.Equal("2 systems ticked", vm.TickedText);
        vm.Roots[0].Children[0].Children[0].IsChecked = true;
        Assert.Equal("1 list and 2 systems ticked", vm.TickedText);
        Assert.True(vm.CanLoad);
    }

    [Fact]
    public async Task Selecting_a_p25_system_shows_its_sites_then_its_talkgroups()
    {
        var (vm, _, _, _) = await Browser(Trunks());
        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        vm.SelectedNode = vm.Roots[4].Children[1];
        await Until(() => vm.Preview.Count == 8);
        Assert.True(vm.PreviewIsSystem);
        Assert.Equal("Souhegan Valley: 2 sites, 4 talkgroups · Project 25 Phase I", vm.PreviewTitle);
        Assert.Equal(new RrTrunkRow("Site", "Name", "Frequencies (c control, a alternate)", "County", "NAC", "Modulation", Heading: true), vm.Preview[0]);
        Assert.Equal(new RrTrunkRow("1", "Milford", "851.0125c 851.2625a 852.5375", "Hillsborough", "293", "C4FM"), vm.Preview[1]);
        Assert.Equal(new RrTrunkRow("2", "Amherst", "853.1125c 853.6125", "Hillsborough", "2A1", "C4FM · TDMA control"), vm.Preview[2]);
        Assert.True(((RrTrunkRow)vm.Preview[3]).Heading);
        Assert.Equal(new RrTrunkRow("102", "Milford FD", "Fire dispatch", "Fire Dispatch", "T", "Fire"), vm.Preview[5]);
        Assert.Equal("DE", ((RrTrunkRow)vm.Preview[6]).Mode);

        vm.SelectedNode = vm.Roots[4].Children[0];
        Assert.Empty(vm.Preview);
        Assert.Equal("Nashua Public Safety: not a P25 system, so the scanner cannot follow it", vm.PreviewTitle);

        vm.SelectedNode = vm.Roots[0].Children[0].Children[0];
        await Until(() => vm.Preview.Count == 2);
        Assert.False(vm.PreviewIsSystem);
        Assert.IsType<RrFreqRow>(vm.Preview[0]);
    }

    [Fact]
    public void A_sites_county_is_its_name_or_the_name_of_its_number()
    {
        var counties = new Dictionary<int, string> { [1766] = "Hillsborough" };
        RrTrsSite Site(string county, int ctid) => new(1, 1, "Milford", 1, "293", county, "C4FM", 0, null, null, [], ctid);
        Assert.Equal("Merrimack", RrTrunkImport.County(Site(" Merrimack ", 1766), counties));     // the name given wins
        Assert.Equal("Hillsborough", RrTrunkImport.County(Site("", 1766), counties));
        Assert.Equal("", RrTrunkImport.County(Site("", 1767), counties));                        // another state's: not known
        Assert.Equal("", RrTrunkImport.County(Site("", 1766), null));
        Assert.Equal("", RrTrunkImport.County(Site("", 0), counties));
    }

    [Fact]
    public async Task A_ticked_system_is_loaded_whole_with_no_group_name()
    {
        var trunks = Trunks();
        var (vm, _, store, _) = await Browser(trunks);
        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        vm.LoadGroup = "";
        vm.Roots[4].Children[1].IsChecked = true;
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("Souhegan Valley: 2 sites and 4 talkgroups stored", vm.Status);
        Assert.Empty(store.Channels);
        var system = trunks.Systems.Single();
        Assert.Equal((6000, "Souhegan Valley", "Project 25 Phase I", "Standard", "Analog and Digital", 0x3A1, 0xBEE00, "Hillsborough County, NH"),
            (system.RrSid, system.Name, system.Type, system.Flavor, system.Voice, system.SysId, system.Wacn, system.Location));
        var sites = trunks.Sites(system.Id);
        Assert.Equal([(1, 0x293, false), (2, 0x2A1, true)], sites.Select(s => (s.Number, s.Nac!.Value, s.TdmaControl)));
        // The second site gives its county by number only: named from the state's county list, with no call to the service for it.
        Assert.Equal(["Hillsborough", "Hillsborough"], sites.Select(s => s.County));
        Assert.Equal([new TrunkSiteFreq(851_012_500, 1, TrunkFreqUse.Control), new TrunkSiteFreq(851_262_500, 2, TrunkFreqUse.Alternate),
            new TrunkSiteFreq(852_537_500, 3, TrunkFreqUse.Voice)], sites[0].Freqs);
        var talkgroups = trunks.Talkgroups(system.Id);
        Assert.Equal([(101, "D", true, "Police"), (102, "T", true, "Fire"), (103, "DE", false, "Police"), (104, "De", true, "Police")],
            talkgroups.Select(t => (t.Dec, t.Mode, t.Listen, t.Category)));
        Assert.Equal(("Milford PD", "Police dispatch", "Law Dispatch"), (talkgroups[0].Alpha, talkgroups[0].Descr, talkgroups[0].Tag));
        Assert.Equal("2 sites, 4 talkgroups", vm.Roots[4].Children[1].Detail);
        // And it is on disk.
        Assert.Equal(4, TrunkStore.Load(TrunkPath(trunks)).Talkgroups(system.Id).Count);
    }

    [Fact]
    public async Task Loading_a_system_again_updates_it_and_keeps_the_ticks()
    {
        var trunks = Trunks();
        var (vm, service, _, _) = await Browser(trunks);
        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        vm.Roots[4].Children[1].IsChecked = true;
        await vm.LoadCommand.ExecuteAsync(null);
        long id = trunks.Systems.Single().Id;
        trunks.SetListen(id, 101, false);
        trunks.SetListen(id, 103, true);

        // RadioReference renames one, drops one and adds one.
        _talkgroups = [.. Talkgroups().Take(3), Talkgroup(105, "Hwy Dept", "Highway", "D", 0, "Public Works", 2)];
        _talkgroups[0] = Talkgroup(101, "Milford PD 1", "Police dispatch", "D", 0, "Law Dispatch", 1);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("Souhegan Valley: 2 sites and 4 talkgroups updated (your talkgroup ticks are kept)", vm.Status);
        Assert.Equal(2, service.Count("getTrsTalkgroups"));                            // asked afresh, not from what was kept
        Assert.Equal(id, trunks.Systems.Single().Id);
        var talkgroups = trunks.Talkgroups(id);
        Assert.Equal([(101, "Milford PD 1", false), (102, "Milford FD", true), (103, "PD Tac", true), (105, "Hwy Dept", true)],
            talkgroups.Select(t => (t.Dec, t.Alpha, t.Listen)));
    }

    [Fact]
    public async Task Loading_lists_and_a_system_together_says_both()
    {
        var trunks = Trunks();
        var (vm, _, store, _) = await Browser(trunks);
        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        vm.Roots[0].Children[0].Children[0].IsChecked = true;
        vm.Roots[4].Children[1].IsChecked = true;
        vm.LoadGroup = "";
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("Type a group name for the loaded channels", vm.Status);   // the list needs one
        Assert.Empty(trunks.Systems);

        vm.LoadGroup = "Hillsborough, NH";
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.StartsWith("1 list into \"Hillsborough, NH\": 2 channels added", vm.Status);
        Assert.EndsWith(". Souhegan Valley: 2 sites and 4 talkgroups stored", vm.Status);
        Assert.Equal(2, store.Channels.Count);
        Assert.Single(trunks.Systems);
    }

    private static string TrunkPath(TrunkStore store) =>
        (string)typeof(TrunkStore).GetField("_path", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
}
