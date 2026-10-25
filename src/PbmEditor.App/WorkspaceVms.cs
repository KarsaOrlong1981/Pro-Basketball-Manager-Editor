using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using PbmEditor.Core;

namespace PbmEditor.App;

static class GridHelper
{
    /// <summary>Table may not exist in other game versions: return null instead of failing.</summary>
    public static TableGrid? Make(PbmDatabase db, string table, string title, string? col = null, object? val = null, string? where = null) =>
        db.GetTable(table) == null ? null : new TableGrid(db, table, col, val, where) { Title = Loc.T(title) };

    public static void Apply(params TableGrid?[] grids)
    {
        foreach (var g in grids) g?.Apply();
    }
}

// ---------------------------------------------------------------- Teams

public sealed class TeamsVm : ObservableObject, IWorkspacePart
{
    readonly PbmDatabase _db;
    readonly ImageStore _store;
    RowEditor? _team, _arena;
    TeamItem? _selected;
    LeagueItem? _leagueFilter;
    PlayerItem? _selectedPlayer;
    StaffItem? _selectedStaff;
    string _finance = "";

    public ObservableCollection<LeagueItem> Leagues { get; } = new();
    public ICollectionView Teams { get; private set; } = CollectionViewSource.GetDefaultView(new List<TeamItem>());
    public ObservableCollection<PlayerItem> Players { get; } = new();
    public List<Field> TeamFields { get; private set; } = new();
    public List<Field> ArenaFields { get; private set; } = new();
    public bool HasArena => _arena is { Exists: true };
    public bool NoArena => _selected != null && !HasArena;
    public string Finance { get => _finance; private set => Set(ref _finance, value); }
    public PlayerVm? Player { get; private set; }
    public ObservableCollection<StaffItem> Staff { get; } = new();
    public PersonVm? Person { get; private set; }

    public StaffItem? SelectedStaff
    {
        get => _selectedStaff;
        set
        {
            try { Person?.Flush(); }
            catch (FormatException e) { Dialogs.Error(e.Message); Resync(nameof(SelectedStaff)); return; }
            if (Set(ref _selectedStaff, value)) LoadPerson();
        }
    }

    public ImageSlot Logo { get; }
    public ImageSlot JerseyHome { get; }
    public ImageSlot JerseyAway { get; }
    public ImageSlot ArenaInside { get; }
    public ImageSlot ArenaOutside { get; }
    public ICommand CreateArenaCommand { get; }

    public LeagueItem? LeagueFilter
    {
        get => _leagueFilter;
        set { if (Set(ref _leagueFilter, value)) Teams.Refresh(); }
    }

    public PlayerItem? SelectedPlayer
    {
        get => _selectedPlayer;
        set
        {
            if (!TryFlushPlayer()) { Resync(nameof(SelectedPlayer)); return; }
            if (Set(ref _selectedPlayer, value)) LoadPlayer();
        }
    }

    public TeamItem? SelectedTeam
    {
        get => _selected;
        set
        {
            if (!TryFlush()) { Resync(nameof(SelectedTeam)); return; }
            if (Set(ref _selected, value)) { _selectedPlayer = null; Notify(nameof(SelectedPlayer)); LoadTeam(); }
        }
    }

    public TeamsVm(PbmDatabase db, ImageStore store)
    {
        _db = db; _store = store;
        Logo = new ImageSlot(store, "Logo", "team_logos", "{0}.png", ImageKind.Fit, 800, 800, 120);
        // game rule: home kit = main couleur_home / secondary couleur_away, away kit = the reverse
        JerseyHome = new ImageSlot(store, "Jersey home (front + back)", "jerseys", "{0}_HOME.png", ImageKind.Fit, 800, 800, 160,
            () => JerseyKit.Render(_team?["couleur_home"]?.Text, _team?["couleur_away"]?.Text));
        JerseyAway = new ImageSlot(store, "Jersey away (front + back)", "jerseys", "{0}_AWAY.png", ImageKind.Fit, 800, 800, 160,
            () => JerseyKit.Render(_team?["couleur_away"]?.Text, _team?["couleur_home"]?.Text));
        ArenaInside = new ImageSlot(store, "Arena inside", "arenas", "{0}_INSIDE.jpg", ImageKind.Jpeg, 1920, 1080, 120);
        ArenaOutside = new ImageSlot(store, "Arena outside", "arenas", "{0}_OUTSIDE.jpg", ImageKind.Jpeg, 1920, 1080, 120);
        CreateArenaCommand = new RelayCommand(CreateArena, () => NoArena);
        Reload();
    }

