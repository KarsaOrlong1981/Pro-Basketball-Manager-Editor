using Microsoft.Data.Sqlite;
using PbmEditor.Core;
using Xunit;

namespace PbmEditor.Tests;

/// <summary>
/// Runs against a temporary copy of the game's default database (PBM_TEST_DB, or the Steam default path).
/// Without a database the tests pass trivially, like the other real-data tests.
/// </summary>
public class LeagueCreatorTests
{
    const string SteamDefault = @"C:\Program Files (x86)\Steam\steamapps\common\Pro Basketball Manager 2025\PBM2025_Data\Datas\BM_database.pbmsave";
    const long League2 = 15, League3 = 125, Germany = 4;

    sealed class CopyDb : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "pbmleague_" + Guid.NewGuid().ToString("N"));
        public PbmDatabase Db { get; }

        CopyDb(string source)
        {
            Directory.CreateDirectory(_dir);
            var file = Path.Combine(_dir, "Database.pbmsave");
            // backup API instead of File.Copy: includes content that still sits in the WAL file
            using (var src = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False"))
            using (var dst = new SqliteConnection($"Data Source={file};Pooling=False"))
            {
                src.Open(); dst.Open();
                src.BackupDatabase(dst);
            }
            Db = PbmDatabase.Open(file, Path.Combine(_dir, "bk"));
        }

        public static CopyDb? Create()
        {
            var path = Environment.GetEnvironmentVariable("PBM_TEST_DB");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) path = SteamDefault;
            return File.Exists(path) ? new CopyDb(path) : null;
        }

        public void Dispose()
        {
            Db.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    static NewLeague Sample(PbmDatabase db, int teamsPerGroup = 14, IReadOnlyList<string>? names = null) =>
        new("German League 4", "GL4", Germany, League2, 4, 20, teamsPerGroup, League3, PromotionMode.FinalRanking, 4, 2,
            LeagueCreator.SuggestTeamLevel(db, League3), 7, names);

    [Fact]
    public void Create_BuildsLeagueRulesPhasesAndPromotion()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var r = LeagueCreator.Create(db, Sample(db));

        var league = db.Query("SELECT * FROM DYN_championnat WHERE id=@p0", r.LeagueId).Single();
        Assert.True(PbmCodec.TryDecode(league.Str("nom_championnat"), out var name)); Assert.Equal("German League 4", name);
        Assert.Equal(Germany, league.Int("championnat_pays")); Assert.Equal(4, league.Int("division"));
        Assert.Equal(0, db.Query("SELECT * FROM STA_competition_regles WHERE ligue_id=@p0", r.LeagueId).Single().Int("equipe_espoirs"));

        var phases = db.Query("SELECT * FROM STA_competition_phase WHERE id_competition=@p0 ORDER BY ordre", r.LeagueId);
        Assert.Equal(2, phases.Count);
        Assert.Equal(14, phases[0].Int("nombre_equipe_total")); Assert.Equal(14, phases[0].Int("nombre_equipe_par_groupe"));
        Assert.Equal(8, phases[1].Int("nombre_equipe_total")); // playoffs unchanged
        Assert.All(phases, p => Assert.NotEqual(279, p.Int("id")));
        Assert.Equal(1, db.Query("SELECT COUNT(*) n FROM STA_competition_phase_rounds_details WHERE id_phase=@p0", phases[1].Int("id")).Single().Int("n"));

        var up = db.Query("SELECT * FROM STA_competition_relegations WHERE id_competition=@p0 AND type=1 ORDER BY position", r.LeagueId);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, up.Select(x => x.Int("position")).ToArray());
        Assert.All(up, x => { Assert.Equal(League3, x.Int("id_competition_future")); Assert.Equal(255, x.Int("ordre_phase")); });
        Assert.Equal(2, db.Query("SELECT COUNT(*) n FROM STA_competition_relegations WHERE id_competition=@p0 AND type=0", r.LeagueId).Single().Int("n"));

        Assert.Equal(8, db.Query("SELECT COUNT(*) n FROM STA_competition_prize_money WHERE id_competition=@p0", r.LeagueId).Single().Int("n"));
        Assert.Empty(r.Warnings); // League 3 relegates 4 and receives 4
        // League 3 now sends its four relegated teams down to the new league
        Assert.Equal(4, db.Query("SELECT COUNT(*) n FROM STA_competition_relegations WHERE id_competition=@p0 AND type=0 AND id_competition_future=@p1", League3, r.LeagueId).Single().Int("n"));
        Assert.Contains(db.Changes, c => c.Contains("relegated teams now move down"));
        Assert.Equal("ok", db.IntegrityCheck());
    }

    [Fact]
    public void Create_BuildsCompleteTeamsWithRostersStaffAndArena()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var r = LeagueCreator.Create(db, Sample(db, 12, new[] { "Hamburg Test Hawks" }));

        Assert.Equal(12, r.TeamIds.Count);
        Assert.Equal(r.TeamIds.Count, r.TeamIds.Distinct().Count());
        Assert.Equal(12, db.Query("SELECT COUNT(*) n FROM DYN_equipe WHERE championnat_id=@p0", r.LeagueId).Single().Int("n"));
        Assert.Equal("Hamburg Test Hawks", ChangeNames.Team(db, r.TeamIds[0]));

        var minBudget = db.Query("SELECT min_budget FROM DYN_championnat WHERE id=@p0", r.LeagueId).Single().Int("min_budget");
        Assert.Equal(minBudget, db.Query("SELECT MIN(budget) m FROM DYN_equipe WHERE championnat_id=@p0", r.LeagueId).Single().Int("m"));

        foreach (var id in r.TeamIds)
        {
            var players = GameService.PlayersOfTeam(db, id);
            Assert.Equal(13, players.Count);
            Assert.Equal(13, players.Select(p => p.Number).Distinct().Count()); // unique jersey numbers
            Assert.All(players, p => Assert.True(p.Salary > 0 && GameService.ActiveContractId(db, p.Id, id) != null));
            Assert.Equal(2, GameService.StaffOfTeam(db, id).Count(s => s.JobType is 0 or 1));
            Assert.NotNull(db.Scalar("SELECT 1 FROM DYN_facilities WHERE id_team=@p0", id));
            var fin = GameService.TeamFinance(db, id);
            Assert.True(fin.Monthly * 13 < fin.Budget, "payroll must fit the budget");
        }

        // a lower division is weaker than the league above
        static double TopTen(PbmDatabase d, long league) => d.Query(
                "SELECT j.team_id, j.moyenne FROM DYN_joueur j JOIN DYN_equipe e ON e.id_equipe=j.team_id WHERE e.championnat_id=@p0 ORDER BY j.team_id, j.moyenne DESC", league)
            .GroupBy(x => x.Int("team_id")).Average(g => g.Take(10).Average(x => x.Dbl("moyenne")));
        Assert.True(TopTen(db, r.LeagueId) < TopTen(db, League3));
        Assert.Equal("ok", db.IntegrityCheck());
    }

    [Fact]
    public void Create_IsDeterministicForTheSameSeed()
    {
        using var t1 = CopyDb.Create(); if (t1 == null) return;
        using var t2 = CopyDb.Create()!;
        var a = LeagueCreator.Create(t1.Db, Sample(t1.Db));
        var b = LeagueCreator.Create(t2.Db, Sample(t2.Db));
        Assert.Equal(a.TeamIds, b.TeamIds);
        Assert.Equal(GameService.PlayersOfTeam(t1.Db, a.TeamIds[3]).Select(p => p.Name), GameService.PlayersOfTeam(t2.Db, b.TeamIds[3]).Select(p => p.Name));
    }

    [Fact]
    public void Rollback_RemovesEverything()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var (leagues, teams, players) = (Count(db, "DYN_championnat"), Count(db, "DYN_equipe"), Count(db, "DYN_joueur"));
        LeagueCreator.Create(db, Sample(db));
        Assert.True(db.HasPendingChanges);
        db.Rollback();
        Assert.Equal((leagues, teams, players), (Count(db, "DYN_championnat"), Count(db, "DYN_equipe"), Count(db, "DYN_joueur")));
    }

    [Fact]
    public void Create_Failure_LeavesNothingBehind()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var leagues = Count(db, "DYN_championnat");
        // a country without any city passes validation but fails while placing the teams
        var empty = Convert.ToInt64(db.Scalar("SELECT id FROM DYN_pays WHERE id NOT IN (SELECT DISTINCT id_pays FROM DYN_ville) LIMIT 1"));
        Assert.Throws<InvalidOperationException>(() => LeagueCreator.Create(db, Sample(db) with { CountryId = empty }));
        Assert.Equal(leagues, Count(db, "DYN_championnat"));
        Assert.Equal(0, Count(db, "STA_competition_phase", "id_competition=(SELECT MAX(id)+1 FROM DYN_championnat)"));
        Assert.Equal(2, db.Query("SELECT COUNT(*) n FROM STA_competition_phase WHERE id_competition=@p0", League2).Single().Int("n"));
    }

    [Fact]
    public void Validate_RejectsBadInput()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var ok = Sample(db);
        Assert.Null(LeagueCreator.Validate(db, ok));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { Name = " " }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TeamsPerGroup = 13 }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TeamsPerGroup = 2 }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { Promoted = 8 }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { PromoteToLeagueId = 0 }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TemplateLeagueId = 99999 }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TemplateLeagueId = 44 })); // American League uses divisions, which are not copied
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TeamNames = Enumerable.Repeat("x", 15).ToList() }));
        Assert.Throws<InvalidOperationException>(() => LeagueCreator.Create(db, ok with { TeamsPerGroup = 13 }));
        Assert.False(db.HasPendingChanges);
    }

    [Fact]
    public void Warns_WhenPromotionAndRelegationDoNotBalance()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var r = LeagueCreator.Create(t.Db, Sample(t.Db) with { Promoted = 2 });
        Assert.Single(r.Warnings);
    }

    [Fact]
    public void SecondLeagueBelow_DoesNotHijackAnExistingRelegationTarget()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var first = LeagueCreator.Create(t.Db, Sample(t.Db));
        var second = LeagueCreator.Create(t.Db, Sample(t.Db) with { Name = "German League 4b", Abbreviation = "G4B" });
        Assert.Equal(0, t.Db.Query("SELECT COUNT(*) n FROM STA_competition_relegations WHERE id_competition=@p0 AND id_competition_future=@p1", League3, second.LeagueId).Single().Int("n"));
        Assert.Equal(4, t.Db.Query("SELECT COUNT(*) n FROM STA_competition_relegations WHERE id_competition=@p0 AND id_competition_future=@p1", League3, first.LeagueId).Single().Int("n"));
        Assert.Contains(second.Warnings, w => w.Contains("another league"));
    }


    // ---- several groups (template: French League 4, four groups of 14) ----

    const long FrenchLeague4 = 42;

    static NewLeague Regional(PbmDatabase db, IReadOnlyList<string>? names = null) =>
        new("German Regional League", "GRL", Germany, FrenchLeague4, 4, 20, 14, League3, PromotionMode.GroupWinners, 1, 0,
            LeagueCreator.SuggestTeamLevel(db, League3), 11, names);

    [Fact]
    public void MultiGroup_BuildsGroupsBracketsAndGroupWinnerPromotion()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        Assert.Equal(4, LeagueCreator.GroupsOf(db, FrenchLeague4));
        var r = LeagueCreator.Create(db, Regional(db));

        Assert.Equal(56, r.TeamIds.Count);
        var phases = db.Query("SELECT * FROM STA_competition_phase WHERE id_competition=@p0 ORDER BY ordre", r.LeagueId);
        Assert.Equal(4, phases[0].Int("nombre_groupes")); Assert.Equal(14, phases[0].Int("nombre_equipe_par_groupe")); Assert.Equal(56, phases[0].Int("nombre_equipe_total"));
        Assert.Equal(8, phases[1].Int("nombre_equipe_total"));

        // playoff brackets are copied and point at the new league's standings
        var brackets = db.Query("SELECT * FROM STA_competition_phase_brackets WHERE id_phase=@p0", phases[1].Int("id"));
        Assert.Equal(8, brackets.Count);
        Assert.All(brackets, b => Assert.Equal(r.LeagueId, b.Int("id_competition_phase_precedente")));
        Assert.Equal(brackets.Count, brackets.Select(b => b.Int("id")).Distinct().Count());

        // first season: every team has a fixed group, 14 per group
        var forced = db.Query("SELECT * FROM STA_phase_forced_teams WHERE id_phase=@p0", phases[0].Int("id"));
        Assert.Equal(56, forced.Count);
        Assert.Equal(56, forced.Select(f => f.Int("id_team")).Distinct().Count());
        Assert.All(forced, f => Assert.Equal(2024, f.Int("season_start")));
        Assert.Equal(new[] { 14, 14, 14, 14 }, forced.GroupBy(f => f.Int("group")).OrderBy(g => g.Key).Select(g => g.Count()).ToArray());
        Assert.Equal(new HashSet<long>(r.TeamIds), forced.Select(f => f.Int("id_team")).ToHashSet());

        // the champion of every group moves up to League 3; nobody is relegated
        var up = db.Query("SELECT * FROM STA_competition_relegations WHERE id_competition=@p0 AND type=1 ORDER BY groupe", r.LeagueId);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, up.Select(x => x.Int("groupe")).ToArray());
        Assert.All(up, x => { Assert.Equal(1, x.Int("position")); Assert.Equal(0, x.Int("ordre_phase")); Assert.Equal(League3, x.Int("id_competition_future")); });
        Assert.Equal(0, db.Query("SELECT COUNT(*) n FROM STA_competition_relegations WHERE id_competition=@p0 AND type=0", r.LeagueId).Single().Int("n"));

        // League 3 relegates 4 teams and receives 4: balanced, and it sends them to the new league
        Assert.Empty(r.Warnings);
        Assert.Equal(4, db.Query("SELECT COUNT(*) n FROM STA_competition_relegations WHERE id_competition=@p0 AND type=0 AND id_competition_future=@p1", League3, r.LeagueId).Single().Int("n"));

        // awards are copied, template-specific objectives and non-relegable teams are not
        Assert.Equal(1, db.Query("SELECT COUNT(*) n FROM STA_competition_recompenses_informations WHERE id_competition=@p0", r.LeagueId).Single().Int("n"));
        Assert.Equal(0, db.Query("SELECT COUNT(*) n FROM STA_competition_objectifs WHERE id_competition=@p0", r.LeagueId).Single().Int("n"));
        Assert.Equal(0, db.Query("SELECT COUNT(*) n FROM STA_competition_equipes_non_relegables WHERE id_competition=@p0", r.LeagueId).Single().Int("n"));
        // the template league itself is untouched
        Assert.Equal(168, db.Query("SELECT COUNT(*) n FROM STA_phase_forced_teams WHERE id_phase=140").Single().Int("n"));
        Assert.Equal("ok", db.IntegrityCheck());
    }

    [Fact]
    public void MultiGroup_FinalRankingPromotionAndRelegationPerGroup()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var r = LeagueCreator.Create(db, Regional(db) with { Promotion = PromotionMode.FinalRanking, Promoted = 4, Relegated = 2 });
        var up = db.Query("SELECT * FROM STA_competition_relegations WHERE id_competition=@p0 AND type=1 ORDER BY position", r.LeagueId);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, up.Select(x => x.Int("position")).ToArray());
        Assert.All(up, x => { Assert.Equal(0, x.Int("groupe")); Assert.Equal(255, x.Int("ordre_phase")); });
        var down = db.Query("SELECT * FROM STA_competition_relegations WHERE id_competition=@p0 AND type=0 ORDER BY groupe, position", r.LeagueId);
        Assert.Equal(8, down.Count); // 2 per group x 4 groups
        Assert.Equal(new long[] { 1, 1, 2, 2, 3, 3, 4, 4 }, down.Select(x => x.Int("groupe")).ToArray());
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void MultiGroup_PinnedTeamsGoIntoTheirGroup()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var r = LeagueCreator.Create(db, Regional(db, new[] { "Hamburg Test Hawks;1", "Muenchen Test Hawks;4", "Cologne Test Hawks;2", "Plain Test Team" }));
        var phase = db.Query("SELECT id FROM STA_competition_phase WHERE id_competition=@p0 AND ordre=0", r.LeagueId).Single().Int("id");
        long GroupOf(int index) => db.Query("SELECT \"group\" g FROM STA_phase_forced_teams WHERE id_phase=@p0 AND id_team=@p1", phase, r.TeamIds[index]).Single().Int("g");
        Assert.Equal(1, GroupOf(0)); Assert.Equal(4, GroupOf(1)); Assert.Equal(2, GroupOf(2));
        Assert.Equal(new[] { 14, 14, 14, 14 }, db.Query("SELECT \"group\" g, COUNT(*) n FROM STA_phase_forced_teams WHERE id_phase=@p0 GROUP BY 1 ORDER BY 1", phase).Select(x => (int)x.Int("n")).ToArray());
        // named teams keep their names; "Plain Test Team" matches no city and still gets a place
        Assert.Equal("Plain Test Team", ChangeNames.Team(db, r.TeamIds[3]));
    }

    [Fact]
    public void MultiGroup_GeneratedTeamsAreSplitByRegion()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        // template German League 3: two groups of 14
        var r = LeagueCreator.Create(db, new("Split Test League", "STL", Germany, League3, 4, 20, 14, League3, PromotionMode.FinalRanking, 2, 2,
            LeagueCreator.SuggestTeamLevel(db, League3), 3));
        var phase = db.Query("SELECT id FROM STA_competition_phase WHERE id_competition=@p0 AND ordre=0", r.LeagueId).Single().Int("id");
        var members = db.Query(@"SELECT f.""group"" g, v.latitude la, v.longitude lo FROM STA_phase_forced_teams f
                                 JOIN DYN_equipe e ON e.id_equipe=f.id_team JOIN DYN_ville v ON v.id=e.id_ville WHERE f.id_phase=@p0", phase);
        Assert.Equal(28, members.Count);
        Assert.All(members, m => Assert.NotEqual(0.0, m.Dbl("la"))); // only cities with coordinates are used
        var centers = members.GroupBy(m => m.Int("g")).ToDictionary(g => g.Key, g => (La: g.Average(m => m.Dbl("la")), Lo: g.Average(m => m.Dbl("lo"))));
        double D((double La, double Lo) c, Row m) => Math.Pow(m.Dbl("la") - c.La, 2) + Math.Pow((m.Dbl("lo") - c.Lo) * 0.65, 2);
        var nearest = members.Count(m => D(centers[m.Int("g")], m) <= D(centers[3 - m.Int("g")], m));
        Assert.True(nearest >= 26, $"{nearest} of 28 teams sit nearer to their own group than to the other");
    }

    [Fact]
    public void MultiGroup_SameSeedGivesTheSameGroups()
    {
        using var t1 = CopyDb.Create(); if (t1 == null) return;
        using var t2 = CopyDb.Create()!;
        var a = LeagueCreator.Create(t1.Db, Regional(t1.Db)); var b = LeagueCreator.Create(t2.Db, Regional(t2.Db));
        static string Groups(PbmDatabase d, LeagueResult r) => string.Join(",", d.Query("SELECT id_team, \"group\" g FROM STA_phase_forced_teams WHERE id_team IN (" + string.Join(",", r.TeamIds) + ") ORDER BY id_team").Select(x => x.Int("g")));
        Assert.Equal(Groups(t1.Db, a), Groups(t2.Db, b));
    }

    [Fact]
    public void Cities_AreMergedAndOnlyThoseWithCoordinatesAreUsed()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var r = LeagueCreator.Create(db, Regional(db));
        var names = r.TeamIds.Select(id => ChangeNames.Team(db, id)).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.CurrentCultureIgnoreCase).Count()); // no duplicate club names
        // spellings of one place are merged (Munich, Munchen, Muenchen, Muenich) and German clubs get German city names
        Assert.DoesNotContain(names, n => n.StartsWith("Muenich") || n.StartsWith("Munchen") || n.StartsWith("Hessen") || n.StartsWith("Munich") || n.StartsWith("Cologne") || n.StartsWith("Koeln"));
        var cityIds = db.Query("SELECT id_ville FROM DYN_equipe WHERE id_equipe IN (" + string.Join(",", r.TeamIds) + ")").Select(x => x.Int("id_ville")).ToList();
        Assert.Equal(cityIds.Count, cityIds.Distinct().Count()); // every club in its own city while free cities remain
    }

    [Fact]
    public void Validate_RejectsBadGroupInput()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var ok = Regional(db);
        Assert.Null(LeagueCreator.Validate(db, ok));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TeamNames = new[] { "A;5" } })); // only 4 groups
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TeamNames = new[] { "A;0" } }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TeamNames = Enumerable.Range(0, 15).Select(i => $"Team {i};1").ToList() })); // 15 pinned teams, 14 places
        Assert.NotNull(LeagueCreator.Validate(db, ok with { Promoted = 8 })); // more than half of a group
        Assert.Null(LeagueCreator.Validate(db, ok with { Promotion = PromotionMode.FinalRanking, Promoted = 8 }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TeamsPerGroup = 3 }));
    }

    [Fact]
    public void ParseTeamLine_ReadsAnOptionalGroup()
    {
        Assert.Equal(("TSV Beispiel", (int?)3), LeagueCreator.ParseTeamLine("TSV Beispiel;3"));
        Assert.Equal(("TSV Beispiel", (int?)3), LeagueCreator.ParseTeamLine(" TSV Beispiel ; 3 "));
        Assert.Equal(("TSV Beispiel", (int?)null), LeagueCreator.ParseTeamLine("TSV Beispiel"));
        Assert.Equal(("TSV; Beispiel", (int?)null), LeagueCreator.ParseTeamLine("TSV; Beispiel"));
        Assert.Equal((";3", (int?)null), LeagueCreator.ParseTeamLine(";3"));
    }


    // ---- team list (CSV import) ----

    const string RegioSample =
        "Gruppe;Staffel;Team;Stadt;Breitengrad;Laengengrad\n" +
        "Nord;;Berlin Test Dreams;;;\n" +
        "Nord;;Bremen Test Club;Bremen;;\n" +
        "Südost;Süd;München Test Basket;München;;\n" +
        "Südwest;;Ulm Test BBU;;48.4;9.9833\n" +
        "Südwest;;Saar Test Kings;;49.3;6.75\n";

    [Fact]
    public void TeamList_PlacesClubsByCoordinatesCityAndNameAndPinsGroups()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var list = TeamListImporter.Parse(RegioSample);
        var r = LeagueCreator.Create(db, Regional(db) with { Teams = list.Entries });

        // the five listed clubs come first and keep their names
        Assert.Equal(list.Entries.Select(e => e.Name), r.TeamIds.Take(5).Select(id => ChangeNames.Team(db, id)));
        (double Lat, double Lon, string Name) CityOf(int i) => db.Query("SELECT v.latitude la, v.longitude lo, v.nom_ville_en n FROM DYN_equipe e JOIN DYN_ville v ON v.id=e.id_ville WHERE e.id_equipe=@p0", r.TeamIds[i])
            .Select(x => (x.Dbl("la"), x.Dbl("lo"), x.Str("n"))).Single();
        Assert.InRange(CityOf(3).Lat, 48.3, 48.5); Assert.InRange(CityOf(3).Lon, 9.9, 10.1);   // coordinates -> Ulm
        Assert.InRange(CityOf(4).Lat, 49.1, 49.5); Assert.InRange(CityOf(4).Lon, 6.6, 6.9);    // coordinates -> Saarlouis area
        Assert.InRange(CityOf(2).Lat, 48.0, 48.3); Assert.InRange(CityOf(2).Lon, 11.4, 11.7);  // city "München"
        Assert.InRange(CityOf(1).Lat, 52.9, 53.3); Assert.InRange(CityOf(1).Lon, 8.5, 9.1);    // city "Bremen"
        Assert.InRange(CityOf(0).Lat, 52.3, 52.7); Assert.InRange(CityOf(0).Lon, 13.1, 13.7);  // club name contains "Berlin"

        // labels became groups 1..3 in order of appearance; the clubs of one label share a group
        var phase = db.Query("SELECT id FROM STA_competition_phase WHERE id_competition=@p0 AND ordre=0", r.LeagueId).Single().Int("id");
        long GroupOf(int i) => db.Query("SELECT \"group\" g FROM STA_phase_forced_teams WHERE id_phase=@p0 AND id_team=@p1", phase, r.TeamIds[i]).Single().Int("g");
        Assert.Equal(GroupOf(0), GroupOf(1));
        Assert.Equal(GroupOf(3), GroupOf(4));
        Assert.Equal(new long[] { 1, 1, 2, 3, 3 }, Enumerable.Range(0, 5).Select(GroupOf).ToArray());
        // generated clubs fill the rest: 14 per group, group 4 (no label) consists of generated clubs only
        Assert.Equal(new[] { 14, 14, 14, 14 }, db.Query("SELECT \"group\" g, COUNT(*) n FROM STA_phase_forced_teams WHERE id_phase=@p0 GROUP BY 1 ORDER BY 1", phase).Select(x => (int)x.Int("n")).ToArray());
        Assert.Equal(56, r.TeamIds.Count);
        Assert.Equal("ok", db.IntegrityCheck());
    }

    [Fact]
    public void TeamList_WarnsAboutClubsWithoutAMatchingCity()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var list = new[] { new TeamEntry("Berlin Test Dreams", 1), new TeamEntry("Nowhere Test Club", 1, "Nowhereville"), new TeamEntry("Ulm Test BBU", 2, null, 48.4, 9.9833) };
        var r = LeagueCreator.Create(db, Regional(db) with { Teams = list });
        Assert.Single(r.Warnings, w => w.Contains("Nowhere Test Club"));   // only the unmatched club is reported
        Assert.DoesNotContain(r.Warnings, w => w.Contains("Berlin Test Dreams") || w.Contains("Ulm Test BBU"));
        Assert.Equal("Nowhere Test Club", ChangeNames.Team(db, r.TeamIds[1])); // and it is still created
    }

    [Fact]
    public void TeamList_CityNamesMatchWholeWordsOnly()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        // "Ulmer" must not be taken for the city Ulm, "Bonner SC" is Bonn only by the exact city column
        var r = LeagueCreator.Create(db, Regional(db) with { Teams = new[] { new TeamEntry("Ulmer Wanderverein", 1) } });
        Assert.Contains(r.Warnings, w => w.Contains("Ulmer Wanderverein"));
    }

    [Fact]
    public void TeamList_WinsOverNameLines()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var r = LeagueCreator.Create(db, Regional(db, new[] { "Ignored Name;1" }) with { Teams = new[] { new TeamEntry("Listed Club") } });
        Assert.Equal("Listed Club", ChangeNames.Team(db, r.TeamIds[0]));
        Assert.DoesNotContain(r.TeamIds, id => ChangeNames.Team(db, id) == "Ignored Name");
    }

    [Fact]
    public void TeamList_IsValidated()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        var ok = Regional(db);
        Assert.Null(LeagueCreator.Validate(db, ok with { Teams = new[] { new TeamEntry("A", 4) } }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { Teams = new[] { new TeamEntry("A", 5) } })); // the template has 4 groups
        Assert.NotNull(LeagueCreator.Validate(db, ok with { Teams = Enumerable.Range(0, 57).Select(i => new TeamEntry("T" + i)).ToList() })); // 56 places
        Assert.NotNull(LeagueCreator.Validate(db, ok with { Teams = Enumerable.Range(0, 15).Select(i => new TeamEntry("T" + i, 2)).ToList() })); // 14 places per group
    }

    [Fact]
    public void TeamList_FromTheRegionalligaFormat_ExceedingTheGroupsIsRejected()
    {
        using var t = CopyDb.Create(); if (t == null) return;
        var db = t.Db;
        // five labels, but the template league has four groups
        var list = TeamListImporter.Parse("Team;Gruppe\nA;1\nB;2\nC;3\nD;4\nE;5\n");
        Assert.NotNull(LeagueCreator.Validate(db, Regional(db) with { Teams = list.Entries }));
    }

    static long Count(PbmDatabase db, string table, string? where = null) =>
        Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM \"{table}\"" + (where == null ? "" : " WHERE " + where)));
}
