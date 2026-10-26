using System.Globalization;
using System.Text;

namespace PbmEditor.Core;

/// <summary>One club of a team list. City and coordinates are optional; they decide where the club is placed on the map.</summary>
public sealed record TeamEntry(string Name, int? Group = null, string? City = null, double? Lat = null, double? Lon = null);

/// <summary>The clubs of a list, with the group labels in the order of their first appearance (label n = group n).</summary>
public sealed record ImportedTeamList(IReadOnlyList<TeamEntry> Entries, IReadOnlyList<string> GroupLabels, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads a team list from CSV text. Columns (header row required, any order, German or English): Team, Gruppe/Group, Stadt/City,
/// Breitengrad/Latitude, Laengengrad/Longitude. Other columns are ignored. Delimiter ; , or tab; decimal comma allowed.
/// </summary>
public static class TeamListImporter
{
    static readonly string[] TeamHeaders = { "team", "name", "teamname", "verein", "mannschaft", "club" };
    static readonly string[] GroupHeaders = { "gruppe", "group" };
    static readonly string[] CityHeaders = { "stadt", "city", "ort" };
    static readonly string[] LatHeaders = { "breitengrad", "latitude", "lat" };
    static readonly string[] LonHeaders = { "laengengrad", "längengrad", "longitude", "lon", "lng" };

    /// <exception cref="FormatException">The list is empty, has no Team column or a line is invalid (the message names the line).</exception>
    public static ImportedTeamList Parse(string text)
    {
        var rows = ReadRows(text);
        if (rows.Count == 0) throw new FormatException(Loc.T("The team list is empty."));

        var header = rows[0].Fields.Select(h => h.Trim().Trim('﻿').ToLowerInvariant()).ToList();
        int Col(string[] names) => header.FindIndex(h => names.Contains(h));
        var (team, group, city, lat, lon) = (Col(TeamHeaders), Col(GroupHeaders), Col(CityHeaders), Col(LatHeaders), Col(LonHeaders));
        if (team < 0) throw new FormatException(Loc.T("The team list needs a header row with a 'Team' column."));

        string Cell(List<string> f, int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";
        var entries = new List<TeamEntry>();
        var labels = new List<string>();
        var warnings = new List<string>();
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var (lineNo, fields) in rows.Skip(1))
        {
            var name = Cell(fields, team);
            if (name.Length == 0) throw new FormatException(Loc.F("Line {0}: the team name is missing.", lineNo));
            if (!names.Add(name)) warnings.Add(Loc.F("Line {0}: the team name '{1}' appears more than once.", lineNo, name));

            int? g = null;
            var label = Cell(fields, group);
            if (label.Length > 0)
            {
                var i = labels.FindIndex(l => l.Equals(label, StringComparison.CurrentCultureIgnoreCase));
                if (i < 0) { labels.Add(label); i = labels.Count - 1; }
                g = i + 1;
            }

            double? la = null, lo = null;
            var (laText, loText) = (Cell(fields, lat), Cell(fields, lon));
            if (laText.Length > 0 || loText.Length > 0)
            {
                if (!TryNumber(laText, -90, 90, out var a) || !TryNumber(loText, -180, 180, out var b))
                    throw new FormatException(Loc.F("Line {0}: latitude and longitude must both be numbers (latitude -90 to 90, longitude -180 to 180).", lineNo));
                (la, lo) = (a, b);
            }
            var c = Cell(fields, city);
            entries.Add(new TeamEntry(name, g, c.Length > 0 ? c : null, la, lo));
        }
        if (entries.Count == 0) throw new FormatException(Loc.T("The team list is empty."));
        return new ImportedTeamList(entries, labels, warnings);
    }

    static bool TryNumber(string s, double min, double max, out double v) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= min && v <= max;

    /// <summary>Splits CSV text into rows of fields (quotes, doubled quotes, line breaks inside quotes). Blank rows are dropped; line numbers are 1-based.</summary>
    static List<(int Line, List<string> Fields)> ReadRows(string text)
    {
        text = text.TrimStart('﻿');
        var delimiter = Delimiter(text);
        var rows = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var (inQuotes, line, startLine) = (false, 1, 1);

        void EndField() { fields.Add(field.ToString()); field.Clear(); }
        void EndRow()
        {
            EndField();
            if (fields.Any(f => f.Trim().Length > 0)) rows.Add((startLine, fields));
            fields = new List<string>();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else { field.Append(ch); if (ch == '\n') line++; }
            }
            else if (ch == '"' && field.Length == 0) inQuotes = true;
            else if (ch == delimiter) EndField();
            else if (ch == '\r') { }
            else if (ch == '\n') { EndRow(); line++; startLine = line; }
            else field.Append(ch);
        }
        if (field.Length > 0 || fields.Count > 0) EndRow();
        return rows;
    }

    /// <summary>The delimiter that occurs most in the first non-empty line (outside quotes); ; wins ties.</summary>
    static char Delimiter(string text)
    {
        var first = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        var counts = new Dictionary<char, int> { [';'] = 0, ['\t'] = 0, [','] = 0 };
        var quoted = false;
        foreach (var ch in first)
        {
            if (ch == '"') quoted = !quoted;
            else if (!quoted && counts.ContainsKey(ch)) counts[ch]++;
        }
        return counts.OrderByDescending(c => c.Value).First().Key;
    }
}
