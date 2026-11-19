using System;
using System.IO;
using System.Linq;
using System.Windows.Controls;

namespace AppKeeper;

internal static class PigeonCopySelfTest
{
    internal static void Run(string directory)
    {
        var root = Directory.CreateDirectory(Path.Combine(directory, "pigeon-copy")).FullName;
        var text = new PigeonItem(Guid.NewGuid(), "peer", false, "text", "中文消息🐦\r\n第二行", DateTimeOffset.Now, "saved");
        Check(PigeonClipboard.CreateData(text).GetText() == text.Value, "copy full Unicode message with line breaks");
        var path = Path.Combine(root, "资料.zip");
        File.WriteAllText(path, "clipboard fixture");
        var sent = text with { Id = Guid.NewGuid(), Kind = "file", Outgoing = true, State = "sent", Path = path };
        var received = sent with { Outgoing = false, State = "saved" };
        foreach (var item in new[] { sent, received })
            Check(PigeonClipboard.CreateData(item).GetFileDropList().Cast<string>().SequenceEqual([path]),
                "sent and received ZIP use native file clipboard format rather than just a filename");
        try { PigeonClipboard.CreateData(received with { State = "receiving" }); throw new Exception("Incomplete file copied"); }
        catch (InvalidOperationException) { }
        try { PigeonClipboard.CreateData(received with { Path = path + ".missing" }); throw new Exception("Missing file copied"); }
        catch (FileNotFoundException) { }

        using var service = new PigeonService(root, startNetworking: false);
        var page = new PigeonPage(service);
        var messages = new StackPanel();
        PigeonPage.RefreshMessages(messages, [text, received], page.Message);
        var view = (Border)messages.Children[0];
        var field = (TextBox)((StackPanel)view.Child).Children[0];
        field.Select(2, 2);
        Check(field.IsReadOnly && field.SelectedText == "消息", "message supports selecting a substring");
        var next = text with { Id = Guid.NewGuid(), Value = "另一条消息" };
        PigeonPage.RefreshMessages(messages, [text with { Unread = true }, received with { Progress = 5 }, next], page.Message);
        Check(ReferenceEquals(messages.Children[0], view) && field.SelectedText == "消息" && messages.Children.Count == 3,
            "incoming messages, unread updates and file progress preserve an existing text selection");
        Check(((Border)messages.Children[1]).ContextMenu.Items.OfType<MenuItem>().Any(item => (string)item.Header == "复制文件"),
            "file bubble exposes copy action");
        PigeonPage.RefreshMessages(messages, [], page.Message);
        Check(messages.Children.Count == 0, "removed history leaves no stale message controls");
    }

    private static void Check(bool passed, string message)
    { if (!passed) throw new InvalidOperationException("Pigeon copy test failed: " + message); }
}
