using System.Globalization;
using System.Reflection;

namespace PbmEditor.Core;

/// <summary>
/// UI translations. The English text is the key; a missing entry falls back to English, so a gap is never fatal.
/// Table: embedded translations.tsv (English TAB German TAB French), one entry per line.
/// </summary>
public static class Loc
{
    public static readonly (string Code, string Name)[] Languages = { ("en", "English"), ("de", "Deutsch"), ("fr", "Français") };

    static readonly Dictionary<string, (string De, string Fr)> Table = LoadTable();
    static readonly List<WeakReference<Action>> Listeners = new();
    static string _lang = "en";

    public static string Language
    {
        get => _lang;
        set
        {
            var v = Languages.Any(l => l.Code == value) ? value : "en";
            if (v == _lang) return;
            _lang = v;
            foreach (var w in Listeners.ToList())
            {
                if (w.TryGetTarget(out var a)) a();
                else Listeners.Remove(w);
            }
        }
    }

    /// <summary>Called whenever the language changes (held weakly).</summary>
    public static void Subscribe(Action onChange) => Listeners.Add(new WeakReference<Action>(onChange));

    public static string T(string english)
    {
        if (_lang == "en" || !Table.TryGetValue(english, out var t)) return english;
        var s = _lang == "de" ? t.De : t.Fr;
        return s.Length == 0 ? english : s;
    }

    public static string F(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);

    static Dictionary<string, (string, string)> LoadTable()
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PbmEditor.Core.translations.tsv");
        if (s == null) return d;
        using var r = new StreamReader(s);
        while (r.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split('\t');
            if (p.Length >= 3) d[Unescape(p[0])] = (Unescape(p[1]), Unescape(p[2]));
        }
        return d;
    }

    static string Unescape(string s)
    {
        const char Backslash = (char)92;
        if (s.IndexOf(Backslash) < 0) return s;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == Backslash && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] == 'n' ? (char)10 : s[i]); // \n = newline, \\ = backslash
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>All entries (for tests): English key, German, French.</summary>
    public static IEnumerable<(string En, string De, string Fr)> Entries => Table.Select(e => (e.Key, e.Value.De, e.Value.Fr));

    // ---- persisted choice ----

    static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PbmEditor", "language.txt");

    public static void LoadSetting()
    {
        try
        {
            if (File.Exists(SettingsFile)) Language = File.ReadAllText(SettingsFile).Trim();
        }
        catch (IOException) { /* keep English */ }
    }

    public static void SaveSetting()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, _lang);
        }
        catch (IOException) { /* not critical */ }
    }
}
