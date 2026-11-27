using Avalonia.Styling;
using SimpleScanner.ViewModels;

namespace SimpleScanner.Tests;

/// <summary>
/// What the main window's deck and tabs show and keep: the groups line, the counts and the filter on Groups,
/// the look, the quick search ranges and the choice of radio.
/// </summary>
public class DeckTests
{
    private static MemoryChannel Ch(string name, double mhz, string group) => new() { Name = name, FrequencyHz = mhz * 1e6, Groups = [group] };
    private static Receiver Rx(string serial) => new(new FakeSource { Serial = serial }, playAudio: false, sampleRate: 2.4e6);

    private static (string path, string db) Files() =>
        (Path.Combine(Path.GetTempPath(), $"ss-deck-settings-{Guid.NewGuid():N}.json"), Path.Combine(Path.GetTempPath(), $"ss-deck-{Guid.NewGuid():N}.db"));

    private static void Remove(params string[] files)
    {
        foreach (string f in files) File.Delete(f);
    }

    [Fact]
    public void GroupsHaveTheirCountsTheDeckSaysWhatIsScannedAndTheFilterNarrowsTheLists()
    {
        var (path, db) = Files();
        try
        {
            var store = new MemoryStore(db);
            store.Channels.AddRange([Ch("Tower", 120, "Air"), Ch("Ground", 121.9, "Air"), Ch("Engine 1", 154, "Fire"), Ch("Harbour", 156.8, "Sea")]);
            store.Save();
            var settings = Settings.Load(path);
            settings.Entry("A").Groups = ["Air"];
            using var rx = Rx("A");
            var main = new MainViewModel(settings, [rx], MemoryStore.Load(db));
            GroupRow Row(string name) => main.GroupRows.First(g => g.Name == name);

            Assert.Equal(2, Row("Air").Count);
            Assert.Equal(1, Row("Fire").Count);
            Assert.Equal("Air", main.GroupsSummary);
            Assert.Equal(2, main.ActiveCount);
            Assert.Equal(2, main.ShownActive.Count);

            // The filter shows only what matches, groups and frequencies alike, and ticks nothing.
            main.GroupFilter = "fire";
            Assert.False(Row("Air").IsShown);
            Assert.True(Row("Fire").IsShown);
            Assert.Empty(main.ShownActive);
            main.GroupFilter = "GROUND";
            Assert.Equal(["Ground"], main.ShownActive.Select(a => a.Name));
            main.GroupFilter = "121.9";
            Assert.Equal(["Ground"], main.ShownActive.Select(a => a.Name));
            main.GroupFilter = "";
            Assert.All(main.GroupRows, g => Assert.True(g.IsShown));
            Assert.Equal(2, main.ShownActive.Count);
            Assert.Equal("Air", main.GroupsSummary);

            // Two groups are named; more are counted.
            Row("Fire").Selected = true;
            Assert.Equal("Air, Fire", main.GroupsSummary);
            main.ShowAllGroupsCommand.Execute(null);
            Assert.Equal("Air, Fire  +1", main.GroupsSummary);
            Assert.Equal(4, main.ActiveCount);
            main.ClearGroupsCommand.Execute(null);
            Assert.Equal("No groups ticked", main.GroupsSummary);
            Assert.Equal(0, main.ActiveCount);

            // A frequency unticked in the scan list is one fewer to scan; the filter keeps it in place.
            main.ShowAllGroupsCommand.Execute(null);
            main.GroupFilter = "tower";
            main.ShownActive[0].Scan = false;
            Assert.Equal(3, main.ActiveCount);
            Assert.False(MemoryStore.Load(db).Channels.First(c => c.Name == "Tower").Scan);
        }
        finally { Remove(path, db); }
    }

    [Fact]
    public void TheLookIsAppliedToTheDisplayAndKept()
    {
        var (path, db) = Files();
        try
        {
            using (var rx = Rx("A"))
            {
                var main = new MainViewModel(Settings.Load(path), [rx], new MemoryStore(db));
                Assert.Equal("System", main.SelectedAppearance);
                Assert.Equal(MainViewModel.StaysDark, main.SelectedDisplayLook);
                Assert.Equal(ThemeVariant.Dark, main.DisplayVariant);

                main.SelectedAppearance = "Light";
                main.SelectedDisplayLook = MainViewModel.Follows;
                Assert.Equal(ThemeVariant.Default, main.DisplayVariant);
                main.Shutdown();      // saves
            }
            var kept = Settings.Load(path);
            Assert.Equal("Light", kept.Appearance);
            Assert.True(kept.DisplayFollowsLight);

            using (var rx = Rx("A"))
            {
                var next = new MainViewModel(kept, [rx], new MemoryStore(db));
                Assert.Equal("Light", next.SelectedAppearance);
                Assert.Equal(MainViewModel.Follows, next.SelectedDisplayLook);
            }

            // A look not offered is System.
            var odd = Settings.Load(path);
            odd.Appearance = "Purple";
            using (var rx = Rx("A"))
                Assert.Equal("System", new MainViewModel(odd, [rx], new MemoryStore(db)).SelectedAppearance);
        }
        finally { Remove(path, db); }
    }

    [Fact]
    public void AQuickRangeFillsTheSearchBoxesAndControlsForChoosesTheRadio()
    {
        var (path, db) = Files();
        try
        {
            using var rxA = Rx("A");
            using var rxB = Rx("B");
            var main = new MainViewModel(Settings.Load(path), [rxA, rxB], new MemoryStore(db));

            var air = main.SearchRanges.First(r => r.Label.StartsWith("Airband"));
            main.SetSearchRangeCommand.Execute(air);
            Assert.Equal(("118", "137"), (main.SearchFrom, main.SearchTo));
            Assert.All(main.SearchRanges, r => Assert.True(double.Parse(r.From, System.Globalization.CultureInfo.InvariantCulture)
                < double.Parse(r.To, System.Globalization.CultureInfo.InvariantCulture), r.Label));

            var (a, b) = (main.RadioList[0], main.RadioList[1]);
            Assert.Same(a, main.Radio);
            main.SelectRadioCommand.Execute(b);
            Assert.Same(b, main.Radio);
            Assert.True(b.IsSelected);
            Assert.False(a.IsSelected);
        }
        finally { Remove(path, db); }
    }
}
