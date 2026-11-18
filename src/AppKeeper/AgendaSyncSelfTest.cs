using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppKeeper;

internal static class AgendaSyncSelfTest
{
    internal static async Task Run(string directory)
    {
        var root = Directory.CreateDirectory(Path.Combine(directory, "markdo-phone-server")).FullName;
        var store = new AgendaStore(root);
        using var sync = new AgendaLanSync(store, root);
        await sync.StartAsync(IPAddress.Loopback, 0);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        { BaseAddress = new Uri($"http://127.0.0.1:{sync.Snapshot().Port}"), Timeout = TimeSpan.FromSeconds(10) };
        using var probe = await client.GetAsync("/v1/pairing");
        Check(probe.StatusCode == HttpStatusCode.OK && probe.Headers.GetValues("X-MarkDo-Server").Single() == store.DeviceId, "phone service identity header");
        var session = sync.CreateSession(["192.168.1.2"]);
        var requestId = AgendaSyncProtocol.NewKey();
        var key = AgendaSyncProtocol.PairKey(session.Offer.SessionId, session.Offer.Secret, session.Code);
        var identity = new { deviceId = "phone", deviceName = "测试手机", serverId = store.DeviceId, requestId };
        using (var wrong = new HttpRequestMessage(HttpMethod.Post, "/v1/pair"))
        {
            wrong.Headers.Add("X-MarkDo-Session", session.Offer.SessionId);
            wrong.Content = new StringContent(AgendaSyncProtocol.Encrypt(identity, RandomNumberGenerator.GetBytes(32), AgendaSyncProtocol.PairContext));
            using var answer = await client.SendAsync(wrong);
            Check(answer.StatusCode == HttpStatusCode.Unauthorized && session.Attempts == 1 && sync.Snapshot().Phones.Length == 0, "reject wrong phone confirmation key");
        }
        using var pair = new HttpRequestMessage(HttpMethod.Post, "/v1/pair");
        pair.Headers.Add("X-MarkDo-Session", session.Offer.SessionId);
        pair.Content = new StringContent(AgendaSyncProtocol.Encrypt(identity, key, AgendaSyncProtocol.PairContext));
        using var paired = await client.SendAsync(pair);
        Check(paired.StatusCode == HttpStatusCode.OK, "pair with original MarkDo envelope");
        var pairing = AgendaSyncProtocol.Decrypt(await paired.Content.ReadAsStringAsync(), key, AgendaSyncProtocol.PairContext);
        Check(pairing.GetProperty("requestId").GetString() == requestId && sync.Snapshot().Session == null, "pairing nonce and single-use session");
        var token = pairing.GetProperty("key").GetString()!;
        var shared = AgendaSyncProtocol.DecodeKey(token);
        store.Append("create", "desktop-task", new { title = "电脑任务", category = "工作" });
        var payload = JsonSerializer.SerializeToElement(new { title = "手机任务", category = "生活" });
        var operation = new AgendaOperation("phone-op", "phone", 2, "create", "phone-task", payload, DateTimeOffset.UtcNow.ToString("O"));
        using var push = new HttpRequestMessage(HttpMethod.Post, "/v1/sync");
        push.Headers.Add("X-MarkDo-Device", "phone");
        push.Content = new StringContent(AgendaSyncProtocol.Encrypt(new { version = 4, deviceId = "phone", requestId = AgendaSyncProtocol.NewKey(), operations = new[] { operation } }, shared));
        using var result = await client.SendAsync(push);
        Check(result.StatusCode == HttpStatusCode.OK, "encrypted phone sync");
        var received = AgendaSyncProtocol.Decrypt(await result.Content.ReadAsStringAsync(), shared);
        Check(received.GetProperty("operations").GetArrayLength() == 2 && store.Tasks().Length == 2 && sync.Snapshot().LastSync.HasValue,
            "desktop and phone operations merge bidirectionally");
        Check(!File.ReadAllText(Path.Combine(root, "AppKeeper.agenda-sync.json")).Contains(token), "protect phone credential at rest");
    }

    internal static void Layout(string directory)
    {
        var root = Directory.CreateDirectory(Path.Combine(directory, "phone-panel-layout")).FullName;
        using var sync = new AgendaLanSync(new AgendaStore(root), root);
        Task.Run(() => sync.StartAsync(IPAddress.Loopback, 0)).GetAwaiter().GetResult();
        sync.CreateSession(["192.168.1.2"]);
        var panel = new AgendaSyncPanel(sync);
        var surface = new Border { Padding = new(10), Background = new SolidColorBrush(Color.FromRgb(32, 47, 65)),
            Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        foreach (var width in new[] { 690, 210 })
        {
            var size = new Size(width, 600);
            surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
            Check(surface.ActualWidth == width && panel.ActualWidth <= width, "responsive phone connection panel");
            var target = Environment.GetEnvironmentVariable("APPKEEPER_UI_TEST_DIR");
            if (string.IsNullOrEmpty(target)) continue;
            Directory.CreateDirectory(target);
            var bitmap = new RenderTargetBitmap(width, 600, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(target, $"phone-pairing-{width}.png")); encoder.Save(output);
        }
    }
    private static void Check(bool valid, string message)
    { if (!valid) throw new InvalidOperationException("MarkDo sync test failed: " + message); }
}
