using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppKeeper;

internal static class PigeonUnreadSelfTest
{
    internal static void Run(string directory)
    {
        var root = Directory.CreateDirectory(Path.Combine(directory, "pigeon-unread")).FullName;
        var path = Path.Combine(root, "AppKeeper.pigeon.json");
        var a = new PigeonPeer("office", "办公电脑", "127.0.0.1", 1, "paired", false);
        var b = new PigeonPeer("home", "家里笔记本", "127.0.0.1", 1, "paired", false);
        var first = new PigeonItem(Guid.NewGuid(), a.Key, false, "text", "第一条", DateTimeOffset.Now, "saved", Unread: true);
        var second = first with { Id = Guid.NewGuid(), Kind = "file", Value = "资料.zip", State = "receiving" };
        var other = first with { Id = Guid.NewGuid(), Peer = b.Key, Value = "另一台电脑的消息" };
        var data = new PigeonData { Peers = [a, b], Items = [first, second, other,
            first with { Id = Guid.NewGuid(), Outgoing = true }, first with { Id = Guid.NewGuid(), Archived = true },
            first with { Id = Guid.NewGuid(), Kind = "pair" }, first with { Id = Guid.NewGuid(), Peer = "unknown" }] };
        ModuleFiles.Write(path, data);
        using (var service = new PigeonService(root, startNetworking: false))
        {
            Check(service.UnreadCounts()[a.Key] == 2 && service.UnreadCounts()[b.Key] == 1, "count only received text and file notifications");
            var events = 0;
            service.UnreadChanged += () => events++;
            Check(!service.MarkRead(a.Key, [other.Id, Guid.NewGuid()]) && events == 0, "one peer cannot read another peer's items");
            Check(service.MarkRead(a.Key, [first.Id]) && service.UnreadCounts()[a.Key] == 1 && service.UnreadCounts()[b.Key] == 1,
                "read only the displayed snapshot, preserving another peer and a later message");
            Check(!service.MarkRead(a.Key, [first.Id]) && events == 1, "repeated reading does not write or notify again");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { service.MarkRead(a.Key, [second.Id]); throw new InvalidOperationException("Read should fail while the data file cannot be replaced"); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                Check(service.UnreadCounts()[a.Key] == 1 && events == 1, "failed persistence never silently clears a badge");
            }
            service.MarkRead(a.Key, [second.Id]);
            Check(!service.UnreadCounts().ContainsKey(a.Key) && service.UnreadCounts()[b.Key] == 1 && events == 2,
                "only the selected computer clears after durable read");
        }
        using (var restored = new PigeonService(root, startNetworking: false))
        {
            Check(restored.UnreadCounts().Count == 1 && restored.UnreadCounts()[b.Key] == 1, "unread and read states survive restart");
            restored.ClearHistory(b.Key);
            Check(restored.UnreadCounts().Count == 0 && restored.History(b.Key).Length == 0, "clear history removes unread notifications");
        }
        var old = new PigeonData { Peers = [a], Items = [first with { Unread = false }] };
        File.WriteAllText(path, JsonSerializer.Serialize(old).Replace(",\"Unread\":false", ""));
        Check(!File.ReadAllText(path).Contains("\"Unread\""), "legacy fixture has no unread metadata");
        using (var legacy = new PigeonService(root, startNetworking: false))
            Check(legacy.UnreadCounts().Count == 0 && legacy.History(a.Key).Length == 1, "upgrade does not flag all old chat history as new");

        Check(PigeonUnread.ReadingPeer("a", true, true, false, false) == "a", "foreground conversation is reading");
        Check(PigeonUnread.ReadingPeer("a", false, true, false, false) == null &&
            PigeonUnread.ReadingPeer("a", true, false, false, false) == null &&
            PigeonUnread.ReadingPeer("a", true, true, true, false) == null &&
            PigeonUnread.ReadingPeer("a", true, true, false, true) == null &&
            PigeonUnread.ReadingPeer(null, true, true, false, false) == null,
            "hidden, background, minimized, computer list and no selection never implicitly read messages");
        var badge = new PigeonUnreadBadge();
        badge.Update(100);
        Check(badge.DisplayText == "99+" && badge.Visibility == Visibility.Visible && !badge.IsHitTestVisible, "counter cap and click-through badge");
        badge.Update(0);
        Check(badge.Visibility == Visibility.Collapsed, "no badge remains after all messages are read");
        Layout(directory, a, b);
    }

    private static void Layout(string directory, PigeonPeer a, PigeonPeer b)
    {
        var root = Directory.CreateDirectory(Path.Combine(directory, "pigeon-unread-layout")).FullName;
        ModuleFiles.Write(Path.Combine(root, "AppKeeper.pigeon.json"), new PigeonData { Peers = [a, b], Items =
            Enumerable.Range(0, 5).Select(i => new PigeonItem(Guid.NewGuid(), i < 3 ? a.Key : b.Key, false,
                "text", "测试未读消息", DateTimeOffset.Now, "saved", Unread: true)).ToList() });
        using var service = new PigeonService(root, startNetworking: false);
        var page = new PigeonPage(service);
        page.OpenList();
        var window = new MainWindow((App)Application.Current);
        try
        {
            window.AppsPage.Visibility = Visibility.Collapsed;
            window.ModuleHost.Visibility = Visibility.Visible;
            window.ModuleHost.Content = page;
            window.AppsTab.Background = Brushes.Transparent;
            window.PigeonTab.Background = ModuleUi.Accent;
            ((PigeonUnreadBadge)window.PigeonUnreadHost.Content).Update(5);
            var surface = (FrameworkElement)window.Content;
            foreach (var width in new[] { 820, 300 })
            {
                var size = new Size(width, 600);
                surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
                Check(service.UnreadCounts().Values.Sum() == 5, "opening the module or rendering it does not read either conversation");
                var output = Environment.GetEnvironmentVariable("APPKEEPER_UI_TEST_DIR");
                if (string.IsNullOrEmpty(output)) continue;
                Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap(width, 600, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"pigeon-unread-{width}.png")); encoder.Save(file);
            }
        }
        finally { window.CloseForExit(); }
    }

    private static void Check(bool passed, string message)
    { if (!passed) throw new InvalidOperationException("Pigeon unread test failed: " + message); }
}
