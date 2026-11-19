using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AppKeeper;

internal sealed class PigeonService : IDisposable
{
    private const int DiscoveryPort = 48619;
    internal const int MessageLimit = 2000;
    // Older versions could already send up to 12,000 UTF-16 units.
    private const int LegacyReceiveLimit = 12000;

    internal static int MessageLength(string text)
    {
        var count = 0;
        foreach (var rune in text.Trim().EnumerateRunes())
            count++;
        return count;
    }
    private readonly string root;
    private readonly string statePath;
    private readonly string staging;
    private readonly X509Certificate2 identity;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly PigeonData data;
    private readonly ConcurrentDictionary<string, DiscoveredPeer> discovered = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> online = new();
    private readonly ConcurrentDictionary<Guid, byte> transfers = new();
    private readonly ConcurrentDictionary<Guid, long> progress = new();
    private readonly ConcurrentDictionary<string, byte> working = new();
    private readonly SemaphoreSlim connections = new(24);
    private TcpListener? listener;
    private string warning = "";
    private readonly string key;
    private string? readingPeer;
    internal event Action? UnreadChanged;

    public PigeonService(string root, bool startNetworking = true)
    {
        this.root = root;
        statePath = Path.Combine(root, "AppKeeper.pigeon.json");
        staging = Path.Combine(root, "pigeon-transfer");
        data = ModuleFiles.Read<PigeonData>(statePath);
        identity = PigeonWire.Identity(Path.Combine(root, "AppKeeper.pigeon.pfx"));
        key = PigeonWire.Fingerprint(identity);
        if (startNetworking)
        {
            StartListener();
            _ = Task.Run(() => DiscoveryLoop(stop.Token));
            _ = Task.Run(() => DeliveryLoop(stop.Token));
        }
    }

    public string Warning => warning;
    public (int Port, string Share, long Limit) Settings
    {
        get { lock (gate) return (data.Port, SharePath(), data.AutoLimit); }
    }

    public string Code(PigeonPeer peer) => PigeonWire.Code(key, peer.Key);

    public PigeonPeer[] Peers()
    {
        lock (gate) return data.Peers.ToArray();
    }

    public DiscoveredPeer[] Discovered() => discovered.Values
        .Where(item => DateTimeOffset.UtcNow - item.LastSeen < TimeSpan.FromSeconds(12)).ToArray();

    public bool Online(string peer) => online.TryGetValue(peer, out var at) &&
        DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(10);

    public PigeonItem[] History(string peer)
    {
        lock (gate) return data.Items.Where(item => item.Peer == peer && !item.Archived)
            .Select(item => progress.TryGetValue(item.Id, out var count) && item.State is "queued" or "receiving"
                ? item with { Progress = count } : item).ToArray();
    }

    internal Dictionary<string, int> UnreadCounts()
    {
        lock (gate) return PigeonUnread.Counts(data);
    }

    // This foreground context is intentionally transient. A restart never reads existing messages implicitly.
    internal void SetReadingPeer(string? peer)
    {
        lock (gate) readingPeer = peer != null && data.Peers.Any(item => item.Key == peer && item.State == "paired") ? peer : null;
    }

    internal bool MarkRead(string peer, IEnumerable<Guid> displayed)
    {
        var ids = displayed.ToHashSet();
        lock (gate)
        {
            if (!data.Items.Any(item => item.Peer == peer && item.Unread && PigeonUnread.Incoming(item) && ids.Contains(item.Id)))
                return false;
            Edit(next =>
            {
                for (var i = 0; i < next.Items.Count; i++)
                {
                    var item = next.Items[i];
                    if (item.Peer == peer && item.Unread && PigeonUnread.Incoming(item) && ids.Contains(item.Id))
                        next.Items[i] = item with { Unread = false };
                }
            });
            return true;
        }
    }

    private string SharePath() => Path.GetFullPath(data.Share, root);

