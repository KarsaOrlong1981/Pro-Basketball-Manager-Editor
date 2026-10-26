using Microsoft.Data.Sqlite;
using PbmEditor.Core;
using Xunit;

namespace PbmEditor.Tests;

public class PlayerCreatorTests
{
    sealed class PlayerDb : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "pbmtest_" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(_dir, "Database.pbmsave");

        public PlayerDb(string contractExtra = "")
        {
            Directory.CreateDirectory(_dir);
            using var c = new SqliteConnection($"Data Source={File};Pooling=False");
            c.Open();
            var attrs = string.Join(",", FieldConfigs.AttributeColumns.Select(a => a + " FLOAT"));
            var cmd = c.CreateCommand();
            cmd.CommandText = $@"
CREATE TABLE DYN_equipe (id_equipe INTEGER PRIMARY KEY, nom_equipe TEXT, championnat_id INT);
CREATE TABLE DYN_date (date_jour INT, date_mois INT, date_annee INT);
CREATE TABLE DYN_joueur (joueur_id INTEGER PRIMARY KEY, joueur_nom TEXT, joueur_prenom TEXT, jour_naissance TINYINT NOT NULL, mois_naissance TINYINT NOT NULL,
  annee_naissance SMALLINT NOT NULL, team_id INT NOT NULL DEFAULT 0, nationalite INT NOT NULL, taille SMALLINT NOT NULL, sexe TINYINT NOT NULL,
  numero_actuel TINYINT NOT NULL DEFAULT 255, moyenne FLOAT, joueur_valeur INT, age TINYINT NOT NULL DEFAULT 0, newcol_required INT NOT NULL);
CREATE TABLE DYN_joueur_attributes (joueur_id INTEGER PRIMARY KEY, {attrs}, moyenne FLOAT, cache_potentiel FLOAT);
CREATE TABLE DYN_contrat (id INTEGER PRIMARY KEY, id_joueur INT, id_equipe INT, salaire INT{contractExtra}, accepte INT, debut_annee INT, fin_annee INT, fin_mois INT, fin_jour INT,
  start_timestamp INT, end_timestamp INT);
CREATE TABLE DYN_langue_pays (id INTEGER PRIMARY KEY, id_langue INT, id_pays INT, ordre INT);
CREATE TABLE DYN_joueur_langue (id_joueur INT, id_langue INT, apprentissage FLOAT);
INSERT INTO DYN_equipe VALUES (1,'{PbmCodec.Encode("Alpha")}',5);
INSERT INTO DYN_date VALUES (1,7,2024);
INSERT INTO DYN_joueur (joueur_id,joueur_nom,joueur_prenom,jour_naissance,mois_naissance,annee_naissance,team_id,nationalite,taille,sexe,numero_actuel,newcol_required)
  VALUES (100119000,'','',1,1,2000,1,2,190,0,5,0);
INSERT INTO DYN_langue_pays VALUES (1, 69, 23, 1), (2, 2, 23, 2);";
            cmd.ExecuteNonQuery();
        }

