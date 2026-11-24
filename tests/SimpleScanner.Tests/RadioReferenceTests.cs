using System.Net;
using System.Text;
using SimpleScanner.Dsp;
using SimpleScanner.RadioReference;
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

    /// <summary>A small New Hampshire: one county with two categories, a county agency, a state agency and a trunked system.</summary>
    private static string Answer(string call, string body)
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
            "getUserData" => Envelope(call, Item("return", ("username", "kc2rgw"), ("subExpireDate", "2027-03-01"))),
            "getCountryInfo" => Envelope(call, Item("return", ("coid", "1"), ("countryName", "United States"), ("countryCode", "US"),
                ("agencyList", List("agencyList", Item("item", ("aid", "900"), ("aName", "Federal Aviation Administration"), ("aType", "1")))),
                ("stateList", List("stateList",
                    Item("item", ("stid", "33"), ("stateName", "New Hampshire"), ("stateCode", "NH")),
                    Item("item", ("stid", "23"), ("stateName", "Maine"), ("stateCode", "ME")))))),
            "getStateInfo" => Envelope(call, Item("return", ("stid", "33"), ("stateName", "New Hampshire"),
                ("trsList", List("trsList")),
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
                ("trsList", List("trsList", Item("item", ("sid", "5000"), ("sName", "Nashua Public Safety"), ("sCity", "Nashua")))),
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
            _ => Fault("No such call in the test: " + call),
        };
    }

    private static (RrClient Client, Service Service) Signed(string password = "right")
    {
        var service = new Service { Answer = Answer };
        var client = new RrClient(new RrCache(":memory:"), service) { Auth = new RrAuth("kc2rgw", password, "the-key") };
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
        Assert.Equal(new RrSystem(5000, "Nashua Public Safety", "Nashua"), county.Systems.Single());
    }

    [Fact]
    public async Task An_answer_is_kept_and_not_asked_for_twice_unless_asked_afresh()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ss-rr-{Guid.NewGuid():N}.db");
        try
        {
            var service = new Service { Answer = Answer };
            using (var cache = new RrCache(path))
            using (var client = new RrClient(cache, service) { Auth = new RrAuth("kc2rgw", "right", "k") })
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
        client.Auth = new RrAuth("kc2rgw", "right", "k");
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

    internal static async Task<(RadioReferenceViewModel Vm, Service Service, MemoryStore Store, Settings.RrPlace Place)> Browser()
    {
        var service = new Service { Answer = Answer };
        var store = Store();
        var place = new Settings.RrPlace();
        var vm = new RadioReferenceViewModel(store, new RrClient(new RrCache(":memory:"), service), place, () => "the-key");
        await vm.OpenAsync();
        vm.Username = "kc2rgw";
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
        vm.Username = "kc2rgw";
        vm.Password = "wrong";
        await vm.SignInCommand.ExecuteAsync(null);
        Assert.False(vm.SignedIn);
        Assert.Equal("RadioReference says: Invalid username or password", vm.Status);
    }

    [Fact]
    public async Task A_copy_built_without_a_key_says_so_and_asks_nothing()
    {
        var service = new Service { Answer = Answer };
        var vm = new RadioReferenceViewModel(Store(), new RrClient(new RrCache(":memory:"), service), null, () => null);
        await vm.OpenAsync();
        Assert.Contains("without a RadioReference application key", vm.Status);
        Assert.Empty(service.Asked);
    }

    [Fact]
    public async Task A_zip_code_shows_its_county_as_a_tree_with_distances()
    {
        var (vm, _, _, place) = await Browser();
        Assert.True(vm.SignedIn);
        Assert.Equal("Signed in as kc2rgw · Premium until 1 Mar 2027", vm.AccountText);
        Assert.Equal(["Maine", "New Hampshire"], vm.States.Select(s => s.Name));

        vm.Zip = "03101";
        await vm.FindZipCommand.ExecuteAsync(null);
        Assert.Equal(("New Hampshire", "Hillsborough"), (vm.SelectedState?.Name, vm.SelectedCounty?.Name));
        Assert.Equal("Hillsborough, NH", vm.LoadGroup);
        Assert.Equal((1, 33, 1766, "03101"), (place.Country, place.State, place.County, place.Zip));

        Assert.Equal(["Hillsborough", "Agencies in Hillsborough", "New Hampshire statewide agencies", "United States nationwide agencies", "Trunked systems"],
            vm.Roots.Select(r => r.Title));
        var police = vm.Roots[0].Children[0];
        Assert.Equal(["Manchester", "Nashua"], police.Children.Select(c => c.Title));
        Assert.Equal(["0 mi", "16 mi"], police.Children.Select(c => c.Detail));        // from the ZIP code's own position
        Assert.Equal("", vm.Roots[0].Children[1].Children[0].Detail);                  // a list with no position
        Assert.False(vm.Roots[4].Children[0].CanCheck);                                // a trunked system cannot be ticked
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
        var client = new RrClient(cache, service) { Auth = new RrAuth("kc2rgw", "right", "k") };
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
}
