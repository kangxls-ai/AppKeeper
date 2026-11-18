using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AppKeeper;

internal static class RecentUsageSelfTest
{
    internal static void Run(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "recent-usage")).FullName;
        var store = new RecentUsageStore(directory);
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        var target = Path.Combine(directory, "报告.txt");
        File.WriteAllText(target, "keep this content");
        store.Observe([new(target, false, now.AddDays(-1), false, "历史导入")], now);
        Require(store.Snapshot().Items.Single().Visits.Count == 0, "historical import does not fabricate counts");
        store.Observe([new(target, false, now, true, "最近项目"), new(target.ToUpperInvariant(), false, now.AddSeconds(2), true, "重复通知")], now.AddSeconds(3));
        store.Observe([new(target, false, now.AddSeconds(10), true, "最近项目")], now.AddSeconds(11));
        Require(RecentUsageStore.Count(store.Snapshot().Items.Single(), now.AddSeconds(12)) == 2,
            "case-insensitive identity and notification deduplication");
        Require(RecentUsageStore.Filter(store.Snapshot(), now, 30, 1, "baogao").Count() == 1 &&
            !RecentUsageStore.Filter(store.Snapshot(), now, 30, 2, "").Any(), "pinyin and file/folder filters");
        store.Hide([target.ToUpperInvariant()]);
        Require(!RecentUsageStore.Filter(new RecentUsageStore(directory).Snapshot(), now, 0, 0, "").Any(), "hidden items survive restart");
        store.RestoreHidden();
        Require(RecentUsageStore.Filter(store.Snapshot(), now, 0, 0, "").Single().Visits.Count == 2, "restore hidden item and its existing counts");
        var nested = Path.Combine(directory, "private", "inner", "secret.txt");
        var sibling = Path.Combine(directory, "private-other", "visible.txt");
        store.Observe([new(nested, false, now, true, "测试"), new(sibling, false, now, true, "测试")], now);
        store.Exclude(Path.Combine(directory, "private"));
        store.Observe([new(nested, false, now.AddSeconds(10), true, "测试")], now.AddSeconds(10));
        Require(store.Snapshot().Items.All(item => item.Path != nested) && store.Snapshot().Items.Any(item => item.Path == sibling),
            "exclusion covers descendants but not similarly named siblings");
        store.Hide([target, sibling]);
        Require(!RecentUsageStore.Filter(store.Snapshot(), now, 0, 0, "").Any(), "batch hide");
        store.RestoreHidden();
        store.RemoveExclusion(Path.Combine(directory, "private"));
        store.SetEnabled(false);
        store.Observe([new(nested, false, now, true, "测试")], now);
        Require(store.Snapshot().Items.All(item => item.Path != nested), "pause stops collecting");
        store.SetEnabled(true);
        var clearAt = now.AddMinutes(1);
        store.Clear(clearAt);
        store = new RecentUsageStore(directory);
        store.Observe([new(target, false, now, false, "旧系统记录")], clearAt.AddSeconds(1));
        Require(store.Snapshot().Items.Count == 0 && File.ReadAllText(target) == "keep this content", "clear survives restart and cannot reimport old history");
        store.Observe([new(target, false, clearAt.AddSeconds(10), true, "新访问")], clearAt.AddSeconds(10));
        Require(store.Snapshot().Items.Single().Visits.Count == 1, "new visits appear after clear");
        // Force an additional valid backup, then verify corruption recovery retains a valid state.
        store.SetEnabled(true);
        File.WriteAllText(store.FilePath, "invalid");
        store = new RecentUsageStore(directory);
        Require(store.Notice.Length > 0 && store.Snapshot().Items.Single().Visits.Count == 1, "recover valid activity backup");
        store.SetEnabled(true);
        Require(new RecentUsageStore(directory).Snapshot().Items.Count == 1, "save after recovering a corrupt primary");

        var ranking = new RecentUsageData { Items = Enumerable.Range(0, 65).Select(i => new RecentUsageItem(
            Path.Combine(directory, $"rank-{i}.txt"), false, now.AddSeconds(i), "测试",
            Enumerable.Range(0, i + 1).Select(n => now.AddMinutes(-n)).ToList())).ToList() };
        var ranked = RecentUsageStore.Rank(ranking.Items, now.AddMinutes(1)).ToArray();
        Require(ranked.Length == 50 && ranked.First().Path.EndsWith("rank-64.txt") && ranked.Last().Path.EndsWith("rank-15.txt"), "top fifty frequency ranking");
        Require(!RecentUsageStore.Rank(ranking.Items, now.AddDays(8)).Any(), "rolling seven-day expiry");
        ranking.Items.Add(new(Path.Combine(directory, "old.txt"), false, now.AddDays(-15), "历史", []));
        Require(RecentUsageStore.Filter(ranking, now, 7, 0, "old.txt").Count() == 0 &&
            RecentUsageStore.Filter(ranking, now, 30, 0, "old.txt").Count() == 1 &&
            RecentUsageStore.Filter(ranking, now.AddYears(1), 0, 0, "old.txt").Count() == 1, "seven, thirty and all-time filters");
        var tie = new[] { ranking.Items[1] with { Visits = [now], Last = now }, ranking.Items[2] with { Visits = [now], Last = now.AddSeconds(1) } };
        Require(RecentUsageStore.Rank(tie, now.AddMinutes(1)).First().Path == tie[1].Path, "recency breaks frequency ties");
        VerifySource(directory, target);
        VerifyLayout(directory);
    }

    private static void VerifySource(string directory, string target)
    {
        var recent = Directory.CreateDirectory(Path.Combine(directory, "windows-recent-fixture")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(directory, "真实文件夹")).FullName;
        foreach (var path in new[] { target, folder })
        {
            var link = RecentShortcut.Create();
            try
            {
                ((RecentShortcut.IShellLinkW)link).SetPath(path);
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(Path.Combine(recent, Path.GetFileName(path) + ".lnk"), true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        File.WriteAllText(Path.Combine(recent, "broken.lnk"), "broken link");
        var source = new WindowsRecentSource();
        var now = DateTimeOffset.UtcNow;
        var first = source.Read(now, recent, explorer: false);
        Require(first.Length == 2 && first.All(item => !item.Count) && first.Single(item => item.Path == folder).Folder,
            "real Windows shortcut import, directory type, and damaged link isolation");
        var file = Path.Combine(recent, Path.GetFileName(target) + ".lnk");
        var original = File.ReadAllBytes(file);
        File.SetLastWriteTimeUtc(file, now.AddSeconds(10).UtcDateTime);
        var changed = source.Read(now.AddSeconds(11), recent, explorer: false);
        Require(changed.Count(item => item.Count) == 1 && changed.Single(item => item.Count).Path == target,
            "a changed Windows recent link produces one observation");
        Require(source.Read(now.AddSeconds(12), recent, explorer: false).All(item => !item.Count), "stable links do not repeatedly increment counts");
        source.Reset();
        Require(source.Read(now.AddSeconds(13), recent, explorer: false).All(item => !item.Count) &&
            File.ReadAllBytes(file).SequenceEqual(original), "resuming imports a baseline and never rewrites Windows history");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Recent usage test failed: " + label);
    }

    private static void VerifyLayout(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "page-layout")).FullName;
        using var service = new RecentUsageService(directory, observeWindows: false);
        var now = DateTimeOffset.UtcNow;
        service.Store.Observe([
            new(Path.Combine(directory, "需求规格.md"), false, now, true, "界面测试样本"),
            new(Path.Combine(directory, "发布资料"), true, now.AddSeconds(-10), false, "界面测试样本"),
            new(Path.Combine(directory, "测试计划.txt"), false, now.AddSeconds(-20), false, "界面测试样本")], now);
        service.Store.SetEnabled(false);
        var page = new RecentUsagePage(service);
        var surface = new System.Windows.Controls.Border
        {
            Child = page, Padding = new(10), Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(32, 47, 65))
        };
        foreach (var width in new[] { 690, 210 })
        {
            var size = new System.Windows.Size(width, width == 690 ? 540 : 330);
            surface.Measure(size);
            surface.Arrange(new System.Windows.Rect(size));
            surface.UpdateLayout();
            Require(page.IsCompact == (width == 210) && page.VisibleRecentCount == 3, "wide and narrow recent page layout");
            var output = Environment.GetEnvironmentVariable("APPKEEPER_UI_TEST_DIR");
            if (string.IsNullOrEmpty(output)) continue;
            Directory.CreateDirectory(output);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, $"recent-page-{width}.png"));
            png.Save(file);
        }
    }
}
