using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace AppKeeper;

internal sealed class AgendaPage : UserControl
{
    private readonly AgendaStore store;
    private readonly AgendaLanSync sync;
    private AgendaSyncPanel? syncPanel;
    private readonly StackPanel content = new();
    private readonly TextBox quick = ModuleUi.Input();
    private readonly StackPanel footer = new();
    private readonly Button[] tabs;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private AgendaTask[] tasks = [];
    private DateTime selected = DateTime.Today;
    private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private int page;

    internal AgendaPage(AgendaStore store, AgendaLanSync sync)
    {
        this.store = store;
        this.sync = sync;
        var layout = new DockPanel();
        var header = new StackPanel { Margin = new(0, 0, 0, 5) };
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(ModuleUi.Text("日程管理", true, 19));
        var navigation = new WrapPanel();
        tabs = new[] { "待办", "日历", "已完成", "连接手机", "设置" }.Select((label, index) => ModuleUi.Button(label, () =>
        {
            page = index;
            Render();
        })).ToArray();
        foreach (var tab in tabs) navigation.Children.Add(tab);
        navigation.Children.Add(ModuleUi.Button("导出 Markdown", Export));
        header.Children.Add(navigation);
        layout.Children.Add(header);
        DockPanel.SetDock(footer, Dock.Bottom);
        var quickActions = new WrapPanel();
        quickActions.Children.Add(ModuleUi.Button("快速添加", QuickAdd, true));
        quickActions.Children.Add(ModuleUi.Button("时间与标签", () => Edit(null, null)));
        footer.Children.Add(quick);
        footer.Children.Add(quickActions);
        quick.ToolTip = "输入待办事项";
        quick.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { QuickAdd(); e.Handled = true; }
        };
        layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
        timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { Refresh(force: true); timer.Start(); }
            else timer.Stop();
        };
    }

    private void Refresh(bool force = false)
    {
        try
        {
            var next = store.Tasks();
            if (force || !next.SequenceEqual(tasks))
            {
                tasks = next;
                Render();
            }
        }
        catch (Exception error)
        {
            content.Children.Clear();
            content.Children.Add(ModuleUi.Text(error.Message));
            content.Children.Add(ModuleUi.Button("日程设置", () => { page = 4; Render(); }));
        }
    }

    private void Render()
    {
        content.Children.Clear();
        for (var i = 0; i < tabs.Length; i++)
            tabs[i].Background = i == page ? ModuleUi.Accent : System.Windows.Media.Brushes.Transparent;
        footer.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (page == 4) ShowSettings();
        else if (page == 3) content.Children.Add(syncPanel ??= new AgendaSyncPanel(sync));
        else if (page == 1) ShowCalendar();
        else if (page == 2)
        {
            content.Children.Add(ModuleUi.Text("已完成", true, 16));
            ShowTasks(tasks.Where(task => task.Completed != null).OrderByDescending(task => task.Completed).ToArray(), "暂无完成记录");
        }
        else
        {
            content.Children.Add(ModuleUi.Text("今日 · " + DateTime.Today.ToString("M 月 d 日 ddd", CultureInfo.GetCultureInfo("zh-CN")), true, 16));
            ShowTasks(tasks.Where(task => task.Completed == null).ToArray(), "没有待办");
        }
    }

    private void ShowCalendar()
    {
        var navigation = new WrapPanel();
        navigation.Children.Add(ModuleUi.Button("‹", () => { month = month.AddMonths(-1); Render(); }));
        navigation.Children.Add(ModuleUi.Text(month.ToString("yyyy 年 M 月"), true));
        navigation.Children.Add(ModuleUi.Button("›", () => { month = month.AddMonths(1); Render(); }));
        navigation.Children.Add(ModuleUi.Button("今天", () =>
        {
            selected = DateTime.Today;
            month = new(selected.Year, selected.Month, 1);
            Render();
        }));
        content.Children.Add(navigation);
        var grid = new UniformGrid { Columns = 7, Margin = new(0, 2, 0, 8) };
        foreach (var day in "一二三四五六日")
            grid.Children.Add(new TextBlock { Text = day.ToString(), HorizontalAlignment = HorizontalAlignment.Center });
        for (var i = 0; i < ((int)month.DayOfWeek + 6) % 7; i++) grid.Children.Add(new TextBlock());
        for (var day = 1; day <= DateTime.DaysInMonth(month.Year, month.Month); day++)
        {
            var date = month.AddDays(day - 1);
            var pending = tasks.Count(task => task.Completed == null && task.On(date));
            var done = tasks.Any(task => task.Completed != null && task.On(date));
            var number = day.ToString();
            if (pending > 0) number += "\n" + pending + " 项";
            if (done) number += "\n•";
            var button = ModuleUi.Button(number, () => { selected = date; Render(); }, selected.Date == date);
            button.Padding = new(1, 4, 1, 4);
            button.MinHeight = 38;
            grid.Children.Add(button);
        }
        content.Children.Add(grid);
        content.Children.Add(ModuleUi.Text(selected.ToString("M 月 d 日"), true));
        content.Children.Add(ModuleUi.Button("添加到这一天", () => Edit(null, selected)));
        ShowTasks(tasks.Where(task => task.On(selected)).ToArray(), "这一天没有安排");
    }

    private void ShowTasks(AgendaTask[] entries, string empty)
    {
        if (entries.Length == 0) content.Children.Add(ModuleUi.Text(empty));
        foreach (var task in entries)
        {
            var record = new StackPanel();
            record.Children.Add(ModuleUi.Button(task.Title, () => Edit(task, null)));
            var detail = new[] { task.Tag,
                task.Start == null ? "" : "计划 " + AgendaStore.Format(task.Start, task.StartTime),
                task.Due == null ? "" : "截止 " + AgendaStore.Format(task.Due, task.DueTime),
                task.Actual == null || task.Completed != null ? "" : "进行中",
                task.Completed == null ? "" : "完成于 " + AgendaStore.Format(task.Completed, true) };
            record.Children.Add(ModuleUi.Text(string.Join(" · ", detail.Where(value => value.Length > 0)), size: 12));
            var actions = new WrapPanel { Margin = new(0, 4, 0, 0) };
            if (task.Completed == null)
            {
                actions.Children.Add(ModuleUi.Button("完成", () => Update("complete", task.Id,
                    new { completedAt = AgendaStore.Iso(DateTimeOffset.Now), note = "" }), true));
                actions.Children.Add(ModuleUi.Button(task.Actual == null ? "开始" : "撤销开始", () =>
                {
                    if (task.Actual == null)
                        Update("start", task.Id, new { actualStartedAt = AgendaStore.Iso(DateTimeOffset.Now) });
                    else
                        Update("edit", task.Id, new { actualStartedAt = (string?)null });
                }));
            }
            else
                actions.Children.Add(ModuleUi.Button("恢复为待办", () => Update("reopen", task.Id, new { })));
            actions.Children.Add(ModuleUi.Button(task.Completed.HasValue ? "补充反馈" : "完成并记录反馈", () => Feedback(task)));
            record.Children.Add(actions);
            content.Children.Add(new Border
            {
                Child = record, CornerRadius = new(10), Padding = new(8), Margin = new(0, 5, 0, 0),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 45, 66, 90))
            });
        }
    }

    private void QuickAdd()
    {
        if (string.IsNullOrWhiteSpace(quick.Text)) return;
        if (Update("create", Guid.NewGuid().ToString(), new { title = quick.Text.Trim(), category = "" }))
            quick.Clear();
    }

    private bool Update(string kind, string id, object payload)
    {
        try { store.Append(kind, id, payload); Refresh(force: true); return true; }
        catch (Exception error) { ModuleUi.Error(this, error); return false; }
    }

    private static (DatePicker Date, TextBox Clock) DateFields(Panel form, string label, DateTimeOffset? time, bool timed)
    {
        form.Children.Add(ModuleUi.Text(label));
        var row = new WrapPanel();
        var picker = new DatePicker { SelectedDate = time?.LocalDateTime.Date, Width = 142 };
        var clock = ModuleUi.Input(timed ? time?.ToLocalTime().ToString("HH:mm") ?? "" : "");
        clock.Width = 74;
        clock.ToolTip = "可选时间 HH:mm";
        row.Children.Add(picker);
        row.Children.Add(clock);
        row.Children.Add(ModuleUi.Text(" 可选时间 HH:mm"));
        form.Children.Add(row);
        return (picker, clock);
    }

    private static (DateTimeOffset? Date, bool Timed) ReadDate((DatePicker Date, TextBox Clock) field)
    {
        var text = field.Clock.Text.Trim();
        if (field.Date.SelectedDate == null)
        {
            if (text.Length > 0) throw new ArgumentException("请先选择日期。");
            return (null, false);
        }
        var time = TimeSpan.Zero;
        if (text.Length != 0 && (!TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out time) ||
            time.Hours > 23))
            throw new ArgumentException("时间请使用 HH:mm 格式。");
        return (new DateTimeOffset(field.Date.SelectedDate.Value.Date.Add(time)), text.Length != 0);
    }

    private void Edit(AgendaTask? task, DateTime? date)
    {
        var form = new StackPanel { Margin = new(15) };
        var title = ModuleUi.Input(task?.Title ?? quick.Text);
        var tag = ModuleUi.Input(task?.Tag ?? "");
        form.Children.Add(ModuleUi.Text("事项"));
        form.Children.Add(title);
        form.Children.Add(ModuleUi.Text("标签"));
        form.Children.Add(tag);
        var start = DateFields(form, "计划开始", task?.Start ?? (date.HasValue ? new DateTimeOffset(date.Value) : null), task?.StartTime == true);
        var due = DateFields(form, "计划截止", task?.Due, task?.DueTime == true);
        var window = ModuleUi.Dialog(this, task == null ? "新增事项" : "事项详情", form);
        form.Children.Add(ModuleUi.Button("保存", () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title.Text)) throw new ArgumentException("请填写事项名称。");
                var (planned, timedStart) = ReadDate(start);
                var (deadline, timedDue) = ReadDate(due);
                if (deadline.HasValue && planned.HasValue && deadline.Value.Date < planned.Value.Date)
                    throw new ArgumentException("截止日期早于开始日期。");
                if (Update(task == null ? "create" : "edit", task?.Id ?? Guid.NewGuid().ToString(),
                    new { title = title.Text.Trim(), category = tag.Text.Trim(), plannedStart = AgendaStore.Iso(planned),
                        deadline = AgendaStore.Iso(deadline), startHasTime = timedStart, deadlineHasTime = timedDue }))
                {
                    if (task == null) quick.Clear();
                    window.Close();
                }
            }
            catch (Exception error) { ModuleUi.Error(form, error); }
        }, true));
        if (task != null)
            form.Children.Add(ModuleUi.Button("删除事项", () =>
            {
                if (MessageBox.Show(window, "删除这个事项？", "MarkDo", MessageBoxButton.YesNo) == MessageBoxResult.Yes &&
                    Update("delete", task.Id, new { })) window.Close();
            }));
        window.ShowDialog();
    }

    private void Feedback(AgendaTask task)
    {
        var form = new StackPanel { Margin = new(15) };
        var effort = ModuleUi.Input(task.Seconds.HasValue ? (task.Seconds.Value / 60m).ToString("0.##") : "");
        var rating = ModuleUi.Input(task.Rating?.ToString() ?? "");
        var note = ModuleUi.Input(task.Note);
        form.Children.Add(ModuleUi.Text("投入分钟（可选）")); form.Children.Add(effort);
        form.Children.Add(ModuleUi.Text("评分 1–5（可选）")); form.Children.Add(rating);
        form.Children.Add(ModuleUi.Text("备注（可选）")); form.Children.Add(note);
        var window = ModuleUi.Dialog(this, task.Completed == null ? "完成并记录反馈" : "补充完成反馈", form);
        form.Children.Add(ModuleUi.Button("保存", () =>
        {
            var values = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["note"] = note.Text.Trim(), ["rating"] = null, ["completedAt"] = AgendaStore.Iso(task.Completed ?? DateTimeOffset.Now)
            };
            if (effort.Text.Trim().Length > 0)
            {
                if (!decimal.TryParse(effort.Text, out var minutes) || minutes is < 0 or > 10000000)
                {
                    ModuleUi.Error(form, new ArgumentException("请输入有效的投入分钟。")); return;
                }
                values["effortSeconds"] = (int)Math.Round(minutes * 60);
                values["inputValue"] = values["effortSeconds"];
                values["inputUnit"] = "second";
            }
            if (rating.Text.Trim().Length > 0)
            {
                if (!int.TryParse(rating.Text, out var score) || score is < 1 or > 5)
                {
                    ModuleUi.Error(form, new ArgumentException("评分须为 1 至 5 的整数。")); return;
                }
                values["rating"] = score;
            }
            if (Update(task.Completed == null ? "complete" : "feedback", task.Id, values)) window.Close();
        }, true));
        window.ShowDialog();
    }

    private void ShowSettings()
    {
        content.Children.Add(ModuleUi.Text("任务数据库", true, 16));
        content.Children.Add(ModuleUi.Text(store.PathName));
        content.Children.Add(ModuleUi.Button("打开现有 MarkDo 数据库…", () =>
        {
            try
            {
                var found = store.FindOriginal();
                var picker = new OpenFileDialog { Filter = "MarkDo 数据库|*.db", FileName = found ?? "" };
                if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
                store.Connect(picker.FileName);
                Refresh(force: true);
            }
            catch (Exception error) { ModuleUi.Error(this, error); }
        }));
        content.Children.Add(ModuleUi.Button("使用 AppKeeper 本地数据库", () =>
        {
            try { store.Connect(null); Refresh(force: true); }
            catch (Exception error) { ModuleUi.Error(this, error); }
        }));
        content.Children.Add(ModuleUi.Button("连接 MarkDo 手机端", () => { page = 3; Render(); }));
        content.Children.Add(ModuleUi.Text("AppKeeper 直接提供手机配对与局域网同步服务，无需同时运行原版 Windows MarkDo。", size: 12));
        var reminders = new CheckBox { IsChecked = store.Reminders, Content = "启用开始和截止提醒", Foreground = System.Windows.Media.Brushes.White };
        reminders.Click += (_, _) =>
        {
            try { store.SetReminders(reminders.IsChecked == true); }
            catch (Exception error) { ModuleUi.Error(this, error); }
        };
        content.Children.Add(reminders);
    }

    private void Export()
    {
        var form = new StackPanel { Margin = new(15) };
        var start = new DatePicker { SelectedDate = DateTime.Today.AddDays(-6) };
        var end = new DatePicker { SelectedDate = DateTime.Today };
        form.Children.Add(ModuleUi.Text("开始日期")); form.Children.Add(start);
        form.Children.Add(ModuleUi.Text("结束日期")); form.Children.Add(end);
        var window = ModuleUi.Dialog(this, "导出成长记录", form);
        foreach (var copy in new[] { true, false })
            form.Children.Add(ModuleUi.Button(copy ? "复制 Markdown" : "保存 Markdown…", () =>
            {
                try
                {
                    if (start.SelectedDate is not { } first || end.SelectedDate is not { } last || last < first)
                        throw new ArgumentException("请选择有效的导出范围。");
                    var markdown = store.Markdown(first, last);
                    if (copy) Clipboard.SetText(markdown);
                    else
                    {
                        var picker = new SaveFileDialog { FileName = "MarkDo-record.md", Filter = "Markdown|*.md" };
                        if (picker.ShowDialog(window) != true) return;
                        File.WriteAllText(picker.FileName, markdown);
                    }
                    window.Close();
                }
                catch (Exception error) { ModuleUi.Error(form, error); }
            }));
        window.ShowDialog();
    }
}