        public PbmDatabase Open() => PbmDatabase.Open(File, Path.Combine(_dir, "bk"));
        public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(_dir, true); } catch (IOException) { } }
    }

    static NewPlayer Sample(long team = 0, long? number = null, long salary = 4000, int years = 2) =>
        new("Jan", "Müller", 15, 3, 2002, false, 23, 198, 92, PlayerPosition.Center, 9.0, 14, 0, 11, 1, 0, team, number, salary, years);

    [Fact]
    public void FreeAgent_GetsPlayerAttributesLanguageAndNoContract()
    {
        using var t = new PlayerDb(); using var db = t.Open();
        var id = PlayerCreator.Create(db, Sample());
        Assert.Equal(100119001, id);
        var p = db.Query("SELECT * FROM DYN_joueur WHERE joueur_id=@p0", id).Single();
        Assert.True(PbmCodec.TryDecode(p.Str("joueur_nom"), out var last)); Assert.Equal("Müller", last);
        Assert.Equal(0, p.Int("team_id")); Assert.Equal(255, p.Int("numero_actuel"));
        Assert.Equal(22, p.Int("age")); // born 15 Mar 2002, game date 1 Jul 2024
        Assert.Equal(0, p.Int("newcol_required")); // required column unknown to the code gets a neutral value
        var a = db.Query("SELECT * FROM DYN_joueur_attributes WHERE joueur_id=@p0", id).Single();
        Assert.Equal(14.0, a.Dbl("cache_potentiel"));
        Assert.Equal(p.Dbl("moyenne"), a.Dbl("moyenne"));
        Assert.Empty(db.Query("SELECT 1 FROM DYN_contrat"));
        var lang = db.Query("SELECT * FROM DYN_joueur_langue WHERE id_joueur=@p0", id).Single();
        Assert.Equal(69, lang.Int("id_langue")); Assert.Equal(100.0, lang.Dbl("apprentissage"));
        Assert.Contains(db.Changes, c => c.Contains("free agent"));
    }

    [Fact]
    public void TeamPlayer_GetsActiveContractWithGameDatesAndFreeNumber()
    {
        using var t = new PlayerDb(); using var db = t.Open();
        var id = PlayerCreator.Create(db, Sample(team: 1));
        var p = db.Query("SELECT team_id, numero_actuel FROM DYN_joueur WHERE joueur_id=@p0", id).Single();
        Assert.Equal(1, p.Int("team_id")); Assert.Equal(0, p.Int("numero_actuel")); // 5 is taken, 0 is the first free number
        var c = db.Query("SELECT * FROM DYN_contrat WHERE id_joueur=@p0", id).Single();
        Assert.Equal(0, c.Int("accepte")); Assert.Equal(1, c.Int("id_equipe")); Assert.Equal(4000, c.Int("salaire"));
        Assert.Equal(2026, c.Int("fin_annee")); Assert.Equal(7, c.Int("fin_mois")); Assert.Equal(1, c.Int("fin_jour"));
        Assert.Equal(1719792000, c.Int("start_timestamp")); // 2024-07-01 00:00 UTC
        Assert.Equal(1782864000, c.Int("end_timestamp")); // 2026-07-01 00:00 UTC
        Assert.Contains(db.Changes, x => x.Contains("Alpha"));
    }

    [Fact]
    public void TakenJerseyNumber_IsRejected_WithoutWriting()
    {
        using var t = new PlayerDb(); using var db = t.Open();
        Assert.Throws<InvalidOperationException>(() => PlayerCreator.Create(db, Sample(team: 1, number: 5)));
        Assert.False(db.HasPendingChanges);
        Assert.Equal(1L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM DYN_joueur")));
    }

    [Theory]
    [InlineData("", "X", 1, 1, 2000)]
    [InlineData("A", "", 1, 1, 2000)]
    [InlineData("A", "B", 31, 2, 2000)]
    [InlineData("A", "B", 1, 13, 2000)]
    public void InvalidInput_ReturnsAMessage(string first, string last, int d, int m, int y)
    {
        using var t = new PlayerDb(); using var db = t.Open();
        var p = Sample() with { FirstName = first, LastName = last, BirthDay = d, BirthMonth = m, BirthYear = y };
        Assert.NotNull(PlayerCreator.Validate(db, p));
        Assert.Throws<InvalidOperationException>(() => PlayerCreator.Create(db, p));
    }

    [Fact]
    public void FailureInTheMiddle_RollsBackThePartialPlayer()
    {
        using var t = new PlayerDb(contractExtra: " CHECK(salaire < 100)"); using var db = t.Open();
        Assert.ThrowsAny<SqliteException>(() => PlayerCreator.Create(db, Sample(team: 1, salary: 5000)));
        Assert.Equal(1L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM DYN_joueur")));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM DYN_joueur_attributes")));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM DYN_joueur_langue")));
        Assert.Empty(db.Changes);
    }

    [Fact]
    public void Attributes_FollowOverallAndPosition()
    {
        var cols = FieldConfigs.AttributeColumns.ToList();
        var pg = PlayerCreator.Attributes(10, PlayerPosition.PointGuard);
        var c = PlayerCreator.Attributes(10, PlayerPosition.Center);
        Assert.Equal(cols.Count, pg.Length);
        Assert.All(pg.Concat(c), v => Assert.InRange(v, 1, 20));
        Assert.True(pg[cols.IndexOf("attribut_dribble")] > c[cols.IndexOf("attribut_dribble")]);
        Assert.True(c[cols.IndexOf("attribut_contre")] > pg[cols.IndexOf("attribut_contre")]);
        Assert.InRange(PlayerCreator.OverallFor(pg.Sum()), 9.5, 10.5);
        Assert.InRange(PlayerCreator.OverallFor(c.Sum()), 9.5, 10.5);
    }

    [Fact]
    public void SuggestedSalaryAndValue_GrowWithOverall()
    {
        long prevSalary = 0, prevValue = 0;
        foreach (var o in new[] { 4.0, 6.5, 8.0, 10.2, 12.0, 15.0 })
        {
            Assert.True(PlayerCreator.SuggestSalary(o) > prevSalary); prevSalary = PlayerCreator.SuggestSalary(o);
            Assert.True(PlayerCreator.SuggestValue(o) > prevValue); prevValue = PlayerCreator.SuggestValue(o);
        }
    }
}
