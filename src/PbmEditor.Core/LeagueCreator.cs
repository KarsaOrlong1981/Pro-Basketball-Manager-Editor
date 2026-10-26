namespace PbmEditor.Core;

/// <summary>How the promoted teams are chosen.</summary>
public enum PromotionMode
{
    /// <summary>The best N teams of the final ranking of the whole league (like French League 4, decided after the playoffs).</summary>
    FinalRanking,
    /// <summary>The best N teams of every group (like a regional league whose group champions move up).</summary>
    GroupWinners,
}

/// <summary>
/// Input for a new league. Structure (phases, playoff brackets, rules, prize money) is cloned from a template league; its first phase
/// decides the number of groups. Teams are generated with rosters and staff.
/// <see cref="Teams"/> (for example from a CSV list) or <see cref="TeamNames"/> may name fewer clubs than there are places (the rest is generated);
/// a name line may end with ";2" to put that team into group 2. <see cref="Teams"/> wins over <see cref="TeamNames"/>.
/// <see cref="Promoted"/> counts per league (<see cref="PromotionMode.FinalRanking"/>) or per group (<see cref="PromotionMode.GroupWinners"/>);
/// <see cref="Relegated"/> is per group.
/// </summary>
public sealed record NewLeague(
    string Name, string Abbreviation, long CountryId, long TemplateLeagueId, int Division, int Reputation,
    int TeamsPerGroup, long PromoteToLeagueId, PromotionMode Promotion, int Promoted, int Relegated,
    double TeamLevel, int Seed, IReadOnlyList<string>? TeamNames = null, int RosterSize = 13, double BudgetScale = 0.7,
    IReadOnlyList<TeamEntry>? Teams = null);

public sealed record LeagueResult(long LeagueId, IReadOnlyList<long> TeamIds, IReadOnlyList<string> Warnings);

/// <summary>Creates a complete league: league row, rules, phases, brackets, prize money, promotion/relegation, group assignment and the clubs with rosters.</summary>
public static class LeagueCreator
{
    // tables keyed by the league that this creator can neither copy nor sensibly skip: such a template is refused
    static readonly (string Table, string Column)[] UnsupportedByLeague =
    {
        ("STA_competition_classement_final_impose", "id_competition"), ("STA_Equipes_Division", "id_competition"),
        ("STA_competition_cursus_informations", "id_competition"), ("STA_competition_jeunes_budget_repartition", "id_competition"),
    };

    // phase tables that are not copied: a template that uses them is refused
    static readonly string[] UnsupportedByPhase =
    {
        "STA_competition_phase_equipes_qualifiees", "STA_competition_coupure", "STA_competition_a_attendre",
        "STA_competition_schedules_to_generate_after", "STA_phase_end_meeting_national_team", "STA_phase_start_meeting_national_team",
    };

    // child tables of a phase that are copied with the new phase ID
    static readonly string[] PhaseChildren =
    {
        "STA_competition_phase_rounds_details", "STA_competition_jours_matchs", "STA_competition_phase_nom_tours", "STA_competition_phase_brackets",
    };

    const long GermanyId = 4; // German city names (nom_ville_de) are used for German clubs

    static readonly long[] Colors =
    {
        Argb(0xE00000), Argb(0xFFFFFF), Argb(0x0033A0), Argb(0xFFD100), Argb(0x00A650), Argb(0x111111),
        Argb(0xFF7A00), Argb(0x7B1FA2), Argb(0x00AEEF), Argb(0x8B0000), Argb(0x006400), Argb(0xC0C0C0),
    };

    static long Argb(int rgb) => (int)(0xFF000000u | (uint)rgb);

    static string Dec(object? v) => v is string s && PbmCodec.TryDecode(s, out var d) ? d : v?.ToString() ?? "";

    /// <summary>Number of groups of the template's first phase (1 = a single table).</summary>
    public static int GroupsOf(PbmDatabase db, long templateLeagueId) =>
        db.Query("SELECT nombre_groupes FROM STA_competition_phase WHERE id_competition=@p0 ORDER BY ordre LIMIT 1", templateLeagueId)
            .FirstOrDefault() is { } r ? (int)Math.Max(1, r.Int("nombre_groupes")) : 1;

