using System;
using System.Collections.Generic;
using System.Linq;

namespace AppKeeper;

internal static class LauncherLayout
{
    internal sealed record Tab(string Key,string? Name){internal string Label=>Name??"全部";}
    internal static Tab[] Tabs(Library library)
    {
        var available=new[]{new Tab("all",null)}.Concat(library.Tags.Select(name=>new Tab("tag:"+name,name))).ToDictionary(t=>t.Key,StringComparer.OrdinalIgnoreCase);
        var keys=(library.LauncherTagOrder??[]).Where(key=>key!=null&&available.ContainsKey(key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        keys.AddRange(available.Keys.Where(key=>!keys.Contains(key,StringComparer.OrdinalIgnoreCase)));
        library.LauncherTagOrder=keys;return keys.Select(key=>available[key]).ToArray();
    }
    internal static void MoveTab(Library library,string source,string target,bool after)
    {
        _=Tabs(library);var order=library.LauncherTagOrder;var from=order.FindIndex(key=>key.Equals(source,StringComparison.OrdinalIgnoreCase));var to=order.FindIndex(key=>key.Equals(target,StringComparison.OrdinalIgnoreCase));
        if(from<0||to<0||from==to)return;
        var key=order[from];order.RemoveAt(from);to=order.FindIndex(value=>value.Equals(target,StringComparison.OrdinalIgnoreCase));order.Insert(to+(after?1:0),key);
        library.Tags=Tabs(library).Where(t=>t.Name!=null).Select(t=>t.Name!).ToList();
    }
    internal static string Scope(string? tag,Guid? folder=null,bool inbox=false)=>folder.HasValue?"folder:"+folder:tag!=null?"tag:"+tag:inbox?"inbox":"all";
    internal static List<Guid> Pins(Library library,string scope)
    {
        library.LauncherPins??=new(StringComparer.OrdinalIgnoreCase);
        if(!library.LauncherPins.TryGetValue(scope,out var pins)||pins==null)library.LauncherPins[scope]=pins=[];
        return pins;
    }
    internal static Entry[] Ordered(Library library,IEnumerable<Entry> source,string scope)
    {
        var positions=Pins(library,scope).Distinct().Select((id,index)=>(id,index)).ToDictionary(p=>p.id,p=>p.index);
        return source.OrderBy(entry=>positions.GetValueOrDefault(entry.Id,int.MaxValue)).ToArray();
    }
    internal static void Pin(Library library,string scope,Guid id,bool enabled)
    {var pins=Pins(library,scope);pins.RemoveAll(value=>value==id);if(enabled)pins.Insert(0,id);}
    internal static bool MovePin(Library library,string scope,Guid source,Guid target,bool after)
    {
        var pins=Pins(library,scope);if(source==target||!pins.Contains(source)||!pins.Contains(target))return false;
        pins.Remove(source);pins.Insert(pins.IndexOf(target)+(after?1:0),source);return true;
    }
    internal static void Normalize(Library library)
    {
        _=Tabs(library);var ids=library.Entries.Select(e=>e.Id).ToHashSet();
        library.LauncherPins=(library.LauncherPins??[]).Where(pair=>pair.Key!=null).GroupBy(pair=>pair.Key,StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.SelectMany(pair=>pair.Value??[]).Where(ids.Contains).Distinct().ToList(),StringComparer.OrdinalIgnoreCase);
    }
}
