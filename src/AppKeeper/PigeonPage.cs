using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AppKeeper;

internal sealed class PigeonPage : UserControl
{
    private readonly PigeonService service;
    private readonly StackPanel list = new();
    private readonly StackPanel messages = new();
    private readonly DockPanel conversation = new();
    private readonly ScrollViewer history;
    private readonly ScrollViewer people;
    private readonly TextBlock title = ModuleUi.Text("选择电脑", true, 18);
    private readonly TextBlock status = ModuleUi.Text("");
    private readonly TextBox input = ModuleUi.Input();
    private readonly TextBlock length = ModuleUi.Text("", size: 11);
    private readonly Button send;
    private readonly Button file;
    private readonly Button folder;
    private readonly Grid body = new();
    private readonly Button back;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private string? selected;
    private bool narrow;
    private bool choosing = true;
    private string listStamp = "";
    private PigeonItem[] previous = [];

    internal PigeonPage(PigeonService service)
    {
        this.service = service;
        var layout = new DockPanel();
        var toolbar = new WrapPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        back = ModuleUi.Button("‹ 电脑列表", () => { choosing = true; Fit(); });
        toolbar.Children.Add(back);
        toolbar.Children.Add(ModuleUi.Button("＋ 手动添加", () => Pair()));
        toolbar.Children.Add(ModuleUi.Button("传书设置", Settings));
        layout.Children.Add(toolbar);
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition());
        people = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new(0, 3, 8, 0) };
        body.Children.Add(people);
        Grid.SetColumn(conversation, 1);
        body.Children.Add(conversation);
        layout.Children.Add(body);
        Content = layout;

        var heading = new StackPanel { Margin = new(0, 2, 0, 5) };
        heading.Children.Add(title);
        heading.Children.Add(status);
        DockPanel.SetDock(heading, Dock.Top);
        conversation.Children.Add(heading);
        var composer = new StackPanel { Margin = new(0, 5, 0, 0) };
        DockPanel.SetDock(composer, Dock.Bottom);
        input.AcceptsReturn = true;
        input.TextWrapping = TextWrapping.Wrap;
        input.MinLines = 2;
        input.MaxLines = 3;
        input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        input.ToolTip = "Enter 发送，Shift+Enter 换行";
        AutomationProperties.SetName(input, "输入传书消息");
        composer.Children.Add(input);
        var actions = new WrapPanel { Margin = new(0, 5, 0, 0) };
        send = ModuleUi.Button("发送", Send, true);
        file = ModuleUi.Button("发文件", SelectFiles);
        folder = ModuleUi.Button("发文件夹", SelectFolder);
        actions.Children.Add(send);
        actions.Children.Add(file);
        actions.Children.Add(folder);
        length.Margin = new Thickness(3, 0, 0, 4);
        length.ToolTip = "Enter 发送，Shift+Enter 换行";
        actions.Children.Add(length);
        composer.Children.Add(actions);
        input.TextChanged += (_, _) => UpdateComposer(input.IsEnabled);
        UpdateComposer(false);
        conversation.Children.Add(composer);
        history = new ScrollViewer { Content = messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        conversation.Children.Add(history);
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                Send();
            }
        };
        conversation.AllowDrop = true;
        conversation.DragOver += (_, e) =>
        {
            e.Effects = IsPaired() && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        conversation.Drop += async (_, e) =>
        {
            e.Handled = true;
            if (IsPaired() && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                await SendPaths(paths);
        };
        timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(); timer.Start(); }
            else timer.Stop();
        };
        SizeChanged += (_, _) => Fit();
        Fit();
    }

    private void Fit()
    {
        narrow = ActualWidth < 540;
        back.Visibility = narrow && !choosing ? Visibility.Visible : Visibility.Collapsed;
        body.ColumnDefinitions[0].Width = narrow
            ? new GridLength(choosing ? 1 : 0, GridUnitType.Star) : new GridLength(175);
        body.ColumnDefinitions[1].Width = new GridLength(narrow && choosing ? 0 : 1, GridUnitType.Star);
        people.Visibility = !narrow || choosing ? Visibility.Visible : Visibility.Collapsed;
        conversation.Visibility = !narrow || !choosing ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsPaired() => service.Peers().Any(peer => peer.Key == selected && peer.State == "paired");

    private void Select(PigeonPeer peer)
    {
        if (peer.State == "incoming") { Pair(incoming: peer); return; }
        selected = peer.Key;
        choosing = false;
        previous = [];
        messages.Children.Clear();
        Fit();
        Refresh();
    }

    private void Refresh()
    {
        var peers = service.Peers();
        var nearby = service.Discovered().Where(peer => !peers.Any(known => known.Key == peer.Computer.Key))
            .OrderBy(peer => peer.Computer.Name).ToArray();
        var stamp = selected + string.Join("|", peers.Select(peer => $"{peer.Key}{peer.State}{service.Online(peer.Key)}")
            .Concat(nearby.Select(peer => $"{peer.Computer.Key}{peer.Address}{peer.Computer.Port}")));
        if (stamp != listStamp)
        {
            listStamp = stamp;
            list.Children.Clear();
            list.Children.Add(ModuleUi.Text("电脑", true));
            foreach (var peer in peers.OrderBy(peer => peer.Name))
                list.Children.Add(ModuleUi.Button(peer.Name + "\n" + PeerState(peer), () => Select(peer), peer.Key == selected));
            foreach (var peer in nearby)
                list.Children.Add(ModuleUi.Button(peer.Computer.Name + "\n局域网发现 · 未配对", () => Pair(peer)));
            if (peers.Length + nearby.Length == 0)
                list.Children.Add(ModuleUi.Text("暂无电脑。可手动添加。"));
        }

        var current = peers.FirstOrDefault(peer => peer.Key == selected);
        var paired = current?.State == "paired";
        var online = paired && service.Online(current!.Key);
        title.Text = current?.Name ?? "选择电脑";
        status.Text = current == null ? service.Warning : PeerState(current);
        input.IsEnabled = file.IsEnabled = folder.IsEnabled = paired;
        UpdateComposer(paired);
        send.Content = online ? "发送" : "排队发送";
        var items = paired ? service.History(current!.Key) : [];
        if (items.SequenceEqual(previous)) return;
        var follow = history.VerticalOffset >= history.ScrollableHeight - 20;
        var oldScroll = history.VerticalOffset;
        messages.Children.Clear();
        foreach (var item in items)
            messages.Children.Add(Message(item));
        previous = items;
        Dispatcher.BeginInvoke(() => { if (follow) history.ScrollToEnd(); else history.ScrollToVerticalOffset(oldScroll); });
    }

    private string PeerState(PigeonPeer peer) => peer.State switch
    {
        "paired" => service.Online(peer.Key) ? "在线 · 直连" : "离线 · 可排队发送",
        "incoming" => "收到配对请求",
        "outgoing" => "等待确认 · 校验码 " + service.Code(peer),
        _ => "未配对"
    };

    private Border Message(PigeonItem item)
    {
        var details = new StackPanel();
        var caption = ModuleUi.Text(item.Value, item.Kind == "file");
        details.Children.Add(caption);
        var state = item.State switch
        {
            "queued" => item.Kind == "text" ? "等待送达" : "等待接收或重连",
            "pending" => "等待你接收",
            "receiving" => "等待重连或正在接收",
            "saved" => "已接收",
            "sent" => "已送达",
            "cancelled" or "cancelling" => "已取消",
            "failed" => "源文件已变动或不可用",
            "rejected" => "已拒绝",
            _ => ""
        };
        details.Children.Add(ModuleUi.Text(item.At.ToLocalTime().ToString("MM-dd HH:mm") + " · " +
            (item.Kind == "file" ? $"{FormatSize(item.Size)} · " : "") + state, size: 11));
        if (item.Progress > 0 && item.Size > 0)
        {
            details.Children.Add(new ProgressBar { Maximum = item.Size, Value = item.Progress, Height = 4, Margin = new(0, 4, 0, 2) });
            details.Children.Add(ModuleUi.Text($"已传输 {FormatSize(item.Progress)}", size: 11));
        }
        var controls = new WrapPanel();
        if (item.State == "pending")
        {
            controls.Children.Add(ModuleUi.Button("接收", () => Action(() => service.Answer(item.Id, true)), true));
            controls.Children.Add(ModuleUi.Button("拒绝", () => Action(() => service.Answer(item.Id, false))));
        }
        if (item.Kind == "file" && item.State is "queued" or "receiving")
            controls.Children.Add(ModuleUi.Button("取消", () => Action(() => service.Cancel(item.Id))));
        if (item.State == "failed")
            controls.Children.Add(ModuleUi.Button("重试", () => Action(() => service.Retry(item.Id))));
        if (item.LocalFile is { } path)
        {
            void Open() => Action(() => OpenFile(path));
            caption.Cursor = Cursors.Hand;
            caption.ToolTip = "双击打开文件";
            caption.Focusable = true;
            AutomationProperties.SetName(caption, $"{item.Value}，双击或按 Enter 打开文件");
            caption.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    e.Handled = true;
                    Open();
                }
            };
            caption.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    Open();
                }
            };
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "打开文件" };
            open.Click += (_, _) => Open();
            menu.Items.Add(open);
            caption.ContextMenu = menu;
            controls.Children.Add(ModuleUi.Button("打开所在文件夹", () => Action(() => RevealFile(path))));
            if (item.OriginalFolder is { } original && Path.IsPathFullyQualified(original))
                controls.Children.Add(ModuleUi.Button("打开原文件夹", () => Action(() => OpenDirectory(original))));
        }
        details.Children.Add(controls);
        return new Border { Child = details, Background = item.Outgoing ? ModuleUi.Accent : System.Windows.Media.Brushes.DarkSlateGray,
            CornerRadius = new(10), Padding = new(8), Margin = new(0, 2, 6, 4),
            HorizontalAlignment = item.Outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left };
    }

    private static void OpenFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到此文件，可能已移动或删除。");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void RevealFile(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        else
            OpenDirectory(Path.GetDirectoryName(path));
    }

    private static void OpenDirectory(string? path)
    {
        if (path == null || !Directory.Exists(path))
            throw new DirectoryNotFoundException("找不到此文件夹，可能已移动或删除。");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string FormatSize(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1e9:F2} GB" :
        bytes >= 1_000_000 ? $"{bytes / 1e6:F1} MB" : $"{bytes / 1e3:F1} KB";

    private void Action(Action action)
    {
        try { action(); Refresh(); }
        catch (Exception error) { ModuleUi.Error(this, error); }
    }

    private void UpdateComposer(bool paired)
    {
        var count = PigeonService.MessageLength(input.Text);
        var overLimit = count > PigeonService.MessageLimit;
        length.Text = $"{count}/{PigeonService.MessageLimit}";
        length.Foreground = overLimit ? Brushes.LightSalmon : Brushes.LightSteelBlue;
        send.IsEnabled = paired && count > 0 && !overLimit;
    }

    private void Send()
    {
        if (!IsPaired() || string.IsNullOrWhiteSpace(input.Text)) return;
        Action(() => { service.SendText(selected!, input.Text); input.Clear(); });
    }

    private async void SelectFiles()
    {
        var picker = new OpenFileDialog { Multiselect = true };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await SendPaths(picker.FileNames);
    }

    private async void SelectFolder()
    {
        var picker = new OpenFolderDialog { Multiselect = true };
        if (picker.ShowDialog(Window.GetWindow(this)) == true) await SendPaths(picker.FolderNames);
    }

    private async System.Threading.Tasks.Task SendPaths(string[] paths)
    {
        var destination = selected!;
        try
        {
            foreach (var path in paths)
                await service.SendPath(destination, path);
            Refresh();
        }
        catch (Exception error) { ModuleUi.Error(this, error); }
    }

    private void Pair(DiscoveredPeer? discovered = null, PigeonPeer? incoming = null)
    {
        var content = new StackPanel { Margin = new(15) };
        var address = ModuleUi.Input(discovered?.Address ?? "");
        var port = ModuleUi.Input((discovered?.Computer.Port ?? 48620).ToString());
        var automatic = new CheckBox { Content = "允许自动接收这台电脑的文件", Margin = new(0, 10, 0, 8) };
        automatic.Foreground = System.Windows.Media.Brushes.White;
        var window = ModuleUi.Dialog(this, incoming == null ? "添加电脑" : "确认配对", content);
        if (incoming != null)
        {
            content.Children.Add(ModuleUi.Text(incoming.Name, true));
            content.Children.Add(ModuleUi.Text("请与对方核对校验码：" + service.Code(incoming)));
        }
        else
        {
            content.Children.Add(ModuleUi.Text("对方 IPv4 地址"));
            content.Children.Add(address);
            content.Children.Add(ModuleUi.Text("通信端口"));
            content.Children.Add(port);
        }
        content.Children.Add(automatic);
        var buttons = new WrapPanel();
        var confirm = ModuleUi.Button(incoming == null ? "发送配对请求" : "确认配对", () => { }, true);
        confirm.Click += async (_, _) =>
        {
            confirm.IsEnabled = false;
            try
            {
                if (incoming != null)
                    service.Allow(incoming.Key, automatic.IsChecked == true);
                else
                {
                    if (!int.TryParse(port.Text, out var number)) throw new ArgumentException("请输入有效端口。");
                    var peer = await service.Pair(address.Text, number, discovered?.Computer.Key, automatic.IsChecked == true);
                    MessageBox.Show(window, "配对校验码：" + service.Code(peer) + "\n请与对方核对后由对方确认。", "配对请求");
                }
                window.Close();
                Refresh();
            }
            catch (Exception error) { ModuleUi.Error(content, error); confirm.IsEnabled = true; }
        };
        buttons.Children.Add(confirm);
        buttons.Children.Add(ModuleUi.Button(incoming == null ? "关闭" : "拒绝", () =>
        {
            if (incoming != null) Action(() => service.Reject(incoming.Key));
            window.Close();
        }));
        content.Children.Add(buttons);
        window.ShowDialog();
    }

    private void Settings()
    {
        var content = new StackPanel { Margin = new(15) };
        var state = service.Settings;
        var port = ModuleUi.Input(state.Port.ToString());
        var directory = ModuleUi.Input(state.Share);
        var limit = ModuleUi.Input((state.Limit / 1_000_000_000m).ToString(CultureInfo.CurrentCulture));
        var window = ModuleUi.Dialog(this, "飞鸽传书设置", content);
        content.Children.Add(ModuleUi.Text("通信端口"));
        content.Children.Add(port);
        content.Children.Add(ModuleUi.Text("接收目录"));
        content.Children.Add(directory);
        content.Children.Add(ModuleUi.Button("选择目录…", () =>
        {
            var picker = new OpenFolderDialog();
            if (picker.ShowDialog(window) == true) directory.Text = picker.FolderName;
        }));
        content.Children.Add(ModuleUi.Text("自动接收上限（GB，按文件或压缩后的 ZIP 计算）"));
        content.Children.Add(limit);
        var chosen = service.Peers().FirstOrDefault(peer => peer.Key == selected && peer.State == "paired");
        var auto = new CheckBox { Content = chosen == null ? "" : $"允许自动接收 {chosen.Name} 的文件",
            IsChecked = chosen?.Auto, Visibility = chosen == null ? Visibility.Collapsed : Visibility.Visible,
            Foreground = System.Windows.Media.Brushes.White };
        var peerIp = ModuleUi.Input(chosen?.Address ?? "");
        var peerPort = ModuleUi.Input(chosen?.Port.ToString() ?? "");
        if (chosen != null)
        {
            content.Children.Add(ModuleUi.Text(chosen.Name + " · 手动连接地址"));
            content.Children.Add(peerIp);
            content.Children.Add(ModuleUi.Text("对方通信端口"));
            content.Children.Add(peerPort);
        }
        content.Children.Add(auto);
        content.Children.Add(ModuleUi.Button("保存", () =>
        {
            try
            {
                if (!int.TryParse(port.Text, out var number) || !decimal.TryParse(limit.Text, out var gb) ||
                    gb is < 0 or > 1000000 || string.IsNullOrWhiteSpace(directory.Text))
                    throw new ArgumentException("请填写有效的端口、目录和大小上限。");
                var endpointPort = 0;
                if (chosen != null && !int.TryParse(peerPort.Text, out endpointPort))
                    throw new ArgumentException("请输入有效的对方 IPv4 地址和端口。");
                service.Configure(number, directory.Text.Trim(), (long)(gb * 1_000_000_000m),
                    chosen == null ? null : chosen with { Address = peerIp.Text, Port = endpointPort, Auto = auto.IsChecked == true });
                window.Close();
                Refresh();
            }
            catch (Exception error) { ModuleUi.Error(content, error); }
        }, true));
        if (chosen != null)
            content.Children.Add(ModuleUi.Button("清空该电脑的已结束会话记录", () =>
            {
                if (MessageBox.Show(window, "清空已结束的聊天和传输记录？接收的文件会保留。", "清空记录",
                    MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                    Action(() => service.ClearHistory(chosen.Key));
            }));
        window.ShowDialog();
    }
}
