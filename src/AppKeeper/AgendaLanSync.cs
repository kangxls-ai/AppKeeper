using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AppKeeper;

internal sealed record AgendaPhone(string Id, string Name, string ProtectedKey, DateTimeOffset LastSeen);
internal sealed class AgendaSyncSettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 8787;
    public List<AgendaPhone> Phones { get; set; } = [];
}
internal sealed record AgendaPairOffer(string App, int Version, string ServerId, string ServerName, string[] Hosts,
    int Port, string SessionId, string Secret, string Expires);
internal sealed class AgendaPairSession(AgendaPairOffer offer, string code)
{
    internal AgendaPairOffer Offer { get; } = offer;
    internal string Code { get; } = code;
    internal int Attempts { get; set; }
    internal bool Available => Attempts < 8 && DateTimeOffset.UtcNow < DateTimeOffset.Parse(Offer.Expires);
}
internal sealed record AgendaSyncState(bool Enabled, bool Serving, int Port, string Status, DateTimeOffset? LastSync,
    AgendaPhone[] Phones, AgendaPairSession? Session);

internal sealed class AgendaLanSync : IDisposable
{
    private readonly AgendaStore store;
    private readonly string file;
    private readonly object gate = new();
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly Dictionary<string, AgendaPairOffer> manual = [];
    private readonly HashSet<string> seen = [];
    private readonly Queue<string> seenOrder = new();
    private AgendaSyncSettings settings;
    private WebApplication? application;
    private AgendaPairSession? session;
    private DateTimeOffset? lastSync;
    private string status = "手机同步服务尚未启动";
    private string credentialNotice = "";
    private int port;
    private bool disposed;
    internal string ServerName => Environment.MachineName;
    internal string ServerId => store.DeviceId;

    internal AgendaLanSync(AgendaStore store, string directory)
    {
        this.store = store;
        file = Path.Combine(directory, "AppKeeper.agenda-sync.json");
        try { settings = ReadSettings(File.Exists(file) ? file : File.Exists(file + ".bak") ? file + ".bak" : file); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            if (!File.Exists(file + ".bak")) throw new InvalidDataException("手机同步设置无法读取，请保留设置文件并恢复备份。", error);
            settings = ReadSettings(file + ".bak");
            status = "手机同步设置已从备份恢复";
        }
        if (settings.Port is < 1 or > 65535 || settings.Phones == null || settings.Phones.Any(phone => phone == null) ||
            settings.Phones.Select(phone => phone.Id).Distinct().Count() != settings.Phones.Count)
            throw new InvalidDataException("手机同步设置无效。");
        var readable = new List<AgendaPhone>();
        foreach (var phone in settings.Phones)
        {
            try { _ = AgendaSyncProtocol.DecodeKey(AgendaKeyProtection.Unprotect(phone.ProtectedKey)); readable.Add(phone); }
            catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
            { credentialNotice = "部分手机凭据无法用于当前 Windows 用户，请重新配对；日程保留。"; }
        }
        settings.Phones = readable;
    }

    private static AgendaSyncSettings ReadSettings(string file) => ModuleFiles.Read<AgendaSyncSettings>(file);
    private void Persist(AgendaSyncSettings next)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ModuleFiles.Write(temp, next);
            var good = false;
            if (File.Exists(file))
            {
                try { _ = ReadSettings(file); good = true; }
                catch (Exception error) when (error is IOException or JsonException) { }
                File.Replace(temp, file, good ? file + ".bak" : null);
            }
            else File.Move(temp, file);
            settings = next;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal AgendaSyncState Snapshot()
    {
        lock (gate) return new(settings.Enabled, application != null, port == 0 ? settings.Port : port,
            credentialNotice.Length == 0 ? status : status + " · " + credentialNotice, lastSync,
            settings.Phones.ToArray(), session);
    }

