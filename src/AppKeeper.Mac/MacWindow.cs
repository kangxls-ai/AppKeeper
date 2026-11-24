using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using QRCoder;
using System.Text.Json;

namespace AppKeeper;

internal sealed partial class MacWindow : Window
{
    private readonly MacState state;
    private readonly PigeonService pigeon;
    private readonly AgendaStore agenda;
    private readonly AgendaLanSync sync;
    private readonly ContentControl host=new();
    private readonly TextBlock notice=new(){TextWrapping=TextWrapping.Wrap,FontSize=12,MinHeight=18};
    private readonly Dictionary<string,Button> navigation=[];
    private readonly TextBox query=new(){Watermark="搜索应用、拼音或首字母",MinHeight=40,MaxLength=256};
    private readonly WrapPanel tags=new(){Orientation=Orientation.Horizontal};
    private readonly WrapPanel tiles=new(){Orientation=Orientation.Horizontal};
    private readonly StackPanel recentBar=new(){Orientation=Orientation.Horizontal,Spacing=6};
    private readonly StackPanel searchBar=new(){Orientation=Orientation.Horizontal,Spacing=5};
    private readonly TextBlock pageNumber=new();
    private Button? backGroup;
    private readonly Dictionary<string,string> icons=[];
    private string section="快捷应用";
    private string? tag;
    private Guid? folder;
    private int page;
    private string? peer;
    private const int pageSize=MacLauncher.PageSize;
    private readonly DispatcherTimer poll=new(){Interval=TimeSpan.FromMilliseconds(750)};
    private string chatStamp="";
    private StackPanel? peerList;
    private StackPanel? chatMessages;
    private TextBox? composer;
    private ContentControl? chatHost;
    private bool refreshing;
    private bool compact;
    internal bool FinalClose;
    internal event Action? PreferencesSaved;
    private IBrush Soft => Application.Current?.ActualThemeVariant==Avalonia.Styling.ThemeVariant.Dark ? new SolidColorBrush(Color.Parse("#28364c")) : new SolidColorBrush(Color.Parse("#e9eff8"));
    private IBrush Accent => new SolidColorBrush(Color.Parse("#477ec7"));
    internal MacWindow(MacState state,PigeonService pigeon,AgendaStore agenda,AgendaLanSync sync)
    {
        this.state=state;this.pigeon=pigeon;this.agenda=agenda;this.sync=sync;
        Title="EasyAppKeeper";Width=state.Data.WindowSize?.Width??1000;Height=state.Data.WindowSize?.Height??720;MinWidth=300;MinHeight=440;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        TransparencyLevelHint=[WindowTransparencyLevel.AcrylicBlur,WindowTransparencyLevel.Transparent];
        void UpdateAppearance()=>Background=new SolidColorBrush(Application.Current?.ActualThemeVariant==Avalonia.Styling.ThemeVariant.Dark?Color.FromArgb(234,28,36,51):Color.FromArgb(234,245,248,252));
        UpdateAppearance();ActualThemeVariantChanged+=(_,_)=>UpdateAppearance();
        var shell=new Grid{ColumnDefinitions=new ColumnDefinitions("Auto,*"),RowDefinitions=new RowDefinitions("*,Auto"),Margin=new Thickness(8)};
        var rail=new StackPanel{Spacing=7,Width=112,Margin=new Thickness(0,20,12,0)};
        foreach(var label in new[]{"快捷应用","最近访问","飞鸽传书","日程管理"}){var button=B(label,()=>Navigate(label));button.HorizontalAlignment=HorizontalAlignment.Stretch;navigation[label]=button;rail.Children.Add(button);}
        rail.Children.Add(B("偏好设置",Preferences));shell.Children.Add(rail);
        var content=new DockPanel{Margin=new Thickness(0,8,8,0)};DockPanel.SetDock(notice,Dock.Bottom);content.Children.Add(notice);content.Children.Add(host);Grid.SetColumn(content,1);shell.Children.Add(content);Content=shell;
        var credit=new TextBlock{Text="开发者 小李鱼 · 来信建议：kangxls@gmail.com",FontSize=11,Opacity=.7,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,HorizontalAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(4,8,4,2)};
        Grid.SetRow(credit,1);Grid.SetColumnSpan(credit,2);shell.Children.Add(credit);
        Closing+=(_,e)=>{if(!FinalClose){e.Cancel=true;Hide();pigeon.SetReadingPeer(null);state.Save();}};
        Deactivated+=(_,_)=>pigeon.SetReadingPeer(null);
        Activated+=(_,_)=>RefreshChat();
        PropertyChanged+=(_,e)=>{if(e.Property==WindowStateProperty){if(WindowState==WindowState.Minimized)pigeon.SetReadingPeer(null);} };
        SizeChanged+=(_,_)=>{rail.Width=Bounds.Width<640?78:112;if(WindowState==WindowState.Normal && Bounds.Width>=300){state.Data.WindowSize=new(Width,Height);}var next=Bounds.Width<650;if(next!=compact){compact=next;if(section=="最近访问")Recent();else if(section=="飞鸽传书"){var selected=pigeon.Peers().FirstOrDefault(p=>p.Key==peer);Pigeon();if(selected!=null)_=Safe(()=>ChoosePeer(selected));}}};
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape){Hide();pigeon.SetReadingPeer(null);} };
        query.TextChanged+=(_,_)=>{page=0;RenderTiles();};
        query.KeyDown+=(_,e)=>{if(e.Key==Key.Enter&&VisibleEntries().FirstOrDefault() is {} entry){e.Handled=true;Run(()=>{if(entry.Kind==EntryKind.Group){folder=entry.Id;RenderTiles();}else Open(entry);});}};
        ConfigureLauncherInput();
        pigeon.UnreadChanged+=()=>Dispatcher.UIThread.Post(UpdateBadges);
        poll.Tick+=(_,_)=>{if(IsVisible && section=="飞鸽传书")RefreshChat();};poll.Start();
        Navigate("快捷应用");
        if(MacPlatform.TestRoot==null)StartEntryInbox();
    }
    internal void Status(string text)=>notice.Text=text;
    private static TextBlock T(string text,double size=14)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap};
    private Button B(string label,Action action)=>Button(label,()=>{if(!dragging)Run(action);});
    private static Button Button(string label,Action action){var b=new Button{Content=label,Padding=new Thickness(10,7)};if(label is "发送" or "快速添加" or "确定" or "＋ 添加")b.Classes.Add("primary");b.Click+=(_,_)=>action();return b;}
    private async void Run(Action action){try{action();}catch(Exception e){await Alert(e.Message);}}
    private async Task Safe(Func<Task> action){try{await action();}catch(Exception e){if(MacPlatform.TestRoot!=null)throw;await Alert(e.Message);}}
    private async Task Alert(string message)=>await Dialog("提示",[T(message)],false);
    private StackPanel Stack(params Control[] children){var panel=new StackPanel{Spacing=12};foreach(var c in children)panel.Children.Add(c);return panel;}
    private Control Scroll(Control control)=>new ScrollViewer{Content=control,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private async Task<bool> Dialog(string title,IEnumerable<Control> fields,bool confirm=true)
    {
        var window=new Window{Title=title,Width=460,MinWidth=300,MaxHeight=720,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Spacing=12,Margin=new Thickness(20)};foreach(var field in fields)panel.Children.Add(field);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,HorizontalAlignment=HorizontalAlignment.Right};
        if(confirm)buttons.Children.Add(Button("确定",()=>window.Close(true)));buttons.Children.Add(Button(confirm?"取消":"关闭",()=>window.Close(false)));panel.Children.Add(buttons);window.Content=Scroll(panel);return await window.ShowDialog<bool>(this);
    }
    private async Task<string?> Prompt(string title,string value="") {var input=new TextBox{Text=value,MinHeight=36};return await Dialog(title,[input])?input.Text:null;}
    internal void Reveal(bool home=false)
    {
        if(home)Navigate("快捷应用");if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        if(!IsVisible){var screen=Screens.ScreenFromPoint(Position)??Screens.Primary;if(screen!=null){var scale=screen.Scaling;Width=Math.Min(Width,screen.WorkingArea.Width/scale);Height=Math.Min(Height,screen.WorkingArea.Height/scale);}}
        Show();Activate();if(section=="快捷应用")query.Focus();
    }
    private void Navigate(string name)
    {
        if(section=="快捷应用" && query.Text?.Trim().Length>0){QuickHistory.RecordSearch(state.Data,query.Text);state.Save();}
        pigeon.SetReadingPeer(null);section=name;peer=null;folder=null;page=0;
        foreach(var n in navigation)n.Value.Background=n.Key==name?Soft:null;
        if(name=="快捷应用")Apps();else if(name=="最近访问")Recent();else if(name=="飞鸽传书")Pigeon();else Agenda();UpdateBadges();
    }
    private void UpdateBadges()
    {
        var count=pigeon.UnreadCounts().Values.Sum();SetNavigationBadge(count);
        if(section=="飞鸽传书"&&IsVisible)RefreshChat();
    }
    private void SetNavigationBadge(int count)
    {
        var content=new Grid{RowDefinitions=new RowDefinitions("Auto,Auto")};content.Children.Add(T("飞鸽传书",12));
        if(count>0)
        {
            var badge=new Border{Background=new SolidColorBrush(Color.Parse("#e84d60")),CornerRadius=new CornerRadius(9),Padding=new Thickness(5,1),MinHeight=18,MinWidth=20,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,3,0,0),Child=new TextBlock{Text=PigeonUnread.Label(count),Foreground=Brushes.White,FontSize=11,HorizontalAlignment=HorizontalAlignment.Center}};
            Grid.SetRow(badge,1);content.Children.Add(badge);
        }
        navigation["飞鸽传书"].Content=content;Avalonia.Automation.AutomationProperties.SetName(navigation["飞鸽传书"],count>0?$"飞鸽传书，{count} 条未读消息":"飞鸽传书");
    }
    private void Apps()
    {
        foreach(var control in new Control[]{query,searchBar,recentBar,tags,tiles,pageNumber,gridViewport})
        {
            if(control.Parent is Panel panel)panel.Children.Remove(control);
            else if(control.Parent is ContentControl content)content.Content=null;
        }
        var header=new WrapPanel{Orientation=Orientation.Horizontal};header.Children.Add(T("快捷应用",24));header.Children.Add(B("＋ 添加",()=>_=Safe(AddEntry)));header.Children.Add(B("重新扫描",()=>_=ScanApps()));header.Children.Add(B("清理历史",()=>_=Safe(ClearQuickHistory)));
        searchBar.Children.Clear();foreach(var q in state.Data.SearchHistory){var button=B(q,()=>query.Text=q);var menu=new ContextMenu();menu.Items.Add(Menu("清除此条",()=>{QuickHistory.RemoveSearch(state.Data,q);state.Save();Apps();}));button.ContextMenu=menu;searchBar.Children.Add(button);}
        recentBar.Children.Clear();foreach(var entry in QuickHistory.Entries(state.Data)){var button=B((state.Data.PinnedEntries.Contains(entry.Id)?"📌 ":"")+entry.Name,()=>Open(entry));button.ContextMenu=EntryMenu(entry);recentBar.Children.Add(button);}
        tags.Children.Clear();foreach(var slot in MacLauncher.Tags(state))
        {
            var b=B(slot.Label,()=>{tag=slot.Name;folder=null;page=0;Apps();});b.Background=tag==slot.Name?Soft:null;
            b.Tag=new LauncherTag(slot.Key,slot.Name);EnableDrag(b,"tag",slot.Key);DragDrop.SetAllowDrop(b,true);
            b.AddHandler(DragDrop.DropEvent,(_,e)=>
            {
                if(e.Handled)return;Run(()=>
                {
                    if(e.DataTransfer.TryGetValue(MacLauncher.TagFormat) is string source)MacLauncher.MoveTag(state,source,slot.Key,e.GetPosition(b).X>b.Bounds.Width/2);
                    else if(Dragged(e) is {} item){if(slot.Name!=null)Layout.AssignTag(state.Data,item,slot.Name);}
                    else return;state.Save();Apps();
                });e.Handled=true;
            });
            if(slot.Name!=null)b.ContextMenu=new ContextMenu{Items={Menu("删除标签",()=>{Layout.RemoveTag(state.Data,slot.Name);if(tag==slot.Name)tag=null;state.Save();Apps();})}};
            tags.Children.Add(b);
        }
        tags.Children.Add(B("＋ 标签",()=>_=Safe(async()=>{var name=await Prompt("标签名称（最多 24 字）");if(name==null)return;if(!Layout.IsTagName(name)||state.Data.Tags.Contains(name))throw new ArgumentException("标签名称无效或已存在。");state.Data.Tags.Add(name);Layout.EnsureTagOrders(state.Data);state.Save();Apps();})));
        var paging=new StackPanel{Orientation=Orientation.Horizontal,Spacing=10,HorizontalAlignment=HorizontalAlignment.Center};paging.Children.Add(B("‹",()=>{page=Math.Max(0,page-1);RenderTiles();}));paging.Children.Add(pageNumber);paging.Children.Add(B("›",()=>{page++;RenderTiles();}));
        gridViewport.Content=tiles;
        var layout=new DockPanel{Background=Brushes.Transparent};var top=Stack(header,query,new ScrollViewer{Content=searchBar,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto},T("最近打开 · 右键锁定",12),new ScrollViewer{Content=recentBar,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto},tags);DockPanel.SetDock(top,Dock.Top);layout.Children.Add(top);
        backGroup=B("‹ 返回应用页",()=>{folder=null;RenderTiles();});backGroup.IsVisible=folder.HasValue;
        var footer=Stack(backGroup,T("每页 50 个 · 上下滚动 · 左右滑动翻页",11),paging);DockPanel.SetDock(footer,Dock.Bottom);layout.Children.Add(footer);layout.Children.Add(gridViewport);host.Content=layout;gesture.Reset();RenderTiles();
    }
    private Entry[] VisibleEntries()=>MacLauncher.Ordered(state,folder.HasValue?state.Data.Entries.Where(e=>e.Group==folder):Layout.Visible(state.Data,false,query.Text??"",tag),PinScope);
    private void RenderTiles()
    {
        if(section!="快捷应用")return;tiles.Children.Clear();gridViewport.Offset=default;
        var all=VisibleEntries();
        var pages=Math.Max(1,(all.Length+pageSize-1)/pageSize);page=Math.Clamp(page,0,pages-1);pageNumber.Text=$"{page+1} / {pages}";
        if(backGroup!=null)backGroup.IsVisible=folder.HasValue;
        foreach(var entry in all.Skip(page*pageSize).Take(pageSize))
        {
            var inner=new StackPanel{Spacing=6,HorizontalAlignment=HorizontalAlignment.Center};
            if((entry.Icon??icons.GetValueOrDefault(entry.Target)) is {} image){try{inner.Children.Add(new Image{Source=new Bitmap(new MemoryStream(Convert.FromBase64String(image))),Width=48,Height=48});}catch{inner.Children.Add(T("▣",32));}}
            else inner.Children.Add(new Border{Background=Soft,CornerRadius=new CornerRadius(12),Padding=new Thickness(12),Child=T(entry.Kind==EntryKind.Group?"▦":entry.Kind==EntryKind.Directory?"📁":entry.Kind==EntryKind.Web?"◎":"▣",28)});
            inner.Children.Add(new TextBlock{Text=(MacLauncher.Pins(state,PinScope).Contains(entry.Id)?"📌 ":"")+entry.Name,FontSize=12,MaxWidth=102,TextTrimming=TextTrimming.CharacterEllipsis,HorizontalAlignment=HorizontalAlignment.Center});
            var button=B("",()=>{if(entry.Kind==EntryKind.Group){folder=entry.Id;page=0;RenderTiles();}else Open(entry);});button.Content=inner;button.Width=116;button.Height=108;button.Background=Brushes.Transparent;ToolTip.SetTip(button,entry.Name+"\n"+entry.Target);button.ContextMenu=EntryMenu(entry);
            button.Tag=entry;EnableDrag(button,"entry",entry.Id.ToString());DragDrop.SetAllowDrop(button,true);button.AddHandler(DragDrop.DropEvent,(_,e)=>TileDrop(button,entry,e));tiles.Children.Add(button);
        }
    }
    private MenuItem Menu(string text,Action action){var m=new MenuItem{Header=text};m.Click+=(_,_)=>Run(action);return m;}
    private ContextMenu EntryMenu(Entry entry)
    {
        var menu=new ContextMenu();menu.Items.Add(Menu("打开",()=>Open(entry)));
        var scope=PinScope;var pinned=MacLauncher.Pins(state,scope).Contains(entry.Id);
        menu.Items.Add(Menu(pinned?"再次置顶（排第一）":"置顶入口",()=>{MacLauncher.Pin(state,scope,entry.Id,true);page=0;RenderTiles();}));
        if(pinned)menu.Items.Add(Menu("取消置顶",()=>{MacLauncher.Pin(state,scope,entry.Id,false);RenderTiles();}));
        menu.Items.Add(Menu(state.Data.PinnedEntries.Contains(entry.Id)?"取消锁定":"锁定常用",()=>{if(!state.Data.PinnedEntries.Remove(entry.Id))state.Data.PinnedEntries.Add(entry.Id);state.Save();Apps();}));
        menu.Items.Add(Menu("清除此条访问记录",()=>{QuickHistory.RemoveVisit(state.Data,entry.Id);state.Save();Apps();}));
        menu.Items.Add(Menu("重命名",()=>_=Safe(async()=>{var name=await Prompt("入口名称",entry.Name);if(!string.IsNullOrWhiteSpace(name)){entry.Name=name.Trim();state.Save();Apps();}})));
        foreach(var name in state.Data.Tags)menu.Items.Add(Menu((entry.Tags.Contains(name)?"✓ ":"")+name,()=>{if(entry.Tags.Contains(name))Layout.UnassignTag(state.Data,entry,name);else Layout.AssignTag(state.Data,entry,name);state.Save();Apps();}));
        if(entry.Group!=null)menu.Items.Add(Menu("移出分组",()=>{Layout.MoveHome(state.Data,entry);state.Save();RenderTiles();}));
        menu.Items.Add(Menu("移除入口",()=>{Layout.Remove(state.Data,entry);state.Save();Apps();}));return menu;
    }
    private void Open(Entry entry){MacPlatform.Open(entry.Target);if(section=="快捷应用")QuickHistory.RecordSearch(state.Data,query.Text??"");state.Visit(entry);Hide();pigeon.SetReadingPeer(null);}
    private async Task AddEntry()
    {
        var choice=new ComboBox{ItemsSource=new[]{"应用或文件","文件夹","网址"},SelectedIndex=0};if(!await Dialog("添加入口",[choice]))return;
        if(choice.SelectedIndex==2){var url=await Prompt("网址（http / https）");if(url!=null){if(!WindowsCatalog.IsWeb(url))throw new ArgumentException("请输入有效网址。");state.Add(url,tag);}}
        else if(choice.SelectedIndex==1){foreach(var f in await StorageProvider.OpenFolderPickerAsync(new(){AllowMultiple=true}))if(f.TryGetLocalPath() is {} p)state.Add(p,tag,f.Name);}
        else {foreach(var f in await StorageProvider.OpenFilePickerAsync(new(){AllowMultiple=true,Title="选择文件（应用可从 Finder 直接拖入）"}))if(f.TryGetLocalPath() is {} p)state.Add(p,tag);}
        Apps();
    }
    internal async Task ScanApps()=>await Safe(async()=>
    {
        Status("正在扫描本机应用…");var json=await MacPlatform.Native("--scan");
        var scanned=JsonSerializer.Deserialize<ScanEntry[]>(json)??[];
        foreach(var item in scanned){icons[item.Path]=item.Icon;if(!state.Data.Entries.Any(e=>e.Target==item.Path))state.Data.Entries.Add(new Entry{Name=item.Name,Target=item.Path,Kind=EntryKind.File});}
        state.Save();Status($"已扫描 {scanned.Length} 个应用");if(section=="快捷应用")Apps();
    });
    private sealed record ScanEntry(string Name,string Path,string Icon);
    private async Task ClearQuickHistory()
    {
        var searches=new CheckBox{Content="清空检索记录",IsChecked=true};var visits=new CheckBox{Content="清空未锁定的最近打开记录",IsChecked=true};
        if(await Dialog("清理历史",[searches,visits,T("锁定项保留；可通过入口右键单独清除。",12)])){if(searches.IsChecked==true)state.Data.SearchHistory.Clear();if(visits.IsChecked==true)QuickHistory.ClearUnpinnedVisits(state.Data);state.Save();Apps();}
    }
    internal async Task Smoke()
    {
        var output=Environment.GetEnvironmentVariable("APPKEEPER_MAC_UI_DIR");
        await LauncherInputSmoke();
        foreach(var width in new[]{1000,360})
        {
            Width=width;Height=720;await Task.Delay(150);
            foreach(var name in new[]{"快捷应用","最近访问","飞鸽传书","日程管理","快捷应用"})
            {
                Console.WriteLine($"UI: {name} {width}");
                Navigate(name);await Task.Delay(80);
                if(name=="快捷应用"){Apps();query.Text="jisuanqi";query.Text="";}
                if(name=="飞鸽传书"&&pigeon.Peers().FirstOrDefault() is {} samplePeer)
                {
                    await ChoosePeer(samplePeer);RefreshChat();
                    var message=chatMessages?.Children.OfType<Border>().FirstOrDefault()?.Child as StackPanel;
                    if(message?.Children.FirstOrDefault() is not TextBox text)throw new Exception("聊天文字控件未创建。");
                    text.SelectionStart=0;text.SelectionEnd=2;RefreshChat();
                    if(text.SelectedText!="中文")throw new Exception("聊天文字部分选择未保留。");
                }
                if(name=="日程管理"){Agenda(1);await Task.Delay(80);Agenda();}
                await Task.Delay(80);
                foreach(var count in new[]{1,99,100})
                {
                    SetNavigationBadge(count);await Task.Delay(30);
                    var button=navigation["飞鸽传书"];var badge=((Grid)button.Content!).Children.OfType<Border>().Single();
                    var position=badge.TranslatePoint(default,button)??throw new Exception("未读角标未挂载。");
                    if(position.X<0||position.Y<0||position.X+badge.Bounds.Width>button.Bounds.Width||position.Y+badge.Bounds.Height>button.Bounds.Height)throw new Exception("未读角标超出菜单按钮。");
                }
                if(!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);using var bitmap=new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Bounds.Width,(int)Bounds.Height));bitmap.Render(this);bitmap.Save(Path.Combine(output,$"{name}-{width}.png"));
                }
            }
        }
    }
}