    void Resync(string prop) => Application.Current.Dispatcher.BeginInvoke(() => Notify(prop));

    bool TryFlushPlayer()
    {
        try { Player?.Flush(); return true; }
        catch (FormatException e) { Dialogs.Error(e.Message); return false; }
    }

    bool TryFlush()
    {
        try { Flush(); return true; }
        catch (FormatException e) { Dialogs.Error(e.Message); return false; }
    }

    public void Reload()
    {
        var selId = _selected?.Id;
        Leagues.Clear();
        Leagues.Add(new LeagueItem(-1, Loc.T("(all leagues)"), 0, 0));
        foreach (var l in GameService.Leagues(_db)) Leagues.Add(l);
        _leagueFilter = Leagues[0]; Notify(nameof(LeagueFilter));
        var teams = GameService.Teams(_db);
        Teams = CollectionViewSource.GetDefaultView(teams);
        Teams.Filter = o => _leagueFilter is null or { Id: -1 } || ((TeamItem)o).LeagueId == _leagueFilter.Id;
        Notify(nameof(Teams));
        _selected = selId == null ? null : teams.FirstOrDefault(t => t.Id == selId);
        Notify(nameof(SelectedTeam));
        LoadTeam();
    }

    void LoadTeam()
    {
        Players.Clear();
        _team = _arena = null; TeamFields = new(); ArenaFields = new();
        Staff.Clear(); _selectedStaff = null; Person = null;
        Finance = "";
        Player = null;
        foreach (var s in new[] { Logo, JerseyHome, JerseyAway, ArenaInside, ArenaOutside }) s.SetId(_selected?.Id);
        if (_selected != null)
        {
            var id = _selected.Id;
            _team = new RowEditor(_db, "DYN_equipe", "id_equipe", id, FieldConfigs.Team());
            TeamFields = _team.Fields;
            foreach (var f in TeamFields.Where(f => f.Name is "couleur_home" or "couleur_away"))
                f.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Field.Text)) { JerseyHome.Refresh(); JerseyAway.Refresh(); } };
            if (_db.GetTable("DYN_facilities") != null)
            {
                _arena = new RowEditor(_db, "DYN_facilities", "id_team", id, FieldConfigs.Arena());
                if (_arena.Exists) ArenaFields = _arena.Fields;
            }
            RefreshPlayers();
            RefreshFinance();
            RefreshStaff();
        }
        foreach (var p in new[] { nameof(TeamFields), nameof(ArenaFields), nameof(HasArena), nameof(NoArena), nameof(Player) }) Notify(p);
    }

    void RefreshFinance()
    {
        if (_selected == null) return;
        var f = GameService.TeamFinance(_db, _selected.Id);
        Finance = Loc.F("Payroll per month: players {0:N0} + staff {1:N0} = {2:N0}.   Estimated season cost (13 x monthly, unverified): {3:N0}.   Budget {4:N0}  ->  estimated end of season {5:N0}",
                      f.MonthlyPlayers, f.MonthlyStaff, f.Monthly, f.SeasonCostEstimate, f.Budget, f.BudgetAfterSeason) + (f.BudgetAfterSeason < 0 ? Loc.T("  (NEGATIVE)") : "");
    }

    void RefreshPlayers()
    {
        var keep = _selectedPlayer?.Id;
        Players.Clear();
        foreach (var p in GameService.PlayersOfTeam(_db, _selected!.Id)) Players.Add(p);
        _selectedPlayer = keep == null ? null : Players.FirstOrDefault(p => p.Id == keep);
        Notify(nameof(SelectedPlayer));
    }

    void LoadPlayer()
    {
        Player = _selectedPlayer == null ? null
            : new PlayerVm(_db, _store, _selectedPlayer.Id, _selected!.Id, GameService.Teams(_db).Where(t => t.Id != _selected.Id).ToList(), OnPlayerStructureChanged);
        Notify(nameof(Player));
    }

    void RefreshStaff()
    {
        var keep = _selectedStaff?.Id;
        Staff.Clear();
        if (_selected != null) foreach (var s in GameService.StaffOfTeam(_db, _selected.Id)) Staff.Add(s);
        _selectedStaff = keep == null ? null : Staff.FirstOrDefault(s => s.Id == keep);
        Notify(nameof(SelectedStaff));
    }

    void LoadPerson()
    {
        Person = _selectedStaff == null ? null
            : new PersonVm(_db, _store, _selectedStaff.Id, _selected!.Id, GameService.Teams(_db).Where(t => t.Id != _selected.Id).ToList(), OnStaffStructureChanged);
        Notify(nameof(Person));
    }

    void OnStaffStructureChanged()
    {
        RefreshStaff();
        RefreshFinance();
        LoadPerson();
    }

    void OnPlayerStructureChanged()
    {
        RefreshPlayers();
        RefreshFinance();
        LoadPlayer();
    }

    void CreateArena()
    {
        if (_selected == null) return;
        GameService.CreateArena(_db, _selected.Id, _selected.Name + " Arena");
        var keep = _selected;
        LoadTeam();
        Notify(nameof(SelectedTeam));
        _ = keep;
    }

    public void Flush()
    {
        _team?.Save();
        _arena?.Save();
        Player?.Flush();
        Person?.Flush();
        if (_selected != null) { RefreshFinance(); }
    }
}

