using System.Runtime.InteropServices;
using System.Text;

namespace AndrewDemo.AgentRateLimit.Core.Storage;

internal sealed class SqliteDatabase : IDisposable
{
    private const int SqliteOpenReadWrite = 0x00000002;
    private const int SqliteOpenCreate = 0x00000004;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteInteger = 1;
    private const int SqliteText = 3;
    private const int SqliteNull = 5;
    private static readonly IntPtr SqliteTransient = new(-1);

    private readonly IntPtr _handle;
    private bool _disposed;

    static SqliteDatabase()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(SqliteDatabase).Assembly,
            static (libraryName, assembly, searchPath) =>
            {
                if (!string.Equals(libraryName, "sqlite3", StringComparison.Ordinal))
                {
                    return IntPtr.Zero;
                }

                var candidates = new[]
                {
                    "/opt/homebrew/opt/sqlite/lib/libsqlite3.dylib",
                    "/usr/local/opt/sqlite/lib/libsqlite3.dylib",
                    "libsqlite3"
                };

                foreach (var candidate in candidates)
                {
                    if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                    {
                        return handle;
                    }
                }

                return IntPtr.Zero;
            });
    }

    private SqliteDatabase(IntPtr handle)
    {
        _handle = handle;
        ExecuteNonQuery("PRAGMA foreign_keys = ON;");
        ExecuteNonQuery("PRAGMA busy_timeout = 5000;");
    }

    public long LastInsertRowId => sqlite3_last_insert_rowid(_handle);

    public static SqliteDatabase Open(string databasePath)
    {
        var result = sqlite3_open_v2(
            databasePath,
            out var handle,
            SqliteOpenReadWrite | SqliteOpenCreate,
            IntPtr.Zero);
        if (result != 0)
        {
            var message = handle == IntPtr.Zero ? "Unable to open SQLite database." : GetErrorMessage(handle);
            if (handle != IntPtr.Zero)
            {
                sqlite3_close_v2(handle);
            }

            throw new InvalidOperationException(message);
        }

        return new SqliteDatabase(handle);
    }

    public void ExecuteScript(string script)
    {
        ThrowIfDisposed();
        var result = sqlite3_exec(_handle, script, IntPtr.Zero, IntPtr.Zero, out var error);
        if (result != 0)
        {
            var message = error == IntPtr.Zero ? GetErrorMessage(_handle) : Marshal.PtrToStringUTF8(error);
            if (error != IntPtr.Zero)
            {
                sqlite3_free(error);
            }

            throw new InvalidOperationException(message ?? "SQLite script execution failed.");
        }
    }

    public int ExecuteNonQuery(string sql, params object?[] parameters)
    {
        using var statement = Prepare(sql, parameters);
        statement.StepUntilDone();
        return sqlite3_changes(_handle);
    }

    public IReadOnlyList<T> Query<T>(string sql, Func<SqliteRow, T> map, params object?[] parameters)
    {
        using var statement = Prepare(sql, parameters);
        var rows = new List<T>();
        while (statement.Step())
        {
            rows.Add(map(statement.CurrentRow));
        }

        return rows;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        sqlite3_close_v2(_handle);
        _disposed = true;
    }

    private SqliteStatement Prepare(string sql, IReadOnlyList<object?> parameters)
    {
        ThrowIfDisposed();
        var sqlBytes = Encoding.UTF8.GetBytes(sql + "\0");
        var result = sqlite3_prepare_v2(_handle, sqlBytes, sqlBytes.Length, out var statement, IntPtr.Zero);
        if (result != 0)
        {
            throw new InvalidOperationException(GetErrorMessage(_handle));
        }

        var wrapped = new SqliteStatement(_handle, statement);
        try
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                wrapped.Bind(i + 1, parameters[i]);
            }

            return wrapped;
        }
        catch
        {
            wrapped.Dispose();
            throw;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string GetErrorMessage(IntPtr database)
    {
        return Marshal.PtrToStringUTF8(sqlite3_errmsg(database)) ?? "SQLite error.";
    }

    private sealed class SqliteStatement : IDisposable
    {
        private readonly IntPtr _database;
        private readonly IntPtr _statement;
        private bool _disposed;

        public SqliteStatement(IntPtr database, IntPtr statement)
        {
            _database = database;
            _statement = statement;
            CurrentRow = new SqliteRow(statement);
        }

        public SqliteRow CurrentRow { get; }

        public void Bind(int index, object? value)
        {
            var result = value switch
            {
                null => sqlite3_bind_null(_statement, index),
                string text => BindText(index, text),
                int number => sqlite3_bind_int64(_statement, index, number),
                long number => sqlite3_bind_int64(_statement, index, number),
                bool flag => sqlite3_bind_int64(_statement, index, flag ? 1 : 0),
                _ => BindText(index, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
            };

            if (result != 0)
            {
                throw new InvalidOperationException(GetErrorMessage(_database));
            }
        }

        public bool Step()
        {
            var result = sqlite3_step(_statement);
            if (result == SqliteRow)
            {
                return true;
            }

            if (result == SqliteDone)
            {
                return false;
            }

            throw new InvalidOperationException(GetErrorMessage(_database));
        }

        public void StepUntilDone()
        {
            while (Step())
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            sqlite3_finalize(_statement);
            _disposed = true;
        }

        private int BindText(int index, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return sqlite3_bind_text(_statement, index, bytes, bytes.Length, SqliteTransient);
        }
    }

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        out IntPtr database,
        int flags,
        IntPtr vfs);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(
        IntPtr database,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        IntPtr callback,
        IntPtr callbackArgument,
        out IntPtr errorMessage);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sqlite3_free(IntPtr pointer);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(
        IntPtr database,
        byte[] sql,
        int byteCount,
        out IntPtr statement,
        IntPtr tail);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_null(IntPtr statement, int index);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(
        IntPtr statement,
        int index,
        byte[] value,
        int length,
        IntPtr destructor);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr statement);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_name(IntPtr statement, int columnIndex);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr statement, int columnIndex);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr statement, int columnIndex);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int columnIndex);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_changes(IntPtr database);

    [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_last_insert_rowid(IntPtr database);

    internal static int ColumnCount(IntPtr statement) => sqlite3_column_count(statement);

    internal static string ColumnName(IntPtr statement, int columnIndex)
    {
        return Marshal.PtrToStringUTF8(sqlite3_column_name(statement, columnIndex)) ?? string.Empty;
    }

    internal static int ColumnType(IntPtr statement, int columnIndex) => sqlite3_column_type(statement, columnIndex);

    internal static long ColumnInt64(IntPtr statement, int columnIndex) => sqlite3_column_int64(statement, columnIndex);

    internal static string? ColumnText(IntPtr statement, int columnIndex)
    {
        var pointer = sqlite3_column_text(statement, columnIndex);
        return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
    }

    internal static bool IsNullType(int type) => type == SqliteNull;

    internal static bool IsIntegerType(int type) => type == SqliteInteger;

    internal static bool IsTextType(int type) => type == SqliteText;
}
