using SimpleScanner.Dsp;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Tests;

/// <summary>The RadioReference CSV import, channels in several groups, and what the display says about a channel.</summary>
public class ImportTests
{
    // Rows in the form of a RadioReference county download (the header and quoting are theirs).
    private const string Csv = """
        "Frequency Output","Frequency Input","FCC Callsign",Agency/Category,Description,"Alpha Tag","PL Output Tone","PL Input Tone",Mode,"Class Station Code",Tag
        33.740000,0.00000,KDA385,Greenville,"Fire Operations",GFDDISP,"136.5 PL",,FM,BM,"Fire Dispatch"
        33.740000,0.00000,KDA385,Mason,"Fire/EMS Operations","Mason FD/EMS","136.5 PL",,FM,BM,"Fire Dispatch"
        122.850000,0.00000,KOI9,"Nashua Airport, ""Boire Field"" (ASH)","GFW Aeroservices","GFW Aero",CSQ,,AM,BM,Aircraft
        464.925000,469.92500,WPSI324,"Businesses Private Education","St. Paul's School - Security","StPaul Scrty","CC 7|TG 1100|SL 1",,DMRE,RM,Security
        154.815000,0.00000,WQAB123,Hooksett,"Police Dispatch","Hooksett PD","117 NAC",,P25,RM,"Law Dispatch"
        851.050000,0.00000," ",Nashua,"Site 002 Nashua Simulcast",,,,"Project 25",,TRS
        836.500000,0.00000,X,Somebody,"In the cellular band",Cell,,,FMN,BM,Business
        """;

    [Fact]
    public void A_radioreference_download_is_read_field_by_field()
    {
        var list = RadioReferenceCsv.Parse(Csv);
        Assert.Equal(7, list.Count);

        var fire = list[0];
        Assert.Equal("GFDDISP", fire.Name);
        Assert.Equal(33.74e6, fire.FrequencyHz);
        Assert.Equal("Greenville", fire.Agency);
        Assert.Equal("Fire Operations", fire.Description);
        Assert.Equal("Fire Dispatch", fire.Tag);
        Assert.Equal("136.5 PL", fire.Tone);
        Assert.Equal("KDA385", fire.Callsign);
        Assert.Equal("BM", fire.StationClass);
        Assert.Null(fire.InputHz);
        Assert.Equal((Mode.Nfm, BandPlan.NfmWide), (fire.Mode, fire.BandwidthHz));       // "FM" is the 25 kHz channel

        Assert.Equal("Nashua Airport, \"Boire Field\" (ASH)", list[2].Agency);           // comma and quotes inside a cell
        Assert.Equal((Mode.Am, BandPlan.AmAir), (list[2].Mode, list[2].BandwidthHz));

        var dmr = list[3];
        Assert.Equal(469.925e6, dmr.InputHz);
        Assert.Equal("CC 7 TG 1100 SL 1", dmr.Tone);
        Assert.Equal("DMRE", dmr.Format);
        Assert.Equal((Mode.Nfm, BandPlan.NfmNarrow), (dmr.Mode, dmr.BandwidthHz));       // digital voice: the narrow FM channel

        // A trunked site's row has no alpha tag or callsign: it is named for the site.
        Assert.Equal("Site 002 Nashua Simulcast", list[5].Name);
        Assert.Null(list[5].Callsign);
        Assert.True(RadioReferenceCsv.IsTrunked(list[5]));
    }

    [Fact]
    public void A_file_that_is_not_a_frequency_list_is_refused()
    {
        Assert.Throws<FormatException>(() => RadioReferenceCsv.Parse("Decimal,Hex,\"Alpha Tag\",Mode,Description,Tag\n1,001,A,D,B,C\n"));
        Assert.Throws<FormatException>(() => RadioReferenceCsv.Parse(""));
    }

    [Fact]
    public void Importing_twice_adds_nothing_and_keeps_edits()
    {
        var store = new MemoryStore(Path.Combine(Path.GetTempPath(), $"ss-import-{Guid.NewGuid():N}.json"));
        var first = RadioReferenceCsv.Import(store, RadioReferenceCsv.Parse(Csv), "Hillsborough");
        Assert.Equal(new ImportResult(Added: 6, AlreadyStored: 0, Skipped: 1, Trunked: 1), first);     // the cellular one is left out
        Assert.DoesNotContain(store.Channels, c => c.Name == "Cell");
        Assert.All(store.Channels, c => Assert.Equal(["Hillsborough"], c.Groups));
        Assert.False(store.Channels.Single(RadioReferenceCsv.IsTrunked).Scan);
        Assert.True(store.Channels[0].Scan);

        store.Channels[0].Name = "Greenville Fire";
        var again = RadioReferenceCsv.Import(store, RadioReferenceCsv.Parse(Csv), "Second look");
        Assert.Equal(new ImportResult(0, 6, 1, 0), again);
        Assert.Equal(6, store.Channels.Count);
        Assert.Equal("Greenville Fire", store.Channels[0].Name);
        Assert.Equal(["Hillsborough", "Second look"], store.Channels[0].Groups);
    }

