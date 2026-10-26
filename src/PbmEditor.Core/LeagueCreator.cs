namespace PbmEditor.Core;

/// <summary>
/// Input for a new league. The structure (phases, rules, prize money) is cloned from a single-group template league.
/// Teams are generated with rosters and staff; <see cref="TeamNames"/> may be shorter than <see cref="TeamCount"/> (the rest is generated).
/// </summary>
public sealed record NewLeague(
    string Name, string Abbreviation, long CountryId, long TemplateLeagueId, int Division, int Reputation,
    int TeamCount, long PromoteToLeagueId, int Promoted, int Relegated,
    double TeamLevel, int Seed, IReadOnlyList<string>? TeamNames = null, int RosterSize = 13, double BudgetScale = 0.7);

public sealed record LeagueResult(long LeagueId, IReadOnlyList<long> TeamIds, IReadOnlyList<string> Warnings);

/// <summary>Creates a complete league: league row, rules, phases, prize money, promotion/relegation and the clubs with rosters.</summary>
public static class LeagueCreator
{
    // tables keyed by the league that this creator does not copy; a template that uses them is refused instead of half-copied
    static readonly (string Table, string Column)[] UnsupportedByLeague =
    {
        ("STA_competition_objectifs", "id_competition"), ("STA_competition_classement_final_impose", "id_competition"),
        ("STA_competition_equipes_non_relegables", "id_competition"), ("STA_competition_recompenses_informations", "id_competition"),
        ("STA_Equipes_Division", "id_competition"), ("STA_competition_cursus_informations", "id_competition"),
        ("STA_competition_jeunes_budget_repartition", "id_competition"),
    };

    static readonly string[] UnsupportedByPhase =
    {
        "STA_competition_phase_brackets", "STA_competition_phase_equipes_qualifiees", "STA_phase_forced_teams", "STA_competition_coupure",
        "STA_competition_a_attendre", "STA_competition_schedules_to_generate_after", "STA_phase_end_meeting_national_team", "STA_phase_start_meeting_national_team",
    };

    // child tables of a phase that are copied with the new phase ID
    static readonly string[] PhaseChildren = { "STA_competition_phase_rounds_details", "STA_competition_jours_matchs", "STA_competition_phase_nom_tours" };

    static readonly long[] Colors =
    {
        Argb(0xE00000), Argb(0xFFFFFF), Argb(0x0033A0), Argb(0xFFD100), Argb(0x00A650), Argb(0x111111),
        Argb(0xFF7A00), Argb(0x7B1FA2), Argb(0x00AEEF), Argb(0x8B0000), Argb(0x006400), Argb(0xC0C0C0),
    };

    static long Argb(int rgb) => (int)(0xFF000000u | (uint)rgb);

    static string Dec(object? v) => v is string s && PbmCodec.TryDecode(s, out var d) ? d : v?.ToString() ?? "";

