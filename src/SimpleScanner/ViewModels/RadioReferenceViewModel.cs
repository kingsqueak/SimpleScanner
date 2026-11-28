// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleScanner.RadioReference;

namespace SimpleScanner.ViewModels;

public enum RrNodeKind { Heading, Category, List, Agency, System, Note }

/// <summary>
/// A line of the RadioReference browser's tree: a heading, a category, a list of frequencies
/// that can be ticked for loading, an agency (whose lists are fetched when it is opened), or a
/// trunked system (shown, not loadable).
/// </summary>
public sealed partial class RrNode : ObservableObject
{
    private readonly Action<RrNode>? _opened;
    private readonly Action? _ticked;
    private bool? _isChecked = false;

    public RrNode(RrNodeKind kind, int id, string title, bool canCheck, Action<RrNode>? opened = null, Action? ticked = null)
    {
        Kind = kind;
        Id = id;
        Title = title;
        CanCheck = canCheck;
        _opened = opened;
        _ticked = ticked;
    }

    public RrNodeKind Kind { get; }
    public int Id { get; }
    public string Title { get; }
    /// <summary>Whether the line has a tick box. Trunked systems, notes and the headings over agencies have none.</summary>
    public bool CanCheck { get; }
    public RrNode? Parent { get; private set; }
    public ObservableCollection<RrNode> Children { get; } = [];
    /// <summary>For an agency: its lists have been fetched. Everything else is whole from the start.</summary>
    public bool Loaded { get; set; } = true;
    /// <summary>The words put in a channel's Agency field for a list under this line.</summary>
    public string AgencyLabel { get; init; } = "";
    /// <summary>The words put in a channel's Location for a list at or under this line.</summary>
    public string Location { get; init; } = "";
    public bool Dim => Kind is RrNodeKind.System or RrNodeKind.Note;

    /// <summary>Said after the title, muted: a distance, a count of frequencies.</summary>
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _opened?.Invoke(this);
    }

    /// <summary>Ticked, not ticked, or (null) ticked for some of what is under it.</summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            // A click on a half-ticked box ticks everything under it.
            bool on = value ?? true;
            if (!CanCheck || (_isChecked == on && value is not null)) return;
            SetDown(on);
            Parent?.Recount();
            _ticked?.Invoke();
        }
    }

    public void Add(RrNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    private void SetDown(bool on)
    {
        _isChecked = on;
        OnPropertyChanged(nameof(IsChecked));
        foreach (var c in Children.Where(c => c.CanCheck)) c.SetDown(on);
    }

    private void Recount()
    {
        var boxes = Children.Where(c => c.CanCheck).ToList();
        if (boxes.Count == 0 || !CanCheck) return;
        bool? now = boxes.All(c => c.IsChecked == true) ? true : boxes.All(c => c.IsChecked == false) ? false : null;
        if (now != _isChecked)
        {
            _isChecked = now;
            OnPropertyChanged(nameof(IsChecked));
        }
        Parent?.Recount();
    }

    /// <summary>The lists ticked at or under this line, and the ticked agencies not yet opened (whose lists are all wanted).</summary>
    public void Ticked(List<RrNode> lists, List<RrNode> unopened)
    {
        if (CanCheck && IsChecked == false) return;
        if (Kind == RrNodeKind.List && IsChecked == true) lists.Add(this);
        else if (Kind == RrNodeKind.Agency && !Loaded && IsChecked == true) unopened.Add(this);
        else foreach (var c in Children) c.Ticked(lists, unopened);
    }
}

/// <summary>A frequency of the list that is selected in the tree, to look over before loading.</summary>
public sealed record RrFreqRow(string MHz, string Name, string Description, string Tag, string Type, string Tone);

