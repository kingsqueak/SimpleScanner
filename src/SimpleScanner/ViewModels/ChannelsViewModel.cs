using Avalonia.Controls;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleScanner.Dsp;

namespace SimpleScanner.ViewModels;

/// <summary>A stored channel as a row of the channel editor: every field can be changed.</summary>
public sealed class ChannelRow(MemoryChannel channel, Action touched) : ObservableObject
{
    public static IReadOnlyList<string> ReceiveChoices { get; } =
        ["Auto (from the band)", "AM", "AM 8.33 kHz", "NFM 12.5 kHz", "NFM 25 kHz", "WFM broadcast"];

    public MemoryChannel Channel { get; } = channel;
    /// <summary>The list's column headings, whose widths the row's columns follow.</summary>
    public IReadOnlyList<ColumnHead>? Heads { get; init; }

    private void Set(Action apply, [CallerMemberName] string? property = null)
    {
        apply();
        OnPropertyChanged(property);
        touched();
    }

    private static string? Opt(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public string Name { get => Channel.Name; set => Set(() => Channel.Name = value ?? ""); }
    public string Agency { get => Channel.Agency ?? ""; set => Set(() => Channel.Agency = Opt(value)); }
    public string Description { get => Channel.Description ?? ""; set => Set(() => Channel.Description = Opt(value)); }
    public string Tag { get => Channel.Tag ?? ""; set => Set(() => Channel.Tag = Opt(value)); }
    public string Format { get => Channel.Format ?? ""; set => Set(() => Channel.Format = Opt(value)); }
    public string Tone { get => Channel.Tone ?? ""; set => Set(() => Channel.Tone = Opt(value)); }
    public string Callsign { get => Channel.Callsign ?? ""; set => Set(() => Channel.Callsign = Opt(value)); }
    public string StationClass { get => Channel.StationClass ?? ""; set => Set(() => Channel.StationClass = Opt(value)); }
    public bool Scan { get => Channel.Scan; set => Set(() => Channel.Scan = value); }

    /// <summary>The frequency in MHz. Something that is not a frequency the receiver covers is not taken.</summary>
    public string Frequency
    {
        get => MHz(Channel.FrequencyHz);
        set => Set(() =>
        {
            if (TryMHz(value, out double hz) && hz is >= MainViewModel.MinHz and <= MainViewModel.MaxHz) Channel.FrequencyHz = hz;
        });
    }

    /// <summary>The repeater input in MHz; empty for none.</summary>
    public string Input
    {
        get => Channel.InputHz is double hz ? MHz(hz) : "";
        set => Set(() =>
        {
            if (string.IsNullOrWhiteSpace(value)) Channel.InputHz = null;
            else if (TryMHz(value, out double hz) && hz > 0) Channel.InputHz = hz;
        });
    }

    /// <summary>How the channel is received: left to the band plan, or a mode and filter width.</summary>
    public string Receive
    {
        get => Channel.Mode switch
        {
            null => ReceiveChoices[0],
            Mode.Am => Channel.BandwidthHz < 9000 ? ReceiveChoices[2] : ReceiveChoices[1],
            Mode.Wfm => ReceiveChoices[5],
            _ => Channel.BandwidthHz < 13000 ? ReceiveChoices[3] : ReceiveChoices[4],
        };
        set
        {
            int i = ReceiveChoices.ToList().IndexOf(value ?? "");
            if (i < 0) return;                // a ComboBox says null while its row is being swapped
            Set(() => (Channel.Mode, Channel.BandwidthHz) = i switch
            {
                1 => (Mode.Am, BandPlan.AmAir),
                2 => (Mode.Am, BandPlan.AmAir833),
                3 => (Mode.Nfm, BandPlan.NfmNarrow),
                4 => (Mode.Nfm, BandPlan.NfmWide),
                5 => (Mode.Wfm, BandPlan.Wfm),
                _ => ((Mode?)null, (double?)null),
            });
        }
    }

    public string Groups => string.Join(", ", Channel.Groups);

    public void GroupsChanged() => OnPropertyChanged(nameof(Groups));
    public void ScanChanged() => OnPropertyChanged(nameof(Scan));

    /// <summary>True when every word of the filter is somewhere in the channel's fields.</summary>
    public bool Matches(string[] words)
    {
        if (words.Length == 0) return true;
        string all = string.Join('\n', Frequency, Channel.Name, Channel.Agency, Channel.Description, Channel.Tag,
            Channel.Format, Channel.Tone, Channel.Callsign, Groups);
        return words.All(w => all.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    private static string MHz(double hz) => (hz / 1e6).ToString("0.0000##", CultureInfo.InvariantCulture);

    private static bool TryMHz(string? text, out double hz)
    {
        bool ok = double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
        hz = Math.Round(v * 1e6 / 10) * 10;
        return ok;
    }
}

/// <summary>A group as a tick box beside the selected channels: ticked when all are in it, half when some are.</summary>
public sealed class GroupTick(string name, bool? member, Action<string, bool> set) : ObservableObject
{
    public string Name { get; } = name;
    public bool? Member
    {
        get => member;
        set
        {
            // Clicking a half-ticked box takes every selected channel out of the group.
            bool on = value == true;
            if (member == on) return;
            member = on;
            OnPropertyChanged();
            set(Name, on);
        }
    }
}

/// <summary>A column heading of the channel list: clicking it sorts by that column, and it shows which way.</summary>
/// <remarks>It also holds the column's width, which the heading and every row follow; a width of 0 is the
/// one column that takes whatever room the others leave.</remarks>
public sealed partial class ColumnHead(string key, string title, double width) : ObservableObject
{
    public const double Narrowest = 28;
    public string Key { get; } = key;
    public string Title { get; } = title;
    /// <summary>The width the column starts with.</summary>
    public double NormalWidth { get; } = width;
    [ObservableProperty] private string _text = title;
    [ObservableProperty] private GridLength _width = width > 0 ? new GridLength(width) : GridLength.Star;
}

/// <summary>A file that has been read and waits for the user to name the group its channels go in.</summary>
public sealed record PendingImport(string FileName, List<MemoryChannel> Channels)
{
    public string Summary
    {
        get
        {
            int trunked = Channels.Count(RadioReferenceCsv.IsTrunked);
            return $"{FileName}: {Channels.Count} channels" + (trunked > 0 ? $" ({trunked} of them trunked-system frequencies)" : "");
        }
    }
}

/// <summary>
/// The channel editor: every stored channel in a list that can be filtered, the fields of the
/// selected one, the groups the selected ones are in, and the import of RadioReference files.
/// </summary>
public sealed partial class ChannelsViewModel : ObservableObject
{
    public const string AllGroups = "All groups", NoGroup = "(in no group)";
    private readonly MemoryStore _store;
    private List<ChannelRow> _selection = [];
    private readonly Queue<PendingImport> _queue = new();
    private bool _dirty, _loading;
    private int _seen;

    /// <summary>The channels have been changed and saved: other windows should show them again.</summary>
    public event Action? Edited;
    /// <summary>A group has a new name (old, new): a window that had it ticked should tick the new one.</summary>
    public event Action<string, string>? GroupRenamed;

    /// <summary>The list's column headings, in the order of the columns.</summary>
    public IReadOnlyList<ColumnHead> Heads { get; } =
    [
        new("Scan", "Scan", 46), new("MHz", "MHz", 80), new("Name", "Name", 130), new("Agency", "Agency", 150),
        new("Description", "Description", 0),
        new("Tag", "Tag", 104), new("Type", "Type", 58), new("Tone", "Tone / code", 92), new("Groups", "Groups", 150),
    ];

    /// <summary>The widths the user has dragged the columns to, by column key, to keep between runs.</summary>
    public Dictionary<string, double> ColumnWidths() =>
        Heads.Where(h => h.Width.IsAbsolute && h.Width.Value != h.NormalWidth).ToDictionary(h => h.Key, h => h.Width.Value);
    private string _sortKey = "MHz";
    private bool _sortDown;

    public ObservableCollection<ChannelRow> Rows { get; } = [];
    public ObservableCollection<GroupTick> GroupTicks { get; } = [];
    public IReadOnlyList<string> ReceiveChoices => ChannelRow.ReceiveChoices;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private IReadOnlyList<string> _groupChoices = [AllGroups];
    [ObservableProperty] private string _groupFilter = AllGroups;
    [ObservableProperty] private string _countText = "";
    /// <summary>The one selected channel, whose fields are shown; null with none or several selected.</summary>
    [ObservableProperty] private ChannelRow? _selected;
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _newGroup = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private PendingImport? _pending;
    [ObservableProperty] private string _importGroup = "";
    /// <summary>A real group is chosen in the filter, so it can be renamed or deleted.</summary>
    [ObservableProperty] private bool _isGroupChosen;
    [ObservableProperty] private string _groupNewName = "";

    public ChannelsViewModel(MemoryStore store, IReadOnlyDictionary<string, double>? columnWidths = null)
    {
        _store = store;
        foreach (var head in Heads)
            if (head.Width.IsAbsolute && columnWidths is not null && columnWidths.TryGetValue(head.Key, out double w) && double.IsFinite(w))
                head.Width = new GridLength(Math.Clamp(w, ColumnHead.Narrowest, 2000));
        _seen = store.Version;
        ShowSort();
        Reload();
    }

    // ---- the list ---------------------------------------------------------------------------

    private void Touch() => _dirty = true;

    /// <summary>Write the changes made since the last call, and tell the other windows.</summary>
    public void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        _store.Save();
        _seen = _store.Version;
        RefreshGroupChoices();      // an edited tag can make or end a tag's group
        Edited?.Invoke();
    }

    /// <summary>Show the channels again if something else (the main window) has changed them.</summary>
    public void ReloadIfChanged()
    {
        if (_store.Version == _seen) return;
        _seen = _store.Version;
        Reload();
    }

    /// <summary>The channels were read again from a file something else had changed: the rows here are
    /// of channels that are no longer stored, so the list is made again, and any edit not yet written is lost.</summary>
    public void StoreReloaded()
    {
        _dirty = false;
        _seen = _store.Version;
        Reload();
        Status = "The stored channels were changed outside the program and read again";
    }

    private void Reload()
    {
        _loading = true;
        string keep = GroupFilter;
        GroupChoices = Choices();
        GroupFilter = GroupChoices.Contains(keep) ? keep : AllGroups;
        _loading = false;

        string[] words = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Rows.Clear();
        foreach (var c in Sorted())
        {
            if (GroupFilter == NoGroup ? c.Groups.Count > 0 : GroupFilter != AllGroups && !MemoryStore.InGroup(c, GroupFilter)) continue;
            var row = new ChannelRow(c, Touch) { Heads = Heads };
            if (row.Matches(words)) Rows.Add(row);
        }
        CountText = Rows.Count == _store.Channels.Count ? $"{Rows.Count} channels" : $"{Rows.Count} of {_store.Channels.Count} channels";
        SetSelection([]);
    }

    partial void OnFilterChanged(string value) => Reload();

    partial void OnGroupFilterChanged(string value)
    {
        if (value is null) return;
        // A tag's group is made by the program: there is nothing to rename or delete.
        IsGroupChosen = value != AllGroups && value != NoGroup && !MemoryStore.IsTagGroup(value);
        GroupNewName = IsGroupChosen ? value : "";
        if (!_loading) Reload();
    }

    // ---- sorting ----------------------------------------------------------------------------

    /// <summary>Sort by a column; the same column again turns the order round.</summary>
    [RelayCommand]
    private void SortBy(string key)
    {
        _sortDown = key == _sortKey && !_sortDown;
        _sortKey = key;
        ShowSort();
        Reload();
    }

    private void ShowSort()
    {
        foreach (var head in Heads) head.Text = head.Key == _sortKey ? $"{head.Title} {(_sortDown ? "▼" : "▲")}" : head.Title;
    }

    // The channels in the order chosen. Text columns go alphabetically with the empty ones last
    // either way; channels alike in the column stay in frequency order.
    private IEnumerable<MemoryChannel> Sorted()
    {
        var text = StringComparer.CurrentCultureIgnoreCase;
        Func<MemoryChannel, string?>? field = _sortKey switch
        {
            "Name" => c => c.Name,
            "Agency" => c => c.Agency,
            "Description" => c => c.Description,
            "Tag" => c => c.Tag,
            "Type" => c => c.Format,
            "Tone" => c => c.Tone,
            "Groups" => c => string.Join(", ", c.Groups),
            _ => null,
        };
        IOrderedEnumerable<MemoryChannel> sorted;
        if (field is not null)
        {
            var filled = _store.Channels.OrderBy(c => string.IsNullOrEmpty(field(c)));
            sorted = _sortDown ? filled.ThenByDescending(field, text) : filled.ThenBy(field, text);
        }
        else if (_sortKey == "Scan")       // ticked first, then the other way
            sorted = _sortDown ? _store.Channels.OrderBy(c => c.Scan) : _store.Channels.OrderByDescending(c => c.Scan);
        else
            return _sortDown
                ? _store.Channels.OrderByDescending(c => c.FrequencyHz).ThenBy(c => c.Name, text)
                : _store.Channels.OrderBy(c => c.FrequencyHz).ThenBy(c => c.Name, text);
        return sorted.ThenBy(c => c.FrequencyHz).ThenBy(c => c.Name, text);
    }

    /// <summary>The rows selected in the list.</summary>
    public void SetSelection(IReadOnlyList<ChannelRow> rows)
    {
        _selection = [.. rows];
        Selected = _selection.Count == 1 ? _selection[0] : null;
        HasSelection = _selection.Count > 0;
        SelectionText = _selection.Count switch
        {
            0 => "Select a channel to edit it. Select several (Shift or Cmd/Ctrl-click) to put them in groups together.",
            1 => "",
            _ => $"{_selection.Count} channels selected: the groups and buttons below apply to all of them.",
        };
        RefreshTicks();
    }

    // ---- groups -----------------------------------------------------------------------------

    private void RefreshTicks()
    {
        GroupTicks.Clear();
        if (_selection.Count == 0) return;
        foreach (string name in _store.GroupNames().Where(n => !MemoryStore.IsTagGroup(n)))
        {
            int n = _selection.Count(r => r.Channel.Groups.Contains(name));
            GroupTicks.Add(new GroupTick(name, n == 0 ? false : n == _selection.Count ? true : null, SetMembership));
        }
    }

    private void SetMembership(string group, bool member)
    {
        foreach (var row in _selection)
        {
            bool had = row.Channel.Groups.Contains(group);
            if (member && !had) row.Channel.Groups.Add(group);
            else if (!member && had) row.Channel.Groups.Remove(group);
            else continue;
            row.GroupsChanged();
        }
        Touch();
        RefreshGroupChoices();
    }

    // What the filter offers: everything, the user's groups, the groups made from the tags, and the channels in no group.
    private List<string> Choices() =>
        [AllGroups, .. _store.GroupNames().Where(n => !MemoryStore.IsTagGroup(n)), .. _store.TagGroupNames(), NoGroup];

    // Whether a name typed for a group can be used; says why not.
    private bool NameAllowed(string name)
    {
        if (name is not (AllGroups or NoGroup) && !MemoryStore.IsTagGroup(name)) return true;
        Status = MemoryStore.IsTagGroup(name)
            ? $"A group's name cannot begin with \"{MemoryStore.TagPrefix.Trim()}\": those are the groups made from the tags"
            : $"A group cannot be called \"{name}\"";
        return false;
    }

    // The filter's list of groups, kept in step without touching the rows: a channel taken out of
    // the group being shown stays on screen until the filter is next changed.
    private void RefreshGroupChoices()
    {
        var choices = Choices();
        if (choices.SequenceEqual(GroupChoices)) return;
        _loading = true;
        string keep = GroupFilter;
        GroupChoices = choices;
        GroupFilter = choices.FirstOrDefault(c => MemoryStore.SameGroup(c, keep)) ?? AllGroups;
        _loading = false;
    }

    /// <summary>Put the selected channels in a group with the name typed, making it if it is new.</summary>
    [RelayCommand]
    private void AddToNewGroup()
    {
        string name = NewGroup.Trim();
        if (name.Length == 0 || _selection.Count == 0)
        {
            Status = name.Length == 0 ? "Type a name for the group first" : "Select the channels to put in the group first";
            return;
        }
        if (!NameAllowed(name)) return;
        SetMembership(name, true);
        NewGroup = "";
        Status = $"{_selection.Count} channel{(_selection.Count == 1 ? "" : "s")} in \"{name}\"";
        RefreshTicks();
    }

    /// <summary>
    /// Give the group chosen in the filter the name typed. If another group already has that
    /// name the two become one.
    /// </summary>
    [RelayCommand]
    private void RenameGroup()
    {
        string old = GroupFilter, name = GroupNewName.Trim();
        if (!IsGroupChosen || name.Length == 0 || name == old)
        {
            if (IsGroupChosen && name.Length == 0) Status = "Type the group's new name first";
            return;
        }
        if (!NameAllowed(name)) return;
        bool merged = _store.GroupNames().Contains(name);
        int n = 0;
        foreach (var c in _store.Channels)
        {
            int at = c.Groups.IndexOf(old);
            if (at < 0) continue;
            if (c.Groups.Contains(name)) c.Groups.RemoveAt(at);
            else c.Groups[at] = name;
            n++;
        }
        _dirty = true;
        _store.Save();
        _seen = _store.Version;
        _dirty = false;
        GroupRenamed?.Invoke(old, name);
        Edited?.Invoke();
        Status = merged
            ? $"\"{old}\" joined to \"{name}\": {n} channel{(n == 1 ? "" : "s")} moved"
            : $"\"{old}\" is now \"{name}\" ({n} channel{(n == 1 ? "" : "s")})";
        _loading = true;
        GroupChoices = Choices();
        GroupFilter = name;
        _loading = false;
        Reload();
    }

    /// <summary>
    /// Do away with the group chosen in the filter. Its channels stay stored, in whatever other
    /// groups they are in; to remove the channels themselves, select them and use Delete.
    /// </summary>
    [RelayCommand]
    private void DeleteGroup()
    {
        if (!IsGroupChosen) return;
        string old = GroupFilter;
        int n = _store.Channels.Count(c => c.Groups.Remove(old));
        _dirty = true;
        Flush();
        Status = $"Group \"{old}\" deleted; its {n} channel{(n == 1 ? " is" : "s are")} still stored";
        GroupFilter = AllGroups;       // which shows the channels again
    }

    [RelayCommand]
    private void ScanSelected() => SetScan(true);

    [RelayCommand]
    private void UnscanSelected() => SetScan(false);

    private void SetScan(bool on)
    {
        foreach (var row in _selection)
        {
            row.Channel.Scan = on;
            row.ScanChanged();
        }
        if (_selection.Count > 0) Touch();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (_selection.Count == 0) return;
        int n = _selection.Count;
        foreach (var row in _selection.ToList())
        {
            _store.Channels.Remove(row.Channel);
            Rows.Remove(row);
        }
        Touch();
        Status = $"{n} channel{(n == 1 ? "" : "s")} deleted";
        CountText = Rows.Count == _store.Channels.Count ? $"{Rows.Count} channels" : $"{Rows.Count} of {_store.Channels.Count} channels";
        SetSelection([]);
        RefreshGroupChoices();
    }

    // ---- import -----------------------------------------------------------------------------

    /// <summary>A file the user chose. It is read at once and then waits for a group name.</summary>
    public void OfferImport(string fileName, string text)
    {
        try
        {
            var channels = RadioReferenceCsv.Parse(text);
            if (channels.Count == 0)
            {
                Status = $"{fileName}: no frequencies in it";
                return;
            }
            _queue.Enqueue(new PendingImport(fileName, channels));
            if (Pending is null) NextImport();
        }
        catch (FormatException e)
        {
            Status = $"{fileName}: {e.Message}";
        }
    }

    private void NextImport()
    {
        Pending = _queue.Count > 0 ? _queue.Dequeue() : null;
        if (Pending is not null) ImportGroup = DefaultGroup(Pending.FileName);
    }

    // RadioReference names a county's download ctid_<county number>_<time>.csv, and the file does
    // not say which county that is.
    private static string DefaultGroup(string fileName)
    {
        string[] parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
        return parts is ["ctid", var id, ..] ? $"RadioReference county {id}" : Path.GetFileNameWithoutExtension(fileName);
    }

    [RelayCommand]
    private void ConfirmImport()
    {
        if (Pending is null) return;
        string group = ImportGroup.Trim();
        if (group.Length == 0)
        {
            Status = "Type a group name for the imported channels";
            return;
        }
        if (!NameAllowed(group)) return;
        var result = RadioReferenceCsv.Import(_store, Pending.Channels, group);
        Status = $"{Pending.FileName} into \"{group}\": {result}";
        _dirty = true;
        Flush();
        NextImport();
        Reload();
    }

    [RelayCommand]
    private void CancelImport() => NextImport();
}
