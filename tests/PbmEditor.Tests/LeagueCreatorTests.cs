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

    static NewLeague Sample(PbmDatabase db, int teams = 14, IReadOnlyList<string>? names = null) =>
        new("German League 4", "GL4", Germany, League2, 4, 20, teams, League3, 4, 2,
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
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TeamCount = 13 }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TeamCount = 6 }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { Promoted = 8 }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { PromoteToLeagueId = 0 }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TemplateLeagueId = 99999 }));
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TemplateLeagueId = League3 })); // two groups
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TemplateLeagueId = 11 })); // objectives are not copied yet
        Assert.NotNull(LeagueCreator.Validate(db, ok with { TeamNames = Enumerable.Repeat("x", 15).ToList() }));
        Assert.Throws<InvalidOperationException>(() => LeagueCreator.Create(db, ok with { TeamCount = 13 }));
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

    static long Count(PbmDatabase db, string table, string? where = null) =>
        Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM \"{table}\"" + (where == null ? "" : " WHERE " + where)));
}
