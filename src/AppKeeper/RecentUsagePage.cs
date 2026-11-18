using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AppKeeper;

internal sealed class RecentUsagePage : UserControl
{
    private readonly RecentUsageService service;
    private readonly RecentUsageStore store;
    private readonly TextBox search = ModuleUi.Input();
    private readonly ComboBox range = new() { MinWidth = 94, Margin = new(0, 0, 6, 4) };
    private readonly ComboBox kind = new() { MinWidth = 70, Margin = new(0, 0, 6, 4) };
    private readonly TextBlock status = ModuleUi.Text("", size: 11);
    private readonly TextBlock summary = ModuleUi.Text("", size: 11);
    private readonly TextBlock searchHint = ModuleUi.Text("搜索名称、拼音或路径", size: 13);
    private readonly TextBlock coverage = ModuleUi.Text("仅统计运行期间可识别的访问，首次历史不计次。", size: 11);
    private readonly TextBlock leftTitle = ModuleUi.Text("七天高频 · TOP 50", true, 14);
    private readonly TextBlock rightTitle = ModuleUi.Text("最近文件与文件夹", true, 14);
    private readonly StackPanel ranking = new();
    private readonly StackPanel recent = new();
    private readonly DockPanel left = new();
    private readonly DockPanel right = new();
    private readonly Grid columns = new();
    private readonly WrapPanel compactTabs = new();
    private readonly Button frequentTab;
    private readonly Button recentTab;
    private readonly TextBlock pageLabel = ModuleUi.Text("", size: 11);
    private readonly Button previous;
    private readonly Button next;
    private readonly Button hideSelected;
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private long revision = -1;
    private int page;
    private bool showFrequent;
    private bool compactLayout;
    private DateTimeOffset rendered;
    internal bool IsCompact => ActualWidth < 560;
    internal int VisibleRecentCount => recent.Children.Count;

