using Avalonia.Controls.ApplicationLifetimes;
using System.Diagnostics;

namespace AppKeeper;

public sealed partial class MacApp
{
    private async Task ShutdownSmoke(IClassicDesktopStyleApplicationLifetime desktop,MacState state)
    {
        var mode=desktop.Args!.SkipWhile(arg=>arg!="--shutdown-smoke").Skip(1).FirstOrDefault()??"visible";
        var main=window!;var floating=ball!;var closed=false;main.Closed+=(_,_)=>closed=true;
        main.Show();main.Close();
        if(closed||main.IsVisible||!desktop.Windows.Contains(main))throw new Exception("普通关闭没有保持隐藏到后台。");
        var ballClosed=false;floating.Closed+=(_,_)=>ballClosed=true;floating.Close();
        if(ballClosed||floating.IsVisible)throw new Exception("浮球普通关闭错误地结束了窗口。");
        floating.Show();main.Reveal(true);main.Width=824;main.Height=612;await Task.Delay(100);
        var expected=state.Data.WindowSize;
        if(mode=="hidden")main.Hide();
        if(mode=="dialog")
        {
            var file=Path.Combine(state.Root,"关机时尚未确认添加.txt");File.WriteAllText(file,"unchanged");
            await main.OpenPendingForShutdownSmoke(file);
        }
        if(mode=="storage-error")
        {
            File.Copy(state.Store.DataPath,state.Store.DataPath+".before-exit-test",true);
            File.Delete(state.Store.DataPath);Directory.CreateDirectory(state.Store.DataPath);
        }
        var exitCount=0;desktop.Exit+=(_,_)=>exitCount++;
        var watch=Stopwatch.StartNew();
        // ApplicationShouldTerminate is macOS's app-level quit callback. No system shutdown is issued.
        var accepted=mode=="explicit"?(desktop.TryShutdown()?1:0):MacStatusBar.TestTermination();
        if(accepted!=1||exitCount!=1||!closed||!ballClosed)throw new Exception("应用拒绝或未完成退出："+mode);
        if(watch.Elapsed>TimeSpan.FromSeconds(5))throw new Exception("退出清理阻塞过久："+watch.Elapsed);
        if(mode=="storage-error")
        {
            if(!File.Exists(state.Store.DataPath+".before-exit-test"))throw new Exception("保存异常时未保留上次配置。");
        }
        else if(LibraryStore.Read(state.Store.DataPath).WindowSize!=expected)throw new Exception("退出前窗口尺寸未保存。");
        if(mode=="dialog"&&new EntryInbox(state.Root).Read().Files.Length!=1)throw new Exception("系统退出被误认为用户取消，丢失待处理入口。");
        using(var released=new FileStream(Path.Combine(state.Root,"instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){ }
        Console.WriteLine($"PASS: {mode} quit accepted by native/application lifetime; main and floating windows closed, normal close still hides, persistence/queue retained, lock released in {watch.ElapsedMilliseconds}ms");
    }
}

internal sealed partial class MacWindow
{
    internal async Task OpenPendingForShutdownSmoke(string file)
    {
        new EntryInbox(state.Root).Enqueue([file]);await Task.Delay(550);
        _=DrainExternalEntries();await Task.Delay(100);
        if(!OwnedWindows.Any(window=>window.Title=="添加到 EasyAppKeeper"))throw new Exception("退出验收所需的添加窗口未创建。");
    }
}
