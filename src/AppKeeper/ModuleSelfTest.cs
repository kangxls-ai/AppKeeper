using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace AppKeeper;

internal static class ModuleSelfTest
{
    internal static async Task Run(string root)
    {
        VerifyAgenda(root);
        await VerifyPigeon(root);
    }

    private static void VerifyAgenda(string root)
    {
        var original = Path.Combine(root, "original");
        var replacement = Path.Combine(root, "replacement");
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(replacement);
        var first = new AgendaStore(original);
        var second = new AgendaStore(replacement);
        var id = Guid.NewGuid().ToString();
        var deadline = DateTimeOffset.Now.AddDays(1);
        first.Append("create", id, new { title = "原有事项", category = "工作", deadline = AgendaStore.Iso(deadline), deadlineHasTime = true });
        second.Connect(first.PathName);
        Assert(second.Tasks().Single().On(deadline.LocalDateTime.Date), "MarkDo existing task in calendar");
        second.Append("complete", id, new { completedAt = AgendaStore.Iso(DateTimeOffset.Now), effortSeconds = 1500, rating = 5, note = "完成" });
        Assert(first.Tasks().Single().Seconds == 1500 && first.Markdown(DateTime.Today, DateTime.Today).Contains("评分：5/5"),
            "MarkDo save and export");
        second.Connect(null);
        Assert(second.Tasks().Length == 0 && first.Tasks().Length == 1, "local and existing data remain distinct");
    }

    private static async Task VerifyPigeon(string root)
    {
        var aDir = Directory.CreateDirectory(Path.Combine(root, "pigeon-a")).FullName;
        var bDir = Directory.CreateDirectory(Path.Combine(root, "pigeon-b")).FullName;
        var aPort = Port();
        int bPort;
        do { bPort = Port(); } while (bPort == aPort);
        ModuleFiles.Write(Path.Combine(aDir, "AppKeeper.pigeon.json"), new PigeonData { Port = aPort });
        ModuleFiles.Write(Path.Combine(bDir, "AppKeeper.pigeon.json"), new PigeonData { Port = bPort });
        using var a = new PigeonService(aDir);
        string bKey;
        using (var b = new PigeonService(bDir))
        {
            var remote = await a.Pair("127.0.0.1", bPort, null, false);
            bKey = remote.Key;
            var incoming = b.Peers().Single();
            Assert(remote.State == "outgoing" && incoming.State == "incoming" &&
                a.Code(remote) == b.Code(incoming), "pair requires approval and matching codes");
            b.Allow(incoming.Key, true);
            await Wait(() => a.Peers().Single().State == "paired", "paired");
        }

        a.SendText(bKey, "离线排队一");
        a.SendText(bKey, "离线排队二");
        var longMessage = new string('传', 1994) + "\n🐦🐦结束。";
        Assert(PigeonService.MessageLength(longMessage) == 2000, "long message includes line break and emoji");
        a.SendText(bKey, longMessage);
        try
        {
            a.SendText(bKey, longMessage + "字");
            throw new InvalidOperationException("Overlong message should be rejected");
        }
        catch (ArgumentException) { }
        Assert(a.History(bKey).All(item => item.State == "queued"), "offline messages persisted");
        using var receiver = new PigeonService(bDir);
        var aKey = receiver.Peers().Single().Key;
        await Wait(() => receiver.History(aKey).Count(item => item.Kind == "text") == 3, "deliver on reconnect");
        Assert(receiver.History(aKey).Select(item => item.Value).SequenceEqual(new[] { "离线排队一", "离线排队二", longMessage }),
            "message order");

        var folder = Directory.CreateDirectory(Path.Combine(root, "folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "传书测试");
        await a.SendPath(bKey, folder);
        await Wait(() => receiver.History(aKey).Any(item => item.Kind == "file" && item.State == "saved"), "trusted auto receive");
        var item = receiver.History(aKey).Single(item => item.Kind == "file");
        using (var zip = ZipFile.OpenRead(item.Path!))
            Assert(zip.GetEntry("readme.txt") != null, "folder packed without extracting");

        await Wait(() => a.History(bKey).Any(value => value.Id == item.Id && value.State == "sent"), "archive sent");
        var outgoingFolder = a.History(bKey).Single(value => value.Id == item.Id);
        var sentZip = outgoingFolder.LocalFile;
        Assert(outgoingFolder.OriginalFolder == folder && sentZip != null && File.Exists(sentZip) &&
            item.LocalFile == item.Path, "sent and saved zip files can both be opened");
        using (var zip = ZipFile.OpenRead(sentZip!))
            Assert(zip.GetEntry("readme.txt") != null, "retain a valid outgoing ZIP");

        receiver.Configure(bPort, Path.Combine(bDir, "share"), 1);
        var path = Path.Combine(root, "resume.txt");
        File.WriteAllText(path, "0123456789");
        await a.SendPath(bKey, path);
        await Wait(() => receiver.History(aKey).Any(item => item.Value == "resume.txt"), "file offer arrives");
        item = receiver.History(aKey).Single(item => item.Value == "resume.txt");
        Assert(item.State == "pending" && item.LocalFile == null, "pending file cannot be opened");
        var partial = Path.Combine(item.Path!, ".appkeeper-" + item.Id.ToString("N") + ".partial");
        File.WriteAllText(partial, "0123");
        receiver.Answer(item.Id, true);
        await Wait(() => receiver.History(aKey).Any(value => value.Id == item.Id && value.State == "saved"), "resume from partial file");
        var received = receiver.History(aKey).Single(value => value.Id == item.Id);
        Assert(File.ReadAllText(received.Path!) == "0123456789", "resumed file contents");
        await Wait(() => a.History(bKey).Any(value => value.Id == received.Id && value.State == "sent"), "ordinary file sent");
        Assert(a.History(bKey).Single(value => value.Id == received.Id).LocalFile == path &&
            received.LocalFile == received.Path, "ordinary files can be opened at either end");
        receiver.ClearHistory(aKey);
        a.ClearHistory(bKey);
        Assert(File.Exists(received.Path) && File.Exists(path) && Directory.Exists(folder) &&
            !File.Exists(sentZip), "clear history removes generated outgoing zip only");
    }

    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task Wait(Func<bool> ready, string description)
    {
        for (var i = 0; i < 160; i++)
        {
            if (ready()) return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Timed out: " + description);
    }

    private static void Assert(bool passed, string description)
    {
        if (!passed) throw new InvalidOperationException("Test failed: " + description);
    }
}