    internal RecentUsagePage(RecentUsageService service)
    {
        this.service = service;
        store = service.Store;
        Foreground = Brushes.White;
        var layout = new DockPanel();
        var header = new StackPanel { Margin = new(0, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        var heading = new DockPanel();
        var commands = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(commands, Dock.Right);
        commands.Children.Add(ModuleUi.Button("刷新", () => service.Refresh()));
        commands.Children.Add(ModuleUi.Button("管理", Manage));
        heading.Children.Add(commands);
        heading.Children.Add(ModuleUi.Text("最近访问", true, 19));
        header.Children.Add(heading);
        var filters = new WrapPanel { Margin = new(0, 6, 0, 0) };
        range.ItemsSource = new[] { "最近 7 天", "最近 30 天", "全部记录" };
        range.SelectedIndex = 1;
        kind.ItemsSource = new[] { "全部类型", "文件", "文件夹" };
        kind.SelectedIndex = 0;
        range.MinHeight = kind.MinHeight = 29;
        AutomationProperties.SetName(range, "最近访问时间范围");
        AutomationProperties.SetName(kind, "最近访问类型");
        filters.Children.Add(range);
        filters.Children.Add(kind);
        header.Children.Add(filters);
        search.ToolTip = "搜索名称、拼音或完整路径";
        AutomationProperties.SetName(search, "搜索最近访问");
        search.MaxLength = 256;
        var searchFrame = new Grid();
        searchFrame.Children.Add(search);
        searchHint.Margin = new(7, 0, 0, 0);
        searchHint.Foreground = Brushes.LightSteelBlue;
        searchHint.IsHitTestVisible = false;
        searchFrame.Children.Add(searchHint);
        header.Children.Add(searchFrame);
        summary.Margin = new(0, 5, 0, 0);
        header.Children.Add(summary);
        frequentTab = ModuleUi.Button("七天高频", () => { showFrequent = true; Fit(); });
        recentTab = ModuleUi.Button("最近记录", () => { showFrequent = false; Fit(); });
        compactTabs.Children.Add(frequentTab);
        compactTabs.Children.Add(recentTab);
        compactTabs.Margin = new(0, 6, 0, 0);
        header.Children.Add(compactTabs);
        layout.Children.Add(header);

        var footer = new StackPanel { Margin = new(0, 6, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(status);
        status.TextWrapping = TextWrapping.NoWrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.ToolTip = coverage.Text;
        footer.Children.Add(coverage);
        layout.Children.Add(footer);
        columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        left.Margin = new(0, 0, 9, 0);
        leftTitle.Margin = new(0, 0, 0, 6);
        DockPanel.SetDock(leftTitle, Dock.Top);
        left.Children.Add(leftTitle);
        left.Children.Add(Scroller(ranking));
        columns.Children.Add(left);

        rightTitle.Margin = new(0, 0, 0, 6);
        DockPanel.SetDock(rightTitle, Dock.Top);
        right.Children.Add(rightTitle);
        var pagination = new WrapPanel { Margin = new(0, 5, 0, 0) };
        previous = ModuleUi.Button("‹", () => { page--; Render(); });
        next = ModuleUi.Button("›", () => { page++; Render(); });
        AutomationProperties.SetName(previous, "上一页最近记录");
        AutomationProperties.SetName(next, "下一页最近记录");
        hideSelected = ModuleUi.Button("隐藏选中", HideSelected);
        pagination.Children.Add(previous);
        pagination.Children.Add(pageLabel);
        pagination.Children.Add(next);
        pagination.Children.Add(hideSelected);
        DockPanel.SetDock(pagination, Dock.Bottom);
        right.Children.Add(pagination);
        right.Children.Add(Scroller(recent));
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);
        layout.Children.Add(columns);
        Content = layout;
        range.SelectionChanged += (_, _) => FilterChanged();
        kind.SelectionChanged += (_, _) => FilterChanged();
        search.TextChanged += (_, _) => FilterChanged();
        SizeChanged += (_, _) => Fit();
        timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(true); service.Refresh(); timer.Start(); }
            else timer.Stop();
        };
        Render();
        Fit();
    }

    private static ScrollViewer Scroller(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    private void Fit()
    {
        var compact = IsCompact;
        var changed = compactLayout != compact;
        compactLayout = compact;
        compactTabs.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        columns.ColumnDefinitions[0].Width = new GridLength(compact ? (showFrequent ? 1 : 0) : 1, GridUnitType.Star);
        columns.ColumnDefinitions[1].Width = new GridLength(compact ? (showFrequent ? 0 : 1) : 2, GridUnitType.Star);
        left.Visibility = !compact || showFrequent ? Visibility.Visible : Visibility.Collapsed;
        right.Visibility = !compact || !showFrequent ? Visibility.Visible : Visibility.Collapsed;
        left.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 9, 0);
        frequentTab.Background = showFrequent ? ModuleUi.Accent : Brushes.Transparent;
        recentTab.Background = !showFrequent ? ModuleUi.Accent : Brushes.Transparent;
        summary.Visibility = coverage.Visibility = leftTitle.Visibility = rightTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (changed) Render();
    }

    private void FilterChanged()
    {
        searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        page = 0; selected.Clear(); Render();
    }

    private void Refresh(bool force = false)
    {
        status.Text = service.Status;
        if (force || revision != store.Revision || DateTimeOffset.UtcNow - rendered > TimeSpan.FromMinutes(1)) Render();
    }

    private void Render()
    {
        var version = store.Revision;
        var data = store.Snapshot();
        var now = DateTimeOffset.UtcNow;
        var days = range.SelectedIndex == 0 ? 7 : range.SelectedIndex == 1 ? 30 : 0;
        var query = search.Text.Trim();
        var all = RecentUsageStore.Filter(data, now, 0, Math.Max(0, kind.SelectedIndex), query).ToArray();
        var items = all.Where(item => days == 0 || item.Last > now.AddDays(-days)).OrderByDescending(item => item.Last).ToArray();
        var top = RecentUsageStore.Rank(all, now).ToArray();
        page = Math.Clamp(page, 0, Math.Max(0, (items.Length - 1) / 100));
        ranking.Children.Clear();
        recent.Children.Clear();
        for (var i = 0; i < top.Length; i++) ranking.Children.Add(Row(top[i], i + 1, now));
        foreach (var item in items.Skip(page * 100).Take(100)) recent.Children.Add(Row(item, null, now));
        if (top.Length == 0) ranking.Children.Add(ModuleUi.Text("暂无排名\n识别到新访问后开始累计。", size: 12));
        if (items.Length == 0) recent.Children.Add(ModuleUi.Text("暂无符合条件的记录\n可调整筛选，或在管理中检查系统设置、隐藏和排除项。", size: 12));
        summary.Text = $"{items.Length} 项 · 从 {data.Started.ToLocalTime():MM-dd HH:mm} 起统计";
        compactTabs.ToolTip = summary.Text;
        pageLabel.Text = $" {page + 1}/{Math.Max(1, (items.Length + 99) / 100)} ";
        previous.IsEnabled = page > 0;
        next.IsEnabled = (page + 1) * 100 < items.Length;
        hideSelected.IsEnabled = selected.Count > 0;
        revision = version;
        rendered = now;
        status.Text = service.Status;
    }

    private UIElement Row(RecentUsageItem item, int? rank, DateTimeOffset now)
    {
        var row = new DockPanel { Margin = new(0, 0, 0, 5) };
        if (!rank.HasValue)
        {
            var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 5, 0),
                IsChecked = selected.Contains(item.Path) };
            AutomationProperties.SetName(check, "选择 " + item.Name);
            check.Checked += (_, _) => { selected.Add(item.Path); hideSelected.IsEnabled = selected.Count > 0; };
            check.Unchecked += (_, _) => { selected.Remove(item.Path); hideSelected.IsEnabled = selected.Count > 0; };
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
        }
        var details = new StackPanel();
        var name = new DockPanel();
        var glyph = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(item.Folder ? "M1,4 L8,4 L10,6 L19,6 L19,18 L1,18 Z" :
                "M4,1 L12,1 L17,6 L17,19 L4,19 Z M12,1 L12,6 L17,6 M7,10 L14,10 M7,13 L14,13"),
            Stroke = item.Folder ? Brushes.Khaki : Brushes.LightSkyBlue,
            Fill = item.Folder ? new SolidColorBrush(Color.FromRgb(162, 124, 49)) : new SolidColorBrush(Color.FromRgb(42, 73, 100)),
            StrokeThickness = 1, Stretch = Stretch.Uniform, Width = 17, Height = 17,
            VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0)
        };
        name.Children.Add(glyph);
        name.Children.Add(Line(rank.HasValue ? $"{rank}. {item.Name}" : item.Name, 13));
        details.Children.Add(name);
        if (!IsCompact) details.Children.Add(Line(item.Path, 11, true));
        details.Children.Add(Line(rank.HasValue ? $"近七天 {RecentUsageStore.Count(item, now)} 次" :
            $"{item.Last.ToLocalTime():MM-dd HH:mm} · {item.Source}", 11, true));
        var button = ModuleUi.Button("", () => Open(item));
        button.Content = details;
        button.Margin = new(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.ToolTip = item.Path + "\n单击打开，右键管理";
        AutomationProperties.SetName(button, $"打开{(item.Folder ? "文件夹" : "文件")} {item.Name}");
        button.ContextMenu = Menu(item);
        row.Children.Add(button);
        return row;
    }

    private static TextBlock Line(string text, int size, bool secondary = false) => new()
    {
        Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = secondary ? Brushes.LightSteelBlue : Brushes.White, Margin = new(0, 2, 0, 0)
    };

    private ContextMenu Menu(RecentUsageItem item)
    {
        var menu = new ContextMenu();
        void Add(string label, Action action)
        {
            var command = new MenuItem { Header = label };
            command.Click += (_, _) => Run(action);
            menu.Items.Add(command);
        }
        Add("打开", () => Open(item));
        Add("打开所在文件夹", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = false }));
        Add("复制路径", () => Clipboard.SetText(item.Path));
        menu.Items.Add(new Separator());
        Add("隐藏此记录", () => { store.Hide([item.Path]); selected.Remove(item.Path); Render(); });
        Add(item.Folder ? "排除此文件夹及其内容" : "排除此文件所在文件夹", () => Exclude(item.Folder ? item.Path : Path.GetDirectoryName(item.Path)));
        return menu;
    }

    private void Open(RecentUsageItem item) => Run(() =>
    {
        if (item.Folder ? !Directory.Exists(item.Path) : !File.Exists(item.Path))
            throw new FileNotFoundException("此位置可能已移动、删除，或设备未连接。可右键隐藏该记录。");
        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        service.RecordOpen(item.Path, item.Folder);
        Refresh(true);
    });

    private void HideSelected() => Run(() => { store.Hide(selected); selected.Clear(); Render(); });

    private void Exclude(string? path)
    {
        if (path == null) return;
        if (MessageBox.Show(Window.GetWindow(this), $"不再显示或统计此文件夹及其内容？\n{path}\n仅影响本应用。", "排除文件夹",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        store.Exclude(path);
        selected.Clear();
        Render();
    }

    private void Manage()
    {
        var content = new StackPanel { Margin = new(15) };
        var window = ModuleUi.Dialog(this, "最近访问管理", content);
        var data = store.Snapshot();
        var enabled = new CheckBox { Content = "在 AppKeeper 运行期间记录可识别的访问", IsChecked = data.Enabled,
            Foreground = Brushes.White, Margin = new(0, 0, 0, 9) };
        enabled.Click += (_, _) => Run(() => { store.SetEnabled(enabled.IsChecked == true); service.Refresh(); });
        content.Children.Add(enabled);
        content.Children.Add(ModuleUi.Text(WindowsRecentSource.DisabledReason() ?? "Windows 最近项目设置未禁用。", size: 12));
        content.Children.Add(ModuleUi.Text("设置 → 个性化 → 开始，检查最近项目相关选项。记录受具体应用和系统策略影响；文件夹通过资源管理器路径变化识别，快速导航或关闭程序期间可能漏记。", size: 12));
        content.Children.Add(ModuleUi.Button("打开系统设置", () => Run(() => Process.Start(new ProcessStartInfo("ms-settings:personalization-start") { UseShellExecute = true }))));
        if (store.Notice.Length > 0) content.Children.Add(ModuleUi.Text(store.Notice, size: 12));
        content.Children.Add(ModuleUi.Button($"恢复隐藏记录（{data.Hidden.Count} 项）", () => Run(() =>
        {
            store.RestoreHidden(); service.Refresh(); window.Close(); Render();
        })));
        content.Children.Add(ModuleUi.Button("清空本应用的最近记录与排行…", () => Run(() =>
        {
            if (MessageBox.Show(window, "清空本应用保存的最近记录和次数，之后从新访问重新累计？\nWindows 历史和实际文件会保留。", "清空最近访问",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            store.Clear(DateTimeOffset.UtcNow); selected.Clear(); window.Close(); Render();
        })));
        content.Children.Add(ModuleUi.Text("排除文件夹", true));
        content.Children.Add(ModuleUi.Button("添加排除文件夹…", () => Run(() =>
        {
            var picker = new OpenFolderDialog();
            if (picker.ShowDialog(window) == true) { store.Exclude(picker.FolderName); selected.Clear(); window.Close(); Render(); }
        })));
        var excluded = new StackPanel();
        foreach (var folder in data.Excluded)
        {
            var row = new DockPanel();
            var remove = ModuleUi.Button("移除", () => Run(() => { store.RemoveExclusion(folder); service.Refresh(); window.Close(); Render(); }));
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = folder, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = folder });
            excluded.Children.Add(row);
        }
        content.Children.Add(new ScrollViewer { Content = excluded, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(ModuleUi.Button("关闭", window.Close));
        window.ShowDialog();
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { ModuleUi.Error(this, error); }
    }
}
