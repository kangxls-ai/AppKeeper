using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AppKeeper;

internal sealed record AgendaTask(string Id, string Title, string Tag, DateTimeOffset Created,
    DateTimeOffset? Start, DateTimeOffset? Due, bool StartTime, bool DueTime,
    DateTimeOffset? Actual = null, DateTimeOffset? Completed = null, int? Seconds = null,
    int? Rating = null, string Note = "", bool Deleted = false)
{
    public bool On(DateTime day) => Completed.HasValue ? Same(Completed, day) :
        Same(Start, day) || Same(Due, day);

    private static bool Same(DateTimeOffset? value, DateTime day) => value?.LocalDateTime.Date == day.Date;
}

internal sealed class AgendaSettings
{
    public string? Path { get; set; }
    public string Device { get; set; } = Guid.NewGuid().ToString();
    public bool Reminders { get; set; } = true;
}

internal sealed class AgendaStore
{
    private readonly string settingsFile;
    private readonly string defaultPath;
    private AgendaSettings settings;
    public string PathName => settings.Path ?? defaultPath;
    public bool Reminders => settings.Reminders;
    public string? FindOriginal()
    {
        var support = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var directory in Directory.EnumerateDirectories(support))
        {
            var direct = Path.Combine(directory, "markdo.db");
            if (File.Exists(direct)) return direct;
            var nested = Path.Combine(directory, "markdo", "markdo.db");
            if (File.Exists(nested)) return nested;
        }
        return null;
    }

    internal AgendaStore(string root)
    {
        settingsFile = Path.Combine(root, "AppKeeper.agenda.json");
        defaultPath = Path.Combine(root, "AppKeeper.agenda.db");
        settings = ModuleFiles.Read<AgendaSettings>(settingsFile);
        if (!File.Exists(settingsFile))
            ModuleFiles.Write(settingsFile, settings);
        if (settings.Path == null)
        {
            using var sql = Sqlite.Open(defaultPath, true);
            sql.Execute("CREATE TABLE IF NOT EXISTS operations (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, clock INTEGER NOT NULL, kind TEXT NOT NULL, task_id TEXT NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL)");
            sql.Execute("CREATE INDEX IF NOT EXISTS operations_order ON operations(clock,device_id,id)");
        }
    }

    public void Connect(string? path)
    {
        if (path != null)
        {
            using var sql = Sqlite.Open(path);
            _ = Read(sql).ToArray();
        }
        else
        {
            using var sql = Sqlite.Open(defaultPath, true);
            sql.Execute("CREATE TABLE IF NOT EXISTS operations (id TEXT PRIMARY KEY, device_id TEXT NOT NULL, clock INTEGER NOT NULL, kind TEXT NOT NULL, task_id TEXT NOT NULL, payload TEXT NOT NULL, created_at TEXT NOT NULL)");
            sql.Execute("CREATE INDEX IF NOT EXISTS operations_order ON operations(clock,device_id,id)");
        }
        var next = new AgendaSettings { Path = path, Device = settings.Device, Reminders = settings.Reminders };
        ModuleFiles.Write(settingsFile, next);
        settings = next;
    }

    public void SetReminders(bool enabled)
    {
        var next = new AgendaSettings { Path = settings.Path, Device = settings.Device, Reminders = enabled };
        ModuleFiles.Write(settingsFile, next);
        settings = next;
    }

    public AgendaTask[] Tasks()
    {
        using var sql = Sqlite.Open(PathName);
        var tasks = new Dictionary<string, AgendaTask>();
        foreach (var op in Read(sql))
        {
            using var doc = JsonDocument.Parse(op.Payload);
            var p = doc.RootElement;
            if (op.Kind == "create")
            {
                tasks.TryAdd(op.Id, new(op.Id, Str(p, "title") ?? "", Str(p, "category") ?? "",
                    DateTimeOffset.Parse(op.Created), Date(p, "plannedStart"), Date(p, "deadline"),
                    Flag(p, "startHasTime"), Flag(p, "deadlineHasTime")));
            }
            else if (tasks.TryGetValue(op.Id, out var t))
            {
                tasks[op.Id] = op.Kind switch
                {
                    "edit" => t with
                    {
                        Title = Str(p, "title") ?? t.Title,
                        Tag = Str(p, "category") ?? t.Tag,
                        Start = p.TryGetProperty("plannedStart", out _) ? Date(p, "plannedStart") : t.Start,
                        Due = p.TryGetProperty("deadline", out _) ? Date(p, "deadline") : t.Due,
                        StartTime = p.TryGetProperty("startHasTime", out _) ? Flag(p, "startHasTime") : t.StartTime,
                        DueTime = p.TryGetProperty("deadlineHasTime", out _) ? Flag(p, "deadlineHasTime") : t.DueTime,
                        Actual = p.TryGetProperty("actualStartedAt", out _) ? Date(p, "actualStartedAt") : t.Actual
                    },
                    "start" => t with { Actual = Date(p, "actualStartedAt") },
                    "complete" => t with { Completed = Date(p, "completedAt"), Seconds = Int(p, "effortSeconds"),
                        Rating = Int(p, "rating"), Note = Str(p, "note") ?? "" },
                    "feedback" => t with { Seconds = Int(p, "effortSeconds") ?? t.Seconds,
                        Rating = Int(p, "rating"), Note = Str(p, "note") ?? "" },
                    "reopen" => t with { Completed = null },
                    "delete" => t with { Deleted = true },
                    _ => t
                };
            }
        }
        return tasks.Values.Where(task => !task.Deleted).OrderByDescending(task => task.Created).ToArray();
    }

    private static IEnumerable<(string Id, string Kind, string Payload, string Created)> Read(Sqlite sql) =>
        sql.Rows("SELECT task_id,kind,payload,created_at FROM operations ORDER BY clock,device_id,id")
            .Select(row => (row[0], row[1], row[2], row[3]));

    internal void Append(string kind, string id, object payload)
    {
        using var sql = Sqlite.Open(PathName);
        sql.Execute("BEGIN IMMEDIATE");
        try
        {
            sql.Execute("INSERT INTO operations (id,device_id,clock,kind,task_id,payload,created_at) VALUES (?1,?2,(SELECT COALESCE(MAX(clock),0)+1 FROM operations),?3,?4,?5,?6)",
                Guid.NewGuid().ToString(), settings.Device, kind, id, JsonSerializer.Serialize(payload),
                DateTimeOffset.UtcNow.ToString("O"));
            sql.Execute("COMMIT");
        }
        catch { sql.Execute("ROLLBACK"); throw; }
    }

    internal static string? Iso(DateTimeOffset? date) => date?.UtcDateTime.ToString("O");
    private static string? Str(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static DateTimeOffset? Date(JsonElement value, string name) =>
        DateTimeOffset.TryParse(Str(value, name), out var date) ? date : null;
    private static bool Flag(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static int? Int(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n) ? n : null;

    public string Markdown(DateTime from, DateTime to)
    {
        var result = new StringBuilder($"# MarkDo 成长记录\n\n{from:yyyy-MM-dd} 至 {to:yyyy-MM-dd}\n\n");
        foreach (var task in Tasks().Where(task => task.Completed == null ||
            (task.Completed.Value.LocalDateTime.Date >= from && task.Completed.Value.LocalDateTime.Date <= to)))
        {
            result.AppendLine(task.Completed == null ? $"- [ ] {task.Title}" : $"- [x] {task.Title}");
            if (task.Tag.Length > 0) result.AppendLine($"  - 标签：{task.Tag}");
            if (task.Start.HasValue) result.AppendLine("  - 计划开始：" + Format(task.Start, task.StartTime));
            if (task.Due.HasValue) result.AppendLine("  - 计划截止：" + Format(task.Due, task.DueTime));
            if (task.Actual.HasValue) result.AppendLine("  - 实际开始：" + Format(task.Actual, true));
            if (task.Completed.HasValue) result.AppendLine("  - 完成时间：" + Format(task.Completed, true));
            if (task.Seconds.HasValue) result.AppendLine($"  - 投入：{TimeSpan.FromSeconds(task.Seconds.Value).TotalMinutes:0.##} 分钟");
            if (task.Rating.HasValue) result.AppendLine($"  - 评分：{task.Rating}/5");
            if (task.Note.Length != 0) result.AppendLine($"  - 备注：{task.Note.Replace("\n", "\n    ")}");
        }
        return result.ToString();
    }

    internal static string Format(DateTimeOffset? time, bool includeTime) =>
        time?.LocalDateTime.ToString(includeTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd") ?? "未填写";
}

internal sealed class Sqlite : IDisposable
{
    private readonly IntPtr db;
    private Sqlite(IntPtr db) => this.db = db;

    internal static Sqlite Open(string path, bool create = false)
    {
        var result = sqlite3_open_v2(path, out var db, create ? 6 : 2, IntPtr.Zero);
        var connection = new Sqlite(db);
        if (result != 0)
        {
            connection.Dispose();
            throw new IOException("无法打开日程数据库，请核对路径和权限。");
        }
        sqlite3_busy_timeout(db, 3000);
        return connection;
    }

    private IntPtr Prepare(string sql, string[] args)
    {
        Check(sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero));
        try
        {
            for (var i = 0; i < args.Length; i++)
                Check(sqlite3_bind_text(statement, i + 1, args[i], -1, new IntPtr(-1)));
            return statement;
        }
        catch { sqlite3_finalize(statement); throw; }
    }

    internal void Execute(string sql, params string[] args)
    {
        var statement = Prepare(sql, args);
        try { Check(sqlite3_step(statement), 101); }
        finally { sqlite3_finalize(statement); }
    }

    internal IEnumerable<string[]> Rows(string sql)
    {
        var statement = Prepare(sql, []);
        try
        {
            while (true)
            {
                var result = sqlite3_step(statement);
                if (result == 101) yield break;
                Check(result, 100);
                var row = new string[sqlite3_column_count(statement)];
                for (var i = 0; i < row.Length; i++)
                    row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i)) ?? "";
                yield return row;
            }
        }
        finally { sqlite3_finalize(statement); }
    }

    private void Check(int result, int expected = 0)
    {
        if (result != expected)
            throw new IOException("日程数据库：" + Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));
    }

    public void Dispose() { if (db != IntPtr.Zero) sqlite3_close(db); }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int length, IntPtr destructor);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
}
