using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace AppKeeper;

public partial class MainWindow
{
    private void CopyDeveloperName(object sender,RoutedEventArgs e)=>CopyContact("小李鱼");
    private void CopyDeveloperEmail(object sender,RoutedEventArgs e)=>CopyContact("kangxls@gmail.com");
    private void CopyDeveloperCredit(object sender,RoutedEventArgs e)=>CopyContact("开发者 小李鱼 · 来信建议：kangxls@gmail.com");
    private void CopyContact(string text)
    {
        try{Clipboard.SetText(text);}
        catch(Exception error){app.Error("复制失败："+error.Message);}
    }
    private void OpenDeveloperEmail(object sender,RequestNavigateEventArgs e)
    {
        e.Handled=true;
        try{Process.Start(new ProcessStartInfo("mailto:kangxls@gmail.com"){UseShellExecute=true})?.Dispose();}
        catch(Exception error){app.Error("无法打开邮件应用，请检查系统默认邮件应用设置。邮箱可右键复制。\n"+error.Message);}
    }
}
