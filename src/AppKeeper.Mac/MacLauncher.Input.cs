using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AppKeeper;

internal sealed partial class MacWindow
{
    private bool dragging;
    private readonly MacPageGesture gesture=new();
    private readonly ScrollViewer gridViewport=new(){Background=Brushes.Transparent,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto};
    private string PinScope=>MacLauncher.Scope(tag,folder);
    private void ConfigureLauncherInput()
    {
        DragDrop.SetAllowDrop(host,true);
        host.AddHandler(DragDrop.DragOverEvent,(_,e)=>
        {
            if(section!="快捷应用")return;
            e.DragEffects=MacLauncher.Effect(e.DataTransfer);e.Handled=true;
            if(e.DragEffects!=DragDropEffects.None)Status(e.DataTransfer.Contains(DataFormat.File)?"松开创建入口":"左右排序 / 中央分组");
        },RoutingStrategies.Bubble,true);
        // Drop is a bubble-only event in Avalonia 11. Observe handled events as well, so text fields
        // and tag buttons cannot swallow a native Finder payload before the page receives it.
        host.AddHandler(DragDrop.DropEvent,(_,e)=>
        {
            if(section!="快捷应用"||!e.DataTransfer.Contains(DataFormat.File)||e.DataTransfer.Contains(MacLauncher.EntryFormat)||e.DataTransfer.Contains(MacLauncher.TagFormat))return;
            var target=e.Source as Avalonia.Visual;var tagTarget=target?.GetSelfAndVisualAncestors().OfType<Control>().Select(c=>c.Tag).OfType<LauncherTag>().FirstOrDefault();var destination=tagTarget!=null?tagTarget.Name:tag;
            e.Handled=true;e.DragEffects=DragDropEffects.Copy;
            Run(()=>{var paths=MacLauncher.Paths(e.DataTransfer);if(paths.Length==0)throw new IOException("未能读取访达拖入的本机路径。");foreach(var path in paths)state.Add(path,destination);tag=destination;folder=null;query.Clear();page=0;Apps();Status($"已添加或关联 {paths.Length} 个入口");});
        },RoutingStrategies.Bubble,true);
        host.AddHandler(DragDrop.DropEvent,TileBlankDrop);
        tiles.Background=Brushes.Transparent;
        gridViewport.AddHandler(PointerWheelChangedEvent,(_,e)=>
        {
            if(section!="快捷应用"||e.KeyModifiers!=KeyModifiers.None)return;
            var delta=gesture.Update(e.Delta.X,e.Delta.Y,Environment.TickCount64);
            if(Math.Abs(e.Delta.X)<=Math.Abs(e.Delta.Y))return;
            e.Handled=true;
            if(delta!=0){page+=delta;RenderTiles();}
        },RoutingStrategies.Tunnel,true);
    }
    private sealed record LauncherTag(string Key,string? Name);
    private void EnableDrag(Control control,string format,string value)
    {
        PointerPressedEventArgs? press=null;Point origin=default;
        control.AddHandler(PointerPressedEvent,(_,e)=>{if(e.GetCurrentPoint(control).Properties.IsLeftButtonPressed){press=e;origin=e.GetPosition(control);}},RoutingStrategies.Tunnel,true);
        control.AddHandler(PointerMovedEvent,async(_,e)=>
        {
            if(press==null||dragging||!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)return;
            var point=e.GetPosition(control);if(Math.Abs(point.X-origin.X)+Math.Abs(point.Y-origin.Y)<8)return;
            var trigger=press;press=null;dragging=true;e.Pointer.Capture(null);e.Handled=true;
            try{await DragDrop.DoDragDropAsync(trigger,MacLauncher.Transfer(format,value),DragDropEffects.Move);}
            catch(Exception error){await Alert(error.Message);}
            finally{dragging=false;Status("");}
        },RoutingStrategies.Tunnel,true);
        control.AddHandler(PointerReleasedEvent,(_,e)=>{press=null;if(dragging)e.Handled=true;},RoutingStrategies.Tunnel,true);
    }
    private Entry? Dragged(DragEventArgs e)=>Guid.TryParse(e.DataTransfer.TryGetValue(MacLauncher.EntryFormat),out var id)?state.Data.Entries.Find(x=>x.Id==id):null;
    private void TileBlankDrop(object? sender,DragEventArgs e)
    {
        if(e.Handled||section!="快捷应用")return;var source=Dragged(e);if(source==null)return;
        e.Handled=true;Run(()=>{if(tag!=null)Layout.OrderInTag(state.Data,tag,source,null,true);else Layout.MoveHome(state.Data,source);state.Save();RenderTiles();});
    }
    private void TileDrop(Button button,Entry target,DragEventArgs e)
    {
        if(e.Handled||Dragged(e) is not {} source)return;e.Handled=true;
        Run(()=>
        {
            var x=e.GetPosition(button).X/button.Bounds.Width;
            var pins=MacLauncher.Pins(state,PinScope);
            if(pins.Contains(source.Id)&&pins.Contains(target.Id)&&source.Id!=target.Id){pins.Remove(source.Id);pins.Insert(pins.IndexOf(target.Id)+(x>.5?1:0),source.Id);}
            else if(tag!=null)Layout.OrderInTag(state.Data,tag,source,target,x>.5);
            else Layout.Move(state.Data,source,target,Layout.DropAt(source,target,x,folder.HasValue));
            state.Save();RenderTiles();
        });
    }
}
