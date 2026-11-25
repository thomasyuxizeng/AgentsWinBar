using System.Runtime.InteropServices;

namespace CodexBarWin.Services.Native.Cost;

// Windows ships this SQLite library. Keep SQL errors free of paths and input values.
internal sealed class CostIndexDatabase : IDisposable
{
    private IntPtr _handle;

    internal CostIndexDatabase(string path)
    {
        var status = Native.sqlite3_open_v2(path, out _handle, 6, IntPtr.Zero);
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
        if (status != 0) throw new IOException($"Cost index SQLite error {status}.");
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Native.sqlite3_close(_handle);
        _handle = IntPtr.Zero;
    }

    internal sealed class Statement(IntPtr handle) : IDisposable
    {
        internal void Bind(int slot, long value) => Check(Native.sqlite3_bind_int64(handle, slot, value));
        internal void Bind(int slot, string value) => Check(Native.sqlite3_bind_text(handle, slot, value, -1, new IntPtr(-1)));
        internal long Number(int column) => Native.sqlite3_column_int64(handle, column);
        internal string Text(int column) => Marshal.PtrToStringUTF8(Native.sqlite3_column_text(handle, column)) ?? "";
        internal bool Step()
        {
            var status = Native.sqlite3_step(handle);
            if (status == 100) return true;
            if (status != 101) Check(status);
            return false;
        }
        internal void Write()
        {
            Step();
            Check(Native.sqlite3_reset(handle));
        }
        public void Dispose() => Native.sqlite3_finalize(handle);
    }

    private static class Native
    {
        private const string Library = "winsqlite3.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr state, IntPtr error);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_reset(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long sqlite3_column_int64(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
    }
}
