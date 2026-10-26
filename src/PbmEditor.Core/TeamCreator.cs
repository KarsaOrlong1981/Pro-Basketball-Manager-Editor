namespace PbmEditor.Core;

/// <summary>Input for a new club. Everything not listed is copied from the template team.</summary>
public sealed record NewTeam(
    string Name, long LeagueId, long CountryId, long TemplateTeamId, long CityId, string ArenaName, int Capacity,
    long ColorHome, long ColorAway, long RealBudget, int Reputation, int FoundedYear);

/// <summary>Creates a club: team row, arena, head coach and assistant (cloned from the template team's staff).</summary>
static class TeamCreator
{
    /// <summary>Ratio of the game's budget to the "real" budget (all default teams use 1.20945).</summary>
    const double BudgetFactor = 1.20945;

    static long Money(double v) => (long)Math.Round(v / 10) * 10;

    static string Abbreviation(string name)
    {
        var letters = new string(name.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant();
        return letters.PadRight(3, 'X');
    }

    /// <summary>Writes the club into the open transaction and returns the new team ID.</summary>
    public static long Create(PbmDatabase db, NewTeam t, NamePool pool, Random rng)
    {
        var template = db.Query("SELECT * FROM DYN_equipe WHERE id_equipe=@p0", t.TemplateTeamId).FirstOrDefault()
                       ?? throw new InvalidOperationException(Loc.T("Template team not found."));
        var league = db.Query("SELECT game_harmonized_median_budget FROM DYN_championnat WHERE id=@p0", t.LeagueId).FirstOrDefault()
                     ?? throw new InvalidOperationException(Loc.T("League not found."));
        var median = Math.Max(1, league.Dbl("game_harmonized_median_budget"));

        var id = RowCopy.NextId(db, "DYN_equipe", "id_equipe");
        var facility = RowCopy.NextId(db, "DYN_facilities", "id_facility");
        var budget = Money(t.RealBudget * BudgetFactor);
        var name = PbmCodec.Encode(t.Name);

        var team = RowCopy.Copy(template);
        team["championnat_id"] = t.LeagueId; team["id_equipe"] = id;
        team["nom_equipe"] = name; team["nom_court_equipe"] = name; team["fake_name"] = "";
        team["abrev_logo"] = PbmCodec.Encode(id.ToString()); team["abrev_nom"] = PbmCodec.Encode(Abbreviation(t.Name));
        team["creation"] = t.FoundedYear; team["pays_id"] = t.CountryId; team["president"] = "";
        team["couleur_home"] = t.ColorHome; team["couleur_away"] = t.ColorAway; team["color_3rd"] = null;
        team["ennemie_1"] = 0L; team["ennemie_2"] = 0L; team["supporter"] = 0L;
        team["renomee"] = t.Reputation; team["renomee_mondiale"] = (long)Math.Round(t.Reputation * 0.4);
        team["capacite_salle"] = t.Capacity; team["id_ville"] = t.CityId; team["id_salle"] = facility;
        team["equipe_associee"] = 0L; team["type_association"] = 0L; team["masse_salariale"] = 0L; team["masse_salariale_staff"] = null;
        team["budget"] = budget; team["real_budget"] = t.RealBudget;
        team["budget_coefficient_compared_league"] = budget / median; team["id_competition_budget_compared_to"] = t.LeagueId;
        RowCopy.Insert(db, "DYN_equipe", team);

        CreateArena(db, template, id, facility, t);
        CreateStaff(db, t.TemplateTeamId, id, pool, rng);
        db.Note(Loc.F("{0}: new team created in {1} (ID {2})", t.Name, ChangeNames.League(db, t.LeagueId), id));
        return id;
    }

    static void CreateArena(PbmDatabase db, Row templateTeam, long teamId, long facilityId, NewTeam t)
    {
        if (db.GetTable("DYN_facilities") == null) return;
        var source = db.Query("SELECT * FROM DYN_facilities WHERE id_team=@p0", templateTeam.Int("id_equipe")).FirstOrDefault();
        var row = source != null ? RowCopy.Copy(source) : new Dictionary<string, object?> { ["type_arena"] = 1L };
        row["id_facility"] = facilityId; row["id_team"] = teamId; row["name_arena"] = t.ArenaName; row["id_city"] = t.CityId;
        RowCopy.Insert(db, "DYN_facilities", row);
    }

    /// <summary>One head coach and one assistant: the template team's first active staff contract of each role, with a new person.</summary>
    static void CreateStaff(PbmDatabase db, long templateTeamId, long teamId, NamePool pool, Random rng)
    {
        if (db.GetTable("DYN_person") == null || db.GetTable("DYN_contrat_person") == null) return;
        var (year, month, day) = GameService.CurrentDate(db);
        foreach (var job in new[] { 0, 1 })
        {
            var contract = db.Query("SELECT * FROM DYN_contrat_person WHERE id_equipe=@p0 AND accepte=0 AND job_type=@p1 ORDER BY id LIMIT 1", templateTeamId, job).FirstOrDefault();
            var person = contract == null ? null
                : db.Query("SELECT * FROM DYN_person WHERE id_person=@p0", contract.Int("id_person")).FirstOrDefault();
            if (contract == null || person == null) continue;

            var personId = RowCopy.NextId(db, "DYN_person", "id_person");
            var birthYear = (int)person.Int("annee_naissance") + rng.Next(-4, 5);
            var p = RowCopy.Copy(person);
            p["id_person"] = personId; p["equipe_id"] = teamId; p["nom"] = PbmCodec.Encode(pool.LastName(rng)); p["prenom"] = PbmCodec.Encode(pool.FirstName(rng));
            p["annee_naissance"] = birthYear; p["jour_naissance"] = rng.Next(1, 29); p["mois_naissance"] = rng.Next(1, 13); p["age"] = year - birthYear;
            p["nom_chinese"] = null; p["prenom_chinese"] = null; p["id_joueur"] = 0L; p["id_birth_city"] = 0L; p["id_external"] = null;
            RowCopy.Insert(db, "DYN_person", p);

            var c = RowCopy.Copy(contract);
            c["id"] = RowCopy.NextId(db, "DYN_contrat_person", "id"); c["id_person"] = personId; c["id_equipe"] = teamId;
            var end = new DateTime(year, month, day).AddYears(2);
            c["debut_jour"] = day; c["debut_mois"] = month; c["debut_annee"] = year;
            c["emission_jour"] = day; c["emission_mois"] = month; c["emission_annee"] = year;
            c["fin_jour"] = end.Day; c["fin_mois"] = end.Month; c["fin_annee"] = end.Year;
            c["salaire"] = Money(contract.Dbl("salaire") * 0.6);
            RowCopy.Insert(db, "DYN_contrat_person", c);
        }
    }
}
