namespace AppKeeper;

internal static class MacLauncherSelfTest
{
    internal static void Run(string root)
    {
        var state=new MacState(Path.Combine(root,"launcher"));
        var file=Path.Combine(root,"中文 空格.txt");File.WriteAllText(file,"keep original");var folder=Directory.CreateDirectory(Path.Combine(root,"我的.资料")).FullName;var app=Directory.CreateDirectory(Path.Combine(root,"示例.app")).FullName;
        state.Data.Tags.AddRange(["工作","生活"]);
        foreach(var path in new[]{file,folder+Path.DirectorySeparatorChar,app+Path.DirectorySeparatorChar})state.Add(path,"工作");
        state.Add(file,"生活");
        Check(state.Data.Entries.Count==3,"repeated Finder drop is deduplicated");
        Check(state.Data.Entries.Single(e=>e.Target==app).Kind==EntryKind.File&&state.Data.Entries.Single(e=>e.Target==folder).Kind==EntryKind.Directory,"app bundles and folders with trailing separators classified correctly");
        Check(state.Data.Entries.Single(e=>e.Target==folder).Name=="我的.资料","folder name retains its dot suffix");
        var slots=MacLauncher.Tags(state);MacLauncher.MoveTag(state,"all","tag:工作",true);
        Check(MacLauncher.Tags(state).Select(t=>t.Key).SequenceEqual(["tag:工作","all","tag:生活"]),"All is movable instead of forced first");
        MacLauncher.MoveTag(state,"tag:生活","tag:工作",false);
        Check(MacLauncher.Tags(new MacState(state.Root)).Select(t=>t.Key).SequenceEqual(["tag:生活","tag:工作","all"]),"all tag positions persist through restart");
        var entries=state.Data.Entries.ToArray();var all=MacLauncher.Scope(null,null);var work=MacLauncher.Scope("工作",null);
        MacLauncher.Pin(state,all,entries[0].Id,true);MacLauncher.Pin(state,all,entries[2].Id,true);
        Check(MacLauncher.Ordered(state,entries,all).Select(e=>e.Id).SequenceEqual([entries[2].Id,entries[0].Id,entries[1].Id]),"newest pin is first");
        MacLauncher.Pin(state,work,entries[1].Id,true);MacLauncher.Pin(state,all,entries[0].Id,true);
        Check(MacLauncher.Ordered(state,entries,all)[0].Id==entries[0].Id&&MacLauncher.Ordered(state,entries,work)[0].Id==entries[1].Id,"repin and independent tag pin order");
        var restored=new MacState(state.Root);Check(MacLauncher.Ordered(restored,restored.Data.Entries,all)[0].Id==entries[0].Id,"pins survive restart");
        MacLauncher.Pin(state,all,entries[0].Id,false);Check(MacLauncher.Ordered(state,entries,all)[0].Id==entries[2].Id,"unpin keeps other pins");
        using var payload=MacLauncher.Transfer("entry",entries[0].Id.ToString());Check(MacLauncher.Effect(payload)==Avalonia.Input.DragDropEffects.Move,"internal drag advertises move");
        var gesture=new MacPageGesture();Check(gesture.Update(0,-.4,1000)==0&&gesture.Update(0,-.7,1010)==1,"small touchpad deltas accumulate to one page");
        for(var n=1;n<=20;n++)Check(gesture.Update(0,-3,1010+n*30)==0,"inertia cannot skip multiple pages");
        Check(gesture.Update(0,1,2000)==-1,"next separate gesture may go back");
        gesture.Reset();Check(gesture.Update(3,.2,3000)==0,"horizontal gestures ignored");
        Check(MacLauncher.Capacity(850,350)==21&&MacLauncher.Capacity(190,180)==1,"page capacity fits the visible grid");
        Check(File.ReadAllText(file)=="keep original","creating entries does not change source file");
        var unnamed=state.Data.Entries.Single(e=>e.Target==folder);unnamed.Name="";unnamed.Target+=Path.DirectorySeparatorChar;
        ModuleFiles.Write(state.Store.DataPath,state.Data);
        var repaired=new MacState(state.Root);
        Check(repaired.Data.Entries.Count==3&&repaired.Data.Entries.Single(e=>e.Target==folder).Name=="我的.资料","upgrade repairs unnamed directory without rolling back other entries");
        Check(Directory.GetFiles(state.Root,"AppKeeper.data.json.before-name-repair-*").Length==1,"legacy source is preserved before name repair");
    }
    private static void Check(bool value,string label){if(!value)throw new Exception("Launcher regression: "+label);}
}
