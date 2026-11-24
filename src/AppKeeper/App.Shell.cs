using System;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace AppKeeper;

public partial class App
{
    private DispatcherTimer? entryTimer;
    private bool addingEntries;
    private EntryInbox Inbox=>new(profile.DataDirectory);
    private void ApplyShellMenu()
    {
        try{ShellMenu.Apply(profile,data.ShellIntegration);}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){notice="右键菜单设置失败："+e.Message;}
    }
    private void StartEntryInbox()
    {
        entryTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(600)};
        entryTimer.Tick+=(_,_)=>DrainEntries();entryTimer.Start();
    }
    private void DrainEntries()
    {
        if(addingEntries||!writable)return;
        try
        {
            var batch=Inbox.Read();if(batch.Files.Length==0)return;
            if(batch.Files.Any(file=>File.GetLastWriteTimeUtc(file)>DateTime.UtcNow.AddMilliseconds(-500)))return;
            addingEntries=true;
            var valid=batch.Paths.Where(path=>File.Exists(path)||Directory.Exists(path)).ToArray();
            GetLauncher().RevealHome();
            if(valid.Length==0){Error("所选文件或文件夹已不存在，未创建入口。");Inbox.Complete(batch);return;}
            var choice=Dialogs.ExternalEntries(GetLauncher(),data,valid,batch.Paths.Length-valid.Length);
            if(choice==null){Inbox.Complete(batch);return;}
            var entries=valid.Select(WindowsCatalog.FromPath).ToArray();
            if(Commit(library=>
            {
                if(choice.Length>0&&!library.Tags.Contains(choice,StringComparer.OrdinalIgnoreCase))library.Tags.Add(choice);
                Layout.AddEntries(library,entries,null,choice.Length==0?null:choice);
            }))
            {
                Inbox.Complete(batch);GetLauncher().ShowExternalEntries(choice.Length==0?null:choice);
                notice=$"已添加或关联 {entries.Length} 个入口。";launcher?.RefreshStatus();
            }
            else entryTimer?.Stop();
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException){Error("添加入口失败，待处理请求已保留："+e.Message);entryTimer?.Stop();}
        finally{addingEntries=false;}
    }
}
