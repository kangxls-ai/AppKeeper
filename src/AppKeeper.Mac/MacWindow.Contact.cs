using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AppKeeper;

internal sealed partial class MacWindow
{
    private Control ContactFooter()
    {
        ContextMenu CopyMenu()
        {
            var menu=new ContextMenu();
            foreach(var item in new[]{("复制开发者","小李鱼"),("复制邮箱","kangxls@gmail.com"),("复制全部信息","开发者 小李鱼 · 来信建议：kangxls@gmail.com")})
                menu.Items.Add(Menu(item.Item1,()=>_=Safe(async()=>await Clipboard!.SetTextAsync(item.Item2))));
            return menu;
        }
        var label=new SelectableTextBlock{Text="开发者 小李鱼 · 来信建议：",FontSize=11,Opacity=.7,VerticalAlignment=VerticalAlignment.Center,ContextMenu=CopyMenu()};
        ToolTip.SetTip(label,"可选择文字复制，或右键复制开发者信息");
        var email=B("kangxls@gmail.com",()=>
        {
            var start=new System.Diagnostics.ProcessStartInfo("/usr/bin/open"){UseShellExecute=false};start.ArgumentList.Add("mailto:kangxls@gmail.com");
            System.Diagnostics.Process.Start(start)?.Dispose();
        });
        email.Padding=default;email.Margin=default;email.Background=Brushes.Transparent;email.BorderThickness=default;email.MinHeight=0;email.Height=18;
        email.Content=new TextBlock{Text="kangxls@gmail.com",FontSize=11,TextDecorations=TextDecorations.Underline};email.ContextMenu=CopyMenu();
        email.Cursor=new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);ToolTip.SetTip(email,"点击打开默认邮件应用；右键复制邮箱");
        var footer=new WrapPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(4,8,4,2),ContextMenu=CopyMenu()};
        footer.Children.Add(label);footer.Children.Add(email);return footer;
    }
}