// ---------------------------------------------------------------- Free agents

public sealed class FreeAgentsVm : ObservableObject, IWorkspacePart
{
    readonly PbmDatabase _db;
    readonly ImageStore _store;
    List<PlayerItem> _all = new();
    string _search = "";
    PlayerItem? _selected;
    TeamItem? _target;
    string _count = "";

    public ICollectionView Players { get; private set; } = CollectionViewSource.GetDefaultView(new List<PlayerItem>());
    public IReadOnlyList<TeamItem> Teams { get; private set; } = new List<TeamItem>();
    public PlayerVm? Player { get; private set; }
    public string Count { get => _count; private set => Set(ref _count, value); }
    public TeamItem? Target { get => _target; set => Set(ref _target, value); }
    public ICommand SignCommand { get; }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) Players.Refresh(); }
    }

    public PlayerItem? Selected
    {
        get => _selected;
        set
        {
            try { Player?.Flush(); }
            catch (FormatException e) { Dialogs.Error(e.Message); Application.Current.Dispatcher.BeginInvoke(() => Notify(nameof(Selected))); return; }
            if (Set(ref _selected, value))
            {
                Player = value == null ? null : new PlayerVm(_db, _store, value.Id, 0, Teams, () => { });
                Notify(nameof(Player));
            }
        }
    }

    public FreeAgentsVm(PbmDatabase db, ImageStore store)
    {
        _db = db; _store = store;
        SignCommand = new RelayCommand(Sign, () => Selected != null && Target != null);
        Reload();
    }

    public void Reload()
    {
        _all = GameService.FreeAgents(_db);
        Teams = GameService.Teams(_db);
        Players = CollectionViewSource.GetDefaultView(_all);
        Players.Filter = o => _search.Length == 0 || ((PlayerItem)o).Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase);
        _selected = null; Player = null;
        Count = Loc.F("{0} free agents (best 20,000 by rating)", _all.Count);
        foreach (var p in new[] { nameof(Players), nameof(Teams), nameof(Selected), nameof(Player) }) Notify(p);
    }

    void Sign()
    {
        if (Selected == null || Target == null) return;
        try
        {
            Player?.Flush();
            GameService.MovePlayer(_db, Selected.Id, Target.Id);
            _all.Remove(Selected);
            Players.Refresh();
            Selected = null;
            Count = Loc.F("{0} free agents", _all.Count);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { Dialogs.Error(e.Message); }
    }

    public void Flush() => Player?.Flush();
}

