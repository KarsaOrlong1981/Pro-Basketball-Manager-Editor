using System.Globalization;

namespace PbmEditor.Core;

public enum FieldKind { Text, Integer, Real, Bool, Slider, Color, Choice }

public sealed record Choice(long Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record FieldSpec(
    string Label, string Group, FieldKind? Kind = null,
    double Min = 0, double Max = 0, double Step = 1, bool ReadOnly = false,
    Func<PbmDatabase, IReadOnlyList<Choice>>? Choices = null);

/// <summary>Display and ordering rules for one table. Columns without a spec land in group "Other".</summary>
public sealed class FieldSet
{
    readonly List<(string Col, FieldSpec Spec)> _items = new();
    public IReadOnlyList<(string Col, FieldSpec Spec)> Items => _items;
    public FieldSet Add(string col, string label, string group, FieldKind? kind = null,
        double min = 0, double max = 0, double step = 1, bool readOnly = false,
        Func<PbmDatabase, IReadOnlyList<Choice>>? choices = null)
    {
        _items.Add((col, new FieldSpec(Loc.T(label), Loc.T(group), kind, min, max, step, readOnly, choices)));
        return this;
    }
    public FieldSpec? Find(string col) => _items.FirstOrDefault(i => i.Col.Equals(col, StringComparison.OrdinalIgnoreCase)).Spec;
    public int IndexOf(string col) => _items.FindIndex(i => i.Col.Equals(col, StringComparison.OrdinalIgnoreCase));
}

public sealed class Field : ObservableObject
{
    string _text = "";
    public required ColumnInfo Column { get; init; }
    public string Name => Column.Name;
    public required string Label { get; init; }
    public required string Group { get; init; }
    public FieldKind Kind { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; } = 1;
    public bool ReadOnly { get; init; }
    public bool Encoded { get; init; }
    public IReadOnlyList<Choice>? Choices { get; init; }
    public string OriginalText { get; set; } = "";
    public object? OriginalValue { get; set; }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            Notify(nameof(Dirty)); Notify(nameof(Number)); Notify(nameof(Flag));
            Notify(nameof(Hex)); Notify(nameof(ChoiceValue));
        }
    }

    public bool Dirty => !ReadOnly && !string.Equals(_text, OriginalText, StringComparison.Ordinal);

    public double Number
    {
        get => double.TryParse(_text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        set => Text = value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public bool Flag
    {
        get => _text is "1" or "true" or "True";
        set => Text = value ? "1" : "0";
    }

    /// <summary>Signed ARGB integer as stored by the game, shown as #RRGGBB.</summary>
    public string Hex
    {
        get
        {
            if (!long.TryParse(_text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return "";
            return "#" + ((uint)v & 0xFFFFFF).ToString("X6");
        }
        set
        {
            var h = value.Trim().TrimStart('#');
            if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return;
            Text = ((int)(0xFF000000u | (uint)rgb)).ToString(CultureInfo.InvariantCulture);
        }
    }

    public long? ChoiceValue
    {
        get => long.TryParse(_text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        set { if (value != null) Text = value.Value.ToString(CultureInfo.InvariantCulture); }
    }

    public void MarkClean()
    {
        OriginalText = _text;
        Notify(nameof(Dirty));
    }

    /// <summary>Convert the edited text to the value written to the database.</summary>
    public object? ToDbValue()
    {
        var t = _text.Trim();
        if (t.Length == 0 && OriginalValue is null && Column.Kind != ColumnKind.Text) return null;
        switch (Column.Kind)
        {
            case ColumnKind.Integer:
                if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (long)Math.Round(d);
                throw new FormatException(Loc.F("'{0}' ({1}): '{2}' is not a whole number.", Label, Name, t));
            case ColumnKind.Real:
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return r;
                throw new FormatException(Loc.F("'{0}' ({1}): '{2}' is not a number.", Label, Name, t));
            default:
                if (_text.Length == 0 && OriginalValue is null) return null;
                return Encoded ? PbmCodec.Encode(_text) : _text;
        }
    }
}
