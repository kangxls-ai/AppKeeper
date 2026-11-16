using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AppKeeper;

internal static class ModuleUi
{
    internal static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(51, 96, 132));

    internal static TextBlock Text(string text, bool bold = false, double size = 14) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center
    };

    internal static Button Button(string text, Action action, bool emphasis = false)
    {
        var button = new Button { Content = text, Margin = new(0, 0, 5, 4), Padding = new(8, 5, 8, 5) };
        if (emphasis) button.Background = Accent;
        button.Click += (_, _) => action();
        return button;
    }

    internal static TextBox Input(string text = "") => new()
    {
        Text = text, Style = null, FontSize = 15, MinHeight = 34, Padding = new(7),
        Foreground = Brushes.White, CaretBrush = Brushes.White,
        Background = new SolidColorBrush(Color.FromRgb(36, 55, 75)), BorderThickness = new(0)
    };

    internal static Window Dialog(FrameworkElement owner, string title, Panel content) => new()
    {
        Title = title, Owner = Window.GetWindow(owner), Content = content, Width = 425,
        SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(32, 47, 65))
    };

    internal static void Error(FrameworkElement owner, Exception error)
    {
        var message = error is System.OperationCanceledException ? "连接超时，请检查对方已运行并确认地址和端口。" : error.Message;
        MessageBox.Show(Window.GetWindow(owner), message, "AppKeeper", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
