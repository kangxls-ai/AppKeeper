using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AppKeeper;

internal static class Dialogs
{
    private static (Window Window, StackPanel Body) Create(Window owner, string title, double width)
    {
        var body = new StackPanel { Margin = new Thickness(25, 20, 25, 23) };
        var dialog = new Window
        {
            Owner = owner, Title = title, Width = width, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Background = new SolidColorBrush(Color.FromRgb(36, 48, 67)),
            Foreground = Brushes.White, FontFamily = owner.FontFamily, FontSize = 14, Content = body
        };
        return (dialog, body);
    }

    public static string? Text(Window owner, string title, string label, string value, string confirm = "保存")
    {
        var (dialog, body) = Create(owner, title, 430);
        body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { Text = value, MaxLength = 2048, MinHeight = 38 };
        body.Children.Add(input);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = confirm, IsDefault = true, IsEnabled = !string.IsNullOrWhiteSpace(value) };
        input.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        save.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        body.Children.Add(actions);

        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    public static void Settings(Window owner, App app)
    {
        var (dialog, body) = Create(owner, app.ProductName + " 设置", 490);
        var startup = new CheckBox { Content = "登录 Windows 时在托盘启动", IsChecked = app.Data.StartWithWindows,
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 19) };
        if (app.IsStoreEdition)
            AddButton(body, "在 Windows 设置中管理登录自启", () => OpenLink(dialog, "ms-settings:startupapps"));
        else
            body.Children.Add(startup);
        var shell=new CheckBox{Content="启用右键“添加到 EasyAppKeeper”",IsChecked=app.Data.ShellIntegration,Foreground=Brushes.White,Margin=new Thickness(0,0,0,12)};
        body.Children.Add(shell);
        body.Children.Add(new TextBlock { Text = "全局快捷键", FontWeight = FontWeights.SemiBold });

