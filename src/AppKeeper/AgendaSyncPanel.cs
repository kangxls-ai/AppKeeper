using QRCoder;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AppKeeper;

internal sealed class AgendaSyncPanel : UserControl
{
    private readonly AgendaLanSync sync;
    private readonly TextBlock status = ModuleUi.Text("", size: 12);
    private readonly TextBlock lastSync = ModuleUi.Text("", size: 12);
    private readonly TextBlock code = ModuleUi.Text("", true, 28);
    private readonly TextBlock expiry = ModuleUi.Text("", size: 12);
    private readonly TextBlock error = ModuleUi.Text("", size: 12);
    private readonly Image qr = new() { Width = 240, Height = 240, Stretch = Stretch.Uniform };
    private readonly Border qrFrame;
    private readonly StackPanel addresses = new();
    private readonly StackPanel phones = new();
    private readonly CheckBox enabled;
    private readonly TextBox port;
    private readonly Button create;
    private readonly Button apply;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private AgendaPairSession? shown;
    private string phoneStamp = "";
    private bool working;

    internal AgendaSyncPanel(AgendaLanSync sync)
    {
        this.sync = sync;
        Foreground = Brushes.White;
        var content = new StackPanel();
        content.Children.Add(ModuleUi.Text("连接 MarkDo 手机端", true, 18));
        content.Children.Add(ModuleUi.Text("手机与电脑连接同一局域网。在原 MarkDo 手机端打开“连接设备”，扫码后输入六位确认码；也可手动输入下方 IP、端口和确认码。", size: 12));
        status.Margin = new(0, 8, 0, 0);
        content.Children.Add(status);
        content.Children.Add(lastSync);
        create = ModuleUi.Button("显示配对码／连接另一部手机", () => Run(() =>
        {
            sync.CreateSession();
            Refresh();
        }), true);
        create.Margin = new(0, 10, 0, 5);
        create.HorizontalAlignment = HorizontalAlignment.Left;
        content.Children.Add(create);
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        AutomationProperties.SetName(qr, "MarkDo 手机配对二维码");
        AutomationProperties.SetName(code, "六位配对确认码");
        qrFrame = new Border { Child = qr, Background = Brushes.White, Padding = new(12),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 5, 0, 5), Visibility = Visibility.Collapsed };
        content.Children.Add(new Viewbox { Child = qrFrame, MaxWidth = 264, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
        code.Margin = new(0, 4, 0, 0);
        content.Children.Add(code);
        content.Children.Add(expiry);
        error.Foreground = Brushes.LightSalmon;
        content.Children.Add(error);
        content.Children.Add(ModuleUi.Text("电脑 IPv4 地址", true, 14));
        content.Children.Add(ModuleUi.Text("优先尝试 Wi-Fi 或有线网卡地址；多网卡可能需要换一个地址测试。", size: 12));
        content.Children.Add(addresses);
        content.Children.Add(ModuleUi.Button("刷新网卡地址", async () => await RefreshAddresses()));
        content.Children.Add(ModuleUi.Text("手机每约 8 秒主动同步，也可在手机点击“立即同步”。电脑隐藏到托盘后继续服务；双方离线修改会在再次连接时合并。", size: 12));

        content.Children.Add(ModuleUi.Text("已配对手机", true, 14));
        content.Children.Add(phones);
        content.Children.Add(ModuleUi.Text("连接设置", true, 14));
        var state = sync.Snapshot();
        enabled = new CheckBox { Content = "启用手机同步服务", IsChecked = state.Enabled, Foreground = Brushes.White,
            Margin = new(0, 6, 0, 6) };
        content.Children.Add(enabled);
        content.Children.Add(ModuleUi.Text("电脑服务端口（默认 8787）", size: 12));
        port = ModuleUi.Input(state.Port.ToString());
        AutomationProperties.SetName(port, "手机同步服务端口");
        port.MaxLength = 5;
        port.MaxWidth = 160;
        port.HorizontalAlignment = HorizontalAlignment.Left;
        content.Children.Add(port);
        apply = ModuleUi.Button("应用设置", async () => await Apply());
        apply.HorizontalAlignment = HorizontalAlignment.Left;
        content.Children.Add(apply);
        content.Children.Add(ModuleUi.Text("修改端口后请在手机重新配对。首次使用时，如 Windows 提示网络访问，请按需允许专用网络。原版 Windows MarkDo 若占用 8787，可退出它或选择其他端口。", size: 12));
        Content = content;
        timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) { Refresh(); timer.Start(); await RefreshAddresses(); }
            else timer.Stop();
        };
        Refresh();
    }

    private void Refresh()
    {
        var state = sync.Snapshot();
        status.Text = (state.Serving ? $"正在监听端口 {state.Port} · " : "") + state.Status;
        lastSync.Text = state.LastSync.HasValue ? $"上次成功同步：{state.LastSync.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "尚未成功同步";
        create.IsEnabled = state.Serving && !working;
        var current = state.Session;
        if (current?.Available == true)
        {
            if (!ReferenceEquals(shown, current))
            {
                using var generator = new QRCodeGenerator();
                using var matrix = generator.CreateQrCode(JsonSerializer.Serialize(current.Offer, AgendaSyncProtocol.Json), QRCodeGenerator.ECCLevel.L);
                using var png = new PngByteQRCode(matrix);
                using var stream = new MemoryStream(png.GetGraphic(6));
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                qr.Source = bitmap;
                shown = current;
            }
            qrFrame.Visibility = Visibility.Visible;
            code.Text = string.Join(" ", current.Code.ToCharArray());
            expiry.Text = $"{Math.Max(0, (int)(DateTimeOffset.Parse(current.Offer.Expires) - DateTimeOffset.UtcNow).TotalSeconds)} 秒内有效 · 配对一次后失效 · 剩余 {8 - current.Attempts} 次尝试";
        }
        else
        {
            qrFrame.Visibility = Visibility.Collapsed;
            qr.Source = null;
            code.Text = "";
            expiry.Text = shown == null ? "点击显示配对码后再操作手机。" : "配对码已使用、过期或达到尝试上限，请刷新。";
        }
        var stamp = string.Join("|", state.Phones.Select(phone => $"{phone.Id}:{phone.Name}:{phone.LastSeen:O}"));
        if (stamp != phoneStamp || phones.Children.Count == 0)
        {
            phoneStamp = stamp;
            phones.Children.Clear();
            foreach (var phone in state.Phones.OrderBy(item => item.Name))
            {
                var row = new DockPanel { Margin = new(0, 4, 0, 4) };
                var revoke = ModuleUi.Button("断开配对", () => Run(() =>
                {
                    if (MessageBox.Show(Window.GetWindow(this), $"断开与“{phone.Name}”的配对？手机和电脑的日程都会保留，之后需要重新配对。",
                        "断开配对", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                    { sync.Revoke(phone.Id); Refresh(); }
                }));
                DockPanel.SetDock(revoke, Dock.Right);
                row.Children.Add(revoke);
                row.Children.Add(ModuleUi.Text($"{phone.Name}\n上次连接 {phone.LastSeen.ToLocalTime():MM-dd HH:mm}", size: 12));
                phones.Children.Add(row);
            }
            if (state.Phones.Length == 0) phones.Children.Add(ModuleUi.Text("暂无已配对手机", size: 12));
        }
    }

    private async Task RefreshAddresses()
    {
        try
        {
            var items = await Task.Run(AgendaLanSync.Addresses);
            addresses.Children.Clear();
            foreach (var item in items) addresses.Children.Add(ModuleUi.Text($"{item.Address} · {item.Name}", size: 12));
            if (items.Length == 0) addresses.Children.Add(ModuleUi.Text("未找到局域网 IPv4，请检查 Wi-Fi 或有线连接。", size: 12));
        }
        catch (Exception failure) { error.Text = "无法读取网卡地址：" + failure.Message; }
    }

    private async Task Apply()
    {
        if (working) return;
        working = true; apply.IsEnabled = false; create.IsEnabled = false; error.Text = "";
        try
        {
            if (!int.TryParse(port.Text, out var number)) throw new ArgumentException("请输入有效端口。");
            await sync.ConfigureAsync(enabled.IsChecked == true, number);
        }
        catch (Exception failure) { error.Text = failure.Message; }
        finally { working = false; apply.IsEnabled = true; Refresh(); }
    }
    private void Run(Action action)
    {
        try { error.Text = ""; action(); }
        catch (Exception failure) { error.Text = failure.Message; }
    }
}
