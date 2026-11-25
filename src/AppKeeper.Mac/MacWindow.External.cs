using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AppKeeper;

internal sealed partial class MacWindow
{
    private readonly DispatcherTimer entryTimer=new(){Interval=TimeSpan.FromMilliseconds(600)};
    private bool addingEntries;
    internal async Task ApplyFinderIntegration()
    {
        if(MacPlatform.TestRoot!=null)return;
        MacFinderIntegration.Apply(Environment.ProcessPath!,state.Settings.FinderIntegration);
        await MacPlatform.Native("--services-update");
    }
    private void StartEntryInbox()
    {
        entryTimer.Tick+=(_,_)=>_=Safe(async()=>{ProcessActivationRequest();await DrainExternalEntries();});entryTimer.Start();
    }
    internal void ProcessActivationRequest(){if(!FinalClose&&MacStartup.ConsumeActivation(state.Root))Reveal(true);}
    private async Task DrainExternalEntries()
    {
        if(addingEntries||FinalClose)return;
        var inbox=new EntryInbox(state.Root);
        try
        {
            var batch=inbox.Read();if(batch.Files.Length==0)return;
            if(batch.Files.Any(file=>File.GetLastWriteTimeUtc(file)>DateTime.UtcNow.AddMilliseconds(-500)))return;
            addingEntries=true;
            var paths=batch.Paths.Where(path=>File.Exists(path)||Directory.Exists(path)).ToArray();
            Reveal(true);
            if(paths.Length==0){await Alert("所选文件或文件夹已不存在，未创建入口。");inbox.Complete(batch);return;}
            var tags=new ComboBox{ItemsSource=new[]{"全部（不关联标签）"}.Concat(state.Data.Tags).ToArray(),SelectedIndex=0};
            var newTag=new TextBox{Watermark="新建标签（可选，最多 24 字）",MaxLength=24};
            var list=new ListBox{ItemsSource=paths.Select(path=>Path.GetFileName(Path.TrimEndingDirectorySeparator(path))).ToArray(),MaxHeight=180};
            var fields=new Control[]{T($"添加 {paths.Length} 个入口",20),list,T("添加到标签",12),tags,newTag,T("仅创建快捷入口，原文件保留原位；已有入口自动关联标签。",12)};
            var confirmed=await Dialog("添加到 EasyAppKeeper",fields);
            if(FinalClose)return; // System quit closes the dialog; it is not a user's cancellation.
            if(!confirmed){inbox.Complete(batch);return;}
            var value=newTag.Text?.Trim()??"";
            if(value.Length>0&&!Layout.IsTagName(value))throw new ArgumentException("标签名称无效，请重试。待处理请求仍保留。");
            var destination=value.Length>0?state.Data.Tags.FirstOrDefault(t=>t.Equals(value,StringComparison.OrdinalIgnoreCase))??value:tags.SelectedIndex>0?state.Data.Tags[tags.SelectedIndex-1]:null;
            if(destination!=null&&!state.Data.Tags.Contains(destination))state.Data.Tags.Add(destination);
            foreach(var path in paths)state.Add(path,destination,save:false);
            try
            {
                var json=await MacPlatform.Native(new[]{"--icons"}.Concat(paths).ToArray());
                foreach(var icon in System.Text.Json.JsonSerializer.Deserialize<ScanEntry[]>(json)??[])
                {var entry=state.Data.Entries.Find(e=>e.Target==icon.Path);if(entry!=null)entry.Icon=icon.Icon;}
            }
            catch(Exception error){System.Diagnostics.Trace.TraceWarning("入口已创建，部分图标暂未读取："+error.Message);}
            state.Save();inbox.Complete(batch);Navigate("快捷应用");tag=destination;query.Clear();Apps();Status($"已添加或关联 {paths.Length} 个入口");
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException){entryTimer.Stop();throw new IOException("添加入口失败："+e.Message+" 待处理请求已保留，修正后重新启动即可重试。",e);}
        finally{addingEntries=false;}
    }
}
