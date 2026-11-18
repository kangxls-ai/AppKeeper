using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AppKeeper;

internal sealed record RecentObservation(string Path, bool Folder, DateTimeOffset At, bool Count, string Source);
internal sealed record RecentUsageItem(string Path, bool Folder, DateTimeOffset Last, string Source,
    List<DateTimeOffset> Visits)
{
    internal string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name
        ? name : Path;
}

internal sealed class RecentUsageData
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset Started { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ClearedThrough { get; set; } = DateTimeOffset.MinValue;
    public List<RecentUsageItem> Items { get; set; } = [];
    public List<string> Hidden { get; set; } = [];
    public List<string> Excluded { get; set; } = [];
}

// A separate local store: clearing it never changes the Shell's history or launcher entries.
internal sealed class RecentUsageStore
{
    private readonly string file;
    private readonly object gate = new();
    private RecentUsageData data;
    internal string Notice { get; private set; } = "";
    internal long Revision { get; private set; }
    internal string FilePath => file;

    internal RecentUsageStore(string directory)
    {
        file = Path.Combine(directory, "AppKeeper.recent.json");
        if (!File.Exists(file) && !File.Exists(file + ".bak"))
            data = new();
        else
        {
            try { data = Read(file); }
            catch (Exception error) when (ReadError(error))
            {
                try { data = Read(file + ".bak"); }
                catch (Exception backup) when (ReadError(backup))
                {
                    throw new InvalidDataException("最近访问配置和备份无法读取，请保留文件并恢复备份。", error);
                }
                Notice = "最近访问配置已从上次有效备份恢复。";
            }
        }
    }

    private static bool ReadError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException;

    private static RecentUsageData Read(string file)
    {
        using var stream = File.OpenRead(file);
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("最近访问数据过大。");
        var value = JsonSerializer.Deserialize<RecentUsageData>(stream);
        if (value is not { Version: 1 } || value.Items == null || value.Hidden == null || value.Excluded == null ||
            value.Items.Any(item => item == null || Normalize(item.Path) == null || item.Visits == null) ||
            value.Items.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Items.Count ||
            value.Hidden.Concat(value.Excluded).Any(path => Normalize(path) == null))
            throw new InvalidDataException("最近访问配置格式无效。");
        return value;
    }

    internal RecentUsageData Snapshot()
    {
        lock (gate) return Copy(data);
    }

    private static RecentUsageData Copy(RecentUsageData source) => new()
    {
        Enabled = source.Enabled, Started = source.Started, ClearedThrough = source.ClearedThrough,
        Items = source.Items.Select(item => item with { Visits = [..item.Visits] }).ToList(),
        Hidden = [..source.Hidden], Excluded = [..source.Excluded]
    };

    private void Edit(Func<RecentUsageData, bool> edit)
    {
        lock (gate)
        {
            var next = Copy(data);
            if (!edit(next)) return;
            Save(next);
            data = next;
            Revision++;
        }
    }

    private void Save(RecentUsageData next)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, next);
                stream.Flush(true);
            }
            if (!File.Exists(file)) File.Move(temp, file);
            else
            {
                var good = false;
                try { _ = Read(file); good = true; }
                catch (Exception error) when (ReadError(error)) { }
                File.Replace(temp, file, good ? file + ".bak" : null);
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal void Observe(IEnumerable<RecentObservation> observations, DateTimeOffset now)
    {
        var input = observations.ToArray();
        Edit(next =>
        {
            if (!next.Enabled) return false;
            var changed = false;
            var cutoff = now.AddDays(-7);
            var hidden = next.Hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var byPath = next.Items.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var observation in input.OrderBy(item => item.At))
            {
                var path = Normalize(observation.Path);
                if (path == null || IsProgram(path) || hidden.Contains(path) ||
                    next.Excluded.Any(folder => Within(path, folder)) || observation.At <= next.ClearedThrough ||
                    observation.At > now.AddMinutes(1)) continue;
                byPath.TryGetValue(path, out var current);
                var visits = current == null ? new List<DateTimeOffset>() : [..current.Visits];
                // Initial imports have Count=false. Merge notifications for a single launch for five seconds.
                if (observation.Count && observation.At >= next.Started && observation.At > cutoff &&
                    !visits.Any(at => Math.Abs((at - observation.At).TotalSeconds) < 5))
                {
                    visits.Add(observation.At);
                    changed = true;
                }
                if (current == null || observation.At > current.Last || visits.Count != current.Visits.Count)
                {
                    byPath[path] = new(path, observation.Folder, current == null || observation.At > current.Last
                        ? observation.At : current.Last, observation.Source, visits);
                    changed = true;
                }
            }
            foreach (var item in byPath.Values)
                if (item.Visits.RemoveAll(at => at <= cutoff || at > now.AddMinutes(1)) > 0) changed = true;
            next.Items = byPath.Values.OrderByDescending(item => item.Last).ToList();
            return changed;
        });
    }

    internal void Hide(IEnumerable<string> paths) => Edit(next =>
    {
        var hidden = paths.Select(Normalize).Where(path => path != null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        next.Hidden = next.Hidden.Concat(hidden).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return hidden.Count > 0;
    });

    internal void Clear(DateTimeOffset now) => Edit(next =>
    {
        next.Items.Clear();
        next.ClearedThrough = now;
        next.Started = now;
        return true;
    });

    internal void Exclude(string path) => Edit(next =>
    {
        var folder = Normalize(path) ?? throw new ArgumentException("请选择有效文件夹。");
        if (!next.Excluded.Contains(folder, StringComparer.OrdinalIgnoreCase)) next.Excluded.Add(folder);
        next.Items.RemoveAll(item => Within(item.Path, folder));
        return true;
    });

    internal void RemoveExclusion(string path) => Edit(next => next.Excluded.RemoveAll(folder =>
        folder.Equals(path, StringComparison.OrdinalIgnoreCase)) > 0);
    internal void RestoreHidden() => Edit(next => { next.Hidden.Clear(); return true; });
    internal void SetEnabled(bool value) => Edit(next => { next.Enabled = value; return true; });

    internal static string? Normalize(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\") ||
                path.StartsWith(@"\\.\")) return null;
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal static bool Within(string path, string folder) => path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsProgram(string path) => new[] { ".exe", ".com", ".bat", ".cmd", ".lnk", ".url" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static IEnumerable<RecentUsageItem> Filter(RecentUsageData data, DateTimeOffset now, int days, int kind, string query) =>
        data.Items.Where(item => !data.Hidden.Contains(item.Path, StringComparer.OrdinalIgnoreCase) &&
            !data.Excluded.Any(folder => Within(item.Path, folder)) && (days == 0 || item.Last > now.AddDays(-days)) &&
            (kind == 0 || item.Folder == (kind == 2)) &&
            (query.Length == 0 || Pinyin.Matches(item.Name, query) || item.Path.Contains(query, StringComparison.OrdinalIgnoreCase)));

    internal static int Count(RecentUsageItem item, DateTimeOffset now) => item.Visits.Count(at => at > now.AddDays(-7) && at <= now);
    internal static IEnumerable<RecentUsageItem> Rank(IEnumerable<RecentUsageItem> items, DateTimeOffset now) =>
        items.Where(item => Count(item, now) > 0).OrderByDescending(item => Count(item, now))
            .ThenByDescending(item => item.Last).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Take(50);
}
