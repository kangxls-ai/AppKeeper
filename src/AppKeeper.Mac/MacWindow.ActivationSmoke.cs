using Avalonia.Controls;

namespace AppKeeper;

internal sealed partial class MacWindow
{
    private async Task ActivationSmoke()
    {
        Navigate("日程管理");Hide();
        if(MacStatusBar.TestDockReopen()!=1)throw new Exception("未接入原生 Dock reopen 委托。");
        await Task.Delay(150);Check("hidden Dock reopen");
        Navigate("飞鸽传书");WindowState=WindowState.Minimized;await Task.Delay(100);
        if(MacStatusBar.TestDockReopen()!=1)throw new Exception("Dock 最小化恢复事件未触发。");
        await Task.Delay(150);Check("minimized Dock reopen");
        Navigate("日程管理");
        if(MacStatusBar.TestDockReopen()!=1)throw new Exception("Dock 已显示时的 reopen 未触发。");
        await Task.Delay(150);Check("visible Dock reopen");
        Navigate("最近访问");Hide();
        if(MacStatusBar.TestClick()!=1)throw new Exception("原生菜单栏按钮或右键菜单未配置。");
        await Task.Delay(150);Check("native menu-bar click");
        Console.WriteLine("PASS: native Dock delegate reopens hidden/minimized/visible main panel in Apps; native status-item target/action opens Apps and retains context commands");
        void Check(string action)
        {
            if(!IsVisible||WindowState==WindowState.Minimized||section!="快捷应用")throw new Exception(action+" 未恢复快捷应用主页。");
        }
    }
}
