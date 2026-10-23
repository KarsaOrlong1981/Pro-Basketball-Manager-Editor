namespace PbmEditor.Core;

public sealed record LeagueItem(long Id, string Name, long Type, long Country)
{
    public override string ToString() => Name;
}

public sealed record TeamItem(long Id, string Name, long LeagueId, string LeagueName)
{
    public override string ToString() => $"{Name}  [{LeagueName}]";
}

public sealed record PlayerItem(long Id, string First, string Last, double Rating, long Number, long Salary)
{
    public string Name => $"{First} {Last}".Trim();
}

/// <summary>Game-level operations on top of the generic editors (lookups, moving players, finance).</summary>
public static class GameService
{
    public const double RatingPerAttribute = 0.036; // from the modding guide: overall ~ mean of 26 attributes

    static string Dec(object? v) => v is string s && PbmCodec.TryDecode(s, out var d) ? d : v?.ToString() ?? "";

    // ---- lookups ----

    public static List<LeagueItem> Leagues(PbmDatabase db) =>
        db.Query("SELECT id, nom_championnat, type_competition, championnat_pays FROM DYN_championnat")
            .Select(r => new LeagueItem(r.Int("id"), Dec(r["nom_championnat"]), r.Int("type_competition"), r.Int("championnat_pays")))
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static List<TeamItem> Teams(PbmDatabase db)
    {
        var leagues = Leagues(db).ToDictionary(l => l.Id, l => l.Name);
        return db.Query("SELECT id_equipe, nom_equipe, championnat_id FROM DYN_equipe")
            .Select(r => new TeamItem(r.Int("id_equipe"), Dec(r["nom_equipe"]), r.Int("championnat_id"),
                leagues.TryGetValue(r.Int("championnat_id"), out var n) ? n : Loc.T("no league")))
            .OrderBy(t => t.LeagueName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static (int Year, int Month, int Day) CurrentDate(PbmDatabase db)
    {
        var r = db.Query("SELECT date_jour, date_mois, date_annee FROM DYN_date LIMIT 1").FirstOrDefault();
        return r == null ? (DateTime.Now.Year, 1, 1) : ((int)r.Int("date_annee"), (int)r.Int("date_mois"), (int)r.Int("date_jour"));
    }

    // choice lists are cached per database (cities alone are ~8k rows); cleared after each save
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PbmDatabase, Dictionary<string, IReadOnlyList<Choice>>> Cache = new();

    public static void InvalidateCache(PbmDatabase db) => Cache.Remove(db);

    static IReadOnlyList<Choice> Cached(PbmDatabase db, string key, Func<IReadOnlyList<Choice>> make)
    {
        var d = Cache.GetOrCreateValue(db);
        if (!d.TryGetValue(key, out var v)) d[key] = v = make();
        return v;
    }

    // choice providers used by FieldSet specs
    public static IReadOnlyList<Choice> LeagueChoices(PbmDatabase db) => Cached(db, "leagues", () =>
        Leagues(db).Select(l => new Choice(l.Id, $"{l.Name} ({l.Id})")).ToList());

    public static IReadOnlyList<Choice> LeagueChoicesWithNone(PbmDatabase db) =>
        LeagueChoices(db).Prepend(new Choice(0, Loc.T("(none)"))).ToList();

    public static IReadOnlyList<Choice> TeamChoices(PbmDatabase db) => Cached(db, "teams", () =>
        Teams(db).Select(t => new Choice(t.Id, $"{t.Name} ({t.Id})")).ToList());

    public static IReadOnlyList<Choice> TeamChoicesWithFreeAgent(PbmDatabase db) =>
        TeamChoices(db).Prepend(new Choice(0, Loc.T("(free agent)"))).ToList();

    public static IReadOnlyList<Choice> CountryChoices(PbmDatabase db) => Cached(db, "countries", () =>
        db.Query("SELECT id, nom_pays FROM DYN_pays ORDER BY nom_pays").Select(r => new Choice(r.Int("id"), r.Str("nom_pays"))).ToList());

    public static IReadOnlyList<Choice> CountryChoicesWithNone(PbmDatabase db) =>
        CountryChoices(db).Prepend(new Choice(0, Loc.T("(none)"))).ToList();

    public static IReadOnlyList<Choice> CityChoices(PbmDatabase db) => Cached(db, "cities", () =>
        db.Query("SELECT id, nom_ville_en FROM DYN_ville ORDER BY nom_ville_en").Select(r => new Choice(r.Int("id"), $"{r.Str("nom_ville_en")} ({r.Int("id")})"))
            .Prepend(new Choice(0, Loc.T("(none)"))).ToList());

    public static IReadOnlyList<Choice> HairstyleChoices(PbmDatabase db) => Cached(db, "hair", () =>
        db.Query("SELECT DISTINCT type_coiffure, nom FROM STA_coiffures ORDER BY type_coiffure")
            .Select(r => new Choice(r.Int("type_coiffure"), $"{r.Int("type_coiffure")} - {r.Str("nom")}")).ToList());

    // ---- players ----

    public static List<PlayerItem> PlayersOfTeam(PbmDatabase db, long teamId) =>
        db.Query(@"SELECT j.joueur_id, j.joueur_prenom, j.joueur_nom, j.moyenne, j.numero_actuel,
                          (SELECT c.salaire FROM DYN_contrat c WHERE c.id_joueur=j.joueur_id AND c.accepte=0 AND c.id_equipe=@p0 LIMIT 1) AS sal
                   FROM DYN_joueur j WHERE j.team_id=@p0", teamId)
            .Select(r => new PlayerItem(r.Int("joueur_id"), Dec(r["joueur_prenom"]), Dec(r["joueur_nom"]), r.Dbl("moyenne"), r.Int("numero_actuel"), r.Int("sal")))
            .OrderByDescending(p => p.Rating).ToList();

    public static List<PlayerItem> FreeAgents(PbmDatabase db) =>
        db.Query("SELECT joueur_id, joueur_prenom, joueur_nom, moyenne, numero_actuel FROM DYN_joueur WHERE team_id=0 ORDER BY moyenne DESC LIMIT 20000")
            .Select(r => new PlayerItem(r.Int("joueur_id"), Dec(r["joueur_prenom"]), Dec(r["joueur_nom"]), r.Dbl("moyenne"), r.Int("numero_actuel"), 0)).ToList();

    public static long NextFreeNumber(PbmDatabase db, long teamId)
    {
        var used = db.Query("SELECT numero_actuel FROM DYN_joueur WHERE team_id=@p0", teamId).Select(r => r.Int("numero_actuel")).ToHashSet();
        for (long n = 0; n < 100; n++) if (!used.Contains(n)) return n;
        return 99;
    }

    /// <summary>Moves a player to another team: updates team and number, and re-points or creates the active contract.</summary>
    public static void MovePlayer(PbmDatabase db, long playerId, long newTeamId, long? number = null)
    {
        if (newTeamId == 0) { MakeFreeAgent(db, playerId); return; }
        var num = number ?? NextFreeNumber(db, newTeamId);
        if (db.Scalar("SELECT 1 FROM DYN_joueur WHERE team_id=@p0 AND numero_actuel=@p1 AND joueur_id<>@p2", newTeamId, num, playerId) != null)
            throw new InvalidOperationException(Loc.F("Jersey number {0} is already taken in the target team.", num));

        var active = db.Query("SELECT id FROM DYN_contrat WHERE id_joueur=@p0 AND accepte=0", playerId);
        db.Execute("UPDATE DYN_joueur SET team_id=@p0, numero_actuel=@p1 WHERE joueur_id=@p2", newTeamId, num, playerId);
        if (active.Count > 0)
            db.Execute("UPDATE DYN_contrat SET id_equipe=@p0 WHERE id_joueur=@p1 AND accepte=0", newTeamId, playerId);
        else
            CreateContract(db, playerId, newTeamId);
    }

    public static void MakeFreeAgent(PbmDatabase db, long playerId)
    {
        db.Execute("UPDATE DYN_joueur SET team_id=0, numero_actuel=255 WHERE joueur_id=@p0", playerId);
        db.Execute("DELETE FROM DYN_contrat WHERE id_joueur=@p0 AND accepte=0", playerId);
    }

    /// <summary>New active contract, cloned from the player's latest contract or any active contract.</summary>
    public static long CreateContract(PbmDatabase db, long playerId, long teamId)
    {
        var template = db.Query("SELECT * FROM DYN_contrat WHERE id_joueur=@p0 ORDER BY id DESC LIMIT 1", playerId).FirstOrDefault()
                       ?? db.Query("SELECT * FROM DYN_contrat WHERE id_equipe=@p0 AND accepte=0 LIMIT 1", teamId).FirstOrDefault()
                       ?? db.Query("SELECT * FROM DYN_contrat WHERE accepte=0 LIMIT 1").FirstOrDefault()
                       ?? throw new InvalidOperationException(Loc.T("No contract template found to clone."));
        var newId = Convert.ToInt64(db.Scalar("SELECT COALESCE(MAX(id),0)+1 FROM DYN_contrat"));
        var cols = template.Keys.ToList();
        var vals = cols.Select(c => c switch
        {
            "id" => (object?)newId, "id_joueur" => playerId, "id_equipe" => teamId, "accepte" => 0L, _ => template[c],
        }).ToArray();
        db.Execute($"INSERT INTO DYN_contrat ({string.Join(",", cols.Select(c => $"\"{c}\""))}) VALUES ({string.Join(",", cols.Select((_, i) => "@p" + i))})", vals);
        return newId;
    }

