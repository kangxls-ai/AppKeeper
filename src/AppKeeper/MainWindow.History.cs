using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace AppKeeper;

public partial class MainWindow
{
    private string? suppressedSearch;

    private void SaveQuickHistory(Guid? visited = null)
    {
        searchHistoryTimer.Stop();
        if (!IsVisible)
            return;
        var query = Query;
        var saveSearch = query.Length > 0 && !query.Equals(suppressedSearch, StringComparison.OrdinalIgnoreCase) &&
            app.Data.SearchHistory.FirstOrDefault() != query;
        var saveVisit = visited.HasValue && app.Data.RecentEntries.FirstOrDefault() != visited.Value;
        if (!saveSearch && !saveVisit)
            return;

        if (!app.Commit(data =>
        {
            if (saveSearch)
                QuickHistory.RecordSearch(data, query);
            if (saveVisit)
                QuickHistory.RecordVisit(data, visited.GetValueOrDefault());
        }, quiet: true))
            return;

        if (IsVisible && AppsPage.IsVisible)
        {
            if (saveSearch)
                RenderSearchHistory();
            if (saveVisit)
                RenderRecentEntries();
        }
    }

    private void RenderSearchHistory()
    {
        SearchHistoryBar.Children.Clear();
        SearchHistoryBar.Children.Add(StripLabel("最近检索", app.Data.SearchHistory.Count == 0));
        foreach (var query in app.Data.SearchHistory)
        {
            var button = StripButton(query, query == Query);
            button.Click += (_, _) =>
            {
                SearchBox.Text = query;
                SearchBox.CaretIndex = SearchBox.Text.Length;
                SaveQuickHistory();
                SearchBox.Focus();
            };
            button.ToolTip = $"{query}\n右键可清除此记录";
            var menu = new ContextMenu();
            AddItem(menu, "清除此搜索记录", () => ClearSearchHistory(query));
            button.ContextMenu = menu;
            SearchHistoryBar.Children.Add(button);
        }
    }

    private void RenderRecentEntries()
    {
        RecentEntriesBar.Children.Clear();
        var entries = QuickHistory.Entries(app.Data).ToArray();
        RecentEntriesBar.Children.Add(StripLabel("最近访问", entries.Length == 0));
        foreach (var entry in entries)
        {
            var pinned = app.Data.PinnedEntries.Contains(entry.Id);
            var button = StripButton(entry.Name, pinned);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new Viewbox { Width = 22, Height = 22, Margin = new Thickness(0, 0, 5, 0), Child = CreateTileIcon(entry) };
            content.Children.Add(icon);
            content.Children.Add(StripText(entry.Name));
            if (pinned)
            {
                var pin = StripText("\uE718");
                pin.FontFamily = new FontFamily("Segoe MDL2 Assets");
                pin.Margin = new Thickness(4, 0, 0, 0);
                content.Children.Add(pin);
            }
            button.Content = content;
            button.ToolTip = $"{entry.Name}\n右键{(pinned ? "取消锁定" : "锁定到最近访问")}";
            AutomationProperties.SetName(button, pinned ? $"{entry.Name}，已锁定" : entry.Name);
            button.Click += (_, _) => ActivateEntry(entry);
            var menu = new ContextMenu();
            AddPinAction(menu, entry);
            menu.Items.Add(new Separator());
            AddItem(menu, pinned ? "清除此锁定记录…" : "清除此访问记录", () =>
            {
                if (!pinned || MessageBox.Show(this, $"清除「{entry.Name}」的访问记录并取消锁定？",
                    "清除锁定记录", MessageBoxButton.YesNo, MessageBoxImage.Question,
                    MessageBoxResult.No) == MessageBoxResult.Yes)
                    app.Commit(data => QuickHistory.RemoveVisit(data, entry.Id));
            });
            button.ContextMenu = menu;
            RecentEntriesBar.Children.Add(button);
        }
    }

    private void OpenHistoryMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = (Button)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        AddItem(menu, "清空搜索记录", () => ClearSearchHistory());
        ((MenuItem)menu.Items[0]).IsEnabled = app.Data.SearchHistory.Count > 0;
        AddItem(menu, "清空未锁定的访问记录", () => app.Commit(QuickHistory.ClearUnpinnedVisits));
        var pinned = app.Data.PinnedEntries.ToHashSet();
        ((MenuItem)menu.Items[1]).IsEnabled = app.Data.RecentEntries.Any(id => !pinned.Contains(id));
        menu.IsOpen = true;
    }

    private void ClearSearchHistory(string? query = null)
    {
        if (!app.Commit(data =>
        {
            if (query == null)
                data.SearchHistory.Clear();
            else
                QuickHistory.RemoveSearch(data, query);
        }))
            return;
        if (query == null || Query.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            searchHistoryTimer.Stop();
            suppressedSearch = Query;
        }
    }

    private void AddPinAction(ContextMenu menu, Entry entry)
    {
        var pinned = app.Data.PinnedEntries.Contains(entry.Id);
        AddItem(menu, pinned ? "取消锁定" : "锁定到最近访问", () => TogglePin(entry.Id));
    }

    private void TogglePin(Guid id)
    {
        if (!app.Data.Entries.Any(entry => entry.Id == id))
            return;
        var pinned = app.Data.PinnedEntries.Contains(id);
        if (!pinned && app.Data.PinnedEntries.Count >= QuickHistory.EntryLimit)
        {
            app.Error($"最多锁定 {QuickHistory.EntryLimit} 个入口，请先取消一个锁定。");
            return;
        }
        app.Commit(data =>
        {
            if (pinned)
                data.PinnedEntries.Remove(id);
            else
                data.PinnedEntries.Add(id);
        });
    }

    private TextBlock StripLabel(string name, bool empty) => new()
    {
        Text = empty ? name + " · 暂无" : name,
        FontSize = 12,
        Foreground = (Brush)FindResource("Secondary"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0)
    };

    private Button StripButton(string text, bool highlighted) => new()
    {
        Content = StripText(text),
        ToolTip = text,
        Padding = new Thickness(6, 3, 6, 3),
        Margin = new Thickness(0, 0, 4, 0),
        Background = highlighted ? activeBrush : Brushes.Transparent,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock StripText(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        MaxWidth = 105
    };
}
