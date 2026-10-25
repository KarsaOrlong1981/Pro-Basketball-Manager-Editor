using Microsoft.Data.Sqlite;

namespace PbmEditor.Core;

/// <summary>User-facing error text. Database errors can quote SQL, table and column names, so only a code is shown.</summary>
public static class ErrorText
{
    public static string For(Exception e) =>
        e is SqliteException s ? Loc.F("Database error (code {0}).", s.SqliteErrorCode) : e.Message;
}
