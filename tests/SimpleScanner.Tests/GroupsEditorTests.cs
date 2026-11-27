// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleScanner.ViewModels;

namespace SimpleScanner.Tests;

/// <summary>The channel editor's groups editor: a group made by name is kept, empty, until it is deleted.</summary>
public class GroupsEditorTests
{
    [Fact]
    public void AGroupMadeByNameIsKeptEmptyFilledRenamedAndDeleted()
    {
        string db = Path.Combine(Path.GetTempPath(), $"ss-groups-{Guid.NewGuid():N}.db");
        try
        {
            var store = new MemoryStore(db);
            store.Channels.AddRange([
                new MemoryChannel { Name = "Tower", FrequencyHz = 121.3e6, Groups = ["Air"] },
                new MemoryChannel { Name = "Fire", FrequencyHz = 154.43e6 },
            ]);
            store.Save();
            var vm = new ChannelsViewModel(MemoryStore.Load(db));
            Assert.Equal(["Air"], vm.EditorGroups.Select(g => g.Name));

            vm.GroupToMake = "  Weekend  ";
            vm.MakeGroupCommand.Execute(null);
            Assert.Equal("", vm.GroupToMake);
            Assert.Equal("Weekend", vm.GroupFilter);              // shown at once, with nothing in it
            Assert.Empty(vm.Rows);
            Assert.Equal(["Air", "Weekend"], vm.EditorGroups.Select(g => g.Name));
            Assert.Equal(0, vm.EditorGroups[1].Count);

            // The same name again, in any case, is the group there already.
            vm.GroupFilter = ChannelsViewModel.AllGroups;
            vm.GroupToMake = "weekend";
            vm.MakeGroupCommand.Execute(null);
            Assert.Contains("already", vm.Status);
            Assert.Equal("Weekend", vm.GroupFilter);
            Assert.Equal(2, vm.EditorGroups.Count);

            // Kept in the database with no channel in it; the main window lists it among the groups.
            var again = MemoryStore.Load(db);
            Assert.Equal(["Weekend"], again.KeptGroups);
            Assert.Contains("Weekend", again.GroupNames());

            // Filled from the ticks beside the selected channels.
            vm.GroupFilter = ChannelsViewModel.AllGroups;
            vm.SetSelection([vm.Rows.Single(r => r.Channel.Name == "Fire")]);
            vm.GroupTicks.Single(t => t.Name == "Weekend").Member = true;
            vm.Flush();
            Assert.Equal(1, vm.EditorGroups.Single(g => g.Name == "Weekend").Count);

            // Picking it in the editor shows it; renamed, it keeps its channels and stays kept.
            vm.EditorGroup = vm.EditorGroups.Single(g => g.Name == "Weekend");
            Assert.Equal("Weekend", vm.GroupFilter);
            Assert.True(vm.IsGroupChosen);
            vm.GroupNewName = "Saturday";
            vm.RenameGroupCommand.Execute(null);
            again = MemoryStore.Load(db);
            Assert.Equal(["Saturday"], again.KeptGroups);
            Assert.Equal(["Saturday"], again.Channels.Single(c => c.Name == "Fire").Groups);

            // Deleted: gone from the list and the database; its channel stays stored.
            vm.DeleteGroupCommand.Execute(null);
            Assert.Equal(["Air"], vm.EditorGroups.Select(g => g.Name));
            again = MemoryStore.Load(db);
            Assert.Empty(again.KeptGroups);
            Assert.DoesNotContain("Saturday", again.GroupNames());
            Assert.Equal(2, again.Channels.Count);
        }
        finally
        {
            File.Delete(db);
        }
    }
}
