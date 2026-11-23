using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace AppKeeper;

internal sealed partial class MacWindow
{
    private async Task LauncherInputSmoke()
    {
        Navigate("快捷应用");Width=1000;Height=720;await Task.Delay(120);
        var root=Directory.CreateDirectory(Path.Combine(state.Root,"drop-regression")).FullName;
        var file=Path.Combine(root,"回归 文件 A.txt");File.WriteAllText(file,"original file");
        var folderPath=Directory.CreateDirectory(Path.Combine(root,"回归 文件夹")).FullName;
        var appPath=Directory.CreateDirectory(Path.Combine(root,"回归应用.app")).FullName;
        var fileItem=await StorageProvider.TryGetFileFromPathAsync(file)??throw new Exception("系统文件路径接口失败。");
        var folderItem=await StorageProvider.TryGetFolderFromPathAsync(folderPath)??throw new Exception("系统文件夹路径接口失败。");
        var appItem=await StorageProvider.TryGetFolderFromPathAsync(appPath)??throw new Exception("系统应用包路径接口失败。");
        using var external=new DataTransfer();foreach(var item in new IStorageItem[]{fileItem,folderItem,appItem})external.Add(DataTransferItem.Create(DataFormat.File,item));
        void Drop(Control target,IDataTransfer data,DragDropEffects expected,double x=5)
        {
            var over=new DragEventArgs(DragDrop.DragOverEvent,data,target,new Point(x,5),KeyModifiers.None){Source=target};target.RaiseEvent(over);
            if(over.DragEffects!=expected)throw new Exception("拖放接收效果未声明："+expected);
            var drop=new DragEventArgs(DragDrop.DropEvent,data,target,new Point(x,5),KeyModifiers.None){Source=target};target.RaiseEvent(drop);
            if(!drop.Handled)throw new Exception("拖放事件未处理。");
        }
        Drop(query,external,DragDropEffects.Copy);await Task.Delay(80);
        foreach(var path in new[]{file,folderPath,appPath})if(state.Data.Entries.Count(e=>e.Target==path)!=1)throw new Exception("批量拖入未立即创建唯一入口。");
        var tagButton=tags.Children.OfType<Button>().First(b=>b.Tag is LauncherTag t&&t.Name!=null);
        var targetTag=((LauncherTag)tagButton.Tag!).Name!;
        Drop(tagButton,external,DragDropEffects.Copy);await Task.Delay(80);
        foreach(var path in new[]{file,folderPath,appPath})if(!state.Data.Entries.Single(e=>e.Target==path).Tags.Contains(targetTag))throw new Exception("投放标签未关联。");
        var allButton=tags.Children.OfType<Button>().Single(b=>b.Tag is LauncherTag t&&t.Key=="all");
        tagButton=tags.Children.OfType<Button>().First(b=>b.Tag is LauncherTag t&&t.Name!=null);
        using(var allDrag=MacLauncher.Transfer("tag","all"))Drop(tagButton,allDrag,DragDropEffects.Move,tagButton.Bounds.Width*.95);
        if(MacLauncher.Tags(state).First().Key=="all")throw new Exception("全部标签仍被强制放在第一。");
        tag=targetTag;Apps();await Task.Delay(80);
        var onlyAll=Path.Combine(root,"仅全部.txt");File.WriteAllText(onlyAll,"all");var onlyAllItem=await StorageProvider.TryGetFileFromPathAsync(onlyAll)??throw new Exception("获取测试文件失败。");
        using(var payload=new DataTransfer())
        {
            payload.Add(DataTransferItem.Create(DataFormat.File,onlyAllItem));allButton=tags.Children.OfType<Button>().Single(b=>b.Tag is LauncherTag t&&t.Key=="all");Drop(allButton,payload,DragDropEffects.Copy);
        }
        if(state.Data.Entries.Single(e=>e.Target==onlyAll).Tags.Count!=0)throw new Exception("投放全部标签错误地关联当前标签。");
        tag=null;Apps();await Task.Delay(80);
        query.Text="回归";await Task.Delay(80);
        var source=state.Data.Entries.Single(e=>e.Target==file);var destination=state.Data.Entries.Single(e=>e.Target==folderPath);
        var target=tiles.Children.OfType<Button>().Single(b=>b.Tag is Entry item&&item.Id==destination.Id);
        using var drag=MacLauncher.Transfer("entry",source.Id.ToString());Drop(target,drag,DragDropEffects.Move,target.Bounds.Width*.95);
        if(state.Data.Entries.IndexOf(source)<=state.Data.Entries.IndexOf(destination))throw new Exception("图标右侧投放未更新排序。");
        MacLauncher.Pin(state,PinScope,destination.Id,true);MacLauncher.Pin(state,PinScope,source.Id,true);RenderTiles();
        if(tiles.Children.OfType<Button>().First().Tag is not Entry first||first.Id!=source.Id)throw new Exception("新置顶入口未显示在第一位。");
        MacLauncher.Pin(state,PinScope,source.Id,false);MacLauncher.Pin(state,PinScope,destination.Id,false);query.Clear();page=0;RenderTiles();await Task.Delay(80);
        if(VisibleEntries().Length<=pageSize)throw new Exception("翻页验收需要多页入口。");
        using var pointer=new Pointer(Pointer.GetNextFreeId(),PointerType.Mouse,true);
        void Wheel(double y)=>gridViewport.RaiseEvent(new PointerWheelEventArgs(gridViewport,pointer,this,new Point(200,300),0,new PointerPointProperties(),KeyModifiers.None,new Vector(0,y)));
        gesture.Reset();Wheel(-1);for(var n=0;n<6;n++)Wheel(-1);
        if(page!=1)throw new Exception("双指滚动惯性导致漏翻页或多翻页。");
        await Task.Delay(300);Wheel(1);if(page!=0)throw new Exception("反向手势未返回上一页。");
        var restored=new MacState(state.Root);if(restored.Data.Entries.Count(e=>e.Target==file)!=1)throw new Exception("投放入口重启后丢失。");
        Console.WriteLine("PASS: routed file/folder/app drops, automatic folder names, batch/dedup/tag assignment, movable All tab, tile sorting, newest pin and wheel pagination/inertia");
    }
}