    /// <summary>Suggested team level: the ten-best average of the league above, one point lower (a lower division is weaker).</summary>
    public static double SuggestTeamLevel(PbmDatabase db, long upperLeagueId)
    {
        var perTeam = db.Query(@"SELECT j.team_id, j.moyenne FROM DYN_joueur j
                                 JOIN DYN_equipe e ON e.id_equipe=j.team_id WHERE e.championnat_id=@p0 ORDER BY j.team_id, j.moyenne DESC", upperLeagueId)
            .GroupBy(r => r.Int("team_id")).Select(g => g.Take(10).Average(r => r.Dbl("moyenne"))).ToList();
        return perTeam.Count == 0 ? 8.0 : Math.Round(perTeam.Average() - 0.9, 1);
    }

    /// <summary>Splits "Name;2" into the name and the pinned group (null if there is none or it is not a number).</summary>
    public static (string Name, int? Group) ParseTeamLine(string line)
    {
        var i = line.LastIndexOf(';');
        return i > 0 && int.TryParse(line[(i + 1)..].Trim(), out var g) ? (line[..i].Trim(), g) : (line.Trim(), null);
    }

    /// <summary>The named clubs of the input: the team list if there is one, else the name lines.</summary>
    public static IReadOnlyList<TeamEntry> EntriesOf(NewLeague l)
    {
        if (l.Teams != null) return l.Teams;
        return (l.TeamNames ?? Array.Empty<string>()).Where(n => n.Trim().Length > 0)
            .Select(n => { var (name, group) = ParseTeamLine(n); return new TeamEntry(name, group); }).ToList();
    }

    public static string? Validate(PbmDatabase db, NewLeague l)
    {
        if (l.Name.Trim().Length == 0 || l.Abbreviation.Trim().Length == 0) return Loc.T("Please enter a name and an abbreviation for the league.");
        if (db.Scalar("SELECT 1 FROM DYN_championnat WHERE id=@p0", l.TemplateLeagueId) == null) return Loc.T("Template league not found.");
        var phases = db.Query("SELECT * FROM STA_competition_phase WHERE id_competition=@p0 ORDER BY ordre", l.TemplateLeagueId);
        if (phases.Count == 0) return Loc.T("The template league has no phases.");
        var groups = (int)Math.Max(1, phases[0].Int("nombre_groupes"));

        if (l.TeamsPerGroup < 4 || l.TeamsPerGroup > 30 || l.TeamsPerGroup % 2 != 0) return Loc.T("The number of teams per group must be even and between 4 and 30.");
        if (phases.Any(p => p.Int("nombre_equipes_qualifiees") > l.TeamsPerGroup)) return Loc.T("The groups have too few teams for the template's playoffs.");
        var perGroupLimit = l.TeamsPerGroup / 2;
        var promotedLimit = l.Promotion == PromotionMode.FinalRanking ? perGroupLimit * groups : perGroupLimit;
        if (l.Promoted < 0 || l.Promoted > promotedLimit || l.Relegated < 0 || l.Relegated > perGroupLimit)
            return Loc.T("Promoted and relegated teams cannot exceed half of a group.");
        if (l.RosterSize is < RosterGenerator.MinSize or > RosterGenerator.MaxSize)
            return Loc.F("The roster size must be between {0} and {1}.", RosterGenerator.MinSize, RosterGenerator.MaxSize);
        if (db.Scalar("SELECT 1 FROM DYN_pays WHERE id=@p0", l.CountryId) == null) return Loc.T("Country not found.");
        if (l.PromoteToLeagueId == 0 && l.Promoted > 0) return Loc.T("Choose the league the promoted teams move up to.");
        if (l.PromoteToLeagueId != 0 && db.Scalar("SELECT 1 FROM DYN_championnat WHERE id=@p0", l.PromoteToLeagueId) == null) return Loc.T("League not found.");
        if (db.Scalar("SELECT 1 FROM DYN_equipe WHERE championnat_id=@p0", l.TemplateLeagueId) == null) return Loc.T("The template league has no teams to use as a model.");

        var teamCount = groups * l.TeamsPerGroup;
        var entries = EntriesOf(l);
        if (entries.Count > teamCount) return Loc.T("There are more team names than teams.");
        var pinned = entries.Where(e => e.Group != null).Select(e => e.Group!.Value).ToList();
        if (pinned.Any(g => g < 1 || g > groups)) return Loc.F("Group numbers must be between 1 and {0}.", groups);
        if (pinned.GroupBy(g => g).Any(g => g.Count() > l.TeamsPerGroup)) return Loc.T("A group has more named teams than places.");

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
        var groups = GroupsOf(db, l.TemplateLeagueId);

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
        CopyRow(db, "STA_competition_regles", "ligue_id", l.TemplateLeagueId, id, newId: false, extra: r => r["equipe_espoirs"] = 0L);
        // awards (MVP etc.) like the template; objectives and non-relegable teams are specific to the template's clubs and are not copied
        CopyRow(db, "STA_competition_recompenses_informations", "id_competition", l.TemplateLeagueId, id, newId: true);

        // ---- phases and their child rows ----
        long firstPhase = 0;
        foreach (var phase in db.Query("SELECT * FROM STA_competition_phase WHERE id_competition=@p0 ORDER BY ordre", l.TemplateLeagueId))
        {
            var oldPhase = phase.Int("id");
            var newPhase = RowCopy.NextId(db, "STA_competition_phase", "id");
            if (phase.Int("ordre") == 0) firstPhase = newPhase;
            var p = RowCopy.Copy(phase);
            p["id"] = newPhase; p["id_competition"] = id;
            if (phase.Int("ordre") == 0)
            {
                p["nombre_equipe_par_groupe"] = l.TeamsPerGroup; p["nombre_equipe_total"] = groups * l.TeamsPerGroup;
            }
            RowCopy.Insert(db, "STA_competition_phase", p);
            foreach (var table in PhaseChildren)
            {
                if (db.GetTable(table) == null) continue;
                foreach (var child in db.Query($"SELECT * FROM \"{table}\" WHERE id_phase=@p0", oldPhase))
                {
                    var c = RowCopy.Copy(child);
                    c["id"] = RowCopy.NextId(db, table, "id"); c["id_phase"] = newPhase;
                    // brackets point at the league whose standings they use: the new one
                    if (c.TryGetValue("id_competition_phase_precedente", out var prev) && prev is long pl && pl == l.TemplateLeagueId)
                        c["id_competition_phase_precedente"] = id;
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

        WriteRelegations(db, id, l, groups);
        var warnings = new List<string>();
        var teamIds = WriteTeams(db, id, l, groups, firstPhase, rng, warnings);
        warnings.AddRange(Warnings(db, l, id, groups));

        db.Note(Loc.F("{0}: new league created with {1} teams (ID {2})", l.Name.Trim(), teamIds.Count, id));
        return new LeagueResult(id, teamIds, warnings);
    }

    static void CopyRow(PbmDatabase db, string table, string column, long from, long to, bool newId, Action<Dictionary<string, object?>>? extra = null)
    {
        if (db.GetTable(table) == null) return;
        foreach (var source in db.Query($"SELECT * FROM \"{table}\" WHERE \"{column}\"=@p0", from))
        {
            var r = RowCopy.Copy(source);
            r[column] = to;
            if (newId) r["id"] = RowCopy.NextId(db, table, "id");
            extra?.Invoke(r);
            RowCopy.Insert(db, table, r);
        }
    }

    /// <summary>
    /// Promotion: the final ranking (or the best of every group) goes up to the league above. Relegation: the last teams of every group of the first phase
    /// (destination stays 0, like in the game data, unless the league above is linked below).
    /// </summary>
    static void WriteRelegations(PbmDatabase db, long leagueId, NewLeague l, int groups)
    {
        if (db.GetTable("STA_competition_relegations") == null) return;
        void Add(long type, long phaseOrder, long group, long position, long positionType, long future) => RowCopy.Insert(db, "STA_competition_relegations", new Dictionary<string, object?>
        {
            ["id"] = RowCopy.NextId(db, "STA_competition_relegations", "id"), ["id_competition"] = leagueId, ["type"] = type, ["ordre_phase"] = phaseOrder,
            ["id_conference"] = 0L, ["groupe"] = group, ["position"] = position, ["type_position"] = positionType, ["id_competition_future"] = future,
        });

        if (l.Promotion == PromotionMode.FinalRanking)
            for (var i = 1; i <= l.Promoted; i++) Add(1, 255, 0, i, 0, l.PromoteToLeagueId);
        else
            for (var g = 1; g <= groups; g++)
                for (var i = 1; i <= l.Promoted; i++) Add(1, 0, g, i, 0, l.PromoteToLeagueId);

        for (var g = 1; g <= groups; g++)
            for (var i = 0; i < l.Relegated; i++) Add(0, 0, groups == 1 ? 0 : g, i, 1, 0);

        // the league above sends its relegated teams here (the French pyramid in the game data sets the destination on both sides);
        // a destination that already points to another league stays untouched
        if (l.PromoteToLeagueId != 0 && db.Execute(
                "UPDATE STA_competition_relegations SET id_competition_future=@p0 WHERE id_competition=@p1 AND type=0 AND id_competition_future=0",
                leagueId, l.PromoteToLeagueId) > 0)
            db.Note(Loc.F("{0}: relegated teams now move down to {1}", ChangeNames.League(db, l.PromoteToLeagueId), l.Name.Trim()));
    }

    static List<long> WriteTeams(PbmDatabase db, long leagueId, NewLeague l, int groups, long firstPhase, Random rng, List<string> warnings)
    {
        var teamCount = groups * l.TeamsPerGroup;
        var templateTeamId = Convert.ToInt64(db.Scalar("SELECT id_equipe FROM DYN_equipe WHERE championnat_id=@p0 ORDER BY id_equipe LIMIT 1", l.TemplateLeagueId));
        var year = GameService.CurrentDate(db).Year;
        var pool = NamePool.Load(db, l.CountryId);
        var cities = Cities(db, l.CountryId);
        var realMedian = db.Query("SELECT real_median_budget FROM DYN_championnat WHERE id=@p0", leagueId).Single().Dbl("real_median_budget");

        var entries = EntriesOf(l);
        var specs = Enumerable.Range(0, teamCount).Select(i => i < entries.Count ? entries[i] : null).ToList();

        // cities: listed clubs first (coordinates, then city name, then club name), generated clubs next,
        // listed clubs without a match last (a pinned one takes the free city nearest to its group, otherwise a random one)
        var picked = new City?[teamCount];
        for (var i = 0; i < teamCount; i++)
        {
            if (specs[i] is not { } e) continue;
            picked[i] = e.Lat is { } la && e.Lon is { } lo ? cities.TakeNear(la, lo, anyCity: true)
                : (e.City != null ? cities.TakeByCity(e.City) : null) ?? cities.TakeByName(e.Name);
        }
        for (var i = 0; i < teamCount; i++)
            if (picked[i] == null && specs[i] == null) picked[i] = cities.TakeRandom(rng);
        var unmatched = new HashSet<int>();
        for (var i = 0; i < teamCount; i++)
        {
            if (picked[i] != null) continue;
            unmatched.Add(i);
            var anchors = specs[i]?.Group == null ? new List<City>()
                : Enumerable.Range(0, teamCount).Where(j => picked[j] != null && specs[j]?.Group == specs[i]!.Group).Select(j => picked[j]!).ToList();
            picked[i] = anchors.Count > 0
                ? cities.TakeNear(anchors.Average(a => a.Lat), anchors.Average(a => a.Lon))
                : cities.TakeRandom(rng);
        }

        var groupOf = groups == 1
            ? Enumerable.Repeat(1, teamCount).ToArray()
            : GroupAssigner.Assign(picked.Select(c => (c!.Lat, c.Lon)).ToList(), groups, l.TeamsPerGroup, specs.Select(s => s?.Group).ToList(), l.Seed);

        var levels = Enumerable.Range(0, teamCount).Select(i => teamCount == 1 ? 0 : -0.6 + 1.2 * i / (teamCount - 1)).OrderBy(_ => rng.Next()).ToList();
        var ids = new List<long>();
        var budgets = new List<long>();
        var usedNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 0; i < teamCount; i++)
        {
            var city = picked[i]!;
            var baseName = specs[i]?.Name is { Length: > 0 } s ? s : $"{city.Name} Baskets";
            var teamName = baseName;
            for (var k = 2; !usedNames.Add(teamName); k++) teamName = $"{baseName} {k}";
            var colors = Colors.OrderBy(_ => rng.Next()).Take(2).ToArray();
            var realBudget = (long)Math.Round(realMedian * (0.65 + 0.8 * rng.NextDouble()) / 10000) * 10000;

            var teamId = TeamCreator.Create(db, new NewTeam(teamName, leagueId, l.CountryId, templateTeamId, city.Id, $"{city.Name}'s arena",
                rng.Next(3, 16) * 100, colors[0], colors[1], realBudget, 45, rng.Next(1950, year - 5)), pool, rng);
            RosterGenerator.Fill(db, teamId, l.CountryId, pool, rng, l.TeamLevel + levels[i], realBudget * 45 / 100 / 13, l.RosterSize);
            ids.Add(teamId);
            if (unmatched.Contains(i))
                warnings.Add(Loc.F("'{0}': no matching city found, placed in {1}. Give a city or coordinates in the list.", teamName, city.Name));
            budgets.Add(Convert.ToInt64(db.Scalar("SELECT budget FROM DYN_equipe WHERE id_equipe=@p0", teamId)));
        }
        // the league minimum is the cheapest club (as in the default data)
        db.Execute("UPDATE DYN_championnat SET min_budget=@p0, min_ms=@p0 WHERE id=@p1", budgets.Min(), leagueId);

        // first season: fixed groups. Later seasons the game splits the teams geographically on its own (type_repartition 1 in the data).
        if (groups > 1 && db.GetTable("STA_phase_forced_teams") != null)
            for (var i = 0; i < teamCount; i++)
                RowCopy.Insert(db, "STA_phase_forced_teams", new Dictionary<string, object?>
                {
                    ["id"] = RowCopy.NextId(db, "STA_phase_forced_teams", "id"), ["id_phase"] = firstPhase, ["id_team"] = ids[i],
                    ["season_start"] = year, ["group"] = groupOf[i],
                });
        return ids;
    }

    /// <summary>A city of the country. Spellings of the same place (same coordinates) are merged into one.</summary>
    sealed record City(long Id, string Name, double Lat, double Lon, IReadOnlyList<string> Names, IReadOnlyList<long> AllIds);

    sealed class CityPool
    {
        readonly List<City> _all, _free;
        public CityPool(List<City> all, List<City> free) { _all = all; _free = free; }

        /// <summary>The city whose name (or another spelling) equals the text, or null.</summary>
        public City? TakeByCity(string city)
        {
            var hit = _all.FirstOrDefault(c => c.Names.Any(n => n.Equals(city.Trim(), StringComparison.CurrentCultureIgnoreCase)));
            if (hit != null) _free.Remove(hit);
            return hit;
        }

        /// <summary>The city with the longest name that appears as a whole word in the team name, or null.</summary>
        public City? TakeByName(string teamName)
        {
            var hit = _all.Select(c => (City: c, Len: c.Names.Where(n => n.Length >= 3 && ContainsWord(teamName, n)).Select(n => n.Length).DefaultIfEmpty(0).Max()))
                .Where(x => x.Len > 0).OrderByDescending(x => x.Len).Select(x => x.City).FirstOrDefault();
            if (hit != null) _free.Remove(hit);
            return hit;
        }

        static bool ContainsWord(string text, string word)
        {
            for (var i = text.IndexOf(word, StringComparison.CurrentCultureIgnoreCase); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.CurrentCultureIgnoreCase))
            {
                var before = i == 0 || !char.IsLetterOrDigit(text[i - 1]);
                var after = i + word.Length >= text.Length || !char.IsLetterOrDigit(text[i + word.Length]);
                if (before && after) return true;
            }
            return false;
        }

        public City TakeRandom(Random rng)
        {
            var list = _free.Count > 0 ? _free : _all;
            var city = list[rng.Next(list.Count)];
            _free.Remove(city);
            return city;
        }

        /// <param name="anyCity">True: the nearest city at all (a club listed with coordinates belongs where it is, even if another club is there). False: nearest free city.</param>
        public City TakeNear(double lat, double lon, bool anyCity = false)
        {
            var list = anyCity || _free.Count == 0 ? _all : _free;
            var cos = Math.Cos(lat * Math.PI / 180);
            var city = list.OrderBy(c => Math.Pow((c.Lon - lon) * cos, 2) + Math.Pow(c.Lat - lat, 2)).ThenBy(c => c.Id).First();
            _free.Remove(city);
            return city;
        }
    }

    /// <summary>Cities of the country that have coordinates (needed for the regional split). Cities that do not host a club yet are preferred.</summary>
    static CityPool Cities(PbmDatabase db, long country)
    {
        var rows = db.Query(@"SELECT id, nom_ville_en, nom_ville_de, latitude, longitude FROM DYN_ville
                              WHERE id_pays=@p0 AND latitude IS NOT NULL AND longitude IS NOT NULL AND NOT (latitude=0 AND longitude=0) ORDER BY id", country);
        var all = rows.GroupBy(r => (Math.Round(r.Dbl("latitude"), 4), Math.Round(r.Dbl("longitude"), 4)))
            .Select(g =>
            {
                var first = g.First();
                var en = Dec(first["nom_ville_en"]); var de = Dec(first["nom_ville_de"]);
                var display = country == GermanyId && de.Length > 0 ? de : en;
                var names = g.SelectMany(r => new[] { Dec(r["nom_ville_en"]), Dec(r["nom_ville_de"]) }).Where(n => n.Length > 0).Distinct().ToList();
                return new City(first.Int("id"), display.Length > 0 ? display : en, first.Dbl("latitude"), first.Dbl("longitude"), names, g.Select(r => r.Int("id")).ToList());
            }).Where(c => c.Name.Length > 0).ToList();
        if (all.Count == 0) throw new InvalidOperationException(Loc.T("The country has no cities with coordinates. Pick another country."));
        var used = db.Query("SELECT id_ville FROM DYN_equipe WHERE id_ville IS NOT NULL").Select(r => r.Int("id_ville")).ToHashSet();
        return new CityPool(all, all.Where(c => !c.AllIds.Any(used.Contains)).ToList());
    }

    /// <summary>Hints that need a human decision, e.g. promotion and relegation counts that do not balance.</summary>
    static List<string> Warnings(PbmDatabase db, NewLeague l, long leagueId, int groups)
    {
        var warnings = new List<string>();
        if (l.PromoteToLeagueId != 0 && db.GetTable("STA_competition_relegations") != null)
        {
            var down = db.Query("SELECT COUNT(*) n FROM STA_competition_relegations WHERE id_competition=@p0 AND type=0", l.PromoteToLeagueId).Single().Int("n");
            var promoted = l.Promotion == PromotionMode.FinalRanking ? l.Promoted : l.Promoted * groups;
            var other = db.Query("SELECT COUNT(*) n FROM STA_competition_relegations WHERE id_competition=@p0 AND type=0 AND id_competition_future NOT IN (0, @p1)",
                l.PromoteToLeagueId, leagueId).Single().Int("n");
            if (other > 0)
                warnings.Add(Loc.F("{0} already sends its relegated teams to another league. Check its relegation rows.", ChangeNames.League(db, l.PromoteToLeagueId)));
            if (down != promoted)
                warnings.Add(Loc.F("{0} relegates {1} team(s) but {2} team(s) are promoted into it. Adjust the numbers or the relegation rows so the league sizes stay constant.",
                    ChangeNames.League(db, l.PromoteToLeagueId), down, promoted));
        }
        return warnings;
    }
}
