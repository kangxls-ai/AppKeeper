using System;
using System.IO;
using System.Linq;

namespace AppKeeper;

internal static class LauncherLayoutSelfTest
{
    internal static void Run(string root)
    {
        var library=new Library{Tags=["工作","生活","全部"]};
        var entries=Enumerable.Range(0,3).Select(n=>new Entry{Name="入口 "+n,Kind=EntryKind.Web,Target="https://example.com/"+n,Tags=["工作","生活"]}).ToArray();
        library.Entries.AddRange(entries);Layout.EnsureTagOrders(library);
        LauncherLayout.MoveTab(library,"all","tag:工作",true);
        Check(LauncherLayout.Tabs(library).Select(t=>t.Key).SequenceEqual(["tag:工作","all","tag:生活","tag:全部"]),"All is movable and distinct from a real tag named All");
        LauncherLayout.MoveTab(library,"tag:生活","tag:工作",false);
        var all=LauncherLayout.Scope(null);var work=LauncherLayout.Scope("工作");var life=LauncherLayout.Scope("生活");
        Guid[] Order(string scope)=>LauncherLayout.Ordered(library,Layout.Visible(library,false,"",scope==work?"工作":scope==life?"生活":null),scope).Select(e=>e.Id).ToArray();
        var ordinary=Order(work);
        LauncherLayout.Pin(library,work,entries[1].Id,true);LauncherLayout.Pin(library,work,entries[2].Id,true);
        Check(Order(work).SequenceEqual([entries[2].Id,entries[1].Id,entries[0].Id])&&Order(all).SequenceEqual(ordinary)&&Order(life).SequenceEqual(ordinary),"newest pin first, tag pins independent from All and other tags");
        LauncherLayout.Pin(library,all,entries[0].Id,true);LauncherLayout.Pin(library,work,entries[1].Id,true);
        Check(Order(work)[0]==entries[1].Id&&Order(all)[0]==entries[0].Id,"repin moves to first only in its scope");
        LauncherLayout.MovePin(library,work,entries[2].Id,entries[1].Id,false);
        Check(Order(work)[0]==entries[2].Id,"pinned entries can be dragged within pinned region");
        LauncherLayout.Pin(library,work,entries[1].Id,false);LauncherLayout.Pin(library,work,entries[2].Id,false);
        Check(Order(work).SequenceEqual(ordinary)&&library.TagOrders["工作"].SequenceEqual(ordinary),"unpin restores ordinary order without rewriting it");
        LauncherLayout.Pin(library,work,entries[2].Id,true);
        var store=new LibraryStore(Directory.CreateDirectory(Path.Combine(root,"launcher-layout")).FullName);store.Save(library);var restored=store.Load(out _);
        Check(LauncherLayout.Tabs(restored).Select(t=>t.Key).SequenceEqual(["tag:生活","tag:工作","all","tag:全部"])&&LauncherLayout.Ordered(restored,restored.Entries,work)[0].Id==entries[2].Id,"tab positions and scoped pins survive reload");
        var copied=LibraryStore.Copy(restored);Layout.Remove(copied,copied.Entries.Single(e=>e.Id==entries[2].Id));
        Check(!LauncherLayout.Pins(copied,work).Contains(entries[2].Id)&&LauncherLayout.Pins(restored,work).Contains(entries[2].Id),"delete cleans pins and rollback copy is independent");
        Layout.RemoveTag(copied,"工作");Check(!copied.LauncherPins.ContainsKey(work)&&!copied.LauncherTagOrder.Contains(work),"tag deletion clears its order and pins");
        Check(library.PinnedEntries.Count==0&&library.RecentEntries.Count==0,"launcher pins do not modify recent-history locks");
    }
    private static void Check(bool value,string message){if(!value)throw new Exception("Launcher layout: "+message);}
}
