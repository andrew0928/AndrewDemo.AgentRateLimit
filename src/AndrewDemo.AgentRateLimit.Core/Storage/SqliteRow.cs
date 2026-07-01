namespace AndrewDemo.AgentRateLimit.Core.Storage;

internal sealed class SqliteRow
{
    private readonly IntPtr _statement;

    public SqliteRow(IntPtr statement)
    {
        _statement = statement;
    }

    public long GetInt64(int columnIndex)
    {
        return SqliteDatabase.ColumnInt64(_statement, columnIndex);
    }

    public long GetInt64(string columnName)
    {
        return GetInt64(GetColumnIndex(columnName));
    }

    public long? GetNullableInt64(int columnIndex)
    {
        return SqliteDatabase.IsNullType(SqliteDatabase.ColumnType(_statement, columnIndex))
            ? null
            : SqliteDatabase.ColumnInt64(_statement, columnIndex);
    }

    public long? GetNullableInt64(string columnName)
    {
        return GetNullableInt64(GetColumnIndex(columnName));
    }

    public string GetString(string columnName)
    {
        return GetNullableString(columnName) ?? string.Empty;
    }

    public string? GetNullableString(string columnName)
    {
        return GetNullableString(GetColumnIndex(columnName));
    }

    public string? GetNullableString(int columnIndex)
    {
        return SqliteDatabase.IsNullType(SqliteDatabase.ColumnType(_statement, columnIndex))
            ? null
            : SqliteDatabase.ColumnText(_statement, columnIndex);
    }

    private int GetColumnIndex(string columnName)
    {
        var count = SqliteDatabase.ColumnCount(_statement);
        for (var i = 0; i < count; i++)
        {
            if (string.Equals(SqliteDatabase.ColumnName(_statement, i), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"SQLite column not found: {columnName}");
    }
}