/// <summary>
/// The RadioReference browser: sign in, choose a place (by ZIP code, or country, state and county),
/// tick the categories wanted in the tree of what that county has, and load them as stored channels.
/// </summary>
public sealed partial class RadioReferenceViewModel : ObservableObject
{
    private readonly MemoryStore _store;
    private readonly RrClient _client;
    private readonly RrCache? _cache;
    private readonly Settings.RrPlace _place;
    private readonly Func<string?> _findKey;
    private readonly IRrLogins _logins;
    private bool _moving;                 // the choosers are being set by the program, not by the user
    private int _treeFor;                 // counts the trees built, so a slow answer for an old one is dropped
    private (double Lat, double Lon)? _home;
    private RrCountryInfo? _country;
    private RrStateInfo? _state;

    /// <summary>Channels were added to the store and saved: other windows should show them.</summary>
    public event Action? Loaded;
    /// <summary>The place or the account name changed and should be kept with the settings.</summary>
    public event Action? PlaceChanged;

    public ObservableCollection<RrNode> Roots { get; } = [];
    public ObservableCollection<RrFreqRow> Preview { get; } = [];
    /// <summary>The tick box's words: where the password is kept when it is ticked.</summary>
    public string RememberTip => $"Keep your user name, and your password in {_logins.Where}, so that you are signed in when this window opens";

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    /// <summary>Keep the user name and password for the next run. Ticked unless the user unticks it.</summary>
    [ObservableProperty] private bool _remember = true;
    [ObservableProperty] private bool _signedIn;
    [ObservableProperty] private string _accountText = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";

    [ObservableProperty] private string _zip = "";
    [ObservableProperty] private IReadOnlyList<RrCountry> _countries = [];
    [ObservableProperty] private RrCountry? _selectedCountry;
    [ObservableProperty] private IReadOnlyList<RrState> _states = [];
    [ObservableProperty] private RrState? _selectedState;
    [ObservableProperty] private IReadOnlyList<RrCounty> _counties = [];
    [ObservableProperty] private RrCounty? _selectedCounty;

    [ObservableProperty] private RrNode? _selectedNode;
    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string _loadGroup = "";
    [ObservableProperty] private string _tickedText = "Nothing ticked";
    [ObservableProperty] private bool _canLoad;

    public RadioReferenceViewModel(MemoryStore store, RrClient client, Settings.RrPlace? place = null, Func<string?>? findKey = null,
        RrCache? cache = null, IRrLogins? logins = null)
    {
        _store = store;
        _client = client;
        _cache = cache;
        _place = place ?? new Settings.RrPlace();
        _findKey = findKey ?? RrKey.Find;
        // The real program keeps a password where this system keeps them. A caller that supplies
        // the key itself (a test) keeps it in memory unless it says otherwise.
        _logins = logins ?? (findKey is null ? RrLogins.ForThisComputer(AppFiles.Folder) : new MemoryLogins());
        _username = _place.Username ?? "";
        _zip = _place.Zip ?? "";
        // Distances are measured from the ZIP code last looked up, whichever county is shown.
        if (_place is { HomeLat: double lat, HomeLon: double lon }) _home = (lat, lon);
    }

    /// <summary>The place and account name as they now are, to keep with the settings.</summary>
    public Settings.RrPlace Place => _place;

    /// <summary>
    /// Called once when the window opens: the list of countries (which needs no account), and a
    /// sign-in by itself when the password was kept.
    /// </summary>
    public async Task OpenAsync()
    {
        if (_findKey() is null)
        {
            Status = "This copy of SimpleScanner was built without RadioReference's application key, so their web service " +
                     "cannot be used. The packages on SimpleScanner's GitHub Releases page have it. " +
                     "CSV downloads from radioreference.com can still be imported in the channels window.";
            return;
        }
        await Do("Fetching the list of countries", async () =>
        {
            var list = await _client.GetCountriesAsync();
            _moving = true;
            Countries = list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            SelectedCountry = Countries.FirstOrDefault(c => c.Id == _place.Country) ?? Countries.FirstOrDefault(c => c.Code == "US");
            _moving = false;
        });
        FillLocations();
        if (Username.Length > 0 && _logins.Read(Username) is { Length: > 0 } kept)
        {
            Password = kept;
            Remember = true;
            await SignInAsync();
        }
        else if (Status.Length == 0)
            Status = "Sign in with your own RadioReference account. The service needs a Premium subscription.";
    }

