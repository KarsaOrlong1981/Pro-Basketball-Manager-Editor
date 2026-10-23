using System.Xml.Linq;

namespace PbmEditor.Core;

public sealed record ModEntry(string Name, string Kind, string DbPath, string? ImageRoot, bool ReadOnlyHint)
{
    public string Display => $"{Loc.T(Kind)}: {Name}";
    public override string ToString() => Display;
}

/// <summary>Finds local mods, Steam workshop mods and savegames. Any other file can be opened by path.</summary>
public static class ModLocator
{
    static readonly string[] ImageFolders = { "team_logos", "player_faces", "arenas", "jerseys", "league_logos", "coach_faces" };

    public static string GameDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Umix Studios", "Pro Basketball Manager 2025");

    public static List<ModEntry> Discover()
    {
        var list = new List<ModEntry>();
        var mods = Path.Combine(GameDataDir, "Mods");
        if (Directory.Exists(mods))
            foreach (var d in Directory.GetDirectories(mods))
                AddMod(list, d, "Local mod", false);

        foreach (var steam in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
        {
            var ws = Path.Combine(steam, "steamapps", "workshop", "content", "2935400");
            if (Directory.Exists(ws))
                foreach (var d in Directory.GetDirectories(ws))
                    AddMod(list, d, "Workshop (Steam overwrites it)", true);
        }

        var saves = Path.Combine(GameDataDir, "Saves");
        if (Directory.Exists(saves))
            foreach (var f in Directory.GetFiles(saves, "*.pbmsave").Where(f => !f.EndsWith("_StatsPBM.pbmsave", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
                list.Add(new ModEntry(Path.GetFileNameWithoutExtension(f), "Save", f, ImageRootForSave(f), false));
        return list;
    }

    static void AddMod(List<ModEntry> list, string dir, string kind, bool readOnlyHint)
    {
        var db = Path.Combine(dir, "Database.pbmsave");
        if (!File.Exists(db)) return;
        list.Add(new ModEntry(ModTitle(dir) ?? Path.GetFileName(dir), kind, db, dir, readOnlyHint));
    }

    static string? ModTitle(string dir)
    {
        try
        {
            var xml = Path.Combine(dir, "mod_info.xml");
            return File.Exists(xml) ? XDocument.Load(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "Title")?.Value : null;
        }
        catch (Exception e) when (e is System.Xml.XmlException or IOException) { return null; }
    }

    /// <summary>Image folder for an arbitrary database file: its own folder (mod) or the mod a savegame points to.</summary>
    public static string? ImageRootFor(string dbPath, PbmDatabase? db = null)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir != null && ImageFolders.Any(f => Directory.Exists(Path.Combine(dir, f)))) return dir;
        if (db?.GetTable("DYN_param_partie")?.Has("path_mod_associe") == true)
        {
            var p = db.Scalar("SELECT path_mod_associe FROM DYN_param_partie LIMIT 1")?.ToString();
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) return Path.GetFullPath(p);
        }
        return null;
    }

    static string? ImageRootForSave(string savePath)
    {
        try
        {
            using var db = PbmDatabase.Open(savePath);
            return ImageRootFor(savePath, db);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidDataException) { return null; }
    }
}