    internal async Task StartAsync(IPAddress? address = null, int? overridePort = null)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        WebApplication? next = null;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (application != null) return;
                if (!settings.Enabled) { status = "手机同步服务已关闭"; return; }
            }
            var desiredPort = overridePort ?? settings.Port;
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = Path.GetDirectoryName(file) });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IHostLifetime, AgendaHostLifetime>();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 12 * 1024 * 1024;
                options.Limits.MaxConcurrentConnections = 16;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(12);
                options.Listen(address ?? IPAddress.Any, desiredPort);
            });
            next = builder.Build();
            next.Run(Handle);
            await next.StartAsync().ConfigureAwait(false);
            var listening = next.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            lock (gate) { application = next; port = new Uri(listening).Port; status = "电脑已准备连接 MarkDo 手机端"; }
        }
        catch
        {
            if (next != null) await next.DisposeAsync().ConfigureAwait(false);
            lock (gate) status = $"端口 {overridePort ?? settings.Port} 无法启动，可能已被占用；请更换端口或退出原版 Windows MarkDo。";
            throw;
        }
        finally { lifecycle.Release(); }
    }

    internal async Task ConfigureAsync(bool enabled, int number)
    {
        if (number is < 1 or > 65535) throw new ArgumentException("端口范围为 1 至 65535。");
        await StopAsync().ConfigureAwait(false);
        lock (gate) Persist(new() { Enabled = enabled, Port = number, Phones = [..settings.Phones] });
        await StartAsync().ConfigureAwait(false);
    }

    private async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            WebApplication? old;
            lock (gate) { old = application; application = null; port = 0; session = null; manual.Clear(); status = "手机同步服务已停止"; }
            if (old != null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await old.StopAsync(deadline.Token).ConfigureAwait(false); }
                finally { await old.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally { lifecycle.Release(); }
    }

    internal AgendaPairSession CreateSession(string[]? hosts = null)
    {
        var addresses = hosts ?? Addresses().Where(item => IsLan(item.Address)).Select(item => item.Address).Distinct().Take(12).ToArray();
        if (addresses.Length == 0) throw new InvalidOperationException("未找到局域网 IPv4，请先连接 Wi-Fi 或有线网络。");
        lock (gate)
        {
            if (application == null) throw new InvalidOperationException(status);
            var offer = new AgendaPairOffer("markdo", 1, ServerId, ServerName, addresses, port,
                AgendaSyncProtocol.NewKey(), AgendaSyncProtocol.NewKey(), DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
            session = new(offer, RandomNumberGenerator.GetInt32(1000000).ToString("D6"));
            manual.Clear();
            return session;
        }
    }

    internal void Revoke(string id)
    {
        lock (gate)
        {
            Persist(new() { Enabled = settings.Enabled, Port = settings.Port, Phones = settings.Phones.Where(phone => phone.Id != id).ToList() });
            status = "已断开手机配对，手机上的本地日程会保留";
        }
    }

    internal static bool IsLan(string value) => IPAddress.TryParse(value, out var ip) && IsLan(ip);
    private static bool IsLan(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254;
    }
    internal static (string Name, string Address)[] Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses.Where(address => IsLan(address.Address))
            .Select(address => (Name: adapter.Name, Address: address.Address.ToString())))
        .OrderBy(item => new[] { "virtual", "vpn", "wsl", "hyper-v", "vmware", "tun", "tap" }.Any(word => item.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
        .ThenBy(item => item.Address.StartsWith("169.254.", StringComparison.Ordinal)).ToArray();

    private async Task Handle(HttpContext context)
    {
        var response = context.Response;
        response.Headers["X-MarkDo-Server"] = ServerId;
        response.Headers.CacheControl = "no-store";
        response.ContentType = "application/json; charset=utf-8";
        if (context.Connection.RemoteIpAddress is not { } remote || !(IPAddress.IsLoopback(remote) || IsLan(remote)))
        { response.StatusCode = 403; return; }
        try
        {
            var path = context.Request.Path.Value;
            if (context.Request.Method == "GET" && path == "/v1/pairing")
            {
                bool ready;
                lock (gate) ready = session?.Available == true;
                await response.WriteAsync(JsonSerializer.Serialize(new { app = "markdo", serverId = ServerId, serverName = ServerName, ready }, AgendaSyncProtocol.Json));
                return;
            }
            if (context.Request.Method != "POST" || path is not ("/v1/manual-offer" or "/v1/pair" or "/v1/sync"))
            { response.StatusCode = 404; return; }
            var body = await ReadBody(context, path == "/v1/sync" ? 12 * 1024 * 1024 : 16 * 1024);
            string output;
            lock (gate)
            {
                output = path switch
                {
                    "/v1/manual-offer" => ManualOffer(body),
                    "/v1/pair" => Pair(context.Request.Headers["X-MarkDo-Session"].ToString(), body),
                    _ => Sync(context.Request.Headers["X-MarkDo-Device"].ToString(), body)
                };
            }
            await response.WriteAsync(output);
        }
        catch (SyncFailure error) { response.StatusCode = error.Status; }
        catch (BadHttpRequestException error) { response.StatusCode = error.StatusCode; }
        catch (CryptographicException) { response.StatusCode = 401; }
        catch (Exception error) when (error is JsonException or FormatException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { response.StatusCode = 400; }
        catch (OperationCanceledException) { if (!response.HasStarted) response.StatusCode = 408; }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("MarkDo synchronization failed: " + error.Message);
            if (!response.HasStarted) response.StatusCode = 503;
            lock (gate) status = "本次手机同步未完成，请稍后重试并检查数据目录权限。";
        }
    }

    private static async Task<string> ReadBody(HttpContext context, int maximum)
    {
        if (context.Request.ContentLength > maximum) throw new SyncFailure(413);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (bytes.Length + read > maximum) throw new SyncFailure(413);
            bytes.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }

    private string ManualOffer(string body)
    {
        if (session?.Available != true) throw new SyncFailure(410);
        if (body.Length > 1024) throw new SyncFailure(400);
        using var document = JsonDocument.Parse(body);
        var (publicKey, secret) = AgendaSyncProtocol.ManualSecret(document.RootElement.GetProperty("publicKey").GetString()!);
        var offer = session.Offer with { SessionId = AgendaSyncProtocol.NewKey(), Secret = secret };
        if (manual.Count >= 32) manual.Remove(manual.Keys.First());
        manual.Add(offer.SessionId, offer);
        return JsonSerializer.Serialize(new { offer.App, offer.Version, offer.ServerId, offer.ServerName, offer.Hosts, offer.Port,
            offer.SessionId, offer.Expires, publicKey }, AgendaSyncProtocol.Json);
    }

    private string Pair(string sessionId, string body)
    {
        var current = session;
        var offer = sessionId == current?.Offer.SessionId ? current.Offer : manual.GetValueOrDefault(sessionId);
        if (current?.Available != true || offer == null) throw new SyncFailure(410);
        current.Attempts++;
        var key = AgendaSyncProtocol.PairKey(offer.SessionId, offer.Secret, current.Code);
        var message = AgendaSyncProtocol.Decrypt(body, key, AgendaSyncProtocol.PairContext);
        var id = Text(message, "deviceId", 100);
        var name = Text(message, "deviceName", 160);
        var nonce = Text(message, "requestId", 100);
        if (Text(message, "serverId", 100) != ServerId || DateTimeOffset.UtcNow >= DateTimeOffset.Parse(current.Offer.Expires))
            throw new SyncFailure(400);
        var token = AgendaSyncProtocol.NewKey();
        var phone = new AgendaPhone(id, name, AgendaKeyProtection.Protect(token), DateTimeOffset.UtcNow);
        var next = new AgendaSyncSettings { Enabled = settings.Enabled, Port = settings.Port,
            Phones = settings.Phones.Where(item => item.Id != id).Append(phone).ToList() };
        var output = AgendaSyncProtocol.Encrypt(new { serverId = ServerId, serverName = ServerName, requestId = nonce, key = token,
            devices = PublicPhones(next.Phones) }, key, AgendaSyncProtocol.PairContext);
        Persist(next);
        session = null;
        manual.Clear();
        credentialNotice = "";
        status = $"已连接 {name}，等待手机同步";
        return output;
    }

    private string Sync(string id, string body)
    {
        var phone = settings.Phones.FirstOrDefault(item => item.Id == id) ?? throw new SyncFailure(401);
        var key = AgendaSyncProtocol.DecodeKey(AgendaKeyProtection.Unprotect(phone.ProtectedKey));
        var message = AgendaSyncProtocol.Decrypt(body, key);
        if (message.GetProperty("version").GetInt32() != 4) throw new SyncFailure(426);
        if (Text(message, "deviceId", 100) != id) throw new SyncFailure(401);
        var nonce = Text(message, "requestId", 100);
        var replay = id + ":" + nonce;
        if (seen.Contains(replay)) throw new SyncFailure(401);
        var operations = message.GetProperty("operations").Deserialize<AgendaOperation[]>(AgendaSyncProtocol.Json) ?? throw new SyncFailure(400);
        if (operations.Any(operation => operation == null)) throw new SyncFailure(400);
        var now = DateTimeOffset.UtcNow;
        var phones = settings.Phones.Select(item => item.Id == id ? item with { LastSeen = now } : item).ToList();
        var result = store.MergeAndRead(operations, all => AgendaSyncProtocol.Encrypt(new { version = 4, requestId = nonce,
            serverId = ServerId, serverName = ServerName, devices = PublicPhones(phones), operations = all }, key));
        seen.Add(replay); seenOrder.Enqueue(replay);
        if (seenOrder.Count > 2000) seen.Remove(seenOrder.Dequeue());
        Persist(new() { Enabled = settings.Enabled, Port = settings.Port, Phones = phones });
        lastSync = now;
        status = $"已与 {phone.Name} 同步，所有已配对手机通过电脑合并日程";
        return result;
    }

    private static object[] PublicPhones(IEnumerable<AgendaPhone> phones) => phones.Select(phone => (object)new
    { id = phone.Id, name = phone.Name, lastSeen = phone.LastSeen.ToUniversalTime().ToString("O") }).ToArray();
    private static string Text(JsonElement value, string property, int maximum)
    {
        var item = value.GetProperty(property);
        if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } text || text.Length > maximum)
            throw new SyncFailure(400);
        return text;
    }
    private sealed class SyncFailure(int status) : Exception { internal int Status { get; } = status; }
    // The WPF tray application owns process lifetime; a web host must not install console shutdown handlers.
    private sealed class AgendaHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken token) => Task.CompletedTask;
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Task.Run(StopAsync).GetAwaiter().GetResult();
    }
}