    // Channels stored before locations were kept, or from a CSV file, get theirs from what has been fetched.
    private void FillLocations()
    {
        if (_cache is null) return;
        int filled = RrPlaces.FillFromCache(_store, _cache);
        if (filled == 0) return;
        _store.Save();
        Loaded?.Invoke();
        Status = $"{filled} stored channel{(filled == 1 ? " was" : "s were")} given a location from what was fetched before";
    }

    // ---- the account ------------------------------------------------------------------------

    [RelayCommand]
    private async Task SignInAsync()
    {
        string user = Username.Trim();
        if (user.Length == 0 || Password.Length == 0)
        {
            Status = "Type your RadioReference user name and password";
            return;
        }
        if (_findKey() is not { } key)
        {
            Status = "This copy of SimpleScanner has no RadioReference application key, so it cannot sign in";
            return;
        }
        _client.Auth = new RrAuth(user, Password, key);
        bool ok = await Do("Signing in", async () =>
        {
            var who = await _client.GetUserAsync();
            AccountText = Account(who.Username.Length > 0 ? who.Username : user, who.Expires);
        });
        if (!ok)
        {
            _client.Auth = null;
            return;
        }
        SignedIn = true;
        string kept = "";
        if (Remember)
        {
            _place.Username = user;
            if (!_logins.Keep(user, Password)) kept = $" (The password could not be kept in {_logins.Where}: it will be asked for next time.)";
        }
        else
        {
            _logins.Forget(user);
            if (_place.Username == user) _place.Username = null;
        }
        PlaceChanged?.Invoke();
        Status = "Signed in. Choose a place: a ZIP code, or a state and county." + kept;
        await RestorePlaceAsync();
    }

    private static string Account(string user, string expires)
    {
        // The date comes as the service has it; a zero date is an account with no subscription.
        bool none = expires.Length == 0 || expires.StartsWith("0000", StringComparison.Ordinal);
        if (!none && DateTime.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            expires = date.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        return none ? $"Signed in as {user} · no Premium subscription on record" : $"Signed in as {user} · Premium until {expires}";
    }

    [RelayCommand]
    private void SignOut()
    {
        _logins.Forget(Username.Trim());
        _client.Auth = null;
        SignedIn = false;
        Password = "";
        AccountText = "";
        Status = "Signed out. What was fetched before stays on this computer and can still be browsed.";
    }

    // ---- the place --------------------------------------------------------------------------

    private async Task RestorePlaceAsync()
    {
        if (SelectedCountry is null) return;
        await ShowCountryAsync(SelectedCountry, _place.State, _place.County);
    }

    partial void OnSelectedCountryChanged(RrCountry? value)
    {
        if (!_moving && value is not null && SignedIn) _ = ShowCountryAsync(value, 0, 0);
    }

    partial void OnSelectedStateChanged(RrState? value)
    {
        if (!_moving && value is not null) _ = ShowStateAsync(value, 0);
    }

    partial void OnSelectedCountyChanged(RrCounty? value)
    {
        if (!_moving && value is not null) _ = ShowCountyAsync(value, fresh: false);
    }

    private async Task ShowCountryAsync(RrCountry country, int thenState, int thenCounty)
    {
        await Do($"Fetching the states of {country.Name}", async () =>
        {
            _country = await _client.GetCountryAsync(country.Id);
            _moving = true;
            States = _country.States.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Counties = [];
            SelectedState = States.FirstOrDefault(s => s.Id == thenState);
            SelectedCounty = null;
            _moving = false;
            _place.Country = country.Id;
        });
        if (SelectedState is not null) await ShowStateAsync(SelectedState, thenCounty);
    }

    private async Task ShowStateAsync(RrState state, int thenCounty)
    {
        await Do($"Fetching the counties of {state.Name}", async () =>
        {
            _state = await _client.GetStateAsync(state.Id);
            _moving = true;
            Counties = _state.Counties.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            SelectedCounty = Counties.FirstOrDefault(c => c.Id == thenCounty);
            _moving = false;
            _place.State = state.Id;
        });
        if (SelectedCounty is not null) await ShowCountyAsync(SelectedCounty, fresh: false);
    }

    /// <summary>Find the county of a ZIP code and show it. Distances are then measured from that ZIP code.</summary>
    [RelayCommand]
    private async Task FindZipAsync()
    {
        if (!int.TryParse(Zip.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int zip) || Zip.Trim().Length != 5)
        {
            Status = "A ZIP code is five digits";
            return;
        }
        RrZip? found = null;
        if (!await Do($"Looking up {Zip.Trim()}", async () => found = await _client.GetZipAsync(zip)) || found is null) return;
        if (found.CountyId <= 0)
        {
            Status = $"RadioReference does not know the ZIP code {Zip.Trim()}";
            return;
        }
        _place.Zip = Zip.Trim();
        _home = found.Lat is double lat && found.Lon is double lon ? (lat, lon) : null;
        _place.HomeLat = found.Lat;
        _place.HomeLon = found.Lon;
        var us = Countries.FirstOrDefault(c => c.Code == "US");
        if (us is null) return;
        _moving = true;
        SelectedCountry = us;
        _moving = false;
        await ShowCountryAsync(us, found.StateId, found.CountyId);
    }

    /// <summary>Fetch the county again from the service, in place of what was kept.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedCounty is not null) await ShowCountyAsync(SelectedCounty, fresh: true);
    }

