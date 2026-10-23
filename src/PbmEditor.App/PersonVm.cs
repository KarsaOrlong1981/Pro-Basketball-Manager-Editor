using System.Windows.Input;
using System.Windows.Media.Imaging;
using PbmEditor.Core;

namespace PbmEditor.App;

/// <summary>Editor for one staff member (coach, scout, physio, ...): identity, look, skills, contract, photo.</summary>
public sealed class PersonVm : ObservableObject
{
    readonly PbmDatabase _db;
    readonly Action _onStructureChanged;
    RowEditor _person = null!;
    RowEditor? _contract;
    TeamItem? _moveTarget;
    BitmapSource? _avatar;

    public long PersonId { get; }
    public long TeamId { get; private set; }
    public List<Field> Fields { get; private set; } = new();
    public ImageSlot Photo { get; }
    public string Header { get; private set; } = "";
    public string AvatarInfo { get; private set; } = "";
    public BitmapSource? Avatar { get => _avatar; private set => Set(ref _avatar, value); }
    public IReadOnlyList<TeamItem> MoveTargets { get; }
    public TeamItem? MoveTarget { get => _moveTarget; set => Set(ref _moveTarget, value); }
    public bool IsDirty => Fields.Any(f => f.Dirty);
    public ICommand MoveCommand { get; }
    public ICommand ReleaseCommand { get; }

    public PersonVm(PbmDatabase db, ImageStore store, long personId, long teamId, IReadOnlyList<TeamItem> teams, Action onStructureChanged)
    {
        _db = db; PersonId = personId; TeamId = teamId; MoveTargets = teams; _onStructureChanged = onStructureChanged;
        Photo = new ImageSlot(store, "Photo (140x160)", "coach_faces", "{0}.png", ImageKind.Face, previewHeight: 160);
        Photo.SetId(personId);
        MoveCommand = new RelayCommand(Move, () => MoveTarget != null);
        ReleaseCommand = new RelayCommand(Release);
        Load();
    }

    void Load()
    {
        _person = new RowEditor(_db, "DYN_person", "id_person", PersonId, FieldConfigs.Person());
        TeamId = _person["equipe_id"] is { } t && long.TryParse(t.Text, out var tid) ? tid : TeamId;
        var cid = TeamId == 0 ? null : GameService.ActiveStaffContractId(_db, PersonId, TeamId);
        _contract = cid == null ? null : new RowEditor(_db, "DYN_contrat_person", "id", cid.Value, FieldConfigs.PersonContract());

        Fields = _person.Fields.Concat(_contract?.Fields ?? new List<Field>()).ToList();
        var role = _contract?["job_type"] is { } jt && long.TryParse(jt.Text, out var j) ? GameService.JobTypeName(j) : Loc.T("no contract");
        Header = $"{_person["prenom"]?.Text} {_person["nom"]?.Text}  -  {role}";
        Notify(nameof(Fields)); Notify(nameof(Header));

        FilterHairstyles();
        foreach (var f in Fields.Where(f => f.Name is "sexe" or "couleur_peau" or "type_coiffure" or "couleur_cheveux" or "type_barbe"))
            f.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(Field.Text)) return;
                if (f.Name == "sexe") FilterHairstyles();
                RenderAvatar();
            };
        RenderAvatar();
    }

    /// <summary>Only hairstyles that exist for the person's gender are offered.</summary>
    void FilterHairstyles()
    {
        if (_person["type_coiffure"] is not { } hair) return;
        long.TryParse(hair.Text, out var current);
        var female = _person["sexe"]?.Text == "1";
        hair.ReplaceChoices(GameService.HairstyleChoicesFor(_db, female, current));
    }

    /// <summary>Live preview of the game's 2D coach avatar (jacket and tie) for the current appearance values.</summary>
    void RenderAvatar()
    {
        static int N(Field? f) => int.TryParse(f?.Text, out var v) ? v : 0;
        string? home = null, away = null;
        if (TeamId != 0 && _db.Query("SELECT couleur_home, couleur_away FROM DYN_equipe WHERE id_equipe=@p0", TeamId).FirstOrDefault() is { } t)
        {
            home = t["couleur_home"]?.ToString(); away = t["couleur_away"]?.ToString();
        }
        Avatar = AvatarKit.Render(N(_person["sexe"]) == 1, home, away, N(_person["couleur_peau"]), N(_person["couleur_cheveux"]),
            N(_person["type_barbe"]), N(_person["type_coiffure"]), staff: true, hasTeam: TeamId != 0);
        AvatarInfo = Avatar == null ? Loc.T("Game images not found: avatar preview unavailable.")
            : Loc.T("The game draws this avatar for staff without a photo file.");
        if (Avatar != null)
        {
            var note = AvatarKit.HairNote(N(_person["sexe"]) == 1, N(_person["type_coiffure"]), N(_person["type_barbe"]));
            if (note.Length > 0) AvatarInfo += "\n" + note;
        }
        Notify(nameof(AvatarInfo));
    }

    public void Flush()
    {
        if (!IsDirty) return;
        _person.Save(); _contract?.Save();
        Load();
    }

    void Move()
    {
        if (MoveTarget == null) return;
        try
        {
            Flush();
            GameService.MoveStaff(_db, PersonId, MoveTarget.Id);
            Load();
            _onStructureChanged();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { Dialogs.Error(e.Message); }
    }

    void Release()
    {
        if (!Dialogs.Confirm(Loc.T("Release this staff member? The active contract is deleted."))) return;
        try
        {
            Flush();
            GameService.MoveStaff(_db, PersonId, 0);
            Load();
            _onStructureChanged();
        }
        catch (FormatException e) { Dialogs.Error(e.Message); }
    }
}
