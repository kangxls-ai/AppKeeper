using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace AppKeeper;

internal static class ModuleSelfTest
{
    internal static async Task Run(string root)
    {
        VerifyAgenda(root);
        await VerifyPigeon(root);
        await AgendaSyncSelfTest.Run(root);
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
        Assert(receiver.UnreadCounts().GetValueOrDefault(aKey) == 3 && a.UnreadCounts().Count == 0, "received messages are unread but outgoing messages are not");
        var displayed = receiver.History(aKey).Select(value => value.Id).ToArray();

        var folder = Directory.CreateDirectory(Path.Combine(root, "folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "传书测试");
        await a.SendPath(bKey, folder);
        await Wait(() => receiver.History(aKey).Any(item => item.Kind == "file" && item.State == "saved"), "trusted auto receive");
        var item = receiver.History(aKey).Single(item => item.Kind == "file");
        using (var zip = ZipFile.OpenRead(item.Path!))
            Assert(zip.GetEntry("readme.txt") != null, "folder packed without extracting");
        Assert(receiver.UnreadCounts()[aKey] == 4, "file offer and completed transfer count once");
        receiver.MarkRead(aKey, displayed);
        Assert(receiver.UnreadCounts()[aKey] == 1, "a message arriving after the displayed snapshot remains unread");
        receiver.MarkRead(aKey, [item.Id]);
        receiver.SetReadingPeer(aKey);
        a.SendText(bKey, "正在前台查看");
        await Wait(() => receiver.History(aKey).Any(value => value.Value == "正在前台查看"), "deliver to foreground conversation");
        Assert(receiver.UnreadCounts().Count == 0, "foreground conversation does not accumulate unread");
        receiver.SetReadingPeer(null);
        a.SendText(bKey, "窗口隐藏后");
        await Wait(() => receiver.History(aKey).Any(value => value.Value == "窗口隐藏后"), "deliver after hiding");
        Assert(receiver.UnreadCounts()[aKey] == 1, "hidden or background conversation accumulates unread again");

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
        receiver.MarkRead(aKey, receiver.History(aKey).Select(value => value.Id));
        var partial = Path.Combine(item.Path!, ".appkeeper-" + item.Id.ToString("N") + ".partial");
        File.WriteAllText(partial, "0123");
        receiver.Answer(item.Id, true);
        await Wait(() => receiver.History(aKey).Any(value => value.Id == item.Id && value.State == "saved"), "resume from partial file");
        var received = receiver.History(aKey).Single(value => value.Id == item.Id);
        Assert(File.ReadAllText(received.Path!) == "0123456789", "resumed file contents");
        Assert(receiver.UnreadCounts().Count == 0, "resume does not add a second file notification");
        await Wait(() => a.History(bKey).Any(value => value.Id == received.Id && value.State == "sent"), "ordinary file sent");
        Assert(a.History(bKey).Single(value => value.Id == received.Id).LocalFile == path &&
            received.LocalFile == received.Path, "ordinary files can be opened at either end");
        receiver.Configure(bPort, Path.Combine(bDir, "share"), 100);
        await ReadDuringTransfer(receiver, aKey, bKey, aDir, bPort);
        receiver.ClearHistory(aKey);
        a.ClearHistory(bKey);
        Assert(File.Exists(received.Path) && File.Exists(path) && Directory.Exists(folder) &&
            !File.Exists(sentZip), "clear history removes generated outgoing zip only");
    }

    private static async Task ReadDuringTransfer(PigeonService receiver, string peer, string expected, string senderDirectory, int port)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("transfer");
        var id = Guid.NewGuid();
        var packet = new PigeonPacket("offer", id, Name: "read-during-transfer.txt", Size: bytes.Length,
            Digest: Convert.ToHexString(SHA256.HashData(bytes)));
        using var identity = PigeonWire.Identity(Path.Combine(senderDirectory, "AppKeeper.pigeon.pfx"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var stream = PigeonWire.Encrypted(client.GetStream(), expected);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "AppKeeper Pigeon", ClientCertificates = new X509CertificateCollection { identity },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, timeout.Token);
            await PigeonWire.Send(stream, packet, timeout.Token);
            var answer = await PigeonWire.Read(stream, timeout.Token);
            if (attempt == 0)
            {
                Assert(answer.Op == "resume" && receiver.UnreadCounts()[peer] == 1, "receiving file has exactly one unread notification");
                receiver.MarkRead(peer, [id]);
                Assert(receiver.UnreadCounts().Count == 0, "open conversation while the transfer is still blocked awaiting bytes");
                await stream.WriteAsync(bytes, timeout.Token);
                answer = await PigeonWire.Read(stream, timeout.Token);
            }
            Assert(answer.Op == "ok" && receiver.UnreadCounts().Count == 0 &&
                receiver.History(peer).Single(value => value.Id == id) is { State: "saved", Unread: false },
                "completion and duplicate offers cannot resurrect an already-read file notification");
        }
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
