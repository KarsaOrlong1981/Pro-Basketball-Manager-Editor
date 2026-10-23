using System.Text.RegularExpressions;
using PbmEditor.Core;
using Xunit;

namespace PbmEditor.Tests;

[Collection("Loc")]
public class LocTests
{
    static string[] Placeholders(string s) => Regex.Matches(s, @"\{\d+[^}]*\}").Select(m => m.Value).OrderBy(x => x).ToArray();

    [Fact]
    public void TableLoadsAndEveryTranslationKeepsItsPlaceholders()
    {
        var all = Loc.Entries.ToList();
        Assert.True(all.Count > 250);
        foreach (var (en, de, fr) in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(de), "German missing: " + en);
            Assert.False(string.IsNullOrWhiteSpace(fr), "French missing: " + en);
            Assert.Equal(Placeholders(en), Placeholders(de));
            Assert.Equal(Placeholders(en), Placeholders(fr));
        }
    }

    [Fact]
    public void SwitchesLanguage_AndFallsBackToEnglish()
    {
        try
        {
            Loc.Language = "en";
            Assert.Equal("Save", Loc.T("Save"));
            Loc.Language = "de";
            Assert.Equal("Speichern", Loc.T("Save"));
            Loc.Language = "fr";
            Assert.Equal("Enregistrer", Loc.T("Save"));
            Assert.Equal("Not translated yet", Loc.T("Not translated yet"));
            Assert.Equal("3 lignes", Loc.F("{0} rows", 3));
            Loc.Language = "xx"; // unknown code falls back to English
            Assert.Equal("en", Loc.Language);
        }
        finally { Loc.Language = "en"; }
    }

    [Fact]
    public void FieldLabelsAndGroupsFollowTheLanguage()
    {
        try
        {
            Loc.Language = "de";
            var spec = FieldConfigs.Team().Find("budget")!;
            Assert.Equal("Finanzen", spec.Group);
            Assert.Equal("Gründungsjahr", "Gründungsjahr"); // sanity for umlaut handling in the test file
            Assert.Equal("Gegründet (Jahr)", FieldConfigs.Team().Find("creation")!.Label);
            Loc.Language = "fr";
            Assert.Equal("Fondé (année)", FieldConfigs.Team().Find("creation")!.Label);
        }
        finally { Loc.Language = "en"; }
    }

    [Fact]
    public void ChangeNotifiesListeners()
    {
        var n = 0;
        Action a = () => n++;
        Loc.Subscribe(a);
        try { Loc.Language = "de"; Loc.Language = "en"; }
        finally { Loc.Language = "en"; }
        Assert.Equal(2, n);
        GC.KeepAlive(a);
    }
}

[Collection("Loc")]
public class ModCreatorTests
{
    static string FakeGame(string root)
    {
        var g = Path.Combine(root, "game");
        var datas = Path.Combine(g, "PBM2025_Data", "Datas");
        var img = Path.Combine(g, "PBM2025_Data", "images", "modding");
        Directory.CreateDirectory(datas);
        File.WriteAllText(Path.Combine(datas, "BM_database.pbmsave"), "db");
        File.WriteAllText(Path.Combine(datas, "BM_database_stats.pbmsave"), "stats");
        Directory.CreateDirectory(Path.Combine(img, "team_logos"));
        File.WriteAllText(Path.Combine(img, "team_logos", "1.png"), "png");
        File.WriteAllText(Path.Combine(img, "preview.png"), "p");
        return g;
    }

    [Fact]
    public void CreatesModFolderLikeTheGame()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbmmod_" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = FakeGame(root);
            var mods = Path.Combine(root, "Mods");
            var r = ModCreator.Create("Mon Équipe", mods, game);
            Assert.EndsWith("Mon Equipe", r.Folder);
            Assert.Equal("db", File.ReadAllText(r.DatabasePath));
            Assert.Equal("stats", File.ReadAllText(Path.Combine(r.Folder, "Stats.pbmsave")));
            Assert.True(File.Exists(Path.Combine(r.Folder, "team_logos", "1.png")));
            Assert.True(Directory.Exists(Path.Combine(r.Folder, "jerseys")));
            var xml = File.ReadAllText(Path.Combine(r.Folder, "mod_info.xml"));
            Assert.Contains("<Title>Mon Équipe</Title>", xml);
            Assert.Contains("<IsDefaultMod>false</IsDefaultMod>", xml);
            Assert.NotNull(ModCreator.ValidateName("Mon Équipe", mods)); // now exists
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    public void RejectsBadNames(string name)
    {
        Assert.NotNull(ModCreator.ValidateName(name, Path.GetTempPath()));
    }

    [Fact]
    public void FailedCopyLeavesNoHalfMod()
    {
        var root = Path.Combine(Path.GetTempPath(), "pbmmod_" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = FakeGame(root);
            File.Delete(Path.Combine(game, "PBM2025_Data", "Datas", "BM_database_stats.pbmsave"));
            var mods = Path.Combine(root, "Mods");
            Assert.Throws<FileNotFoundException>(() => ModCreator.Create("X", mods, game));
            Assert.False(Directory.Exists(Path.Combine(mods, "X")));
        }
        finally { Directory.Delete(root, true); }
    }
}