// ---------------------------------------------------------------- Leagues and cups

public sealed record PhaseItem(long Id, string Label)
{
    public override string ToString() => Label;
}

public sealed class LeaguesVm : ObservableObject, IWorkspacePart
{
    readonly PbmDatabase _db;
    readonly ImageStore _store;
    LeagueItem? _selected;
    string _search = "";
    long _typeFilter = -1;
    PhaseItem? _phase;
    TeamItem? _addTeam, _selectedTeam;
    LeagueItem? _moveTo;
    RowEditor? _details, _rules;
    List<LeagueItem> _all = new();

    public ICollectionView Competitions { get; private set; } = CollectionViewSource.GetDefaultView(new List<LeagueItem>());
    public List<long> Types { get; private set; } = new();
    public List<Field> DetailFields { get; private set; } = new();
    public List<Field> RuleFields { get; private set; } = new();
    public ImageSlot Logo { get; }
    public ImageSlot LogoSmall { get; }
    public ObservableCollection<TeamItem> LeagueTeams { get; } = new();
    public IReadOnlyList<TeamItem> AllTeams { get; private set; } = new List<TeamItem>();
    public IReadOnlyList<LeagueItem> AllLeagues { get; private set; } = new List<LeagueItem>();
    public List<PhaseItem> Phases { get; private set; } = new();

    public TableGrid? PhaseGrid { get; private set; }
    public TableGrid? PrizeGrid { get; private set; }
    public TableGrid? QuotaGrid { get; private set; }
    public TableGrid? RelegationGrid { get; private set; }
    public TableGrid? ObjectiveGrid { get; private set; }
    public TableGrid? DivisionGrid { get; private set; }
    public TableGrid? ForcedGrid { get; private set; }
    public TableGrid? QualifiedGrid { get; private set; }
    public TableGrid? BracketGrid { get; private set; }
    public TableGrid? MatchDayGrid { get; private set; }

    public ICommand AddTeamCommand { get; }
    public ICommand MoveTeamCommand { get; }

    public string Search { get => _search; set { if (Set(ref _search, value)) Competitions.Refresh(); } }
    public long TypeFilter { get => _typeFilter; set { if (Set(ref _typeFilter, value)) Competitions.Refresh(); } }
    public TeamItem? AddTeam { get => _addTeam; set => Set(ref _addTeam, value); }
    public TeamItem? SelectedTeam { get => _selectedTeam; set => Set(ref _selectedTeam, value); }
    public LeagueItem? MoveTo { get => _moveTo; set => Set(ref _moveTo, value); }

    public PhaseItem? Phase
    {
        get => _phase;
        set
        {
            if (!TryFlush()) { Application.Current.Dispatcher.BeginInvoke(() => Notify(nameof(Phase))); return; }
            if (Set(ref _phase, value)) LoadPhaseGrids();
        }
    }

    public LeagueItem? Selected
    {
        get => _selected;
        set
        {
            if (!TryFlush()) { Application.Current.Dispatcher.BeginInvoke(() => Notify(nameof(Selected))); return; }
            if (Set(ref _selected, value)) LoadLeague();
        }
    }

