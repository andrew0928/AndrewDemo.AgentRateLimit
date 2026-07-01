using System.Runtime.InteropServices;

namespace AndrewDemo.AgentRateLimit.Core.Storage;

internal sealed class SqliteStatement : IDisposable
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteNull = 5;
    private static readonly IntPtr SqliteTransient = new(-1);

    private readonly IntPtr _database;
    private IntPtr _statement;
    private bool _disposed;

    public SqliteStatement(IntPtr database, IntPtr statement)
    {
        _database = database;
        _statement = statement;
    }

    public void BindText(int index, string value)
    {
        ThrowIfDisposed();

        var bytes = SqliteDatabase.ToUtf8(value);
        var result = Native.sqlite3_bind_text(_statement, index, bytes, bytes.Length, SqliteTransient);
        ThrowIfNotOk(result);
    }

    public void BindTextOrNull(int index, string? value)
    {
        if (value is null)
        {
            BindNull(index);
            return;
        }

        BindText(index, value);
    }

    public void BindInt(int index, int value)
    {
        ThrowIfDisposed();

        var result = Native.sqlite3_bind_int(_statement, index, value);
        ThrowIfNotOk(result);
    }

    public void BindNull(int index)
    {
        ThrowIfDisposed();

        var result = Native.sqlite3_bind_null(_statement, index);
        ThrowIfNotOk(result);
    }

    public bool Step()
    {
        ThrowIfDisposed();

        var result = Native.sqlite3_step(_statement);
        return result switch
        {
            SqliteRow => true,
            SqliteDone => false,
            _ => throw new InvalidOperationException(GetErrorMessage())
        };
    }

    public void Execute()
    {
        if (Step())
        {
            throw new InvalidOperationException("SQLite command unexpectedly returned a row.");
        }
    }

    public int ColumnInt(int index)
    {
        ThrowIfDisposed();
        return Native.sqlite3_column_int(_statement, index);
    }

    public string ColumnText(int index)
    {
        ThrowIfDisposed();

        var pointer = Native.sqlite3_column_text(_statement, index);
        if (pointer == IntPtr.Zero)
        {
            return string.Empty;
        }

        var byteCount = Native.sqlite3_column_bytes(_statement, index);
        var buffer = new byte[byteCount];
        Marshal.Copy(pointer, buffer, 0, byteCount);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }

    public DateTimeOffset ColumnDateTimeOffset(int index)
    {
        return SubscriptionCreditSqliteStore.ParseUtc(ColumnText(index));
    }

    public DateTimeOffset? ColumnDateTimeOffsetOrNull(int index)
    {
        ThrowIfDisposed();
        if (Native.sqlite3_column_type(_statement, index) == SqliteNull)
        {
            return null;
        }

        return ColumnDateTimeOffset(index);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_statement != IntPtr.Zero)
        {
            Native.sqlite3_finalize(_statement);
            _statement = IntPtr.Zero;
        }

        _disposed = true;
    }

    private void ThrowIfNotOk(int result)
    {
        if (result != SqliteOk)
        {
            throw new InvalidOperationException(GetErrorMessage());
        }
    }

    private string GetErrorMessage()
    {
        var pointer = Native.sqlite3_errmsg(_database);
        return Marshal.PtrToStringUTF8(pointer) ?? "SQLite statement failed.";
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static class Native
    {
        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_bind_text(
            IntPtr statement,
            int index,
            byte[] value,
            int byteCount,
            IntPtr destructor);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_bind_int(IntPtr statement, int index, int value);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_bind_null(IntPtr statement, int index);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_step(IntPtr statement);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_finalize(IntPtr statement);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_column_int(IntPtr statement, int columnIndex);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_column_text(IntPtr statement, int columnIndex);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_column_bytes(IntPtr statement, int columnIndex);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_column_type(IntPtr statement, int columnIndex);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_errmsg(IntPtr database);
    }
}