    public void Configure(int port, string share, long limit, PigeonPeer? peer = null)
    {
        if (port is < 1 or > 65535 or DiscoveryPort || limit < 0)
            throw new ArgumentException("通信端口或自动接收上限无效。");
        var directory = Path.GetFullPath(share, root);
        if (peer != null)
            peer = peer with { Address = Address(peer.Address, peer.Port).ToString() };
        void Save(PigeonData state)
        {
            state.Port = port;
            state.Share = directory;
            state.AutoLimit = limit;
            if (peer != null)
                Replace(state.Peers, item => item.Key == peer.Key, peer);
        }
        Directory.CreateDirectory(directory);
        lock (gate)
        {
            if (port != data.Port || listener == null)
            {
                var next = new TcpListener(IPAddress.Any, port);
                next.Start(24);
                try { Edit(Save); }
                catch { next.Stop(); throw; }
                listener?.Stop();
                listener = next;
                warning = "";
                _ = Task.Run(() => AcceptLoop(next, stop.Token));
            }
            else
                Edit(Save);
        }
    }

    public async Task<PigeonPeer> Pair(string address, int port, string? expected, bool auto)
    {
        var ip = Address(address, port);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(ip, port, timeout.Token);
        using var stream = PigeonWire.Encrypted(client.GetStream(), expected);
        await Authenticate(stream, timeout.Token);
        var fingerprint = PigeonWire.Fingerprint(stream.RemoteCertificate!);
        if (fingerprint == key)
            throw new InvalidOperationException("无法与本机配对。");
        var answer = await Exchange(stream, new("pair", Name: Environment.MachineName, Port: Settings.Port), timeout.Token);
        if (answer.Op is not ("pending" or "paired"))
            throw new InvalidOperationException("对方未接受配对请求。");
        var peer = new PigeonPeer(fingerprint, CleanName(answer.Name), ip.ToString(), port,
            answer.Op == "paired" ? "paired" : "outgoing", auto);
        Edit(d => Replace(d.Peers, p => p.Key == peer.Key, peer));
        return peer;
    }

    public void Allow(string fingerprint, bool auto) => ChangePeer(fingerprint,
        peer => peer with { State = "paired", Auto = auto });

    public void Reject(string fingerprint) => ChangePeer(fingerprint,
        peer => peer with { State = "rejected", Auto = false });