    public LeaguesVm(PbmDatabase db, ImageStore store)
    {
        _db = db; _store = store;
        Logo = new ImageSlot(store, "Logo", "league_logos", "{0}.png", ImageKind.Fit, 800, 800, 110);
        LogoSmall = new ImageSlot(store, "Logo (small)", "league_logos", "{0}_SMALL.png", ImageKind.Fit, 200, 200, 70);
        AddTeamCommand = new RelayCommand(() => AssignTeam(AddTeam), () => Selected != null && AddTeam != null);
        MoveTeamCommand = new RelayCommand(() => AssignTeam(SelectedTeam, MoveTo?.Id), () => SelectedTeam != null && MoveTo != null);
        Reload();
    }

    bool TryFlush()
    {
        try { Flush(); return true; }
        catch (FormatException e) { Dialogs.Error(e.Message); return false; }
    }

    public void Reload()
    {
        var keep = _selected?.Id;
        _all = GameService.Leagues(_db);
        AllLeagues = _all;
        AllTeams = GameService.Teams(_db);
        Types = _all.Select(l => l.Type).Distinct().OrderBy(t => t).Prepend(-1L).ToList();
        Competitions = CollectionViewSource.GetDefaultView(_all);
        Competitions.Filter = o =>
        {
            var l = (LeagueItem)o;
            return (_typeFilter == -1 || l.Type == _typeFilter) &&
                   (_search.Length == 0 || l.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase) || l.Id.ToString() == _search);
        };
        _selected = keep == null ? null : _all.FirstOrDefault(l => l.Id == keep);
        foreach (var p in new[] { nameof(Competitions), nameof(Types), nameof(AllTeams), nameof(AllLeagues), nameof(Selected) }) Notify(p);
        LoadLeague();
    }

    void LoadLeague()
    {
        _details = _rules = null;
        DetailFields = new(); RuleFields = new(); Phases = new(); _phase = null;
        LeagueTeams.Clear();
        Logo.SetId(_selected?.Id); LogoSmall.SetId(_selected?.Id);
        PhaseGrid = PrizeGrid = QuotaGrid = RelegationGrid = ObjectiveGrid = DivisionGrid = null;
        if (_selected != null)
        {
            var id = _selected.Id;
            _details = new RowEditor(_db, "DYN_championnat", "id", id, FieldConfigs.League());
            DetailFields = _details.Fields;
            if (_db.GetTable("STA_competition_regles") != null)
            {
                _rules = new RowEditor(_db, "STA_competition_regles", "ligue_id", id, FieldConfigs.Rules());
                RuleFields = _rules.Exists ? _rules.Fields : new();
            }
            RefreshTeams();
            PhaseGrid = GridHelper.Make(_db, "STA_competition_phase", "Phases", "id_competition", id);
            PrizeGrid = GridHelper.Make(_db, "STA_competition_prize_money", "Prize money", "id_competition", id);
            QuotaGrid = GridHelper.Make(_db, "STA_competition_quotas", "Quotas", "id_competition", id);
            RelegationGrid = GridHelper.Make(_db, "STA_competition_relegations", "Promotion / relegation", "id_competition", id);
            ObjectiveGrid = GridHelper.Make(_db, "STA_competition_objectifs", "Objectives", "id_competition", id);
            DivisionGrid = GridHelper.Make(_db, "STA_Equipes_Division", "Divisions", "id_competition", id);
            RefreshPhases();
        }
        LoadPhaseGrids();
        foreach (var p in new[] { nameof(DetailFields), nameof(RuleFields), nameof(Phases), nameof(Phase), nameof(PhaseGrid), nameof(PrizeGrid),
                     nameof(QuotaGrid), nameof(RelegationGrid), nameof(ObjectiveGrid), nameof(DivisionGrid) }) Notify(p);
    }

    void RefreshPhases()
    {
        if (_selected == null || _db.GetTable("STA_competition_phase") == null) return;
        Phases = _db.Query("SELECT id, ordre, nom_phase FROM STA_competition_phase WHERE id_competition=@p0 ORDER BY ordre", _selected.Id)
            .Select(r => new PhaseItem(r.Int("id"), $"{r.Int("ordre")} - {r.Str("nom_phase")} (id {r.Int("id")})")).ToList();
        _phase = Phases.FirstOrDefault();
    }

    void LoadPhaseGrids()
    {
        ForcedGrid = QualifiedGrid = BracketGrid = MatchDayGrid = null;
        if (_phase != null)
        {
            ForcedGrid = GridHelper.Make(_db, "STA_phase_forced_teams", "Forced teams", "id_phase", _phase.Id);
            QualifiedGrid = GridHelper.Make(_db, "STA_competition_phase_equipes_qualifiees", "Qualified teams", "id_phase", _phase.Id);
            BracketGrid = GridHelper.Make(_db, "STA_competition_phase_brackets", "Brackets", "id_phase", _phase.Id);
            MatchDayGrid = GridHelper.Make(_db, "STA_competition_jours_matchs", "Match days", "id_phase", _phase.Id);
        }
        foreach (var p in new[] { nameof(ForcedGrid), nameof(QualifiedGrid), nameof(BracketGrid), nameof(MatchDayGrid) }) Notify(p);
    }

    void RefreshTeams()
    {
        LeagueTeams.Clear();
        if (_selected == null) return;
        foreach (var t in GameService.Teams(_db).Where(t => t.LeagueId == _selected.Id)) LeagueTeams.Add(t);
    }

    void AssignTeam(TeamItem? team, long? leagueId = null)
    {
        if (team == null) return;
        var target = leagueId ?? _selected?.Id;
        if (target == null) return;
        GameService.SetTeamLeague(_db, team.Id, target.Value);
        AllTeams = GameService.Teams(_db);
        Notify(nameof(AllTeams));
        RefreshTeams();
    }

    public void Flush()
    {
        _details?.Save();
        _rules?.Save();
        GridHelper.Apply(PhaseGrid, PrizeGrid, QuotaGrid, RelegationGrid, ObjectiveGrid, DivisionGrid, ForcedGrid, QualifiedGrid, BracketGrid, MatchDayGrid);
    }
}

