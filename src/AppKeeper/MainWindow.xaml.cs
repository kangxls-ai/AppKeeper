using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AppKeeper;

public partial class MainWindow : Window
{
    private const string DragFormat = "AppKeeper.EntryId";
    private readonly App app;
    private readonly ShellIcons icons = new();
    private readonly DispatcherTimer edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private readonly SolidColorBrush activeBrush = new(Color.FromArgb(95, 123, 190, 239));
    private IntPtr handle;
    private int page;
    private int pages = 1;
    private int edgeDirection;
    private bool inbox;
    private bool dragging;
    private bool ready;
    private bool draggingOut;
    private bool closingForExit;
    private Guid? openFolder;
    private string? selectedTag;
    private string Query => SearchBox.Text.Trim();

    internal MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        ready = true;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle).AddHook(WindowMessage);
        };
        edgeTimer.Tick += (_, _) => ChangePage(edgeDirection);
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007E && IsVisible)
            _ = Dispatcher.BeginInvoke(CenterOnCursorMonitor);
        return IntPtr.Zero;
    }

    internal void Reveal()
    {
        if (IsVisible)
        {
            Activate();
            return;
        }

        _ = new WindowInteropHelper(this).EnsureHandle();
        CenterOnCursorMonitor();
        Show();
        Activate();
        RefreshData();
        SearchBox.Focus();
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(.8, 1, TimeSpan.FromMilliseconds(135)));
    }

    internal void RevealHome()
    {
        if (!IsVisible)
        {
            selectedTag = null;
            SwitchSection(false);
        }
        Reveal();
    }

    private void CenterOnCursorMonitor()
    {
        var cursor = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(cursor).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this);
        var monitor = MonitorFromPoint(cursor, 2);
        if (GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0)
            scale = new DpiScale(dpiX / 96d, dpiY / 96d);
        var width = Math.Min(area.Width, (int)Math.Round(Width * scale.DpiScaleX));
        var height = Math.Min(area.Height, (int)Math.Round(Height * scale.DpiScaleY));
        SetWindowPos(handle, IntPtr.Zero, area.Left + (area.Width - width) / 2,
            area.Top + (area.Height - height) / 2, width, height, 0x0014);
    }

    internal void CloseForExit()
    {
        closingForExit = true;
        Close();
    }

    private void HideOnClose(object? sender, CancelEventArgs e)
    {
        if (closingForExit)
            return;
        e.Cancel = true;
        Conceal();
    }

    internal void Conceal()
    {
        CloseFolder();
        Hide();
        SearchBox.Clear();
    }

    internal void RefreshStatus()
    {
        if (!ready)
            return;
        StatusText.Text = app.Status.Length > 0 ? app.Status : "拖到图标边缘排序，拖到中央分组";
    }

    internal void RefreshData()
    {
        if (!ready)
            return;
        RefreshStatus();
        InboxButton.Content = $"待整理 · {app.Data.Entries.Count(entry => entry.Inbox)}";
        HomeButton.Background = Query.Length == 0 && !inbox ? activeBrush : Brushes.Transparent;
        InboxButton.Background = Query.Length == 0 && inbox ? activeBrush : Brushes.Transparent;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectedTag != null && !app.Data.Tags.Contains(selectedTag, StringComparer.OrdinalIgnoreCase))
            selectedTag = null;
        if (IsVisible && !dragging)
        {
            RenderTags();
            RenderTiles();
            RenderFolder();
        }
    }

    private void RenderTiles()
    {
        if (TileArea.ActualHeight < 20 || TileArea.ActualWidth < 20)
            return;
        var width = TileArea.ActualWidth;
        var height = TileArea.ActualHeight;
        var columns = Math.Max(1, Math.Min(12, (int)(width / 112)));
        var rows = Math.Max(1, Math.Min(7, (int)(height / 104)));
        var size = columns * rows;
        var matches = Layout.Visible(app.Data, inbox, Query, selectedTag).ToList();
        pages = Math.Max(1, (matches.Count + size - 1) / size);
        page = Math.Clamp(page, 0, pages - 1);
        Tiles.Columns = columns;
        Tiles.Rows = rows;
        Tiles.Height = Math.Min(height, rows * 125);
        Tiles.Children.Clear();
        foreach (var entry in matches.Skip(page * size).Take(size))
            Tiles.Children.Add(Tile(entry, false));
        EmptyState.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = Query.Length > 0 || selectedTag != null ? "没有匹配的应用" : inbox ? "待整理区为空" : "还没有已整理的应用";
        EmptyDetail.Text = selectedTag != null ? "可在应用的右键菜单中设置标签" :
            Query.Length > 0 ? "试试应用名称、全拼或首字母" :
            inbox ? "新发现的应用会出现在这里" : "从「待整理」移动应用，或使用「添加」创建入口";
        PageLabel.Text = $"{page + 1} / {pages}";
        PreviousButton.IsEnabled = page > 0;
        NextButton.IsEnabled = page + 1 < pages;
    }

    private Button Tile(Entry entry, bool insideFolder)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var tile = new Button
        {
            Style = (Style)FindResource("TileButton"),
            Content = stack,
            ToolTip = entry.Missing ? $"{entry.Name} · 不可用\n{entry.Target}" : entry.Name,
            Tag = entry
        };
        AutomationProperties.SetName(tile, entry.Missing ? $"{entry.Name}，不可用" : entry.Name);
        stack.Children.Add(CreateTileIcon(entry));
        stack.Children.Add(new TextBlock
        {
            Text = entry.Name, FontSize = 13, MaxWidth = 103, Margin = new Thickness(0, 7, 0, 0),
            TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (entry.Missing)
            stack.Opacity = .55;
        tile.Click += (_, _) => ActivateEntry(entry);
        tile.ContextMenu = MenuFor(entry);

        if (Query.Length == 0 && selectedTag == null)
        {
            Point? origin = null;
            tile.PreviewMouseLeftButtonDown += (_, e) => origin = e.GetPosition(tile);
            tile.PreviewMouseLeftButtonUp += (_, _) => origin = null;
            tile.PreviewMouseMove += (_, e) =>
            {
                if (origin is not { } start || e.LeftButton != MouseButtonState.Pressed || dragging)
                    return;
                var now = e.GetPosition(tile);
                if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;
                origin = null;
                tile.ReleaseMouseCapture();
                dragging = true;
                try { DragDrop.DoDragDrop(tile, new DataObject(DragFormat, entry.Id.ToString()), DragDropEffects.Move); }
                finally
                {
                    dragging = false;
                    edgeTimer.Stop();
                    if (draggingOut)
                        CloseFolder();
                    draggingOut = false;
                    RefreshData();
                }
                e.Handled = true;
            };
            tile.AllowDrop = true;
            tile.DragOver += (_, e) =>
            {
                if (Dragged(e) is not { } source)
                    return;
                var action = DropPosition(tile, e, source, entry, insideFolder);
                var canMove = Layout.CanMove(source, entry, action);
                tile.Background = canMove ? activeBrush : Brushes.Transparent;
                e.Effects = canMove ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
                edgeTimer.Stop();
            };
            tile.DragLeave += (_, _) => tile.Background = Brushes.Transparent;
            tile.Drop += (_, e) =>
            {
                if (Dragged(e) is not { } source)
                    return;
                var action = DropPosition(tile, e, source, entry, insideFolder);
                if (Layout.CanMove(source, entry, action))
                    app.Commit(library => Layout.Move(library, source, entry, action));
                e.Handled = true;
            };
        }
        return tile;
    }

    private FrameworkElement CreateTileIcon(Entry entry)
    {
        var frame = new Border
        {
            Width = 62, Height = 62, CornerRadius = new CornerRadius(15),
            HorizontalAlignment = HorizontalAlignment.Center,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(54, 255, 255, 255)),
            Background = new LinearGradientBrush(Color.FromRgb(68, 95, 126), Color.FromRgb(43, 58, 84), 55)
        };
        if (icons.Get(entry) is { } image)
        {
            frame.Child = new Image { Source = image, Width = 44, Height = 44 };
        }
        else if (entry.Kind == EntryKind.Group)
        {
            var grid = new UniformGrid { Columns = 3, Rows = 3, Margin = new Thickness(9) };
            foreach (var child in app.Data.Entries.Where(child => child.Group == entry.Id).Take(9))
            {
                var source = icons.Get(child);
                grid.Children.Add(source == null
                    ? new Border { Background = activeBrush, CornerRadius = new CornerRadius(3), Margin = new Thickness(2) }
                    : new Image { Source = source, Margin = new Thickness(1) });
            }
            frame.Child = grid;
        }
        else
        {
            var glyph = entry.Kind switch
            {
                EntryKind.Directory => "\uE8B7",
                EntryKind.Web => "\uE774",
                EntryKind.Store => "\uE719",
                _ => "\uE7C3"
            };
            frame.Child = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 27,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }
        return frame;
    }

    private ContextMenu MenuFor(Entry entry)
    {
        var menu = new ContextMenu();
        AddItem(menu, entry.Kind == EntryKind.Group ? "打开文件夹" : "打开", () => ActivateEntry(entry));
        AddItem(menu, "重命名", () =>
        {
            if (Dialogs.Text(this, "重命名", "显示名称", entry.Name) is { } name)
                app.Commit(_ => entry.Name = name);
        });
        AddItem(menu, "更换图标…", () => ChangeIcon(entry));
        if (entry.Icon != null)
            AddItem(menu, "恢复默认图标", () => app.Commit(_ => entry.Icon = null));
        if (entry.Kind != EntryKind.Group)
        {
            var tags = new MenuItem { Header = "标签" };
            foreach (var tag in app.Data.Tags)
            {
                var item = new MenuItem { Header = new TextBlock { Text = tag }, IsCheckable = true,
                    IsChecked = entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) };
                item.Click += (_, _) => app.Commit(_ =>
                {
                    if (item.IsChecked)
                        entry.Tags.Add(tag);
                    else
                        entry.Tags.RemoveAll(value => value.Equals(tag, StringComparison.OrdinalIgnoreCase));
                });
                tags.Items.Add(item);
            }
            if (tags.Items.Count > 0)
                tags.Items.Add(new Separator());
            AddItem(tags, "新建并添加…", () => AddTag(entry));
            menu.Items.Add(tags);
        }
        if (entry.Group.HasValue || entry.Inbox)
            AddItem(menu, "移到应用页", () => app.Commit(library => Layout.MoveHome(library, entry)));

        if (entry.Kind != EntryKind.Group)
        {
            var groups = app.Data.Entries.Where(group => group.Kind == EntryKind.Group && group.Id != entry.Group).ToList();
            if (groups.Count > 0)
            {
                var move = new MenuItem { Header = "移到文件夹" };
                foreach (var group in groups)
                {
                    var item = new MenuItem { Header = new TextBlock { Text = group.Name } };
                    item.Click += (_, _) => app.Commit(library => Layout.Move(library, entry, group, DropAction.Into));
                    move.Items.Add(item);
                }
                menu.Items.Add(move);
            }
        }

        menu.Items.Add(new Separator());
        AddItem(menu, entry.Kind == EntryKind.Group ? "解散文件夹" : "从 AppKeeper 移除", () =>
            app.Commit(library => Layout.Remove(library, entry)));
        return menu;
    }

    private void ChangeIcon(Entry entry)
    {
        var dialog = new OpenFileDialog { Filter = "图标或图片|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe;*.lnk|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            var icon = ShellIcons.ReadCustom(dialog.FileName);
            icons.Clear();
            app.Commit(_ => entry.Icon = icon);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or
            NotSupportedException or ArgumentException or COMException)
        {
            app.Error("无法读取该图标。");
        }
    }

    private void ActivateEntry(Entry entry)
    {
        if (entry.Kind == EntryKind.Group)
        {
            openFolder = entry.Id;
            RenderFolder();
            return;
        }
        try
        {
            WindowsCatalog.Launch(entry);
            Conceal();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or COMException or IOException)
        {
            app.Error($"无法打开「{entry.Name}」。\n{e.Message}");
        }
    }

    private void RenderFolder()
    {
        var folder = app.Data.Entries.Find(entry => entry.Id == openFolder && entry.Kind == EntryKind.Group);
        FolderShade.Visibility = folder != null ? Visibility.Visible : Visibility.Collapsed;
        FolderTiles.Children.Clear();
        if (folder == null)
            return;

        FolderTitle.Text = folder.Name;
        FolderPanel.Width = Math.Max(200, Math.Min(535, ActualWidth - 64));
        FolderTiles.Columns = Math.Clamp((int)((FolderPanel.Width - 28) / 105), 1, 5);
        FolderScroller.MaxHeight = Math.Max(95, Math.Min(335, ActualHeight - 185));
        foreach (var child in app.Data.Entries.Where(entry => entry.Group == folder.Id))
            FolderTiles.Children.Add(Tile(child, true));
        if (FolderTiles.Children.Count == 0)
            FolderTiles.Children.Add(new TextBlock { Text = "文件夹为空", Margin = new Thickness(8, 20, 0, 10) });
    }

    private void CloseFolder()
    {
        openFolder = null;
        FolderShade.Visibility = Visibility.Collapsed;
        FolderTiles.Children.Clear();
    }

    private static DropAction DropPosition(Button tile, DragEventArgs e, Entry source, Entry target, bool insideFolder)
    {
        var x = e.GetPosition(tile).X / Math.Max(1, tile.ActualWidth);
        return Layout.DropAt(source, target, x, insideFolder);
    }

    private Entry? Dragged(DragEventArgs e) => dragging &&
        e.Data.GetDataPresent(DragFormat, false) && e.Data.GetData(DragFormat, false) is string value &&
        Guid.TryParse(value, out var id) ? app.Data.Entries.Find(entry => entry.Id == id) : null;

    private void RevealHomeForDrag(object sender, DragEventArgs e)
    {
        if (!openFolder.HasValue || draggingOut || Dragged(e) is not { Group: not null })
            return;
        var cursor = e.GetPosition(FolderPanel);
        if (new Rect(FolderPanel.RenderSize).Contains(cursor))
            return;

        draggingOut = true;
        openFolder = null;
        inbox = false;
        FolderShade.Visibility = Visibility.Collapsed;
        RenderTiles();
        RefreshStatus();
    }

    private void WindowDragOver(object sender, DragEventArgs e)
    {
        if (Dragged(e) is { } source && !openFolder.HasValue)
        {
            var x = e.GetPosition(this).X;
            var direction = x < 15 ? -1 : x > ActualWidth - 15 ? 1 : 0;
            if (direction != 0 && page + direction >= 0 && page + direction < pages)
            {
                if (edgeDirection != direction)
                    edgeTimer.Stop();
                edgeDirection = direction;
                edgeTimer.Start();
            }
            else
                edgeTimer.Stop();

            var canMove = !inbox || source.Kind != EntryKind.Group;
            e.Effects = canMove ? DragDropEffects.Move : DragDropEffects.None;
        }
        else
        {
            edgeTimer.Stop();
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void WindowDrop(object sender, DragEventArgs e)
    {
        if (Dragged(e) is { } entry && !openFolder.HasValue && (!inbox || entry.Kind != EntryKind.Group))
        {
            app.Commit(library =>
            {
                Layout.MoveHome(library, entry);
                entry.Inbox = inbox;
            });
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            AddPaths(files);
        e.Handled = true;
    }

    private void OutDragOver(object sender, DragEventArgs e)
    {
        e.Effects = Dragged(e) != null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OutDrop(object sender, DragEventArgs e)
    {
        if (Dragged(e) is { } entry)
        {
            app.Commit(library => Layout.MoveHome(library, entry));
            CloseFolder();
        }
        e.Handled = true;
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        try { AddEntries(paths.Select(WindowsCatalog.FromPath).ToList()); }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            app.Error(e.Message);
        }
    }

    private void AddEntries(List<Entry> entries)
    {
        var group = openFolder;
        if (!group.HasValue)
        {
            inbox = false;
            SearchBox.Clear();
        }
        app.Commit(library =>
        {
            foreach (var entry in entries)
            {
                var existing = library.Entries.Find(item => item.Kind == entry.Kind &&
                    string.Equals(item.Target, entry.Target, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (existing.Inbox || group.HasValue)
                    {
                        existing.Group = group;
                        existing.Inbox = false;
                    }
                    continue;
                }
                entry.Group = group;
                library.Entries.Add(entry);
            }
        });
        if (!group.HasValue)
        {
            page = pages - 1;
            RefreshData();
        }
    }

    private static void AddItem(ItemsControl menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void OpenAdd(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddButton, Placement = PlacementMode.Bottom };
        AddItem(menu, "程序、快捷方式或文件…", () =>
        {
            var dialog = new OpenFileDialog
            {
                Filter = "程序或快捷方式|*.exe;*.lnk;*.url|所有文件|*.*", Multiselect = true
            };
            if (dialog.ShowDialog(this) == true)
                AddPaths(dialog.FileNames);
        });
        AddItem(menu, "文件夹…", () =>
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog(this) == true)
                AddPaths([dialog.FolderName]);
        });
        AddItem(menu, "网址…", () =>
        {
            if (Dialogs.Text(this, "添加网址", "网址", "", "确定") is not { } address)
                return;
            try { AddEntries([WindowsCatalog.FromUrl(address)]); }
            catch (ArgumentException error) { app.Error(error.Message); }
        });
        menu.IsOpen = true;
    }

    internal void ShowSettingsFromTray()
    {
        Reveal();
        Dialogs.Settings(this, app);
    }

    private void OpenSettings(object sender, RoutedEventArgs e) => Dialogs.Settings(this, app);

    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready)
            return;
        CloseFolder();
        page = 0;
        RefreshData();
    }

    private void SwitchSection(bool targetInbox)
    {
        inbox = targetInbox;
        SearchBox.Clear();
        page = 0;
        RefreshData();
    }

    private void ChangePage(int delta)
    {
        var next = Math.Clamp(page + delta, 0, pages - 1);
        if (page == next)
            return;
        page = next;
        RenderTiles();
    }

    private void HandleKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Conceal();
        else if (e.Key == Key.Enter && SearchBox.IsKeyboardFocusWithin && Tiles.Children.Count > 0 &&
            Tiles.Children[0] is Button { Tag: Entry entry } && Query.Length > 0)
            ActivateEntry(entry);
        else if (!openFolder.HasValue && (e.Key is Key.PageUp or Key.PageDown ||
            (!SearchBox.IsKeyboardFocusWithin && e.Key is Key.Left or Key.Right)))
            ChangePage(e.Key is Key.PageUp or Key.Left ? -1 : 1);
        else
            return;
        e.Handled = true;
    }

    private void RenderTags()
    {
        TagBar.Children.Clear();
        foreach (var tag in new string?[] { null }.Concat(app.Data.Tags))
        {
            var button = new Button { Content = tag ?? "全部", FontSize = 12,
                Margin = new Thickness(0, 0, 5, 0), Padding = new Thickness(9, 4, 9, 4),
                Background = tag == selectedTag ? activeBrush : Brushes.Transparent };
            button.Click += (_, _) =>
            {
                selectedTag = tag;
                page = 0;
                RefreshData();
            };
            if (tag != null)
            {
                var menu = new ContextMenu();
                AddItem(menu, "删除标签", () =>
                {
                    if (MessageBox.Show(this, $"删除「{tag}」及其应用关联？", "删除标签",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        app.Commit(data => Layout.RemoveTag(data, tag));
                });
                button.ContextMenu = menu;
            }
            TagBar.Children.Add(button);
        }
    }

    private void AddTag(Entry? entry)
    {
        if (Dialogs.Text(this, "新建标签", "标签名称（最多 24 个字符）", "") is not { } tag)
            return;
        if (!Layout.IsTagName(tag) || app.Data.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            app.Error("请输入 1 至 24 个字符的标签名称，且名称不能重复。");
            return;
        }
        app.Commit(data =>
        {
            data.Tags.Add(tag);
            entry?.Tags.Add(tag);
        });
    }

    private void CreateTag(object sender, RoutedEventArgs e) => AddTag(null);

    private void DismissFolder(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, FolderShade))
            CloseFolder();
    }

    private void HideOnBlank(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, TileArea))
            Conceal();
    }

    private void MoveWindow(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
            DragMove();
    }

    private void ResizeWindow(object sender, DragDeltaEventArgs e)
    {
        var area = Forms.Screen.FromHandle(handle).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this);
        Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, Math.Max(MinWidth, area.Width / scale.DpiScaleX));
        Height = Math.Clamp(Height + e.VerticalChange, MinHeight, Math.Max(MinHeight, area.Height / scale.DpiScaleY));
    }

    private void ResizeTiles(object sender, SizeChangedEventArgs e) => RefreshData();
    private void PreviousPage(object sender, RoutedEventArgs e) => ChangePage(-1);
    private void NextPage(object sender, RoutedEventArgs e) => ChangePage(1);
    private void WheelPage(object sender, MouseWheelEventArgs e) => ChangePage(e.Delta > 0 ? -1 : 1);
    private void OpenHome(object sender, RoutedEventArgs e) => SwitchSection(false);
    private void OpenInbox(object sender, RoutedEventArgs e) => SwitchSection(true);
    private void CloseFolder(object sender, RoutedEventArgs e) => CloseFolder();
    private void HideClicked(object sender, RoutedEventArgs e) => Conceal();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(System.Drawing.Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