    // ---- the tree ---------------------------------------------------------------------------

    private async Task ShowCountyAsync(RrCounty county, bool fresh)
    {
        int mine = ++_treeFor;
        await Do($"Fetching {county.Name}", async () =>
        {
            var info = await _client.GetCountyAsync(county.Id, fresh);
            if (mine != _treeFor) return;
            _place.County = county.Id;
            string name = info.Name.Length > 0 ? info.Name : county.Name;
            Build(info, name);
            LoadGroup = SelectedState is { Code.Length: > 0 } s ? $"{name}, {s.Code}" : name;
            PlaceChanged?.Invoke();
            int lists = info.Cats.Sum(c => c.Subcats.Count);
            static string N(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
            Status = $"{name}: {N(lists, "list", "lists")} in {N(info.Cats.Count, "category", "categories")}, " +
                     $"{N(info.Agencies.Count, "agency", "agencies")}. Tick what you want and press Load.";
        });
    }

    private void Build(RrCountyInfo info, string name)
    {
        Roots.Clear();
        Preview.Clear();
        PreviewTitle = "";
        SelectedNode = null;

        var top = new RrNode(RrNodeKind.Heading, 0, name, canCheck: info.Cats.Count > 0, ticked: CountTicks);
        string code = SelectedState?.Code ?? "";
        string county = RrPlaces.County(name, info.Header, code);
        foreach (var cat in info.Cats)
            top.Add(CatNode(cat, agency: null, list => RrPlaces.ForCountyList(cat, list, name, info.Header, code)));
        if (info.Cats.Count == 0) top.Add(Note("No categories of its own"));
        top.IsExpanded = true;
        Roots.Add(top);

        AddAgencies($"Agencies in {name}", info.Agencies, county);
        if (_state is not null && _state.Id == info.StateId) AddAgencies($"{_state.Name} statewide agencies", _state.Agencies, RrPlaces.State(_state.Name));
        if (_country is not null) AddAgencies($"{_country.Name} nationwide agencies", _country.Agencies, RrPlaces.Country(_country.Name));

        if (info.Systems.Count > 0)
        {
            var systems = new RrNode(RrNodeKind.Heading, 0, "Trunked systems", canCheck: false) { Detail = "the scanner does not follow these yet" };
            foreach (var s in info.Systems.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                systems.Add(new RrNode(RrNodeKind.System, s.Id, s.Name, canCheck: false) { Detail = s.City });
            Roots.Add(systems);
        }
        CountTicks();
    }

    private void AddAgencies(string title, List<RrAgency> agencies, string location)
    {
        if (agencies.Count == 0) return;
        var head = new RrNode(RrNodeKind.Heading, 0, title, canCheck: false) { Detail = agencies.Count.ToString(CultureInfo.CurrentCulture) };
        foreach (var a in agencies.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            // An agency's lists are fetched when it is opened; until then a note stands in for them.
            var node = new RrNode(RrNodeKind.Agency, a.Id, a.Name, canCheck: true, opened: n => _ = OpenAgencyAsync(n), ticked: CountTicks)
            {
                Loaded = false,
                AgencyLabel = a.Name,
                Location = location,
            };
            node.Add(Note("Fetching…"));
            head.Add(node);
        }
        Roots.Add(head);
    }

    private RrNode CatNode(RrCat cat, string? agency, Func<RrSubcat, string> location)
    {
        var node = new RrNode(RrNodeKind.Category, cat.Id, cat.Name, canCheck: cat.Subcats.Count > 0, ticked: CountTicks);
        foreach (var sub in cat.Subcats)
            node.Add(new RrNode(RrNodeKind.List, sub.Id, sub.Name, canCheck: true, ticked: CountTicks)
            {
                // A county's list is named for its user already ("Manchester", "Nashua Airport"); an agency's is not ("Troop A").
                AgencyLabel = agency is null || sub.Name.Contains(agency, StringComparison.CurrentCultureIgnoreCase) ? sub.Name : $"{agency} - {sub.Name}",
                Location = location(sub),
                Detail = Distance(sub),
            });
        return node;
    }

    private static RrNode Note(string text) => new(RrNodeKind.Note, 0, text, canCheck: false);

    private async Task<bool> OpenAgencyAsync(RrNode agency)
    {
        if (agency.Loaded) return true;
        agency.Loaded = true;                    // not asked for twice while the answer is on its way
        bool ok = await Do($"Fetching {agency.Title}", async () =>
        {
            var info = await _client.GetAgencyAsync(agency.Id);
            bool ticked = agency.IsChecked == true;
            agency.Children.Clear();
            foreach (var cat in info.Cats) agency.Add(CatNode(cat, agency.Title, _ => agency.Location));
            if (info.Cats.Count == 0) agency.Add(Note("No frequencies listed"));
            if (ticked)
                foreach (var c in agency.Children.Where(c => c.CanCheck)) c.IsChecked = true;
        });
        if (!ok) agency.Loaded = false;
        CountTicks();
        return ok;
    }

    // How far a list's centre is from the ZIP code that was looked up: miles in the United States, kilometres elsewhere.
    private string Distance(RrSubcat sub)
    {
        if (_home is not { } home || sub.Lat is not double lat || sub.Lon is not double lon) return "";
        const double rad = Math.PI / 180;
        double a = Math.Pow(Math.Sin((lat - home.Lat) * rad / 2), 2) +
                   Math.Cos(home.Lat * rad) * Math.Cos(lat * rad) * Math.Pow(Math.Sin((lon - home.Lon) * rad / 2), 2);
        double km = 2 * 6371 * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        return SelectedCountry?.Code == "US" ? $"{km / 1.609344:0} mi" : $"{km:0} km";
    }

    private void CountTicks()
    {
        List<RrNode> lists = [], unopened = [];
        foreach (var r in Roots) r.Ticked(lists, unopened);
        CanLoad = lists.Count + unopened.Count > 0;
        TickedText = (lists.Count, unopened.Count) switch
        {
            (0, 0) => "Nothing ticked",
            (var l, 0) => $"{l} list{(l == 1 ? "" : "s")} ticked",
            (0, var a) => $"{a} whole agenc{(a == 1 ? "y" : "ies")} ticked",
            var (l, a) => $"{l} list{(l == 1 ? "" : "s")} and {a} whole agenc{(a == 1 ? "y" : "ies")} ticked",
        };
    }

    // ---- looking at a list ------------------------------------------------------------------

    partial void OnSelectedNodeChanged(RrNode? value)
    {
        if (value is { Kind: RrNodeKind.List }) _ = ShowListAsync(value);
    }

    private async Task ShowListAsync(RrNode list)
    {
        await Do($"Fetching {list.Title}", async () =>
        {
            var freqs = await _client.GetFreqsAsync(list.Id);
            if (SelectedNode != list) return;
            Preview.Clear();
            foreach (var c in freqs.OrderBy(f => f.OutMHz).Select(f => RrImport.ToChannel(f, list.AgencyLabel, list.Location)))
                Preview.Add(new RrFreqRow((c.FrequencyHz / 1e6).ToString("0.0000##", CultureInfo.InvariantCulture), c.Name,
                    c.Description ?? "", c.Tag ?? "", c.Format ?? "", c.Tone ?? ""));
            PreviewTitle = $"{list.AgencyLabel}: {freqs.Count} frequenc{(freqs.Count == 1 ? "y" : "ies")} · location {list.Location}";
        });
    }

    // ---- loading ----------------------------------------------------------------------------

    /// <summary>Fetch every ticked list and store its frequencies as channels in the group named.</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        string group = LoadGroup.Trim();
        if (group.Length == 0)
        {
            Status = "Type a group name for the loaded channels";
            return;
        }
        if (MemoryStore.IsAutoGroup(group))
        {
            Status = $"A group's name cannot start with \"{MemoryStore.TagPrefix.Trim()}\" or \"{MemoryStore.PlacePrefix.Trim()}\": those groups are made from the tags and the locations";
            return;
        }
        List<RrNode> lists = [], unopened = [];
        foreach (var r in Roots) r.Ticked(lists, unopened);
        foreach (var agency in unopened)
            if (!await OpenAgencyAsync(agency)) return;
        lists.Clear();
        unopened.Clear();
        foreach (var r in Roots) r.Ticked(lists, unopened);
        if (lists.Count == 0)
        {
            Status = "Nothing is ticked";
            return;
        }

        var channels = new List<MemoryChannel>();
        int done = 0;
        bool ok = await Do("Fetching", async () =>
        {
            foreach (var list in lists)
            {
                Status = $"Fetching {++done} of {lists.Count}: {list.AgencyLabel}…";
                var freqs = await _client.GetFreqsAsync(list.Id);
                list.Detail = $"{freqs.Count} frequenc{(freqs.Count == 1 ? "y" : "ies")}";
                channels.AddRange(freqs.Select(f => RrImport.ToChannel(f, list.AgencyLabel, list.Location)));
            }
        });
        if (!ok) return;
        var result = RrImport.Add(_store, channels, group);
        _store.Save();
        Status = $"{lists.Count} list{(lists.Count == 1 ? "" : "s")} into \"{group}\": {result}";
        Loaded?.Invoke();
    }

    // ---- one thing at a time ----------------------------------------------------------------

    private int _working;

    /// <summary>Run a call to the service: say what is being done, and say what went wrong if it fails.</summary>
    private async Task<bool> Do(string what, Func<Task> work)
    {
        _working++;
        Busy = true;
        // When the work leaves nothing to say, what was said before it stands again (unless that was only progress).
        string before = Status.EndsWith('…') ? "" : Status;
        Status = what + "…";
        try
        {
            await work();
            if (Status == what + "…") Status = before;
            return true;
        }
        catch (RrException e)
        {
            _moving = false;
            Status = e.Message;
            return false;
        }
        finally
        {
            if (--_working == 0) Busy = false;
        }
    }
}
