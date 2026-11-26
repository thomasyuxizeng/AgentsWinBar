using System.Runtime.InteropServices;

namespace AgentsWinBar.Services.Native.Cost;

// Windows ships this SQLite library. Keep SQL errors free of paths and input values.
internal sealed class CostIndexDatabase : IDisposable
{
    private const int OpenReadOnly = 0x01;
    private const int OpenReadWrite = 0x02;
    private const int OpenCreate = 0x04;
    private IntPtr _handle;

    internal CostIndexDatabase(string path, bool readOnly = false)
    {
        var flags = readOnly ? OpenReadOnly : OpenReadWrite | OpenCreate;
        var status = Native.sqlite3_open_v2(path, out _handle, flags, IntPtr.Zero);
        if (status == 0) return;
        Dispose();
        Check(status);
    }

    internal void Execute(string sql) =>
        Check(Native.sqlite3_exec(_handle, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));

    internal Statement Prepare(string sql)
    {
        Check(Native.sqlite3_prepare_v2(_handle, sql, -1, out var statement, IntPtr.Zero));
        return new Statement(statement);
    }

    internal static void Check(int status)
    {
        if (status != 0) throw new SqliteException(status);
    }

    internal sealed class SqliteException(int status) : IOException($"Cost index SQLite error {status}.")
    {
        // Schema errors and malformed databases can be rebuilt. Preserve the index on lock and I/O errors.
        internal bool InvalidIndex => status is 1 or 11 or 17 or 26;
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        // close_v2 never fails on a statement that is still open: the connection closes once the last one is
        // finalized. sqlite3_close would return BUSY and leak the connection, and with it the file handle.
        // Its only other failure is misuse of the handle, which Dispose cannot act on (and must not throw for).
        var status = Native.sqlite3_close_v2(_handle);
        _handle = IntPtr.Zero;
        System.Diagnostics.Debug.Assert(status == 0, "sqlite3_close_v2 failed.");
    }

    internal sealed class Statement(IntPtr statement) : IDisposable
    {
        private IntPtr _handle = statement;

        /// <summary>The live native handle; a disposed statement refuses use instead of touching freed memory.</summary>
        private IntPtr Handle => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(Statement));

        internal bool IsDisposed => _handle == IntPtr.Zero;
        internal void Bind(int slot, long value) => Check(Native.sqlite3_bind_int64(Handle, slot, value));
        internal void BindNull(int slot) => Check(Native.sqlite3_bind_null(Handle, slot));
        internal void Bind(int slot, string value) => Check(Native.sqlite3_bind_text(Handle, slot, value, -1, new IntPtr(-1)));
        internal long Number(int column) => Native.sqlite3_column_int64(Handle, column);
        internal string Text(int column) => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(Handle, column)) ?? "";
        internal bool Step()
        {
            var status = Native.sqlite3_step(Handle);
            if (status == 100) return true;
            if (status != 101) Check(status);
            return false;
        }
        internal void Write()
        {
            var stepped = false;
            try
            {
                Step();
                stepped = true;
            }
            finally
            {
                // Always reset, or a failed step leaves the cached statement holding its error and its locks.
                // The reset status only matters when the step itself succeeded; it must not hide the step's error.
                var reset = Native.sqlite3_reset(Handle);
                if (stepped) Check(reset);
            }
        }

        internal void Reset() => Check(Native.sqlite3_reset(Handle));

        /// <summary>Finalizes the native statement exactly once, however often it is called.</summary>
        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero) Native.sqlite3_finalize(handle);
        }
    }

    private static class Native
    {
        private const string Library = "winsqlite3.dll";

        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr state, IntPtr error);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_reset(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
    }
}