    /// <summary>Suggested team level: the ten-best average of the league above, one point lower (a lower division is weaker).</summary>
    public static double SuggestTeamLevel(PbmDatabase db, long upperLeagueId)
    {
        var perTeam = db.Query(@"SELECT j.team_id, j.moyenne FROM DYN_joueur j
                                 JOIN DYN_equipe e ON e.id_equipe=j.team_id WHERE e.championnat_id=@p0 ORDER BY j.team_id, j.moyenne DESC", upperLeagueId)
            .GroupBy(r => r.Int("team_id")).Select(g => g.Take(10).Average(r => r.Dbl("moyenne"))).ToList();
        return perTeam.Count == 0 ? 8.0 : Math.Round(perTeam.Average() - 0.9, 1);
    }

    public static string? Validate(PbmDatabase db, NewLeague l)
    {
        if (l.Name.Trim().Length == 0 || l.Abbreviation.Trim().Length == 0) return Loc.T("Please enter a name and an abbreviation for the league.");
        if (l.TeamCount is < 8 or > 30 || l.TeamCount % 2 != 0) return Loc.T("The number of teams must be even and between 8 and 30.");
        if (l.Promoted < 0 || l.Promoted > l.TeamCount / 2 || l.Relegated < 0 || l.Relegated > l.TeamCount / 2)
            return Loc.T("Promoted and relegated teams cannot exceed half of the league.");
        if (l.RosterSize is < RosterGenerator.MinSize or > RosterGenerator.MaxSize)
            return Loc.F("The roster size must be between {0} and {1}.", RosterGenerator.MinSize, RosterGenerator.MaxSize);
        if (l.TeamNames is { } names && names.Count > l.TeamCount) return Loc.T("There are more team names than teams.");
        if (db.Scalar("SELECT 1 FROM DYN_pays WHERE id=@p0", l.CountryId) == null) return Loc.T("Country not found.");
        if (l.PromoteToLeagueId == 0 && l.Promoted > 0) return Loc.T("Choose the league the promoted teams move up to.");
        if (l.PromoteToLeagueId != 0 && db.Scalar("SELECT 1 FROM DYN_championnat WHERE id=@p0", l.PromoteToLeagueId) == null) return Loc.T("League not found.");

        if (db.Scalar("SELECT 1 FROM DYN_championnat WHERE id=@p0", l.TemplateLeagueId) == null) return Loc.T("Template league not found.");
        var phases = db.Query("SELECT * FROM STA_competition_phase WHERE id_competition=@p0 ORDER BY ordre", l.TemplateLeagueId);
        if (phases.Count == 0 || phases[0].Int("nombre_groupes") != 1)
            return Loc.T("The template league must have a single group in its first phase (for example German League 2).");
        if (db.Scalar("SELECT 1 FROM DYN_equipe WHERE championnat_id=@p0", l.TemplateLeagueId) == null) return Loc.T("The template league has no teams to use as a model.");
        if (phases.Any(p => p.Int("nombre_equipes_qualifiees") > l.TeamCount)) return Loc.T("The league has too few teams for the template's playoffs.");

        foreach (var (table, column) in UnsupportedByLeague)
            if (db.GetTable(table) != null && db.Scalar($"SELECT 1 FROM \"{table}\" WHERE \"{column}\"=@p0", l.TemplateLeagueId) != null)
                return Loc.T("The template league uses a feature that is not supported yet. Pick a simpler template league.");
        var phaseIds = string.Join(",", phases.Select(p => p.Int("id")));
        foreach (var table in UnsupportedByPhase)
            if (db.GetTable(table) != null && db.Scalar($"SELECT 1 FROM \"{table}\" WHERE id_phase IN ({phaseIds})") != null)
                return Loc.T("The template league uses a feature that is not supported yet. Pick a simpler template league.");
        return null;
    }

    /// <summary>Writes the league and its teams into the open transaction.</summary>
    public static LeagueResult Create(PbmDatabase db, NewLeague l)
    {
        var error = Validate(db, l);
        if (error != null) throw new InvalidOperationException(error);
        return RowCopy.Atomic(db, "new_league", () => Write(db, l));
    }

    static LeagueResult Write(PbmDatabase db, NewLeague l)
    {
        var rng = new Random(l.Seed);
        var (year, _, _) = GameService.CurrentDate(db);
        var template = db.Query("SELECT * FROM DYN_championnat WHERE id=@p0", l.TemplateLeagueId).Single();
        var id = RowCopy.NextId(db, "DYN_championnat", "id");
        var name = PbmCodec.Encode(l.Name.Trim());
        var country = db.Query("SELECT id_continent FROM DYN_pays WHERE id=@p0", l.CountryId).Single();

        // ---- league row ----
        var row = RowCopy.Copy(template);
        row["id"] = id; row["championnat_pays"] = l.CountryId; row["id_continent"] = country.Int("id_continent");
        row["nom_ligue"] = name; row["nom_championnat"] = name; row["fake_name"] = name; row["nom_championnat_original"] = "";
        row["abreviation_championnat"] = PbmCodec.Encode(l.Abbreviation.Trim()); row["abreviation_logo"] = PbmCodec.Encode(id.ToString());
        row["division"] = l.Division; row["reputation"] = l.Reputation; row["creation"] = year;
        row["competition_associee"] = 0L; row["type_association"] = 0L;
        row["nom_ligue_chinese"] = ""; row["nom_championnat_chinese"] = ""; row["abreviation_championnat_chinese"] = "";
        foreach (var c in new[] { "min_budget", "min_ms", "game_harmonized_median_budget", "real_median_budget" })
            row[c] = (long)Math.Round(template.Dbl(c) * l.BudgetScale);
        RowCopy.Insert(db, "DYN_championnat", row);

        // ---- rules (no youth teams: the new clubs have none) ----
        var rules = db.Query("SELECT * FROM STA_competition_regles WHERE ligue_id=@p0", l.TemplateLeagueId).FirstOrDefault();
        if (rules != null)
        {
            var r = RowCopy.Copy(rules);
            r["ligue_id"] = id; r["equipe_espoirs"] = 0L;
            RowCopy.Insert(db, "STA_competition_regles", r);
        }

        // ---- phases and their child rows ----
        foreach (var phase in db.Query("SELECT * FROM STA_competition_phase WHERE id_competition=@p0 ORDER BY ordre", l.TemplateLeagueId))
        {
            var oldPhase = phase.Int("id");
            var newPhase = RowCopy.NextId(db, "STA_competition_phase", "id");
            var p = RowCopy.Copy(phase);
            p["id"] = newPhase; p["id_competition"] = id;
            if (phase.Int("ordre") == 0) { p["nombre_equipe_total"] = l.TeamCount; p["nombre_equipe_par_groupe"] = l.TeamCount; }
            RowCopy.Insert(db, "STA_competition_phase", p);
            foreach (var table in PhaseChildren)
            {
                if (db.GetTable(table) == null) continue;
                foreach (var child in db.Query($"SELECT * FROM \"{table}\" WHERE id_phase=@p0", oldPhase))
                {
                    var c = RowCopy.Copy(child);
                    c["id"] = RowCopy.NextId(db, table, "id"); c["id_phase"] = newPhase;
                    RowCopy.Insert(db, table, c);
                }
            }
        }

        // ---- prize money (scaled with the budgets) ----
        foreach (var prize in db.Query("SELECT * FROM STA_competition_prize_money WHERE id_competition=@p0", l.TemplateLeagueId))
        {
            var c = RowCopy.Copy(prize);
            c["id"] = RowCopy.NextId(db, "STA_competition_prize_money", "id"); c["id_competition"] = id;
            c["amount_in_euros"] = (long)Math.Round(prize.Dbl("amount_in_euros") * l.BudgetScale);
            RowCopy.Insert(db, "STA_competition_prize_money", c);
        }

        WriteRelegations(db, id, l);
        var teamIds = WriteTeams(db, id, l, rng);
        var warnings = Warnings(db, l, id);

        db.Note(Loc.F("{0}: new league created with {1} teams (ID {2})", l.Name.Trim(), teamIds.Count, id));
        return new LeagueResult(id, teamIds, warnings);
    }

    /// <summary>Promotion: final standing 1..n to the league above. Relegation: the last n of the first phase (destination stays 0, like in the game data).</summary>
    static void WriteRelegations(PbmDatabase db, long leagueId, NewLeague l)
    {
        if (db.GetTable("STA_competition_relegations") == null) return;
        void Add(long type, long phaseOrder, long position, long positionType, long future) => RowCopy.Insert(db, "STA_competition_relegations", new Dictionary<string, object?>
        {
            ["id"] = RowCopy.NextId(db, "STA_competition_relegations", "id"), ["id_competition"] = leagueId, ["type"] = type, ["ordre_phase"] = phaseOrder,
            ["id_conference"] = 0L, ["groupe"] = 0L, ["position"] = position, ["type_position"] = positionType, ["id_competition_future"] = future,
        });
        for (var i = 1; i <= l.Promoted; i++) Add(1, 255, i, 0, l.PromoteToLeagueId);
        for (var i = 0; i < l.Relegated; i++) Add(0, 0, i, 1, 0);

        // the league above sends its relegated teams here (the French pyramid in the game data sets the destination on both sides);
        // a destination that already points to another league stays untouched
        if (l.PromoteToLeagueId != 0 && db.Execute(
                "UPDATE STA_competition_relegations SET id_competition_future=@p0 WHERE id_competition=@p1 AND type=0 AND id_competition_future=0",
                leagueId, l.PromoteToLeagueId) > 0)
            db.Note(Loc.F("{0}: relegated teams now move down to {1}", ChangeNames.League(db, l.PromoteToLeagueId), l.Name.Trim()));
    }

    static List<long> WriteTeams(PbmDatabase db, long leagueId, NewLeague l, Random rng)
    {
        var templateTeam = db.Scalar("SELECT id_equipe FROM DYN_equipe WHERE championnat_id=@p0 ORDER BY id_equipe LIMIT 1", l.TemplateLeagueId);
        var templateTeamId = Convert.ToInt64(templateTeam);
        var year = GameService.CurrentDate(db).Year;
        var pool = NamePool.Load(db, l.CountryId);
        var cities = Cities(db, l.CountryId);
        var realMedian = db.Query("SELECT real_median_budget FROM DYN_championnat WHERE id=@p0", leagueId).Single().Dbl("real_median_budget");

        var levels = Enumerable.Range(0, l.TeamCount).Select(i => l.TeamCount == 1 ? 0 : -0.6 + 1.2 * i / (l.TeamCount - 1)).OrderBy(_ => rng.Next()).ToList();
        var ids = new List<long>();
        var budgets = new List<long>();
        for (var i = 0; i < l.TeamCount; i++)
        {
            var wanted = l.TeamNames is { } n && i < n.Count ? n[i].Trim() : "";
            var city = (wanted.Length > 0 ? cities.Take(wanted) : null) ?? cities.TakeRandom(rng);
            var teamName = wanted.Length > 0 ? wanted : $"{city.Name} Baskets";
            var colors = Colors.OrderBy(_ => rng.Next()).Take(2).ToArray();
            var realBudget = (long)Math.Round(realMedian * (0.65 + 0.8 * rng.NextDouble()) / 10000) * 10000;

            var teamId = TeamCreator.Create(db, new NewTeam(teamName, leagueId, l.CountryId, templateTeamId, city.Id, $"{city.Name}'s arena",
                rng.Next(3, 16) * 100, colors[0], colors[1], realBudget, 45, rng.Next(1950, year - 5)), pool, rng);
            RosterGenerator.Fill(db, teamId, l.CountryId, pool, rng, l.TeamLevel + levels[i], realBudget * 45 / 100 / 13, l.RosterSize);
            ids.Add(teamId);
            budgets.Add(Convert.ToInt64(db.Scalar("SELECT budget FROM DYN_equipe WHERE id_equipe=@p0", teamId)));
        }
        // the league minimum is the cheapest club (as in the default data)
        db.Execute("UPDATE DYN_championnat SET min_budget=@p0, min_ms=@p0 WHERE id=@p1", budgets.Min(), leagueId);
        return ids;
    }

    sealed record City(long Id, string Name, string NameDe);

    /// <summary>Cities of the country. Cities that do not host a club yet are preferred.</summary>
    sealed class CityPool
    {
        readonly List<City> _all, _free;
        public CityPool(List<City> all, List<City> free) { _all = all; _free = free; }

        /// <summary>The longest city whose name appears in the team name, or null.</summary>
        public City? Take(string teamName)
        {
            var hit = _all.Where(c => c.Name.Length >= 3 && teamName.Contains(c.Name, StringComparison.CurrentCultureIgnoreCase)
                                      || c.NameDe.Length >= 3 && teamName.Contains(c.NameDe, StringComparison.CurrentCultureIgnoreCase))
                          .OrderByDescending(c => c.Name.Length).FirstOrDefault();
            if (hit != null) _free.Remove(hit);
            return hit;
        }

        public City TakeRandom(Random rng)
        {
            var list = _free.Count > 0 ? _free : _all;
            var city = list[rng.Next(list.Count)];
            _free.Remove(city);
            return city;
        }
    }

    static CityPool Cities(PbmDatabase db, long country)
    {
        var all = db.Query("SELECT id, nom_ville_en, nom_ville_de FROM DYN_ville WHERE id_pays=@p0", country)
            .Select(r => new City(r.Int("id"), Dec(r["nom_ville_en"]), Dec(r["nom_ville_de"]))).Where(c => c.Name.Length > 0).ToList();
        if (all.Count == 0) throw new InvalidOperationException(Loc.T("The country has no cities. Pick another country."));
        var used = db.Query("SELECT id_ville FROM DYN_equipe WHERE id_ville IS NOT NULL").Select(r => r.Int("id_ville")).ToHashSet();
        return new CityPool(all, all.Where(c => !used.Contains(c.Id)).ToList());
    }

    /// <summary>Hints that need a human decision, e.g. promotion and relegation counts that do not balance.</summary>
    static List<string> Warnings(PbmDatabase db, NewLeague l, long leagueId)
    {
        var warnings = new List<string>();
        if (l.PromoteToLeagueId != 0 && db.GetTable("STA_competition_relegations") != null)
        {
            var down = db.Query("SELECT COUNT(*) n FROM STA_competition_relegations WHERE id_competition=@p0 AND type=0", l.PromoteToLeagueId).Single().Int("n");
            var other = db.Query("SELECT COUNT(*) n FROM STA_competition_relegations WHERE id_competition=@p0 AND type=0 AND id_competition_future NOT IN (0, @p1)",
                l.PromoteToLeagueId, leagueId).Single().Int("n");
            if (other > 0)
                warnings.Add(Loc.F("{0} already sends its relegated teams to another league. Check its relegation rows.", ChangeNames.League(db, l.PromoteToLeagueId)));
            if (down != l.Promoted)
                warnings.Add(Loc.F("{0} relegates {1} team(s) but {2} team(s) are promoted into it. Adjust the numbers or the relegation rows so the league sizes stay constant.",
                    ChangeNames.League(db, l.PromoteToLeagueId), down, l.Promoted));
        }
        return warnings;
    }
}
