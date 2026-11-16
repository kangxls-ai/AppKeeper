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
            Layout.Move(data, second, third, Layout.DropAt(second, third, .5, insideFolder: false));
            Check(second.Group == null && !second.Inbox && data.Entries[1] == second, "drag to home tile");
            data.Tags.Add("工作");
            first.Tags.Add("工作");
            Check(Layout.Visible(data, false, "", "工作").SequenceEqual([first]), "tag finds grouped app");
            Check(Layout.Visible(data, false, "example", "工作").SequenceEqual([first]), "search with tag");
            data.Ball = new BallPosition(40, 90, @"\\.\DISPLAY1", DockEdge.Left);

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
            var imported = LibraryStore.Read(export);
            Check(imported.Ball == data.Ball && imported.Tags.SequenceEqual(["工作"]) &&
                imported.Entries.Single(entry => entry.Id == first.Id).Tags.SequenceEqual(["工作"]),
                "export and restore tags and ball position");
            Layout.RemoveTag(imported, "工作");
            Check(imported.Tags.Count == 0 && imported.Entries.All(entry => entry.Tags.Count == 0), "delete tag");
            File.WriteAllText(export, """{"Version":99}""");
            try
            {
                LibraryStore.Read(export);
                throw new InvalidOperationException("Test failed: reject invalid import");
            }
            catch (InvalidDataException) { }
            CheckSearchViewport();
            CheckTagOrganization(directory);
            CheckQuickHistory(directory);
            CheckPortableMigration(directory);
            System.Threading.Tasks.Task.Run(() => ModuleSelfTest.Run(directory)).GetAwaiter().GetResult();
            WriteResult($"PASS · 500-entry pinyin search P95 {searchSamples[23]:F1} ms · tags and history · Pigeon transfer, MarkDo and Store migration");
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

    internal static int CheckStoreActivation(ProductProfile profile)
    {
        if (!profile.IsStore)
            return 1;
        var localState = Path.GetDirectoryName(profile.DataDirectory)!;
        Directory.CreateDirectory(localState);
        File.WriteAllText(Path.Combine(localState, "package-smoke.txt"), profile.Name + "\n" + profile.DataDirectory);
        return 0;
    }

    private static void CheckPortableMigration(string directory)
    {
        var source = Directory.CreateDirectory(Path.Combine(directory, "绿色版")).FullName;
        var portable = ProductProfile.Resolve(null, source, directory);
        var packaged = ProductProfile.Resolve("kangxls.EasyAppKeeper_test", source, directory);
        Check(!portable.IsStore && portable.DataDirectory == source && packaged.IsStore &&
            packaged.DataDirectory.EndsWith(@"kangxls.EasyAppKeeper_test\LocalState\Data"), "separate portable and store environments");
        File.Create(Path.Combine(source, "AppKeeper.exe")).Dispose();
        var library = new Library { Tags = ["工作"], SearchHistory = ["jisuanqi"] };
        var entry = WindowsCatalog.FromUrl("https://example.com");
        Layout.AssignTag(library, entry, "工作");
        library.Entries.Add(entry);
        library.PinnedEntries.Add(entry.Id);
        new LibraryStore(source).Save(library);
        var original = new AgendaStore(source);
        using var holdWal = Sqlite.Open(original.PathName);
        _ = holdWal.Rows("PRAGMA journal_mode=WAL").ToArray();
        original.Append("create", Guid.NewGuid().ToString(), new { title = "既有日程" });
        var keyFile = Path.Combine(source, "AppKeeper.pigeon.pfx");
        using var certificate = PigeonWire.Identity(keyFile);
        var id = Guid.NewGuid();
        var folder = Directory.CreateDirectory(Path.Combine(source, "pigeon-transfer")).FullName;
        var archive = Path.Combine(folder, id.ToString("N") + ".zip");
        File.WriteAllText(archive, "test transfer");
        var share = Directory.CreateDirectory(Path.Combine(source, "share")).FullName;
        var received = Path.Combine(share, "received.txt");
        File.WriteAllText(received, "original received file");
        ModuleFiles.Write(Path.Combine(source, "AppKeeper.pigeon.json"), new PigeonData
        {
            Items = [new(id, "test-peer", true, "file", "queued.zip", DateTimeOffset.Now, "queued", Path: archive),
                new(Guid.NewGuid(), "test-peer", false, "file", "received.txt", DateTimeOffset.Now, "saved", Path: received)]
        });

        PortableMigration.Run(source, packaged.DataDirectory);
        var migrated = new LibraryStore(packaged.DataDirectory).Load(out _);
        Check(migrated.Tags.SequenceEqual(library.Tags) && migrated.PinnedEntries.SequenceEqual([entry.Id]) &&
            migrated.SearchHistory.SequenceEqual(library.SearchHistory) && !migrated.StartWithWindows,
            "migrate library and start with packaged startup disabled");
        Check(new AgendaStore(packaged.DataDirectory).Tasks().Single().Title == "既有日程", "migrate SQLite WAL data");
        using var copied = PigeonWire.Identity(Path.Combine(packaged.DataDirectory, "AppKeeper.pigeon.pfx"));
        Check(PigeonWire.Fingerprint(copied) == PigeonWire.Fingerprint(certificate), "preserve pairing identity");
        var pigeon = ModuleFiles.Read<PigeonData>(Path.Combine(packaged.DataDirectory, "AppKeeper.pigeon.json"));
        Check(pigeon.Items[0].Path != archive && File.Exists(pigeon.Items[0].Path) && File.Exists(archive) &&
            pigeon.Items[1].Path == received && File.Exists(received), "migrate pending archive and keep saved files");
        Check(new LibraryStore(source).Load(out _).StartWithWindows, "retain original preferences");
    }

    private static void CheckQuickHistory(string directory)
    {
        var data = new Library();
        var pinned = WindowsCatalog.FromUrl("https://example.com/pinned");
        data.Entries.Add(pinned);
        data.PinnedEntries.Add(pinned.Id);
        QuickHistory.RecordVisit(data, pinned.Id);
        for (var i = 0; i < 24; i++)
        {
            QuickHistory.RecordSearch(data, $"test {i}");
            var entry = WindowsCatalog.FromUrl($"https://example.com/{i}");
            data.Entries.Add(entry);
            QuickHistory.RecordVisit(data, entry.Id);
        }
        QuickHistory.RecordSearch(data, " TEST 22 ");
        QuickHistory.RecordSearch(data, "  ");
        Check(data.SearchHistory.Count == 10 && data.SearchHistory[0] == "TEST 22" &&
            data.SearchHistory.Count(query => query.Equals("test 22", StringComparison.OrdinalIgnoreCase)) == 1,
            "search history recency, limit and deduplication");
        Check(data.RecentEntries.Count == 20 && !data.RecentEntries.Contains(pinned.Id) &&
            QuickHistory.Entries(data).First() == pinned && QuickHistory.Entries(data).Count() == 20,
            "pinned entry stays visible as visits expire");
        QuickHistory.RecordVisit(data, data.Entries[10].Id);
        Check(QuickHistory.Entries(data).ElementAt(1).Id == data.Entries[10].Id, "revisit moves recent entry forward");
        var folder = Directory.CreateDirectory(Path.Combine(directory, "history")).FullName;
        var store = new LibraryStore(folder);
        store.Save(data);
        var restored = store.Load(out _);
        Check(restored.SearchHistory.SequenceEqual(data.SearchHistory) &&
            restored.RecentEntries.SequenceEqual(data.RecentEntries) && restored.PinnedEntries.SequenceEqual(data.PinnedEntries),
            "history and fixed entries round trip");
        Layout.Remove(restored, restored.Entries[0]);
        Check(restored.PinnedEntries.Count == 0 && QuickHistory.Entries(restored).Count() == 20, "remove stale pin");
        File.WriteAllText(store.DataPath, """{"Version":1}""");
        restored = store.Load(out _);
        Check(restored.SearchHistory.Count == 0 && restored.RecentEntries.Count == 0 &&
            restored.PinnedEntries.Count == 0, "old data loads with empty histories");
    }

    private static void CheckTagOrganization(string directory)
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, "tag-test")).FullName;
        var file = Path.Combine(folder, "example.txt");
        var shortcut = Path.Combine(folder, "example.url");
        File.WriteAllText(file, "example");
        File.WriteAllText(shortcut, "[InternetShortcut]\nURL=https://example.com\n");
        var library = new Library { Tags = ["工作", "生活", "工具"] };
        Check(Layout.MoveTag(library, "工作", "工具", after: true) &&
            library.Tags.SequenceEqual(["生活", "工具", "工作"]), "move tag after another");
        Check(Layout.MoveTag(library, "工作", "生活", after: false) &&
            !Layout.MoveTag(library, "工作", "生活", after: false) &&
            library.Tags.SequenceEqual(["工作", "生活", "工具"]), "move tag before and ignore a no-op");
        Layout.MoveTag(library, "工具", "生活", after: false);

        var directoryEntry = WindowsCatalog.FromPath(folder);
        var fileEntry = WindowsCatalog.FromPath(file);
        var appEntry = WindowsCatalog.FromPath(shortcut);
        var webEntry = WindowsCatalog.FromUrl("https://example.com");
        var group = new Entry { Kind = EntryKind.Group, Name = "分组" };
        library.Entries.Add(group);
        fileEntry.Group = group.Id;
        library.Entries.Add(fileEntry);
        Layout.AssignTag(library, fileEntry, "生活");
        Layout.AddEntries(library, [directoryEntry, WindowsCatalog.FromPath(file), appEntry, webEntry], null, "工作");
        Check(library.Entries.Count == 5 && fileEntry.Group == group.Id &&
            fileEntry.Tags.SequenceEqual(["生活", "工作"]) &&
            Layout.Visible(library, false, "", "工作").Count() == 4, "tagged files, folders, shortcuts and URLs");
        Check(Layout.AssignTag(library, group, "工具") && !Layout.AssignTag(library, group, "工具"), "assign tag without duplicates");
        var untagged = WindowsCatalog.FromUrl("https://example.org");
        Layout.AddEntries(library, [untagged], null, null);
        Check(untagged.Tags.Count == 0, "all view does not add a tag");
        var store = new LibraryStore(folder);
        store.Save(library);
        var saved = store.Load(out _);
        Check(saved.Tags.SequenceEqual(["工作", "工具", "生活"]) &&
            saved.Entries.Single(entry => entry.Id == fileEntry.Id).Tags.SequenceEqual(["生活", "工作"]),
            "tag order and membership survive reload");
    }

    private static void CheckSearchViewport()
    {
        var launcher = new MainWindow((App)System.Windows.Application.Current);
        try
        {
            foreach (var width in new[] { 300, 820 })
            {
                var viewport = new System.Windows.Size(width, 390);
                launcher.SearchBox.Text = "搜索测试 test";
                launcher.Root.Measure(viewport);
                launcher.Root.Arrange(new System.Windows.Rect(viewport));
                launcher.Root.UpdateLayout();
                var editor = launcher.SearchBox.Template.FindName("PART_ContentHost", launcher.SearchBox)
                    as System.Windows.Controls.ScrollViewer;
                Check(editor is { ActualHeight: >= 24, ActualWidth: >= 94 }, "search text viewport at width " + width);
                if (width == 820)
                    Check(Math.Abs(launcher.SearchFrame.ActualWidth * 3 - launcher.Workspace.ActualWidth) < 5,
                        "search uses one third of the row at standard width");
            }
        }
        finally { launcher.CloseForExit(); }
    }

    private static void Check(bool passed, string label)
    {
        if (!passed)
            throw new InvalidOperationException("Test failed: " + label);
    }
}
