using System;
using System.Windows.Controls;

namespace AppKeeper;

public partial class MainWindow
{
    private string PinScope=>LauncherLayout.Scope(selectedTag,openFolder,inbox);
    private void AddLauncherPinActions(ContextMenu menu,Entry entry,string scope)
    {
        var pinned=LauncherLayout.Pins(app.Data,scope).Contains(entry.Id);
        AddItem(menu,pinned?"再次置顶（排第一）":"置顶入口",()=>
        {
            page=0;app.Commit(data=>LauncherLayout.Pin(data,scope,entry.Id,true));
        });
        if(pinned)AddItem(menu,"取消置顶",()=>app.Commit(data=>LauncherLayout.Pin(data,scope,entry.Id,false)));
    }
}
