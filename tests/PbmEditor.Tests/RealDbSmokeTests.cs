using PbmEditor.Core;
using Xunit;

namespace PbmEditor.Tests;

/// <summary>Runs against a real PBM database copy when PBM_TEST_DB is set; otherwise passes trivially.</summary>
public class RealDbSmokeTests
{
    static PbmDatabase? OpenReal(out string? dir)
    {
        dir = null;
        var path = Environment.GetEnvironmentVariable("PBM_TEST_DB");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        dir = Path.Combine(Path.GetTempPath(), "pbm_bk_" + Guid.NewGuid().ToString("N"));
        return PbmDatabase.Open(path, dir);
    }

    [Fact]
    public void EveryEditorLoadsWithoutErrors()
    {
        using var db = OpenReal(out _);
        if (db == null) return;
        var teams = GameService.Teams(db);
        Assert.NotEmpty(teams);
        var trier = teams.First(t => t.Id == 85);
        Assert.Contains("Trier", trier.Name);

        var te = new RowEditor(db, "DYN_equipe", "id_equipe", 85, FieldConfigs.Team());
        Assert.True(te.Exists); Assert.NotEmpty(te.Fields);
        Assert.All(te.Fields.Where(f => f.Kind == FieldKind.Choice), f => Assert.NotNull(f.Choices));

        var players = GameService.PlayersOfTeam(db, 85);
        Assert.NotEmpty(players);
        var p = players[0];
        Assert.True(new RowEditor(db, "DYN_joueur", "joueur_id", p.Id, FieldConfigs.Player()).Exists);
        Assert.True(new RowEditor(db, "DYN_joueur_attributes", "joueur_id", p.Id, FieldConfigs.Attributes()).Exists);
        Assert.NotNull(GameService.ActiveContractId(db, p.Id, 85));

        var league = GameService.Leagues(db).First(l => l.Id == 11);
        Assert.True(new RowEditor(db, "DYN_championnat", "id", league.Id, FieldConfigs.League()).Exists);
        foreach (var t in new[] { "STA_competition_phase", "STA_competition_prize_money", "STA_competition_quotas", "STA_competition_relegations", "STA_Equipes_Division" })
            Assert.NotNull(new TableGrid(db, t, "id_competition", league.Id));

        var fin = GameService.TeamFinance(db, 85);
        Assert.True(fin.Monthly > 0);
        Assert.Empty(db.Log); // nothing was written
    }

    [Fact]
    public void StaffEditorsLoadAndMoveRoundTrips()
    {
        using var db = OpenReal(out _);
        if (db == null) return;
        var staff = GameService.StaffOfTeam(db, 85);
        Assert.NotEmpty(staff);
        var s = staff[0];
        Assert.True(new RowEditor(db, "DYN_person", "id_person", s.Id, FieldConfigs.Person()).Exists);
        var cid = GameService.ActiveStaffContractId(db, s.Id, 85);
        Assert.NotNull(cid);
        Assert.True(new RowEditor(db, "DYN_contrat_person", "id", cid!.Value, FieldConfigs.PersonContract()).Exists);

        GameService.MoveStaff(db, s.Id, 78);
        Assert.Contains(GameService.StaffOfTeam(db, 78), x => x.Id == s.Id);
        GameService.MoveStaff(db, s.Id, 0);
        Assert.Null(GameService.ActiveStaffContractId(db, s.Id, 0));
        db.Rollback();
        Assert.Contains(GameService.StaffOfTeam(db, 85), x => x.Id == s.Id);
    }

    [Fact]
    public void NewPlayers_LoadInTheEditorsAndRollBack()
    {
        using var db = OpenReal(out _);
        if (db == null) return;
        var before = Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM DYN_joueur"));
        var template = new NewPlayer("Test", "Player", 1, 6, 2003, false, 2, 200, 95, PlayerPosition.PowerForward, 10.0, 15, 50, 12, 1, 0);

        var free = PlayerCreator.Create(db, template);
        var signed = PlayerCreator.Create(db, template with { TeamId = 85, Salary = PlayerCreator.SuggestSalary(10.0), ContractYears = 3 });

        Assert.Contains(GameService.FreeAgents(db), p => p.Id == free);
        var onTeam = GameService.PlayersOfTeam(db, 85).Single(p => p.Id == signed);
        Assert.True(onTeam.Salary > 0);
        foreach (var id in new[] { free, signed })
        {
            Assert.True(new RowEditor(db, "DYN_joueur", "joueur_id", id, FieldConfigs.Player()).Exists);
            Assert.True(new RowEditor(db, "DYN_joueur_attributes", "joueur_id", id, FieldConfigs.Attributes()).Exists);
        }
        var cid = GameService.ActiveContractId(db, signed, 85);
        Assert.NotNull(cid);
        Assert.True(new RowEditor(db, "DYN_contrat", "id", cid!.Value, FieldConfigs.Contract()).Exists);
        Assert.Null(GameService.ActiveContractId(db, free, 0));

        db.Rollback();
        Assert.Equal(before, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM DYN_joueur")));
    }

    [Fact]
    public void RoundTripEditAndRollback_LeavesDatabaseUntouched()
    {
        using var db = OpenReal(out _);
        if (db == null) return;
        var before = db.Scalar("SELECT nom_equipe FROM DYN_equipe WHERE id_equipe=85")!.ToString();
        var te = new RowEditor(db, "DYN_equipe", "id_equipe", 85, FieldConfigs.Team());
        te["nom_equipe"]!.Text = "Test Name";
        te.Save();
        Assert.True(PbmCodec.TryDecode(db.Scalar("SELECT nom_equipe FROM DYN_equipe WHERE id_equipe=85")!.ToString()!, out var n));
        Assert.Equal("Test Name", n);
        db.Rollback();
        Assert.Equal(before, db.Scalar("SELECT nom_equipe FROM DYN_equipe WHERE id_equipe=85")!.ToString());
    }
}
