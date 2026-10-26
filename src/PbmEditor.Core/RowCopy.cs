namespace PbmEditor.Core;

/// <summary>Helpers to clone template rows with a few changed values (used by the league and team creators).</summary>
static class RowCopy
{
    public static long NextId(PbmDatabase db, string table, string column) =>
        Convert.ToInt64(db.Scalar($"SELECT COALESCE(MAX(\"{column}\"),0)+1 FROM \"{table}\""));

    /// <summary>Inserts a row with every column of the table that the dictionary names; unknown columns are skipped (other game versions).</summary>
    public static void Insert(PbmDatabase db, string table, IReadOnlyDictionary<string, object?> values)
    {
        var info = db.GetTable(table) ?? throw new InvalidOperationException(Loc.F("Table {0} not found.", table));
        var cols = info.Columns.Where(c => values.ContainsKey(c.Name)).Select(c => c.Name).ToList();
        db.Execute($"INSERT INTO \"{table}\" ({string.Join(",", cols.Select(c => $"\"{c}\""))}) VALUES ({string.Join(",", cols.Select((_, i) => "@p" + i))})",
            cols.Select(c => values[c]).ToArray());
    }

    /// <summary>Copy of a queried row as a mutable dictionary.</summary>
    public static Dictionary<string, object?> Copy(Row row) => new(row, StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs <paramref name="body"/> inside a savepoint: a failure leaves nothing half-written in the transaction.</summary>
    public static T Atomic<T>(PbmDatabase db, string name, Func<T> body)
    {
        db.Execute($"SAVEPOINT {name}");
        var notes = db.Changes.Count;
        try
        {
            var result = body();
            db.Execute($"RELEASE {name}");
            return result;
        }
        catch
        {
            db.Execute($"ROLLBACK TO {name}");
            db.Execute($"RELEASE {name}");
            if (db.Changes.Count > notes) db.Changes.RemoveRange(notes, db.Changes.Count - notes);
            throw;
        }
    }
}
