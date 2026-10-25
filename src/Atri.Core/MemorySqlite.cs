using System.Runtime.InteropServices;
using System.Text;

namespace Atri.Core;

// Windows ships this SQLite library. No external package, download, or service is required.
internal sealed class MemorySqlite : IDisposable
{
    private IntPtr _db;
    public MemorySqlite(string path, bool readOnly = false)
    {
        int result = Native.sqlite3_open_v2(Utf8(path), out _db, (readOnly ? 0x00000001 : 0x00000002 | 0x00000004) | 0x00010000, IntPtr.Zero);
        if (result != 0) { string error = Error; Dispose(); throw new IOException($"无法打开记忆数据库：{error}"); }
        Native.sqlite3_busy_timeout(_db, 5000);
    }

    private string Error => Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(_db)) ?? "SQLite error";
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + '\0');
    public void Execute(string sql, params object?[] args) => Query(sql, args);

    public List<string?[]> Query(string sql, params object?[] args)
    {
        if (_db == IntPtr.Zero) throw new ObjectDisposedException(nameof(MemorySqlite));
        int result = Native.sqlite3_prepare_v2(_db, Utf8(sql), -1, out IntPtr statement, IntPtr.Zero);
        if (result != 0) throw new IOException($"记忆数据库语句失败：{Error}");
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                object? value = args[i];
                result = value switch
                {
                    null => Native.sqlite3_bind_null(statement, i + 1),
                    int n => Native.sqlite3_bind_int64(statement, i + 1, n),
                    long n => Native.sqlite3_bind_int64(statement, i + 1, n),
                    double n => Native.sqlite3_bind_double(statement, i + 1, n),
                    _ => BindText(statement, i + 1, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "")
                };
                if (result != 0) throw new IOException($"记忆数据库参数失败：{Error}");
            }
            var rows = new List<string?[]>();
            int columns = Native.sqlite3_column_count(statement);
            while ((result = Native.sqlite3_step(statement)) == 100)
            {
                var row = new string?[columns];
                for (int i = 0; i < columns; i++)
                    row[i] = Native.sqlite3_column_type(statement, i) == 5 ? null : Marshal.PtrToStringUTF8(Native.sqlite3_column_text(statement, i));
                rows.Add(row);
            }
            if (result != 101) throw new IOException($"记忆数据库写入失败：{Error}");
            return rows;
        }
        finally { Native.sqlite3_finalize(statement); }
    }

    private static int BindText(IntPtr statement, int index, string value)
    {
        byte[] bytes = Utf8(value);
        return Native.sqlite3_bind_text(statement, index, bytes, bytes.Length - 1, new IntPtr(-1));
    }

    public void Transaction(Action action)
    {
        Execute("BEGIN IMMEDIATE");
        try { action(); Execute("COMMIT"); }
        catch { try { Execute("ROLLBACK"); } catch (IOException) { } throw; }
    }

    public void BackupTo(string destination)
    {
        using var target = new MemorySqlite(destination);
        IntPtr backup = Native.sqlite3_backup_init(target._db, Utf8("main"), _db, Utf8("main"));
        if (backup == IntPtr.Zero) throw new IOException("无法开始一致性数据库备份。");
        int result = Native.sqlite3_backup_step(backup, -1);
        int finish = Native.sqlite3_backup_finish(backup);
        if (result != 101 || finish != 0) throw new IOException("数据库备份未完成。");
        if (target.Query("PRAGMA quick_check")[0][0] != "ok") throw new IOException("数据库备份完整性检查失败。");
    }

    public void Dispose()
    {
        if (_db == IntPtr.Zero) return;
        Native.sqlite3_close_v2(_db);
        _db = IntPtr.Zero;
    }

    private static class Native
    {
        private const string Library = "winsqlite3.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_double(IntPtr statement, int index, double value);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_backup_init(IntPtr target, byte[] targetName, IntPtr source, byte[] sourceName);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_backup_step(IntPtr backup, int pages);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_backup_finish(IntPtr backup);
    }
}
