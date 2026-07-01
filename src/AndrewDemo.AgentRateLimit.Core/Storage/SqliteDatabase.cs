using System.Runtime.InteropServices;
using System.Text;

namespace AndrewDemo.AgentRateLimit.Core.Storage;

internal sealed class SqliteDatabase : IDisposable
{
    private const int SqliteOk = 0;
    private const int SqliteOpenReadWrite = 0x00000002;
    private const int SqliteOpenCreate = 0x00000004;

    private IntPtr _handle;
    private bool _disposed;

    private SqliteDatabase(IntPtr handle)
    {
        _handle = handle;
    }

    public static SqliteDatabase Open(string dataSource)
    {
        var result = Native.sqlite3_open_v2(
            dataSource,
            out var handle,
            SqliteOpenReadWrite | SqliteOpenCreate,
            IntPtr.Zero);

        if (result != SqliteOk)
        {
            var message = handle == IntPtr.Zero
                ? "Unable to open SQLite database."
                : GetErrorMessage(handle);

            if (handle != IntPtr.Zero)
            {
                Native.sqlite3_close_v2(handle);
            }

            throw new InvalidOperationException(message);
        }

        Native.sqlite3_busy_timeout(handle, 5000);

        return new SqliteDatabase(handle);
    }

    public void Execute(string sql)
    {
        ThrowIfDisposed();

        var result = Native.sqlite3_exec(_handle, sql, IntPtr.Zero, IntPtr.Zero, out var errorMessage);
        if (result == SqliteOk)
        {
            return;
        }

        var message = errorMessage == IntPtr.Zero
            ? GetErrorMessage(_handle)
            : Marshal.PtrToStringUTF8(errorMessage) ?? GetErrorMessage(_handle);
        if (errorMessage != IntPtr.Zero)
        {
            Native.sqlite3_free(errorMessage);
        }

        throw new InvalidOperationException(message);
    }

    public SqliteStatement Prepare(string sql)
    {
        ThrowIfDisposed();

        var result = Native.sqlite3_prepare_v2(
            _handle,
            sql,
            -1,
            out var statement,
            IntPtr.Zero);

        if (result != SqliteOk)
        {
            throw new InvalidOperationException(GetErrorMessage(_handle));
        }

        return new SqliteStatement(_handle, statement);
    }

    public void BeginImmediateTransaction() => Execute("begin immediate transaction;");

    public void Commit() => Execute("commit;");

    public void Rollback() => Execute("rollback;");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_handle != IntPtr.Zero)
        {
            Native.sqlite3_close_v2(_handle);
            _handle = IntPtr.Zero;
        }

        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string GetErrorMessage(IntPtr database)
    {
        var pointer = Native.sqlite3_errmsg(database);
        return Marshal.PtrToStringUTF8(pointer) ?? "SQLite operation failed.";
    }

    internal static byte[] ToUtf8(string value) => Encoding.UTF8.GetBytes(value);

    private static class Native
    {
        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_open_v2(
            string filename,
            out IntPtr database,
            int flags,
            IntPtr vfs);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close_v2(IntPtr database);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_exec(
            IntPtr database,
            string sql,
            IntPtr callback,
            IntPtr firstArgument,
            out IntPtr errorMessage);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern void sqlite3_free(IntPtr pointer);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_prepare_v2(
            IntPtr database,
            string sql,
            int byteCount,
            out IntPtr statement,
            IntPtr tail);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_errmsg(IntPtr database);
    }
}