    private static IPAddress Address(string address, int port)
    {
        if (!IPAddress.TryParse(address.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.Any.Equals(ip) || port is < 1 or > 65535)
            throw new ArgumentException("请输入有效的 IPv4 地址和端口。");
        return ip;
    }

    private void ChangePeer(string fingerprint, Func<PigeonPeer, PigeonPeer> change)
    {
        Edit(d =>
        {
            var index = d.Peers.FindIndex(p => p.Key == fingerprint);
            if (index >= 0)
                d.Peers[index] = change(d.Peers[index]);
        });
    }

    public void SendText(string peer, string text)
    {
        text = text.Trim();
        if (MessageLength(text) is < 1 or > MessageLimit)
            throw new ArgumentException($"文字消息须在 1 至 {MessageLimit} 字符之间。");
        RequirePeer(peer);
        Edit(d => d.Items.Add(new(Guid.NewGuid(), peer, true, "text", text, DateTimeOffset.Now, "queued")));
    }

    public async Task SendPath(string peer, string path)
    {
        RequirePeer(peer);
        var id = Guid.NewGuid();
        var source = Path.GetFullPath(path);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        var packaged = Directory.Exists(source);
        if (packaged && (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("请选择原始文件夹。");
        if (packaged)
            name += ".zip";
        var item = await Task.Run(() =>
        {
            var input = packaged ? Path.Combine(staging, id.ToString("N") + ".zip") : source;
            try
            {
                if (packaged)
                    Pack(source, input);
                using var file = File.OpenRead(input);
                var hash = Convert.ToHexString(SHA256.HashData(file));
                return new PigeonItem(id, peer, true, "file", SafeName(name), DateTimeOffset.Now,
                    "queued", file.Length, input, hash, SourceModified: File.GetLastWriteTimeUtc(input),
                    OriginalFolder: packaged ? source : null);
            }
            catch
            {
                if (packaged)
                    File.Delete(input);
                throw;
            }
        });
        try { Edit(d => d.Items.Add(item)); }
        catch
        {
            CleanOutgoing(item);
            throw;
        }
    }

    private void Pack(string source, string destination)
    {
        Directory.CreateDirectory(staging);
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var entry in Directory.EnumerateFileSystemEntries(source, "*", options))
        {
            stop.Token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, entry).Replace('\\', '/');
            if (Directory.Exists(entry))
                archive.CreateEntry(relative + "/");
            else
                archive.CreateEntryFromFile(entry, relative, CompressionLevel.Fastest);
        }
    }

    public void Answer(Guid id, bool accept) => SetState(id, accept ? "receiving" : "rejected");
    public void Cancel(Guid id)
    {
        lock (gate)
        {
            var item = data.Items.Find(item => item.Id == id);
            if (item == null || item.State is "sent" or "saved")
                return;
            SetState(id, item.Outgoing ? "cancelling" : "cancelled");
        }
    }

    public void Retry(Guid id) => SetState(id, "queued");

    public void ClearHistory(string peer)
    {
        var removed = new List<PigeonItem>();
        Edit(d =>
        {
            for (var i = d.Items.Count - 1; i >= 0; i--)
            {
                var item = d.Items[i];
                if (item.Peer != peer || item.State is not ("sent" or "saved" or "rejected" or "cancelled"))
                    continue;
                if (item.Outgoing)
                {
                    d.Items.RemoveAt(i);
                    removed.Add(item);
                }
                else
                    d.Items[i] = item with { Archived = true, Value = "", Path = null, Unread = false };
            }
        });
        foreach (var item in removed)
            CleanOutgoing(item);
    }

    private void RequirePeer(string peer)
    {
        if (!Peers().Any(item => item.Key == peer && item.State == "paired"))
            throw new InvalidOperationException("请先完成配对。");
    }

    private void Edit(Action<PigeonData> action)
    {
        bool changed;
        lock (gate)
        {
            var previousUnread = PigeonUnread.Counts(data);
            var snapshot = new PigeonData { Port = data.Port, Share = data.Share,
                AutoLimit = data.AutoLimit, Peers = [..data.Peers], Items = [..data.Items] };
            action(snapshot);
            ModuleFiles.Write(statePath, snapshot);
            data.Port = snapshot.Port;
            data.Share = snapshot.Share;
            data.AutoLimit = snapshot.AutoLimit;
            data.Peers = snapshot.Peers;
            data.Items = snapshot.Items;
            var currentUnread = PigeonUnread.Counts(data);
            changed = previousUnread.Count != currentUnread.Count || previousUnread.Any(item => currentUnread.GetValueOrDefault(item.Key) != item.Value);
        }
        if (changed)
        {
            try { UnreadChanged?.Invoke(); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Unread badge observer failed: " + error.Message); }
        }
    }

    private void SetState(Guid id, string state) => Edit(d =>
    {
        var index = d.Items.FindIndex(item => item.Id == id);
        if (index >= 0)
            d.Items[index] = d.Items[index] with { State = state };
    });

    private static void Replace<T>(List<T> list, Predicate<T> match, T value)
    {
        var index = list.FindIndex(match);
        if (index < 0) list.Add(value);
        else list[index] = value;
    }

    private void StartListener()
    {
        try
        {
            var server = new TcpListener(IPAddress.Any, data.Port);
            server.Start(24);
            listener = server;
            _ = Task.Run(() => AcceptLoop(server, stop.Token));
        }
        catch (SocketException) { warning = "通信端口已被占用，请在传书设置中更改。"; }
    }

    private async Task AcceptLoop(TcpListener server, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await server.AcceptTcpClientAsync(token);
                if (!connections.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                _ = Serve(client, token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task Serve(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            try
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var stream = PigeonWire.Encrypted(client.GetStream());
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = identity, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token);
                var remote = PigeonWire.Fingerprint(stream.RemoteCertificate!);
                var packet = await PigeonWire.Read(stream, timeout.Token);
                var peer = Peers().FirstOrDefault(item => item.Key == remote);
                if (packet.Op == "pair")
                {
                    if (peer == null && packet.Port is > 0 and <= 65535 && Peers().Length < 256 &&
                        client.Client.RemoteEndPoint is IPEndPoint endpoint)
                    {
                        peer = new(remote, CleanName(packet.Name), endpoint.Address.ToString(), packet.Port, "incoming", false);
                        var request = peer;
                        Edit(d => { if (!d.Peers.Any(item => item.Key == remote)) d.Peers.Add(request); });
                    }
                    await PigeonWire.Send(stream, new(peer?.State switch
                    {
                        "paired" => "paired", "incoming" or "outgoing" => "pending", _ => "rejected"
                    }, Name: Environment.MachineName), timeout.Token);
                }
                else if (peer?.State == "paired")
                {
                    online[remote] = DateTimeOffset.UtcNow;
                    if (packet.Op == "offer")
                        await ReceiveFile(stream, peer, packet, timeout);
                    else
                        await HandleMessage(stream, remote, packet, timeout.Token);
                }
                else
                    await PigeonWire.Send(stream, new("rejected"), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
                OperationCanceledException or JsonException or UnauthorizedAccessException or CryptographicException or ArgumentException or InvalidOperationException) { }
            finally { connections.Release(); }
        }
    }

    private async Task HandleMessage(SslStream stream, string remote, PigeonPacket packet, CancellationToken token)
    {
        if (packet.Op == "text" && packet.Id != Guid.Empty && packet.Text is { Length: > 0 and <= LegacyReceiveLimit } text)
        {
            lock (gate)
            {
                if (!data.Items.Any(item => item.Id == packet.Id && item.Peer == remote && !item.Outgoing))
                    Edit(d => d.Items.Add(new(packet.Id, remote, false, "text", text, DateTimeOffset.Now, "saved", Unread: readingPeer != remote)));
            }
        }
        else if (packet.Op == "cancel")
        {
            lock (gate)
            {
                if (data.Items.Any(item => item.Id == packet.Id && item.Peer == remote &&
                    !item.Outgoing && item.State is "pending" or "receiving"))
                    SetState(packet.Id, "cancelled");
            }
        }
        else if (packet.Op != "ping")
        {
            await PigeonWire.Send(stream, new("rejected"), token);
            return;
        }
        await PigeonWire.Send(stream, new("ok"), token);
    }

    private async Task ReceiveFile(SslStream stream, PigeonPeer peer, PigeonPacket offer, CancellationTokenSource idle)
    {
        var token = idle.Token;
        if (offer.Id == Guid.Empty || offer.Size < 0 || offer.Digest?.Length != 64 || !offer.Digest.All(Uri.IsHexDigit) ||
            offer.Name is not { Length: > 0 and <= 240 })
        {
            await PigeonWire.Send(stream, new("rejected"), token);
            return;
        }
        PigeonItem item;
        lock (gate)
        {
            var existing = data.Items.Find(entry => entry.Id == offer.Id);
            item = existing ?? new(offer.Id, peer.Key, false, "file", SafeName(offer.Name),
                DateTimeOffset.Now, peer.Auto && offer.Size <= data.AutoLimit ? "receiving" : "pending",
                offer.Size, SharePath(), offer.Digest, Unread: readingPeer != peer.Key);
            if (existing == null)
                Edit(d => d.Items.Add(item));
        }

        if (item.Outgoing || item.Peer != peer.Key || item.Kind != "file" ||
            item.Digest != offer.Digest || item.Size != offer.Size ||
            item.State is "rejected" or "cancelled")
        {
            await PigeonWire.Send(stream, new("rejected"), token);
            return;
        }
        if (item.State != "receiving" || !transfers.TryAdd(item.Id, 0))
        {
            await PigeonWire.Send(stream, new(item.State == "saved" ? "ok" : "pending"), token);
            return;
        }

        var part = Path.Combine(item.Path!, ".appkeeper-" + item.Id.ToString("N") + ".partial");
        try
        {
            Directory.CreateDirectory(item.Path!);
            var name = SafeName(item.Value);
            using (var file = new FileStream(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 128 * 1024, true))
            {
                if (file.Length > item.Size) file.SetLength(0);
                file.Position = file.Length;
                await PigeonWire.Send(stream, new("resume", Size: file.Length), token);
                var buffer = new byte[128 * 1024];
                while (file.Position < item.Size)
                {
                    lock (gate)
                    {
                        if (!data.Items.Any(entry => entry.Id == item.Id && entry.State == "receiving"))
                            throw new OperationCanceledException();
                    }
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, item.Size - file.Position)), token);
                    if (read == 0) throw new EndOfStreamException();
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                    progress[item.Id] = file.Position;
                }
                file.Flush(true);
                file.Position = 0;
                idle.CancelAfter(TimeSpan.FromMinutes(5));
                if (Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != item.Digest)
                {
                    file.SetLength(0);
                    await PigeonWire.Send(stream, new("retry"), token);
                    return;
                }
            }

            lock (gate)
            {
                if (!data.Items.Any(entry => entry.Id == item.Id && entry.State == "receiving"))
                    return;
                var path = Path.Combine(item.Path!, item.Id.ToString("N")[..8] + "-" + name);
                if (File.Exists(path))
                {
                    using var prior = File.OpenRead(path);
                    if (Convert.ToHexString(SHA256.HashData(prior)) != item.Digest)
                        path = Path.Combine(item.Path!, Guid.NewGuid().ToString("N")[..8] + "-" + name);
                }
                if (File.Exists(path))
                    File.Delete(part);
                else
                    File.Move(part, path);
                Edit(d =>
                {
                    var current = d.Items.Find(value => value.Id == item.Id)!;
                    Replace(d.Items, value => value.Id == item.Id, current with { Path = path, State = "saved" });
                });
            }
            await PigeonWire.Send(stream, new("ok"), token);
        }
        finally
        {
            transfers.TryRemove(item.Id, out _);
            progress.TryRemove(item.Id, out _);
            lock (gate)
                if (data.Items.Any(entry => entry.Id == item.Id && entry.State == "cancelled"))
                    File.Delete(part);
        }
    }

