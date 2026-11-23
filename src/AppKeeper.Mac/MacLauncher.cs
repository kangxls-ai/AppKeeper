using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace AppKeeper;

internal static class MacLauncher
{
    internal sealed record TagSlot(string Key,string? Name){internal string Label=>Name??"全部";}
    internal static TagSlot[] Tags(MacState state)
    {
        var available=new[]{new TagSlot("all",null)}.Concat(state.Data.Tags.Select(n=>new TagSlot("tag:"+n,n))).ToDictionary(t=>t.Key);
        var keys=state.Settings.LauncherTagOrder.Where(available.ContainsKey).Distinct().ToList();keys.AddRange(available.Keys.Where(k=>!keys.Contains(k)));
        state.Settings.LauncherTagOrder=keys;return keys.Select(k=>available[k]).ToArray();
    }
    internal static void MoveTag(MacState state,string source,string target,bool after)
    {
        _=Tags(state);var order=state.Settings.LauncherTagOrder;
        if(source==target||!order.Contains(source)||!order.Contains(target))return;
        order.Remove(source);order.Insert(order.IndexOf(target)+(after?1:0),source);
        state.Data.Tags=Tags(state).Where(t=>t.Name!=null).Select(t=>t.Name!).ToList();state.Save();
    }
    internal static readonly DataFormat<string> EntryFormat=DataFormat.CreateStringApplicationFormat("io.kangxls.easyappkeeper.entry");
    internal static readonly DataFormat<string> TagFormat=DataFormat.CreateStringApplicationFormat("io.kangxls.easyappkeeper.tag");
    internal static string Scope(string? tag,Guid? folder)=>tag!=null?"tag:"+tag:folder.HasValue?"folder:"+folder.Value:"all";
    internal static List<Guid> Pins(MacState state,string scope)
    {
        if(!state.Settings.LauncherPins.TryGetValue(scope,out var pins))state.Settings.LauncherPins[scope]=pins=[];
        return pins;
    }
    internal static Entry[] Ordered(MacState state,IEnumerable<Entry> source,string scope)
    {
        var pins=Pins(state,scope);var positions=pins.Select((id,index)=>(id,index)).GroupBy(p=>p.id).ToDictionary(g=>g.Key,g=>g.First().index);
        return source.OrderBy(e=>positions.GetValueOrDefault(e.Id,int.MaxValue)).ToArray();
    }
    internal static void Pin(MacState state,string scope,Guid id,bool pinned)
    {
        var pins=Pins(state,scope);pins.RemoveAll(value=>value==id);if(pinned)pins.Insert(0,id);state.Save();
    }
    internal static DataTransfer Transfer(string kind,string value)
    {
        var transfer=new DataTransfer();transfer.Add(DataTransferItem.Create(kind=="entry"?EntryFormat:TagFormat,value));return transfer;
    }
    internal static DragDropEffects Effect(IDataTransfer data)=>data.Contains(EntryFormat)||data.Contains(TagFormat)?DragDropEffects.Move:data.Contains(DataFormat.File)?DragDropEffects.Copy:DragDropEffects.None;
    internal static string[] Paths(IDataTransfer data)=>(data.TryGetFiles()??[]).Select(f=>f.TryGetLocalPath()).Where(p=>p!=null)
        .Select(p=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(p!))).Distinct(StringComparer.Ordinal).ToArray();
    internal static int Capacity(double width,double height)=>Math.Clamp((int)(width/121),1,12)*Math.Clamp((int)(height/112),1,7);
}

internal sealed class MacPageGesture
{
    private long last;
    private double accumulated;
    private bool flipped;
    internal int Update(double x,double y,long now)
    {
        if(now-last>250){accumulated=0;flipped=false;}
        last=now;
        if(flipped||Math.Abs(y)<Math.Abs(x)||y==0)return 0;
        accumulated+=y;
        if(Math.Abs(accumulated)<1)return 0;
        flipped=true;return accumulated<0?1:-1;
    }
    internal void Reset(){last=0;accumulated=0;flipped=false;}
}
