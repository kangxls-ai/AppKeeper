using System.Diagnostics;
using System.Text.Json;

namespace AppKeeper;

internal sealed partial class MacWindow
{
    private async Task DockSmoke()
    {
        using var policy=JsonDocument.Parse(await MacPlatform.Native("--helper-policy"));var helper=policy.RootElement;
        if(helper.GetProperty("Identifier").GetString()!="io.kangxls.EasyAppKeeper.helper"||!helper.GetProperty("BackgroundOnly").GetBoolean()||!helper.GetProperty("Agent").GetBoolean()||helper.GetProperty("Policy").GetInt32()!=2)throw new Exception("原生辅助程序没有独立的后台身份。");
        if(MacStatusBar.DockRegularCount()!=1)throw new Exception("程序包存在多个 Dock 可见进程。");
        for(var i=0;i<3;i++)
        {
            Navigate("日程管理");Hide();
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("--secondary-startup-smoke");start.ArgumentList.Add(state.Root);
            using var process=Process.Start(start)!;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await process.WaitForExitAsync(timeout.Token);
            if(process.ExitCode!=0)throw new Exception("重复启动没有在创建界面前退出："+await process.StandardError.ReadToEndAsync());
            ProcessActivationRequest();await Task.Delay(100);
            if(!IsVisible||section!="快捷应用"||MacStatusBar.DockRegularCount()!=1)throw new Exception("重复启动未唤起唯一主窗口或生成了额外 Dock 进程。");
        }
        Console.WriteLine("PASS: embedded helper has background-only identity and prohibited activation; three real secondary launches exit before UI initialization and reveal the single Dock-visible main process");
    }
}