    private async Task DeliveryLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                foreach (var peer in Peers().Where(item => item.State is "paired" or "outgoing"))
                {
                    if (working.TryAdd(peer.Key, 0))
                        _ = Deliver(peer, token);
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) { }
    }

    private async Task Deliver(PigeonPeer peer, CancellationToken token)
    {
        try
        {
            if (peer.State == "outgoing")
            {
                var answer = await Request(peer, new("pair", Name: Environment.MachineName, Port: Settings.Port), token);
                if (answer.Op is "paired" or "rejected")
                    ChangePeer(peer.Key, p => p with { State = answer.Op });
                return;
            }

            var status = await Request(peer, new("ping"), token);
            if (status.Op != "ok")
                return;
            online[peer.Key] = DateTimeOffset.UtcNow;
            foreach (var item in History(peer.Key).Where(item => item.Outgoing && item.State is "queued" or "cancelling")
                .OrderBy(item => item.Kind == "text" ? 0 : 1).ThenBy(item => item.At).Take(16))
            {
                if (item.Kind == "file" && item.State == "queued")
                {
                    if (transfers.TryAdd(item.Id, 0))
                        _ = SendFile(peer, item, token);
                    continue;
                }
                var answer = await Request(peer, item.State == "cancelling" ? new("cancel", item.Id) :
                    new("text", item.Id, item.Value), token);
                if (answer.Op == "ok")
                {
                    SetState(item.Id, item.State == "cancelling" ? "cancelled" : "sent");
                    if (item.State == "cancelling") CleanOutgoing(item);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
            OperationCanceledException or JsonException or UnauthorizedAccessException or CryptographicException or ArgumentException or InvalidOperationException) { }
        finally { working.TryRemove(peer.Key, out _); }
    }

    private async Task SendFile(PigeonPeer peer, PigeonItem item, CancellationToken token)
    {
        try
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (!File.Exists(item.Path))
            {
                SetState(item.Id, "failed");
                return;
            }
            var stamp = new FileInfo(item.Path!);
            if (stamp.Length != item.Size || item.SourceModified.HasValue && stamp.LastWriteTimeUtc != item.SourceModified)
            {
                SetState(item.Id, "failed");
                return;
            }
            idle.CancelAfter(TimeSpan.FromSeconds(10));
            using var client = await Connect(peer, idle.Token);
            using var stream = PigeonWire.Encrypted(client.GetStream(), peer.Key);
            await Authenticate(stream, idle.Token);
            var answer = await Exchange(stream, new("offer", item.Id, Name: item.Value, Size: item.Size, Digest: item.Digest), idle.Token);
            if (answer.Op == "resume" && answer.Size >= 0 && answer.Size <= item.Size)
            {
                using var file = new FileStream(item.Path!, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
                if (file.Length != item.Size || item.SourceModified.HasValue && File.GetLastWriteTimeUtc(item.Path!) != item.SourceModified)
                {
                    SetState(item.Id, "failed");
                    return;
                }
                file.Position = answer.Size;
                var buffer = new byte[128 * 1024];
                while (file.Position < item.Size)
                {
                    lock (gate)
                    {
                        if (!data.Items.Any(entry => entry.Id == item.Id && entry.State == "queued"))
                            return;
                    }
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    var read = await file.ReadAsync(buffer, idle.Token);
                    if (read == 0) throw new EndOfStreamException();
                    await stream.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                    progress[item.Id] = file.Position;
                }
                idle.CancelAfter(TimeSpan.FromMinutes(5));
                answer = await PigeonWire.Read(stream, idle.Token);
            }
            if (answer.Op == "retry")
            {
                SetState(item.Id, "failed");
                return;
            }
            if (answer.Op is "rejected" or "ok")
            {
                SetState(item.Id, answer.Op == "ok" ? "sent" : "rejected");
                if (answer.Op == "rejected")
                    CleanOutgoing(item);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or
            OperationCanceledException or JsonException or UnauthorizedAccessException or CryptographicException or ArgumentException or InvalidOperationException) { }
        finally { transfers.TryRemove(item.Id, out _); progress.TryRemove(item.Id, out _); }
    }

    private void CleanOutgoing(PigeonItem item)
    {
        if (item.Path == Path.Combine(staging, item.Id.ToString("N") + ".zip"))
            File.Delete(item.Path);
    }

    private async Task<PigeonPacket> Request(PigeonPeer peer, PigeonPacket packet, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var client = await Connect(peer, timeout.Token);
        using var stream = PigeonWire.Encrypted(client.GetStream(), peer.Key);
        await Authenticate(stream, timeout.Token);
        return await Exchange(stream, packet, timeout.Token);
    }

    private async Task<TcpClient> Connect(PigeonPeer peer, CancellationToken token)
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            var known = Discovered().FirstOrDefault(item => item.Computer.Key == peer.Key);
            if (known != null && (known.Address != peer.Address || known.Computer.Port != peer.Port))
            {
                using var fast = CancellationTokenSource.CreateLinkedTokenSource(token);
                fast.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await client.ConnectAsync(known.Address, known.Computer.Port, fast.Token);
                    return client;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    client.Dispose();
                    client = new TcpClient(AddressFamily.InterNetwork);
                }
            }
            await client.ConnectAsync(peer.Address, peer.Port, token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private Task Authenticate(SslStream stream, CancellationToken token) => stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
    {
        TargetHost = "AppKeeper Pigeon", ClientCertificates = new X509CertificateCollection { identity },
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
    }, token);

    private static async Task<PigeonPacket> Exchange(SslStream stream, PigeonPacket packet, CancellationToken token)
    {
        await PigeonWire.Send(stream, packet, token);
        return await PigeonWire.Read(stream, token);
    }

    private async Task DiscoveryLoop(CancellationToken token)
    {
        try
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.EnableBroadcast = true;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            var receive = ListenForPeers(socket, token);
            do
            {
                try
                {
                    if (warning.Length == 0)
                        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new Discovery(key, Environment.MachineName, Settings.Port)),
                            new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), token);
                }
                catch (SocketException) { }
            } while (!receive.IsCompleted && await timer.WaitForNextTickAsync(token));
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
    }

    private async Task ListenForPeers(UdpClient socket, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var packet = await socket.ReceiveAsync(token);
                if (packet.Buffer.Length > 512) continue;
                try
                {
                    var ping = JsonSerializer.Deserialize<Discovery>(packet.Buffer);
                    if (ping?.Key is { Length: 64 } && ping.Key != key && ping.Key.All(Uri.IsHexDigit) &&
                        ping.Port is > 0 and <= 65535 && ping.Name is { Length: > 0 and <= 100 })
                    {
                        if (discovered.Count >= 256)
                        {
                            foreach (var stale in discovered.Where(pair => DateTimeOffset.UtcNow - pair.Value.LastSeen > TimeSpan.FromSeconds(12)))
                                discovered.TryRemove(stale.Key, out _);
                        }
                        if (discovered.Count < 256 || discovered.ContainsKey(ping.Key))
                            discovered[ping.Key] = new(ping, packet.RemoteEndPoint.Address.ToString(), DateTimeOffset.UtcNow);
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private static string CleanName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "未命名电脑" : string.Concat(name.Where(character => !char.IsControl(character)).Take(100));

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).Take(180).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "文件" : clean;
    }

    public void Dispose()
    {
        SetReadingPeer(null);
        stop.Cancel();
        listener?.Stop();
        identity.Dispose();
    }
}
