namespace AppKeeper;

public sealed partial class MacApp
{
    private bool shuttingDown;
    private void PrepareShutdown()
    {
        if(shuttingDown)return;
        shuttingDown=true;
        // These flags must be set before Avalonia closes windows, not in its later Exit event.
        ball?.PrepareShutdown();window?.PrepareShutdown();
    }
    private static void Cleanup(Action action)
    {
        try{action();}catch(Exception error){System.Diagnostics.Trace.TraceWarning("退出清理："+error.Message);}
    }
    private void CleanupServices()
    {
        Cleanup(()=>{if(hotkey!=null){if(!hotkey.HasExited)hotkey.Kill();hotkey.Dispose();}});
        Cleanup(()=>statusBar?.Dispose());Cleanup(()=>pigeon?.Dispose());
        // Do not hold the AppKit termination callback indefinitely for an active HTTP sync request.
        var stop=Task.Run(()=>Cleanup(()=>sync?.Dispose()));
        if(!stop.Wait(TimeSpan.FromSeconds(1)))System.Diagnostics.Trace.TraceWarning("手机同步停止仍在后台完成，继续退出。");
        Cleanup(()=>instance?.Dispose());
    }
}

internal sealed partial class MacWindow
{
    internal void PrepareShutdown()
    {
        FinalClose=true;poll.Stop();entryTimer.Stop();pigeon.SetReadingPeer(null);
        try{state.Save();}
        catch(Exception error){System.Diagnostics.Trace.TraceWarning("退出时配置保存失败，保留上次有效文件："+error.Message);}
    }
}