    [Fact]
    public void Channels_keep_several_groups_and_their_details_and_old_files_still_load()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ss-groups-{Guid.NewGuid():N}.json");
        try
        {
            // As written before a channel could be in several groups.
            File.WriteAllText(path, """[{"Name":"Tower","FrequencyHz":121300000,"Group":"Manchester (KMHT)","Scan":true}]""");
            var store = MemoryStore.Load(path);
            Assert.Equal(["Manchester (KMHT)"], store.Channels[0].Groups);

            RadioReferenceCsv.Import(store, RadioReferenceCsv.Parse(Csv), "Hillsborough");
            store.Channels[1].Groups.Add("Fire");
            store.Save();

            var back = MemoryStore.Load(path);
            Assert.Equal(["Manchester (KMHT)"], back.Channels[0].Groups);
            Assert.Equal(["Hillsborough", "Fire"], back.Channels[1].Groups);
            Assert.Equal("Fire Dispatch", back.Channels[1].Tag);
            Assert.Equal(["Fire", "Hillsborough", "Manchester (KMHT)"], back.GroupNames());
            Assert.DoesNotContain("\"Group\"", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_display_says_what_is_stored_about_a_frequency()
    {
        var list = RadioReferenceCsv.Parse(Csv);
        var one = ChannelLabel.For([list[3]]);
        Assert.Equal("StPaul Scrty", one.Title);
        Assert.Equal("Businesses Private Education: St. Paul's School - Security", one.Detail);
        Assert.Equal("Security  ·  DMRE  ·  CC 7 TG 1100 SL 1  ·  Repeater  ·  input 469.9250  ·  WPSI324", one.Info);

        // Two towns share 33.74 MHz: both are named, and what they have in common is said once.
        var shared = ChannelLabel.For([list[0], list[1]]);
        Assert.Equal("GFDDISP / Mason FD/EMS", shared.Title);
        Assert.Equal("Greenville: Fire Operations  ·  Mason: Fire/EMS Operations", shared.Detail);
        Assert.Equal("Fire Dispatch  ·  136.5 PL  ·  Base and mobiles  ·  KDA385", shared.Info);

        // One agency with two uses of a frequency is named once.
        var twice = ChannelLabel.For([list[3], new MemoryChannel { Name = "Ops", Agency = list[3].Agency, Description = "Operations" }]);
        Assert.Equal("Businesses Private Education: St. Paul's School - Security / Operations", twice.Detail);

        Assert.Null(ChannelLabel.For([]).Title);
    }

    [Fact]
    public void The_editor_sorts_by_any_column_and_renames_and_deletes_groups()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ss-sort-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MemoryStore(path);
            RadioReferenceCsv.Import(store, RadioReferenceCsv.Parse(Csv), "County");
            store.Channels.Add(new MemoryChannel { Name = "Bare", FrequencyHz = 146.52e6, Groups = ["Mine"] });
            var vm = new ChannelsViewModel(store);
            string[] Names() => [.. vm.Rows.Select(r => r.Channel.Name)];

            // Frequency order to begin with, marked in the heading; the heading again turns it round.
            Assert.Equal("MHz ▲", vm.Heads[1].Text);
            Assert.Equal(33.74e6, vm.Rows[0].Channel.FrequencyHz);
            vm.SortByCommand.Execute("MHz");
            Assert.Equal("MHz ▼", vm.Heads[1].Text);
            Assert.Equal("Site 002 Nashua Simulcast", Names()[0]);

            // Alphabetical, whatever the case; alike in the column stay in frequency order.
            vm.SortByCommand.Execute("Name");
            Assert.Equal("MHz", vm.Heads[1].Text);
            Assert.Equal("Name ▲", vm.Heads[2].Text);
            Assert.Equal(["Bare", "GFDDISP", "GFW Aero", "Hooksett PD", "Mason FD/EMS", "Site 002 Nashua Simulcast", "StPaul Scrty"], Names());
            vm.SortByCommand.Execute("Tag");
            Assert.Equal(["GFW Aero", "GFDDISP", "Mason FD/EMS", "Hooksett PD", "StPaul Scrty", "Site 002 Nashua Simulcast", "Bare"], Names());
            // The other way round, a channel with nothing in the column is still last.
            vm.SortByCommand.Execute("Tag");
            Assert.Equal("Tag ▼", vm.Heads[5].Text);
            Assert.Equal("Site 002 Nashua Simulcast", Names()[0]);
            Assert.Equal("Bare", Names()[^1]);
            vm.SortByCommand.Execute("Scan");
            Assert.Equal("Site 002 Nashua Simulcast", Names()[^1]);         // the one not ticked

            // Rename a group: every channel in it follows, and a window with it ticked is told.
            (string, string)? renamed = null;
            vm.GroupRenamed += (old, name) => renamed = (old, name);
            Assert.False(vm.IsGroupChosen);
            vm.GroupFilter = "County";
            Assert.True(vm.IsGroupChosen);
            Assert.Equal("County", vm.GroupNewName);
            vm.GroupNewName = "Hillsborough";
            vm.RenameGroupCommand.Execute(null);
            Assert.Equal(("County", "Hillsborough"), renamed);
            Assert.Equal("Hillsborough", vm.GroupFilter);
            Assert.Equal(6, vm.Rows.Count);
            Assert.Equal(["Hillsborough", "Mine"], MemoryStore.Load(path).GroupNames());

            // Renaming to a name already in use joins the two groups.
            vm.GroupFilter = "Mine";
            vm.GroupNewName = "Hillsborough";
            vm.RenameGroupCommand.Execute(null);
            Assert.Equal(["Hillsborough"], store.GroupNames());
            Assert.Equal(7, vm.Rows.Count);
            Assert.All(store.Channels, c => Assert.Equal(["Hillsborough"], c.Groups));

            // Delete the group: the channels stay, in no group.
            vm.DeleteGroupCommand.Execute(null);
            Assert.Empty(store.GroupNames());
            Assert.Equal(7, store.Channels.Count);
            Assert.Equal(ChannelsViewModel.AllGroups, vm.GroupFilter);
            Assert.False(vm.IsGroupChosen);
            Assert.Equal(7, MemoryStore.Load(path).Channels.Count);
            vm.GroupFilter = ChannelsViewModel.NoGroup;
            Assert.Equal(7, vm.Rows.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_editor_filters_edits_and_puts_channels_in_several_groups()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ss-editor-{Guid.NewGuid():N}.json");
        try
        {
            var store = new MemoryStore(path);
            var vm = new ChannelsViewModel(store);
            int edits = 0;
            vm.Edited += () => edits++;

            vm.OfferImport("ctid_1766_1795474067.csv", Csv);
            Assert.NotNull(vm.Pending);
            Assert.Equal("RadioReference county 1766", vm.ImportGroup);
            vm.ImportGroup = "Hillsborough";
            vm.ConfirmImportCommand.Execute(null);
            Assert.Null(vm.Pending);
            Assert.Equal(6, vm.Rows.Count);
            Assert.Equal(1, edits);

            // Filter on words from different fields, then put what is left in a new group.
            vm.Filter = "fire 33.74";
            Assert.Equal(2, vm.Rows.Count);
            vm.SetSelection(vm.Rows.ToList());
            Assert.Null(vm.Selected);                                   // two selected: no single channel to edit
            Assert.True(vm.GroupTicks.Single(t => t.Name == "Hillsborough").Member);
            vm.NewGroup = "Fire";
            vm.AddToNewGroupCommand.Execute(null);
            Assert.All(vm.Rows, r => Assert.Equal(["Hillsborough", "Fire"], r.Channel.Groups));

            // One of them leaves the first group: the tick for it is then half set.
            vm.SetSelection([vm.Rows[0]]);
            vm.GroupTicks.Single(t => t.Name == "Hillsborough").Member = false;
            Assert.Equal(["Fire"], vm.Rows[0].Channel.Groups);
            vm.SetSelection(vm.Rows.ToList());
            Assert.Null(vm.GroupTicks.Single(t => t.Name == "Hillsborough").Member);

            // Edit the one selected channel's fields.
            vm.SetSelection([vm.Rows[0]]);
            var row = vm.Selected!;
            row.Name = "Greenville Fire";
            row.Description = "Dispatch from Milford";
            row.Frequency = "33.76";
            row.Frequency = "not a number";                             // not taken
            row.Receive = "NFM 12.5 kHz";
            row.Tone = "";
            Assert.Equal(33.76e6, row.Channel.FrequencyHz);
            Assert.Equal((Mode.Nfm, BandPlan.NfmNarrow), (row.Channel.Mode, row.Channel.BandwidthHz));
            Assert.Null(row.Channel.Tone);

            vm.Flush();
            Assert.Equal(2, edits);
            var back = MemoryStore.Load(path);
            var saved = back.Channels.Single(c => c.Name == "Greenville Fire");
            Assert.Equal("Dispatch from Milford", saved.Description);
            Assert.Equal(["Fire"], saved.Groups);

            // The group filter, and deleting.
            vm.Filter = "";
            vm.GroupFilter = "Fire";
            Assert.Equal(2, vm.Rows.Count);
            vm.SetSelection([vm.Rows[0]]);
            vm.DeleteSelectedCommand.Execute(null);
            Assert.Equal(5, store.Channels.Count);

            vm.OfferImport("talkgroups.csv", "Decimal,Hex\n1,1\n");
            Assert.Null(vm.Pending);
            Assert.Contains("not a RadioReference frequency download", vm.Status);
        }
        finally { File.Delete(path); }
    }
}
