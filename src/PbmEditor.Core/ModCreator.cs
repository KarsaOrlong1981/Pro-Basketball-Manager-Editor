using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PbmEditor.Core;

public sealed record NewModResult(string Folder, string DatabasePath);

/// <summary>
/// Creates a new local mod from the game's default data, the same way the game's "duplicate default mod" does:
/// Datas\BM_database(.pbmsave|_stats.pbmsave) -> Database.pbmsave / Stats.pbmsave, images\modding\* -> mod folders,
/// plus mod_info.xml.
/// </summary>
public static class ModCreator
{
    static readonly string[] ImageFolders = { "player_faces", "coach_faces", "team_logos", "league_logos", "flags", "jerseys", "arenas" };

    /// <summary>Steam install folder of the game (default locations and extra Steam libraries), or null.</summary>
    public static string? FindGameDir()
    {
        var roots = new List<string> { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" };
        foreach (var r in roots.ToList())
        {
            var vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                roots.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return roots.Select(r => Path.Combine(r, "steamapps", "common", "Pro Basketball Manager 2025"))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "PBM2025_Data", "Datas", "BM_database.pbmsave")));
    }

    public static string FolderNameFor(string title)
    {
        var d = title.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Returns an error message (translated) for an invalid mod name, or null.</summary>
    public static string? ValidateName(string title, string modsDir)
    {
        if (title.Trim().Length == 0) return Loc.T("Please enter a mod name.");
        if (title.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return Loc.T("The name contains characters that are not allowed in folder names.");
        if (Directory.Exists(Path.Combine(modsDir, FolderNameFor(title)))) return Loc.T("A mod with this name already exists.");
        return null;
    }

    public static NewModResult Create(string title, string modsDir, string gameDir, IProgress<string>? progress = null)
    {
        var error = ValidateName(title, modsDir);
        if (error != null) throw new InvalidOperationException(error);

        var data = Path.Combine(gameDir, "PBM2025_Data", "Datas");
        var images = Path.Combine(gameDir, "PBM2025_Data", "images", "modding");
        var srcDb = Path.Combine(data, "BM_database.pbmsave");
        var srcStats = Path.Combine(data, "BM_database_stats.pbmsave");
        if (!File.Exists(srcDb) || !File.Exists(srcStats))
            throw new FileNotFoundException(Loc.F("Default game data not found in {0}.", data));

        var folder = Path.Combine(modsDir, FolderNameFor(title));
        try
        {
            Directory.CreateDirectory(folder);
            progress?.Report(Loc.T("Copying database..."));
            File.Copy(srcDb, Path.Combine(folder, "Database.pbmsave"));
            progress?.Report(Loc.T("Copying statistics (large file)..."));
            File.Copy(srcStats, Path.Combine(folder, "Stats.pbmsave"));
            progress?.Report(Loc.T("Copying images..."));
            var preview = Path.Combine(images, "preview.png");
            if (File.Exists(preview)) File.Copy(preview, Path.Combine(folder, "preview.png"));
            foreach (var f in ImageFolders)
            {
                var src = Path.Combine(images, f);
                var dst = Path.Combine(folder, f);
                Directory.CreateDirectory(dst);
                if (!Directory.Exists(src)) continue;
                foreach (var file in Directory.GetFiles(src)) File.Copy(file, Path.Combine(dst, Path.GetFileName(file)));
            }
            WriteModInfo(folder, title.Trim());
        }
        catch
        {
            // do not leave a half-copied mod behind: the game would list it
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return new NewModResult(folder, Path.Combine(folder, "Database.pbmsave"));
    }

    static void WriteModInfo(string folder, string title)
    {
        var tags = new[] { "Database", "Player Faces", "Coach Faces", "Team Logos", "League Logos", "Flags", "Jerseys", "Arenas" };
        XNamespace xsd = "http://www.w3.org/2001/XMLSchema", xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("ItemModWrapper", new XAttribute(XNamespace.Xmlns + "xsd", xsd), new XAttribute(XNamespace.Xmlns + "xsi", xsi),
                new XElement("m_PublishedFileId", 0),
                new XElement("Title", title),
                new XElement("Description", ""),
                new XElement("ContentFolder", folder.Replace('\\', '/')),
                new XElement("PreviewFileImage", File.Exists(Path.Combine(folder, "preview.png")) ? Path.Combine(folder, "preview.png") : ""),
                new XElement("Tags", tags.Select(t => new XElement("string", t))),
                new XElement("Status", "Installed"),
                new XElement("Visibility", "Public"),
                new XElement("DateCreated", "0001-01-01T00:00:00"),
                new XElement("DateLastUpdate", "0001-01-01T00:00:00"),
                new XElement("IsDefaultMod", "false")));
        doc.Save(Path.Combine(folder, "mod_info.xml"));
    }
}
