using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AppKeeper;

internal static class SelfTest
{
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AppKeeper-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Check(Pinyin.Matches("微软计算器", "jisuan") && Pinyin.Matches("微软计算器", "wrjsq") &&
                Pinyin.Matches("重庆银行", "chongqingyinhang") && Pinyin.Matches("终端 Terminal", "zdT") &&
                Pinyin.Matches("画图", "画") && !Pinyin.Matches("画图", "calc"), "Chinese search");
            var names = Enumerable.Range(0, 500).Select(i => $"微软计算器 {i}").ToArray();
            var searchSamples = Enumerable.Range(0, 25).Select(_ =>
            {
                var time = Stopwatch.StartNew();
                Check(names.Count(name => Pinyin.Matches(name, "wrjsq")) == 500, "500-entry search");
                return time.Elapsed.TotalMilliseconds;
            }).Order().ToArray();

            var data = new Library();
            var first = WindowsCatalog.FromUrl("https://example.com");
            var second = WindowsCatalog.FromUrl("https://example.org");
            var third = WindowsCatalog.FromPath(directory);
            data.Entries.AddRange([first, second, third]);
            Layout.Move(data, third, first, DropAction.Before);
            Check(data.Entries[0] == third, "reorder");
            Layout.Move(data, second, first, DropAction.Into);
            var group = data.Entries.Single(entry => entry.Kind == EntryKind.Group);
            Check(first.Group == group.Id && second.Group == group.Id, "create group");
            Layout.MoveHome(data, second);
            Check(second.Group == null && !second.Inbox, "move out of group");

            var appId = "Sample_123!App";
            var discovered = new Entry { Name = "示例应用", DefaultName = "示例应用", Target = appId,
                Source = "store:" + appId, Kind = EntryKind.Store, Inbox = true };
            var catalog = new CatalogSnapshot(new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
                { [discovered.Source] = discovered }, true, "");
            var priorOrder = data.Entries.Select(entry => entry.Id).ToArray();
            Check(catalog.Merge(data) && data.Entries.Take(priorOrder.Length).Select(entry => entry.Id).SequenceEqual(priorOrder),
                "discovery preserves ordering");
            var candidate = data.Entries[^1];
            Check(candidate.Inbox && candidate.Target == appId, "discovery adds to inbox");
            candidate.Name = "自定义名称";
            Check(!catalog.Merge(data) && candidate.Name == "自定义名称", "discovery preserves rename");
            var missingCatalog = new CatalogSnapshot(new(), false, "");
            Check(!missingCatalog.Merge(data) && !candidate.Missing, "failed scan keeps status");
            Check((missingCatalog with { StoreComplete = true }).Merge(data) && candidate.Missing, "detect removal");
            Layout.Remove(data, candidate);
            Check(!catalog.Merge(data), "removed app stays removed");

            var store = new LibraryStore(directory);
            store.Save(data);
            group.Name = "第二次保存";
            store.Save(data);
            Check(store.Load(out var clean).Entries.Any(entry => entry.Name == "第二次保存") && clean == null, "save");
            File.WriteAllText(store.DataPath, "{invalid");
            var restored = store.Load(out var notice);
            Check(notice != null && restored.Entries.Any(entry => entry.Name == "新建文件夹"), "backup recovery");
            store.Save(restored);
            Check(LibraryStore.Read(store.BackupPath).Entries.Any(entry => entry.Name == "新建文件夹"), "preserve good backup");
            var export = Path.Combine(directory, "export.json");
            restored.Entries[0].Icon = "cG5n";
            store.Export(export, restored);
            Check(LibraryStore.Read(export).Entries[0].Icon == "cG5n", "export embedded icon");
            File.WriteAllText(export, """{"Version":99}""");
            try
            {
                LibraryStore.Read(export);
                throw new InvalidOperationException("Test failed: reject invalid import");
            }
            catch (InvalidDataException) { }
            WriteResult($"PASS · 500-entry pinyin search P95 {searchSamples[23]:F1} ms");
            return 0;
        }
        catch (Exception error)
        {
            WriteResult(error.ToString());
            return 1;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void WriteResult(string result)
    {
        var file = Environment.GetEnvironmentVariable("APPKEEPER_SELFTEST_LOG");
        if (!string.IsNullOrEmpty(file))
            File.WriteAllText(file, result);
    }

    private static void Check(bool passed, string label)
    {
        if (!passed)
            throw new InvalidOperationException("Test failed: " + label);
    }
}