        var modifiers = app.Data.Modifiers;
        var key = app.Data.Key;
        var shortcut = new TextBox { Text = ShortcutName(modifiers, key), IsReadOnly = true,
            IsReadOnlyCaretVisible = false, Margin = new Thickness(0, 8, 0, 0) };
        shortcut.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab || e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
                return;
            var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
            var virtualKey = KeyInterop.VirtualKeyFromKey(pressed);
            e.Handled = true;
            if (virtualKey is <= 0 or > 254 or 16 or 17 or 18 or 91 or 92 or >= 160 and <= 165 ||
                Keyboard.Modifiers == ModifierKeys.None)
                return;
            modifiers = (uint)Keyboard.Modifiers;
            key = virtualKey;
            shortcut.Text = ShortcutName(modifiers, key);
        };
        body.Children.Add(shortcut);
        body.Children.Add(new TextBlock
        {
            Text = "点击输入框，然后按下带 Alt、Ctrl、Shift 或 Win 的组合键。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 19),
            Foreground = (Brush)owner.FindResource("Secondary")
        });

        body.Children.Add(new TextBlock { Text = "应用与备份", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 9) });
        var tools = new WrapPanel();
        AddButton(tools, "重新扫描", async () => await app.Refresh(showResult: true));
        AddButton(tools, "导入备份…", () =>
        {
            var file = new OpenFileDialog { Filter = "AppKeeper 备份|*.json;*.bak|所有文件|*.*" };
            if (file.ShowDialog(dialog) != true ||
                MessageBox.Show(dialog, "导入将替换当前布局与设置，是否继续？", "导入备份",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            app.Import(file.FileName);
            modifiers = app.Data.Modifiers;
            key = app.Data.Key;
            shortcut.Text = ShortcutName(modifiers, key);
            startup.IsChecked = app.Data.StartWithWindows;
        });
        AddButton(tools, "导出备份…", () =>
        {
            var file = new SaveFileDialog { FileName = "AppKeeper-backup.json", Filter = "AppKeeper 备份|*.json" };
            if (file.ShowDialog(dialog) == true)
                app.Export(file.FileName);
        });
        body.Children.Add(tools);
        if (app.IsStoreEdition)
        {
            body.Children.Add(new TextBlock { Text = "关于 EasyAppKeeper", FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 6) });
            AddButton(body, "意见反馈（公开页面）", () => OpenLink(dialog, "https://github.com/kangxls-ai/AppKeeper/issues/new"));
        }

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0) };
        footer.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        var save = new Button { Content = "保存", IsDefault = true };
        save.Click += (_, _) =>
        {
            if (app.SaveSettings(modifiers, key, startup.IsChecked == true,shell.IsChecked==true))
                dialog.DialogResult = true;
        };
        footer.Children.Add(save);
        body.Children.Add(footer);
        dialog.ShowDialog();
    }

    internal static string? ExternalEntries(Window owner,Library data,string[] paths,int missing)
    {
        var (dialog,body)=Create(owner,"添加到 EasyAppKeeper",460);
        body.Children.Add(new TextBlock{Text=$"添加 {paths.Length} 个入口",FontSize=19,FontWeight=FontWeights.SemiBold});
        var list=new ListBox{MaxHeight=190,Margin=new Thickness(0,12,0,12)};
        foreach(var path in paths)list.Items.Add(new TextBlock{Text=System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)),ToolTip=path,TextTrimming=TextTrimming.CharacterEllipsis});
        body.Children.Add(list);
        if(missing>0)body.Children.Add(new TextBlock{Text=$"另有 {missing} 项已不存在，将跳过。",TextWrapping=TextWrapping.Wrap});
        body.Children.Add(new TextBlock{Text="添加到标签",Margin=new Thickness(0,4,0,6)});
        var tags=new ComboBox{ItemsSource=new[]{"全部（不关联标签）"}.Concat(data.Tags).ToArray(),SelectedIndex=0};body.Children.Add(tags);
        var newTag=new TextBox{MaxLength=24,Margin=new Thickness(0,10,0,0),ToolTip="可选：填写后创建并使用此标签"};
        System.Windows.Automation.AutomationProperties.SetAutomationId(newTag,"ExternalNewTag");
        body.Children.Add(new TextBlock{Text="或新建标签（可选，最多 24 字）",Margin=new Thickness(0,12,0,4)});body.Children.Add(newTag);
        body.Children.Add(new TextBlock{Text="仅创建快捷入口，原文件保留原位；已有入口自动关联标签。",TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,12,0,0)});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,18,0,0)};
        actions.Children.Add(new Button{Content="取消",IsCancel=true,Margin=new Thickness(0,0,8,0)});
        string? result=null;var save=new Button{Content="添加入口",IsDefault=true};save.Click+=(_,_)=>
        {
            var value=newTag.Text.Trim();
            if(value.Length>0&&!Layout.IsTagName(value)){MessageBox.Show(dialog,"标签名称无效。请使用 1 至 24 个字符。","添加入口");return;}
            result=value.Length>0?data.Tags.FirstOrDefault(t=>t.Equals(value,StringComparison.OrdinalIgnoreCase))??value:tags.SelectedIndex>0?data.Tags[tags.SelectedIndex-1]:"";
            dialog.DialogResult=true;
        };actions.Children.Add(save);body.Children.Add(actions);
        return dialog.ShowDialog()==true?result:null;
    }

    public static void OfferPortableMigration(ProductProfile profile)
    {
        var body = new StackPanel { Margin = new Thickness(24) };
        var dialog = new Window
        {
            Title = "欢迎使用 " + profile.Name, Width = 485, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(36, 48, 67)), Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), Content = body
        };
        body.Children.Add(new TextBlock { Text = "从绿色版迁移", FontSize = 19, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "可导入原有布局、日程和飞鸽传书记录。已接收的文件留在原目录。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14) });
        var actions = new WrapPanel();
        body.Children.Add(actions);
        AddButton(actions, "从空白开始", () => dialog.Close());
        var migrate = new Button { Content = "选择绿色版目录…" };
        actions.Children.Add(migrate);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(status);
        var busy = false;
        dialog.Closing += (_, e) => e.Cancel = busy;
        migrate.Click += async (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "选择包含 AppKeeper.exe 的目录" };
            if (picker.ShowDialog(dialog) != true)
                return;
            busy = true;
            actions.IsEnabled = false;
            status.Text = "正在复制并验证数据…";
            try
            {
                await System.Threading.Tasks.Task.Run(() => PortableMigration.Run(picker.FolderName, profile.DataDirectory));
                busy = false;
                dialog.Close();
            }
            catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or
                System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
                System.Security.Cryptography.CryptographicException)
            {
                status.Text = error.Message;
                busy = false;
                actions.IsEnabled = true;
            }
        };
        dialog.ShowDialog();
    }

    private static void OpenLink(Window owner, string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception error)
        {
            MessageBox.Show(owner, error.Message, owner.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private static string ShortcutName(uint modifiers, int key)
    {
        var names = new List<string>();
        foreach (var (flag, name) in new[] { (2u, "Ctrl"), (1u, "Alt"), (4u, "Shift"), (8u, "Win") })
        {
            if ((modifiers & flag) != 0)
                names.Add(name);
        }
        names.Add(KeyInterop.KeyFromVirtualKey(key).ToString());
        return string.Join(" + ", names);
    }
}