    public static long? ActiveContractId(PbmDatabase db, long playerId, long teamId) =>
        db.Scalar("SELECT id FROM DYN_contrat WHERE id_joueur=@p0 AND accepte=0 AND id_equipe=@p1 LIMIT 1", playerId, teamId) is { } v ? Convert.ToInt64(v) : null;

    // ---- teams / finance ----

    public static void SetTeamLeague(PbmDatabase db, long teamId, long leagueId) =>
        db.Execute("UPDATE DYN_equipe SET championnat_id=@p0 WHERE id_equipe=@p1", leagueId, teamId);

    public sealed record Finance(long MonthlyPlayers, long MonthlyStaff, long Budget)
    {
        public long Monthly => MonthlyPlayers + MonthlyStaff;
        /// <summary>Guide assumption: season cost = 13 x monthly payroll (derived from observed numbers, unverified).</summary>
        public long SeasonCostEstimate => Monthly * 13;
        public long BudgetAfterSeason => Budget - SeasonCostEstimate;
    }

    public static Finance TeamFinance(PbmDatabase db, long teamId)
    {
        long pl = Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(salaire),0) FROM DYN_contrat WHERE id_equipe=@p0 AND accepte=0", teamId));
        long st = Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(salaire),0) FROM DYN_contrat_person WHERE id_equipe=@p0 AND accepte=0", teamId));
        // saves keep the live budget in DYN_budget, mods only in DYN_equipe
        var live = db.GetTable("DYN_budget") != null ? db.Scalar("SELECT budget_actuel FROM DYN_budget WHERE id_equipe=@p0", teamId) : null;
        var b = live ?? db.Scalar("SELECT budget FROM DYN_equipe WHERE id_equipe=@p0", teamId) ?? 0L;
        return new Finance(pl, st, Convert.ToInt64(b));
    }

    /// <summary>Adds an arena row for a team that has none.</summary>
    public static long CreateArena(PbmDatabase db, long teamId, string name)
    {
        var id = Convert.ToInt64(db.Scalar("SELECT COALESCE(MAX(id_facility),0)+1 FROM DYN_facilities"));
        db.Execute("INSERT INTO DYN_facilities (id_facility,id_team,name_arena,capacity) VALUES (@p0,@p1,@p2,@p3)", id, teamId, name,
            db.Scalar("SELECT capacite_salle FROM DYN_equipe WHERE id_equipe=@p0", teamId) ?? 3000L);
        return id;
    }

    /// <summary>Shifts both stored overall ratings by the change in attribute sum (guide: 0.036 per attribute point).</summary>
    public static void AdjustOverall(PbmDatabase db, long playerId, double attributeDelta)
    {
        var d = Math.Round(attributeDelta * RatingPerAttribute, 2);
        if (d == 0) return;
        db.Execute("UPDATE DYN_joueur SET moyenne=ROUND(moyenne+@p0,2) WHERE joueur_id=@p1", d, playerId);
        db.Execute("UPDATE DYN_joueur_attributes SET moyenne=ROUND(COALESCE(moyenne,0)+@p0,2) WHERE joueur_id=@p1", d, playerId);
    }
}
