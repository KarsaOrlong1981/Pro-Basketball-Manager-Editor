using Microsoft.Data.Sqlite;

namespace PbmEditor.Core;

public enum ColumnKind { Integer, Real, Text }

public sealed record ColumnInfo(string Name, string DeclaredType, bool NotNull, int PkIndex, string? Default)
{
    public bool IsBool => DeclaredType.Contains("BOOL", StringComparison.OrdinalIgnoreCase) ||
                          DeclaredType.Contains("BIT", StringComparison.OrdinalIgnoreCase);

    public ColumnKind Kind
    {
        get
        {
            var t = DeclaredType.ToUpperInvariant();
            if (t.Contains("INT") || IsBool) return ColumnKind.Integer;
            if (t.Contains("FLOA") || t.Contains("DOUB") || t.Contains("REAL") || t.Contains("DECI")) return ColumnKind.Real;
            return ColumnKind.Text;
        }
    }
}

public sealed class TableInfo
{
    public required string Name { get; init; }
    public required IReadOnlyList<ColumnInfo> Columns { get; init; }
    public IReadOnlyList<ColumnInfo> PkColumns => Columns.Where(c => c.PkIndex > 0).OrderBy(c => c.PkIndex).ToList();
    public ColumnInfo? Column(string name) => Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public bool Has(string name) => Column(name) != null;
}

public sealed class Row : Dictionary<string, object?>
{
    public Row() : base(StringComparer.OrdinalIgnoreCase) { }
    public string Str(string c) => this[c]?.ToString() ?? "";
    public long Int(string c) => this[c] is null ? 0 : Convert.ToInt64(this[c]);
    public double Dbl(string c) => this[c] is null ? 0 : Convert.ToDouble(this[c]);
}

/// <summary>
/// One open PBM database (mod or save). Writes go into a lazily started transaction,
/// so Save = Commit and Discard = Rollback. A backup is taken before the first write.
/// </summary>
public sealed class PbmDatabase : IDisposable
{
    readonly SqliteConnection _c;
    SqliteTransaction? _tx;
    readonly Dictionary<string, TableInfo> _tables = new(StringComparer.OrdinalIgnoreCase);

    public string Path { get; }
    public string BackupDir { get; }
    public string? BackupPath { get; private set; }
    public List<string> Log { get; } = new();
    public bool HasPendingChanges => _tx != null;

    PbmDatabase(string path, string backupDir)
    {
        Path = path;
        BackupDir = backupDir;
        _c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        _c.Open();
    }

    public static string DefaultBackupDir =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PbmEditor", "Backups");

    public static PbmDatabase Open(string path, string? backupDir = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(Loc.T("Database not found"), path);
        var db = new PbmDatabase(path, backupDir ?? DefaultBackupDir);
        try
        {
            if (!db.TableNames.Any(n => n.Equals("DYN_equipe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(Loc.T("Not a PBM database (table DYN_equipe missing)."));
        }
        catch { db.Dispose(); throw; }
        return db;
    }

    public IEnumerable<string> TableNames =>
        Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").Select(r => r.Str("name"));

    public TableInfo? GetTable(string name)
    {
        if (_tables.TryGetValue(name, out var t)) return t;
        var cols = Query($"PRAGMA table_info(\"{name.Replace("\"", "")}\")")
            .Select(r => new ColumnInfo(r.Str("name"), r.Str("type"), r.Int("notnull") != 0, (int)r.Int("pk"), r["dflt_value"]?.ToString()))
            .ToList();
        if (cols.Count == 0) return null;
        return _tables[name] = new TableInfo { Name = name, Columns = cols };
    }

    SqliteCommand Cmd(string sql, object?[] args)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("@p" + i, args[i] ?? DBNull.Value);
        return cmd;
    }

    /// <summary>Positional parameters are named @p0, @p1, ...</summary>
    public List<Row> Query(string sql, params object?[] args)
    {
        using var cmd = Cmd(sql, args);
        using var r = cmd.ExecuteReader();
        var rows = new List<Row>();
        while (r.Read())
        {
            var row = new Row();
            for (var i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    public object? Scalar(string sql, params object?[] args)
    {
        using var cmd = Cmd(sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public int Execute(string sql, params object?[] args)
    {
        EnsureTransaction();
        using var cmd = Cmd(sql, args);
        var n = cmd.ExecuteNonQuery();
        Log.Add(sql.Length > 140 ? sql[..140] + "..." : sql);
        return n;
    }

    public long LastInsertId() => Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));

    void EnsureTransaction()
    {
        if (_tx != null) return;
        if (BackupPath == null) Backup();
        _tx = _c.BeginTransaction();
    }

    void Backup()
    {
        Directory.CreateDirectory(BackupDir);
        var dest = System.IO.Path.Combine(BackupDir,
            $"{System.IO.Path.GetFileNameWithoutExtension(Path)}_{DateTime.Now:yyyyMMdd_HHmmss}.pbmsave");
        using (var target = new SqliteConnection($"Data Source={dest};Pooling=False"))
        {
            target.Open();
            _c.BackupDatabase(target);
        }
        BackupPath = dest;
    }

    public void Commit()
    {
        _tx?.Commit();
        _tx?.Dispose();
        _tx = null;
        Log.Clear();
    }

    public void Rollback()
    {
        _tx?.Rollback();
        _tx?.Dispose();
        _tx = null;
        Log.Clear();
    }

    public string IntegrityCheck() => Scalar("PRAGMA integrity_check")?.ToString() ?? "?";

    /// <summary>Game version info shown to the user, so schema drift is visible.</summary>
    public string VersionInfo()
    {
        var parts = new List<string>();
        try
        {
            if (GetTable("DYN_param_partie")?.Has("created_with_version") == true)
                parts.Add(Loc.F("created with {0}", Scalar("SELECT created_with_version FROM DYN_param_partie LIMIT 1")));
            if (GetTable("STA_infos_database")?.Has("annee_debut_jeu") == true)
                parts.Add(Loc.F("start year {0}", Scalar("SELECT annee_debut_jeu FROM STA_infos_database LIMIT 1")));
        }
        catch (SqliteException e) { parts.Add(Loc.F("version info unavailable: {0}", e.Message)); }
        parts.Add(Loc.F("{0} tables", TableNames.Count()));
        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        _tx?.Dispose();
        _c.Dispose();
    }
}