// ---------------------------------------------------------------- Raw tables

public sealed class RawVm : ObservableObject, IWorkspacePart
{
    readonly PbmDatabase _db;
    string? _table;
    string _where = "";
    string _status = "";

    public List<string> Tables { get; }
    public TableGrid? Grid { get; private set; }
    public ICommand LoadCommand { get; }
    public string Where { get => _where; set => Set(ref _where, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public string? Table
    {
        get => _table;
        set
        {
            if (!TryFlush()) { Application.Current.Dispatcher.BeginInvoke(() => Notify(nameof(Table))); return; }
            if (Set(ref _table, value)) Open();
        }
    }

    public RawVm(PbmDatabase db)
    {
        _db = db;
        Tables = db.TableNames.ToList();
        LoadCommand = new RelayCommand(() => { if (TryFlush()) Open(); });
    }

    bool TryFlush()
    {
        try { Flush(); return true; }
        catch (Exception e) when (e is FormatException or Microsoft.Data.Sqlite.SqliteException) { Dialogs.Error(e.Message); return false; }
    }

    void Open()
    {
        Grid = null; Status = "";
        if (_table != null)
        {
            try
            {
                Grid = new TableGrid(_db, _table, where: Where) { RawNames = true };
                Status = Grid.ReadOnly ? Loc.T("Read-only.") :
                    Grid.Truncated ? Loc.F("Showing the first {0} rows - use a filter.", TableGrid.MaxRows) : Loc.F("{0} rows", Grid.Data.Rows.Count);
            }
            catch (Microsoft.Data.Sqlite.SqliteException e) { Status = Loc.F("Query failed: {0}", e.Message); }
        }
        Notify(nameof(Grid));
    }

    public void Reload() => Open();
    public void Flush() => Grid?.Apply();
}
