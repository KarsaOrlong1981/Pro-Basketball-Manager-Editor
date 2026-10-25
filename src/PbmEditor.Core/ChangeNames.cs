namespace PbmEditor.Core;

/// <summary>Friendly names for the change list, so users see "Player X: Salary" instead of SQL.</summary>
public static class ChangeNames
{
    static string Dec(object? v) => v is string s && PbmCodec.TryDecode(s, out var d) ? d : v?.ToString() ?? "";

    static string Lookup(PbmDatabase db, string sql, long id, Func<Row, string> name, string fallback)
    {
        try { return db.Query(sql, id).FirstOrDefault() is { } r ? name(r).Trim() : fallback; }
        catch (Microsoft.Data.Sqlite.SqliteException) { return fallback; }
    }

    public static string Player(PbmDatabase db, long id) =>
        Lookup(db, "SELECT joueur_prenom, joueur_nom FROM DYN_joueur WHERE joueur_id=@p0", id,
            r => $"{Dec(r["joueur_prenom"])} {Dec(r["joueur_nom"])}", Loc.F("Player #{0}", id));

    public static string Person(PbmDatabase db, long id) =>
        Lookup(db, "SELECT prenom, nom FROM DYN_person WHERE id_person=@p0", id,
            r => $"{Dec(r["prenom"])} {Dec(r["nom"])}", Loc.F("Staff #{0}", id));

    public static string Team(PbmDatabase db, long id) =>
        id == 0 ? Loc.T("(free agent)") : Lookup(db, "SELECT nom_equipe FROM DYN_equipe WHERE id_equipe=@p0", id, r => Dec(r["nom_equipe"]), Loc.F("Team #{0}", id));

    public static string League(PbmDatabase db, long id) =>
        id == 0 ? Loc.T("(none)") : Lookup(db, "SELECT nom_championnat FROM DYN_championnat WHERE id=@p0", id, r => Dec(r["nom_championnat"]), Loc.F("League #{0}", id));

    /// <summary>Who or what a row belongs to, e.g. "Player Max Mustermann" or "Contract of Max Mustermann".</summary>
    public static string RowOwner(PbmDatabase db, string table, string keyColumn, object key)
    {
        var id = Convert.ToInt64(key);
        string Owned(string sql, Func<long, string> name, string fmt)
        {
            try { return db.Query(sql, id).FirstOrDefault() is { } r ? Loc.F(fmt, name(Convert.ToInt64(r.Values.First()))) : Loc.F(fmt, "#" + id); }
            catch (Microsoft.Data.Sqlite.SqliteException) { return Loc.F(fmt, "#" + id); }
        }
        return table.ToUpperInvariant() switch
        {
            "DYN_JOUEUR" => Loc.F("Player {0}", Player(db, id)),
            "DYN_JOUEUR_ATTRIBUTES" => Loc.F("Attributes of {0}", Player(db, id)),
            "DYN_CONTRAT" => Owned("SELECT id_joueur FROM DYN_contrat WHERE id=@p0", i => Player(db, i), "Contract of {0}"),
            "DYN_PERSON" => Loc.F("Staff {0}", Person(db, id)),
            "DYN_CONTRAT_PERSON" => Owned("SELECT id_person FROM DYN_contrat_person WHERE id=@p0", i => Person(db, i), "Contract of {0}"),
            "DYN_EQUIPE" => Loc.F("Team {0}", Team(db, id)),
            "DYN_FACILITIES" => Loc.F("Arena of {0}", Team(db, id)),
            "DYN_CHAMPIONNAT" => Loc.F("League {0}", League(db, id)),
            "STA_COMPETITION_REGLES" => Loc.F("Rules of {0}", League(db, id)),
            _ => Loc.T("Data"),
        };
    }

    /// <summary>Value as the user sees it: choice label if the field has choices, long text shortened.</summary>
    public static string Show(Field f, string text)
    {
        if (f.Choices != null && long.TryParse(text, out var v) && f.Choices.FirstOrDefault(c => c.Value == v) is { } c) return c.ToString() ?? text;
        if (f.Kind == FieldKind.Color && long.TryParse(text, out var argb)) return "#" + ((uint)argb & 0xFFFFFF).ToString("X6");
        if (f.Kind == FieldKind.Bool) return text is "1" or "true" or "True" ? Loc.T("yes") : Loc.T("no");
        if (text.Length == 0) return Loc.T("(empty)");
        return text.Length > 60 ? text[..60] + "..." : text;
    }
}
