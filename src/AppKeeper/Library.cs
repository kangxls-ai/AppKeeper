using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AppKeeper;

internal enum EntryKind { File, Directory, Web, Store, Group }

internal sealed class Entry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public EntryKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Source { get; set; }
    public string? DefaultName { get; set; }
    public string? Icon { get; set; }
    public Guid? Group { get; set; }
    public bool Inbox { get; set; }
    public bool Missing { get; set; }
}

internal sealed class Library
{
    public int Version { get; set; } = 1;
    public List<Entry> Entries { get; set; } = [];
    public HashSet<string> Ignored { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool StartWithWindows { get; set; } = true;
    public uint Modifiers { get; set; } = 1;
    public int Key { get; set; } = 32;
}

internal sealed class LibraryStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 12 };
    public string DataPath { get; } = Path.Combine(directory, "AppKeeper.data.json");
    public string BackupPath => DataPath + ".bak";

    public Library Load(out string? notice)
    {
        notice = null;
        if (!File.Exists(DataPath) && !File.Exists(BackupPath))
            return new();

        if (TryRead(DataPath) is { } current)
            return current;

        if (TryRead(BackupPath) is { } backup)
        {
            notice = "主配置无法读取，已从上一次有效备份恢复。";
            return backup;
        }

        throw new InvalidDataException("配置与备份均无法读取。请保留原文件并从托盘导入有效备份。");
    }

    public void Save(Library data)
    {
        var backup = TryRead(DataPath) != null ? BackupPath : null;
        Write(DataPath, data, backup);
    }

    public void Export(string path, Library data)
    {
        if (IsInternalPath(path))
            throw new InvalidDataException("请选择其他位置保存导出的备份。");
        Write(path, data, null);
    }

    public bool IsInternalPath(string path) =>
        new[] { DataPath, BackupPath }.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase);

    private static void Write(string path, Library data, string? backup)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, data, Json);
                file.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(temp, path, backup);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static Library? TryRead(string path)
    {
        if (!File.Exists(path))
            return null;
        try { return Read(path); }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Library Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("备份文件超过 64 MB。");
        var data = JsonSerializer.Deserialize<Library>(file, Json);
        if (data is not { Version: 1 } || data.Entries == null || data.Ignored == null ||
            data.Key is < 1 or > 254 || data.Modifiers is 0 or > 15)
            throw new InvalidDataException("配置版本或格式无效。");

        var ids = new HashSet<Guid>();
        var groups = data.Entries.Where(entry => entry?.Kind == EntryKind.Group)
            .Select(entry => entry.Id).ToHashSet();
        foreach (var entry in data.Entries)
        {
            if (entry == null || entry.Id == Guid.Empty || !ids.Add(entry.Id) ||
                string.IsNullOrWhiteSpace(entry.Name) || entry.Target == null || !Enum.IsDefined(entry.Kind) ||
                (entry.Group.HasValue && (!groups.Contains(entry.Group.Value) || entry.Inbox)) ||
                (entry.Kind == EntryKind.Group && (entry.Group.HasValue || entry.Inbox)) ||
                (entry.Kind == EntryKind.Web && !WindowsCatalog.IsWeb(entry.Target)) ||
                (entry.Kind == EntryKind.Store && !WindowsCatalog.IsAppId(entry.Target)) ||
                (entry.Kind is EntryKind.File or EntryKind.Directory && !Path.IsPathFullyQualified(entry.Target)))
                throw new InvalidDataException("配置包含无效的应用入口或文件夹。");
        }

        data.Ignored = new HashSet<string>(data.Ignored, StringComparer.OrdinalIgnoreCase);
        return data;
    }

    public static Library Copy(Library data)
    {
        var copy = JsonSerializer.Deserialize<Library>(JsonSerializer.SerializeToUtf8Bytes(data, Json), Json)!;
        copy.Ignored = new(copy.Ignored, StringComparer.OrdinalIgnoreCase);
        return copy;
    }
}
