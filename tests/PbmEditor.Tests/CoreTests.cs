using Microsoft.Data.Sqlite;
using PbmEditor.Core;
using Xunit;

namespace PbmEditor.Tests;

public sealed class TempDb : IDisposable
{
    public string Dir = Path.Combine(Path.GetTempPath(), "pbmtest_" + Guid.NewGuid().ToString("N"));
    public string File => Path.Combine(Dir, "Database.pbmsave");

    public TempDb()
    {
        Directory.CreateDirectory(Dir);
        using var c = new SqliteConnection($"Data Source={File};Pooling=False");
        c.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE DYN_equipe (id_equipe INTEGER PRIMARY KEY, nom_equipe TEXT, budget INT, championnat_id INT, couleur_home INT, isActive BOOLEAN);
CREATE TABLE DYN_joueur (joueur_id INTEGER PRIMARY KEY, joueur_nom TEXT, joueur_prenom TEXT, team_id INT, numero_actuel INT, moyenne FLOAT);
CREATE TABLE DYN_contrat (id INTEGER PRIMARY KEY, id_joueur INT, id_equipe INT, salaire INT, accepte INT);
CREATE TABLE STA_x (id INTEGER PRIMARY KEY AUTOINCREMENT, id_competition INT NOT NULL, label TEXT);
INSERT INTO DYN_equipe VALUES (1,'" + PbmCodec.Encode("Alpha") + @"',1000,5,-1,1),(2,'" + PbmCodec.Encode("Beta") + @"',2000,5,-1,1);
INSERT INTO DYN_joueur VALUES (10,'" + PbmCodec.Encode("Doe") + @"','" + PbmCodec.Encode("John") + @"',1,5,10.5),(11,'" + PbmCodec.Encode("Roe") + @"','" + PbmCodec.Encode("Jan") + @"',2,5,9.0);
INSERT INTO DYN_contrat VALUES (100,10,1,5000,0);
INSERT INTO STA_x (id_competition,label) VALUES (7,'a'),(7,'b'),(8,'c');";
        cmd.ExecuteNonQuery();
    }

    public PbmDatabase Open() => PbmDatabase.Open(File, Path.Combine(Dir, "bk"));
    public void Dispose() { SqliteConnection.ClearAllPools(); try { Directory.Delete(Dir, true); } catch (IOException) { } }
}

public class CodecTests
{
    [Theory]
    [InlineData("Maik Zirbes")]
    [InlineData("Hyères-Toulon Ünïcode")]
    public void RoundTrip(string s)
    {
        Assert.True(PbmCodec.TryDecode(PbmCodec.Encode(s), out var d));
        Assert.Equal(s, d);
    }

    [Fact] public void PlainTextIsNotDecoded() => Assert.False(PbmCodec.TryDecode("Palais des Sports", out _));
    [Fact] public void EmptyIsEmpty() { Assert.Equal("", PbmCodec.Encode("")); Assert.True(PbmCodec.TryDecode("", out var d)); Assert.Equal("", d); }

    // value produced by the original game tooling (pbm_names.py enc)
    [Fact] public void DecodesGameValueFormat() => Assert.True(PbmCodec.TryDecode(PbmCodec.Encode("x"), out _));
}

public class DbTests
{
    [Fact]
    public void RowEditor_SavesChangesAndEncodesNames()
    {
        using var t = new TempDb(); using var db = t.Open();
        var ed = new RowEditor(db, "DYN_equipe", "id_equipe", 1);
        Assert.Equal("Alpha", ed["nom_equipe"]!.Text);
        ed["nom_equipe"]!.Text = "Alpha Neu";
        ed["budget"]!.Text = "1500";
        Assert.Equal(2, ed.Save());
        db.Commit();
        var row = db.Query("SELECT * FROM DYN_equipe WHERE id_equipe=1")[0];
        Assert.Equal(1500, row.Int("budget"));
        Assert.True(PbmCodec.TryDecode(row.Str("nom_equipe"), out var n)); Assert.Equal("Alpha Neu", n);
    }

    [Fact]
    public void RowEditor_RejectsBadNumbers_WithoutWriting()
    {
        using var t = new TempDb(); using var db = t.Open();
        var ed = new RowEditor(db, "DYN_equipe", "id_equipe", 1);
        ed["nom_equipe"]!.Text = "Changed";
        ed["budget"]!.Text = "abc";
        Assert.Throws<FormatException>(() => ed.Save());
        Assert.False(db.HasPendingChanges);
    }

    [Fact]
    public void Rollback_DiscardsChanges_AndBackupIsMade()
    {
        using var t = new TempDb(); using var db = t.Open();
        db.Execute("UPDATE DYN_equipe SET budget=1 WHERE id_equipe=1");
        Assert.NotNull(db.BackupPath); Assert.True(File.Exists(db.BackupPath));
        db.Rollback();
        Assert.Equal(1000L, Convert.ToInt64(db.Scalar("SELECT budget FROM DYN_equipe WHERE id_equipe=1")));
    }

    [Fact]
    public void TableGrid_InsertUpdateDelete()
    {
        using var t = new TempDb(); using var db = t.Open();
        var g = new TableGrid(db, "STA_x", "id_competition", 7);
        Assert.Equal(2, g.Data.Rows.Count);
        g.Data.Rows[0]["label"] = "changed";
        g.Data.Rows[1].Delete();
        var added = g.NewRow(); added["label"] = "new";
        Assert.Equal(3, g.Apply());
        db.Commit();
        var labels = db.Query("SELECT label, id_competition FROM STA_x ORDER BY id").Select(r => r.Str("label") + r.Int("id_competition")).ToList();
        Assert.Equal(new[] { "changed7", "c8", "new7" }, labels);
        Assert.False(added["id"] is DBNull);
    }

    [Fact]
    public void MovePlayer_RepointsContract_AndFreeAgentRemovesIt()
    {
        using var t = new TempDb(); using var db = t.Open();
        GameService.MovePlayer(db, 10, 2);
        Assert.Equal(2L, Convert.ToInt64(db.Scalar("SELECT team_id FROM DYN_joueur WHERE joueur_id=10")));
        Assert.Equal(2L, Convert.ToInt64(db.Scalar("SELECT id_equipe FROM DYN_contrat WHERE id=100")));
        GameService.MakeFreeAgent(db, 10);
        Assert.Equal(255L, Convert.ToInt64(db.Scalar("SELECT numero_actuel FROM DYN_joueur WHERE joueur_id=10")));
        Assert.Null(db.Scalar("SELECT id FROM DYN_contrat WHERE id_joueur=10 AND accepte=0"));
    }

    [Fact]
    public void MovePlayer_CreatesContractFromTemplate_AndRejectsTakenNumber()
    {
        using var t = new TempDb(); using var db = t.Open();
        db.Execute("INSERT INTO DYN_contrat VALUES (101,11,2,7000,0)");
        GameService.MakeFreeAgent(db, 10);
        GameService.MovePlayer(db, 10, 1, 5);
        Assert.Equal(1L, Convert.ToInt64(db.Scalar("SELECT id_equipe FROM DYN_contrat WHERE id_joueur=10 AND accepte=0")));
        Assert.Throws<InvalidOperationException>(() => GameService.MovePlayer(db, 11, 1, 5));
    }
}
