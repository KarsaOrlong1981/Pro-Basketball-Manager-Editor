using System.Globalization;

namespace PbmEditor.Core;

/// <summary>Edits one row of a table as a list of typed fields, built from the live schema.</summary>
public sealed class RowEditor
{
    const int MaxEditableTextLength = 1000; // longer values are serialized blobs: show, never write

    readonly PbmDatabase _db;
    readonly string _keyColumn;
    readonly object _keyValue;

    public string Table { get; }
    public bool Exists { get; private set; }
    public List<Field> Fields { get; } = new();
    public bool IsDirty => Fields.Any(f => f.Dirty);

    public RowEditor(PbmDatabase db, string table, string keyColumn, object keyValue, FieldSet? set = null)
    {
        _db = db; Table = table; _keyColumn = keyColumn; _keyValue = keyValue;
        Load(set ?? new FieldSet());
    }

    void Load(FieldSet set)
    {
        var info = _db.GetTable(Table) ?? throw new InvalidOperationException(Loc.T("Data not found."));
        var row = _db.Query($"SELECT * FROM \"{Table}\" WHERE \"{_keyColumn}\"=@p0", _keyValue).FirstOrDefault();
        Exists = row != null;
        if (row == null) return;

        var pk = info.PkColumns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = info.Columns
            .OrderBy(c => set.IndexOf(c.Name) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(c => c.PkIndex == 0);

        foreach (var col in ordered)
        {
            // with a field set, internal columns without a spec stay hidden; without one (generic use) all are shown
            if (set.Items.Count > 0 && set.Find(col.Name) == null) continue;
            var spec = set.Find(col.Name);
            var raw = row[col.Name];
            var encoded = col.Kind == ColumnKind.Text && PbmCodec.IsEncodedColumn(Table, col.Name);
            var text = ToText(raw, encoded);
            var isKey = pk.Contains(col.Name) || col.Name.Equals(_keyColumn, StringComparison.OrdinalIgnoreCase);
            var tooLong = raw is string s && s.Length > MaxEditableTextLength;

            var kind = spec?.Kind ?? (col.IsBool ? FieldKind.Bool : col.Kind switch
            {
                ColumnKind.Integer => FieldKind.Integer,
                ColumnKind.Real => FieldKind.Real,
                _ => FieldKind.Text,
            });
            IReadOnlyList<Choice>? choices = null;
            if (spec?.Choices != null) { choices = spec.Choices(_db); kind = FieldKind.Choice; }
            if (raw is byte[]) { text = "(binary)"; tooLong = true; }

            var f = new Field
            {
                Column = col,
                Label = spec?.Label ?? Prettify(col.Name),
                Group = spec?.Group ?? "Other",
                Kind = kind,
                Min = spec?.Min ?? 0, Max = spec?.Max ?? 0, Step = spec?.Step ?? 1,
                ReadOnly = isKey || tooLong || (spec?.ReadOnly ?? false),
                Encoded = encoded,
                Choices = choices,
                OriginalValue = raw,
            };
            f.Text = text;
            f.OriginalText = text;
            Fields.Add(f);
        }
    }

    static string ToText(object? raw, bool encoded) => raw switch
    {
        null => "",
        string s when encoded => PbmCodec.TryDecode(s, out var d) ? d : s,
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => raw.ToString() ?? "",
    };

    public static string Prettify(string col) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(col.Replace('_', ' '));

    public Field? this[string column] => Fields.FirstOrDefault(f => f.Name.Equals(column, StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes changed fields into the database transaction. Throws FormatException on bad input.</summary>
    public int Save()
    {
        var dirty = Fields.Where(f => f.Dirty).ToList();
        if (dirty.Count == 0) return 0;
        var values = dirty.Select(f => f.ToDbValue()).ToList(); // validate everything before writing
        var owner = ChangeNames.RowOwner(_db, Table, _keyColumn, _keyValue);
        var sets = string.Join(",", dirty.Select((f, i) => $"\"{f.Name}\"=@p{i}"));
        var args = values.Append(_keyValue).ToArray();
        _db.Execute($"UPDATE \"{Table}\" SET {sets} WHERE \"{_keyColumn}\"=@p{values.Count}", args);
        foreach (var f in dirty) _db.Note($"{owner}: {f.Label}: {ChangeNames.Show(f, f.OriginalText)} -> {ChangeNames.Show(f, f.Text)}");
        foreach (var f in dirty) { f.OriginalValue = f.ToDbValue(); f.MarkClean(); }
        return dirty.Count;
    }

    public void Revert()
    {
        foreach (var f in Fields.Where(f => f.Dirty)) f.Text = f.OriginalText;
    }
}
