using System.Data;

namespace PbmEditor.Core;

/// <summary>Generic editable view of a table (optionally filtered) on top of a DataTable.</summary>
public sealed class TableGrid
{
    public const int MaxRows = 5000;

    readonly PbmDatabase _db;
    readonly string? _filterColumn;
    readonly object? _filterValue;
    readonly string? _where;
    TableInfo _info = null!;

    public string Table { get; }
    /// <summary>Column that is fixed by the filter (hidden in the UI, filled automatically for new rows).</summary>
    public string? FilterColumn => _filterColumn;
    /// <summary>Raw view only: show internal column names instead of friendly headers.</summary>
    public bool RawNames { get; init; }
    /// <summary>User-facing name used in the change list; the raw table name is only a fallback (debug raw view).</summary>
    public string Title { get; init; }
    public DataTable Data { get; private set; } = new();
    public bool ReadOnly { get; private set; }
    public bool Truncated { get; private set; }
    public bool IsDirty => Data.GetChanges() != null;

    /// <param name="filterColumn">Rows are limited to filterColumn = filterValue; new rows get that value.</param>
    /// <param name="where">Optional raw WHERE fragment (local tool, raw table browser only).</param>
    public TableGrid(PbmDatabase db, string table, string? filterColumn = null, object? filterValue = null, string? where = null)
    {
        _db = db; Table = table; Title ??= table; _filterColumn = filterColumn; _filterValue = filterValue; _where = where;
        Load();
    }

    public void Load()
    {
        _info = _db.GetTable(Table) ?? throw new InvalidOperationException(Loc.T("Data not found."));
        ReadOnly = _info.PkColumns.Count == 0;

        var sql = $"SELECT * FROM \"{Table}\"";
        var args = new List<object?>();
        var clauses = new List<string>();
        if (_filterColumn != null && _info.Has(_filterColumn)) { clauses.Add($"\"{_filterColumn}\"=@p0"); args.Add(_filterValue); }
        if (!string.IsNullOrWhiteSpace(_where)) clauses.Add("(" + _where + ")");
        if (clauses.Count > 0) sql += " WHERE " + string.Join(" AND ", clauses);
        if (_info.PkColumns.Count > 0) sql += " ORDER BY " + string.Join(",", _info.PkColumns.Select(c => $"\"{c.Name}\""));
        sql += $" LIMIT {MaxRows + 1}";
        var rows = _db.Query(sql, args.ToArray());
        Truncated = rows.Count > MaxRows;
        if (Truncated) rows.RemoveAt(rows.Count - 1);

        var dt = new DataTable(Table);
        foreach (var c in _info.Columns)
        {
            var type = c.Kind switch { ColumnKind.Integer => typeof(long), ColumnKind.Real => typeof(double), _ => typeof(string) };
            dt.Columns.Add(new DataColumn(c.Name, type) { AllowDBNull = true });
        }
        dt.BeginLoadData();
        foreach (var r in rows)
        {
            var vals = _info.Columns.Select(c => Display(c, r[c.Name])).ToArray();
            dt.LoadDataRow(vals, true);
        }
        dt.EndLoadData();
        Data = dt;
    }

    object Display(ColumnInfo c, object? v)
    {
        if (v is null) return DBNull.Value;
        return c.Kind switch
        {
            ColumnKind.Integer => v is long l ? l : Convert.ToInt64(v),
            ColumnKind.Real => Convert.ToDouble(v),
            _ => v is string s && PbmCodec.IsEncodedColumn(Table, c.Name) && PbmCodec.TryDecode(s, out var d) ? d : v.ToString() ?? "",
        };
    }

    object? ToDb(ColumnInfo c, object value)
    {
        if (value is DBNull) return null;
        if (c.Kind == ColumnKind.Text && value is string s && PbmCodec.IsEncodedColumn(Table, c.Name)) return PbmCodec.Encode(s);
        return value;
    }

    public DataRow NewRow()
    {
        var row = Data.NewRow();
        if (_filterColumn != null && Data.Columns.Contains(_filterColumn) && _filterValue != null)
            row[_filterColumn] = Convert.ChangeType(_filterValue, Data.Columns[_filterColumn]!.DataType);
        Data.Rows.Add(row);
        return row;
    }

    /// <summary>Writes pending changes into the database transaction. Returns the number of rows touched.</summary>
    public int Apply()
    {
        if (ReadOnly) return 0;
        var changes = Data.GetChanges();
        if (changes == null) return 0;
        var pk = _info.PkColumns;
        var n = 0;

        foreach (DataRow row in Data.Rows.Cast<DataRow>().ToList())
        {
            switch (row.RowState)
            {
                case DataRowState.Deleted:
                {
                    var where = string.Join(" AND ", pk.Select((c, i) => $"\"{c.Name}\"=@p{i}"));
                    _db.Execute($"DELETE FROM \"{Table}\" WHERE {where}",
                        pk.Select(c => ToDb(c, row[c.Name, DataRowVersion.Original])).ToArray());
                    n++;
                    break;
                }
                case DataRowState.Modified:
                {
                    var cols = _info.Columns.Where(c => !Equals(row[c.Name, DataRowVersion.Original], row[c.Name, DataRowVersion.Current])).ToList();
                    if (cols.Count == 0) break;
                    var args = cols.Select(c => ToDb(c, row[c.Name])).ToList();
                    var sets = string.Join(",", cols.Select((c, i) => $"\"{c.Name}\"=@p{i}"));
                    var where = string.Join(" AND ", pk.Select((c, i) => $"\"{c.Name}\"=@p{cols.Count + i}"));
                    args.AddRange(pk.Select(c => ToDb(c, row[c.Name, DataRowVersion.Original])));
                    _db.Execute($"UPDATE \"{Table}\" SET {sets} WHERE {where}", args.ToArray());
                    n++;
                    break;
                }
                case DataRowState.Added:
                {
                    var cols = _info.Columns.Where(c => row[c.Name] is not DBNull).ToList();
                    var names = string.Join(",", cols.Select(c => $"\"{c.Name}\""));
                    var ps = string.Join(",", cols.Select((_, i) => "@p" + i));
                    _db.Execute($"INSERT INTO \"{Table}\" ({names}) VALUES ({ps})", cols.Select(c => ToDb(c, row[c.Name])).ToArray());
                    // single INTEGER PRIMARY KEY left empty: take the generated id
                    if (pk.Count == 1 && row[pk[0].Name] is DBNull && pk[0].Kind == ColumnKind.Integer)
                        row[pk[0].Name] = _db.LastInsertId();
                    n++;
                    break;
                }
            }
        }
        Data.AcceptChanges();
        if (n > 0) _db.Note(Loc.F("{0}: {1} row(s) changed", Title, n));
        return n;
    }
}
