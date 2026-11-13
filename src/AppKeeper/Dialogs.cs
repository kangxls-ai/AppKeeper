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
        var (dialog, body) = Create(owner, "AppKeeper 设置", 490);
        var startup = new CheckBox { Content = "登录 Windows 时在托盘启动", IsChecked = app.Data.StartWithWindows,
            Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 19) };
        body.Children.Add(startup);
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

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0) };
        footer.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        var save = new Button { Content = "保存", IsDefault = true };
        save.Click += (_, _) =>
        {
            if (app.SaveSettings(modifiers, key, startup.IsChecked == true))
                dialog.DialogResult = true;
        };
        footer.Children.Add(save);
        body.Children.Add(footer);
        dialog.ShowDialog();
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
