using System.Windows.Input;
using PbmEditor.Core;

namespace PbmEditor.App;

/// <summary>Editor for one player: identity, look, attributes and active contract in one field list.</summary>
public sealed class PlayerVm : ObservableObject
{
    readonly PbmDatabase _db;
    readonly Action _onStructureChanged;
    RowEditor _info = null!, _attrs = null!;
    RowEditor? _contract;
    TeamItem? _moveTarget;

    public long PlayerId { get; }
    public long TeamId { get; private set; }
    public List<Field> Fields { get; private set; } = new();
    public ImageSlot Photo { get; }
    public string Header { get; private set; } = "";
    public IReadOnlyList<TeamItem> MoveTargets { get; }
    public TeamItem? MoveTarget { get => _moveTarget; set => Set(ref _moveTarget, value); }
    public bool AutoAdjustOverall { get; set; } = true;
    public ICommand MoveCommand { get; }
    public ICommand FreeAgentCommand { get; }
    public ICommand AddContractCommand { get; }

    public PlayerVm(PbmDatabase db, ImageStore store, long playerId, long teamId, IReadOnlyList<TeamItem> teams, Action onStructureChanged)
    {
        _db = db; PlayerId = playerId; TeamId = teamId; MoveTargets = teams; _onStructureChanged = onStructureChanged;
        Photo = new ImageSlot(store, "Photo (140x160)", "player_faces", "{0}.png", ImageKind.Face, previewHeight: 160);
        Photo.SetId(playerId);
        MoveCommand = new RelayCommand(Move, () => MoveTarget != null);
        FreeAgentCommand = new RelayCommand(MakeFreeAgent);
        AddContractCommand = new RelayCommand(AddContract, () => TeamId != 0 && _contract == null);
        Load();
    }

    void Load()
    {
        _info = new RowEditor(_db, "DYN_joueur", "joueur_id", PlayerId, FieldConfigs.Player());
        _attrs = new RowEditor(_db, "DYN_joueur_attributes", "joueur_id", PlayerId, FieldConfigs.Attributes());
        TeamId = _info["team_id"] is { } t && long.TryParse(t.Text, out var tid) ? tid : TeamId;
        var cid = TeamId == 0 ? null : GameService.ActiveContractId(_db, PlayerId, TeamId);
        _contract = cid == null ? null : new RowEditor(_db, "DYN_contrat", "id", cid.Value, FieldConfigs.Contract());

        Fields = _info.Fields
            .Concat(_attrs.Fields.Where(f => !f.Name.Equals("joueur_id", StringComparison.OrdinalIgnoreCase)))
            .Concat(_contract?.Fields ?? new List<Field>())
            .ToList();

        var (y, m, d) = GameService.CurrentDate(_db);
        _ = int.TryParse(_info["annee_naissance"]?.Text, out var by);
        _ = int.TryParse(_info["mois_naissance"]?.Text, out var bm);
        _ = int.TryParse(_info["jour_naissance"]?.Text, out var bd);
        var age = by == 0 ? "?" : (y - by - ((m, d).CompareTo((bm, bd)) < 0 ? 1 : 0)).ToString();
        Header = Loc.F("{0} {1}  -  age {2}, overall {3}", _info["joueur_prenom"]?.Text, _info["joueur_nom"]?.Text, age, _info["moyenne"]?.Text) +
                 (_contract == null && TeamId != 0 ? Loc.T("  (no active contract)") : "");
        Notify(nameof(Fields)); Notify(nameof(Header));
    }

    /// <summary>Writes edits into the DB transaction; adjusts the overall rating by the attribute change.</summary>
    public void Flush()
    {
        var overallEdited = _info["moyenne"]?.Dirty == true || _attrs["moyenne"]?.Dirty == true;
        var delta = FieldConfigs.AttributeColumns.Select(c => _attrs[c]).Where(f => f is { Dirty: true })
            .Sum(f => f!.Number - double.Parse(f.OriginalText, System.Globalization.CultureInfo.InvariantCulture));
        var anyDirty = Fields.Any(f => f.Dirty);
        if (!anyDirty) return;
        _info.Save(); _attrs.Save(); _contract?.Save();
        if (AutoAdjustOverall && !overallEdited && delta != 0) GameService.AdjustOverall(_db, PlayerId, delta);
        Load(); // show the stored values (e.g. the adjusted overall)
    }

    void Move()
    {
        if (MoveTarget == null) return;
        try
        {
            Flush();
            GameService.MovePlayer(_db, PlayerId, MoveTarget.Id);
            Load();
            _onStructureChanged();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            Dialogs.Error(e.Message);
        }
    }

    void MakeFreeAgent()
    {
        if (!Dialogs.Confirm(Loc.T("Release this player as free agent? The active contract is deleted."))) return;
        try
        {
            Flush();
            GameService.MakeFreeAgent(_db, PlayerId);
            Load();
            _onStructureChanged();
        }
        catch (FormatException e) { Dialogs.Error(e.Message); }
    }

    void AddContract()
    {
        try
        {
            GameService.CreateContract(_db, PlayerId, TeamId);
            Load();
        }
        catch (InvalidOperationException e) { Dialogs.Error(e.Message); }
    }
}
