using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using QRCoder;
using System.Text.Json;

namespace AppKeeper;

internal sealed partial class MacWindow
{
    private void Recent()
    {
        var range=new ComboBox{ItemsSource=new[]{"最近 7 天","最近 30 天","全部"},SelectedIndex=0};
        var kind=new ComboBox{ItemsSource=new[]{"全部类型","应用","文件","文件夹","网址"},SelectedIndex=0};
        var filter=new TextBox{Watermark="搜索名称、拼音或路径"};var rows=new StackPanel{Spacing=8};var ranking=new StackPanel{Spacing=8};
        void Draw()
        {
            rows.Children.Clear();ranking.Children.Clear();var now=DateTimeOffset.Now;
            var eligible=state.Settings.Visits.Where(v=>!state.Settings.Hidden.Contains(v.Path)&&!state.Settings.Excluded.Any(p=>v.Path==p||v.Path.StartsWith(p+Path.DirectorySeparatorChar,StringComparison.Ordinal))).ToArray();
            bool Type(MacVisit v)=>kind.SelectedIndex switch{1=>v.Path.EndsWith(".app"),2=>v.Kind==EntryKind.File&&!v.Path.EndsWith(".app"),3=>v.Kind==EntryKind.Directory,4=>v.Kind==EntryKind.Web,_=>true};
            bool Matches(MacVisit v)=>Type(v)&&(string.IsNullOrWhiteSpace(filter.Text)||Pinyin.Matches(v.Name,filter.Text)||v.Path.Contains(filter.Text,StringComparison.OrdinalIgnoreCase));
            Button Row(MacVisit v,string subtitle)
            {
                var b=B("",()=>{MacPlatform.Open(v.Path);var entry=state.Data.Entries.Find(x=>x.Target==v.Path)??new Entry{Name=v.Name,Target=v.Path,Kind=v.Kind};state.Visit(entry);Draw();});
                b.Content=Stack(T(v.Name),T(subtitle,11));b.HorizontalContentAlignment=HorizontalAlignment.Stretch;
                b.ContextMenu=new ContextMenu{Items={Menu("打开所在位置",()=>MacPlatform.Open(v.Path,true)),Menu("隐藏此项",()=>{state.Settings.Hidden.Add(v.Path);state.Save();Draw();}),Menu("排除此位置",()=>{state.Settings.Excluded.Add(v.Kind==EntryKind.Directory?v.Path:Path.GetDirectoryName(v.Path)??v.Path);state.Save();Draw();})}};return b;
            }
            var days=range.SelectedIndex switch{0=>7,1=>30,_=>int.MaxValue};
            foreach(var item in eligible.Where(v=>(days==int.MaxValue||v.At>now.AddDays(-days))&&Matches(v)).GroupBy(v=>v.Path).Select(g=>g.MaxBy(v=>v.At)!).OrderByDescending(v=>v.At).Take(200)) rows.Children.Add(Row(item,$"{item.At:MM-dd HH:mm} · {item.Path}"));
            foreach(var item in eligible.Where(v=>v.At>now.AddDays(-7)&&Matches(v)).GroupBy(v=>v.Path).OrderByDescending(g=>g.Count()).ThenByDescending(g=>g.Max(v=>v.At)).Take(50))ranking.Children.Add(Row(item.MaxBy(v=>v.At)!,$"近七天 {item.Count()} 次"));
            if(rows.Children.Count==0)rows.Children.Add(T("暂无访问记录。通过快捷应用打开入口后会出现在这里。",12));
        }
        range.SelectionChanged+=(_,_)=>Draw();kind.SelectionChanged+=(_,_)=>Draw();filter.TextChanged+=(_,_)=>Draw();
        var toolbar=new WrapPanel();toolbar.Children.Add(range);toolbar.Children.Add(kind);toolbar.Children.Add(B("管理记录",()=>_=Safe(async()=>{var record=new CheckBox{Content="记录通过本应用打开的入口",IsChecked=state.Settings.RecordVisits};var clear=new CheckBox{Content="清空全部访问记录及排行"};var restore=new CheckBox{Content="恢复隐藏项并清除路径排除"};if(await Dialog("访问记录管理",[record,clear,restore,T("只影响本应用的记录，不删除实际文件。",12)])){state.Settings.RecordVisits=record.IsChecked==true;if(clear.IsChecked==true)state.Settings.Visits.Clear();if(restore.IsChecked==true){state.Settings.Hidden.Clear();state.Settings.Excluded.Clear();}state.Save();Draw();}})));
        var split=new Grid{ColumnDefinitions=new ColumnDefinitions("*,2*")};var left=Stack(T("七天高频 · TOP 50",18),ranking);left.Margin=new Thickness(0,0,20,0);split.Children.Add(left);var right=Stack(T("最近文件与入口",18),rows);Grid.SetColumn(right,1);split.Children.Add(right);
        Control lists=split;if(Bounds.Width<650){split.Children.Clear();lists=Stack(left,right);}
        host.Content=Scroll(Stack(T("最近访问",24),T("仅记录通过 EasyAppKeeper 打开的入口",12),toolbar,filter,lists));Draw();
    }
    private void Pigeon()
    {
        peer=null;peerList=new StackPanel{Spacing=7};chatHost=new ContentControl{Content=T("选择一台电脑查看消息；打开菜单不会清除未读。",14)};
        var toolbar=new WrapPanel();toolbar.Children.Add(B("＋ 手动添加",()=>_=Safe(()=>PairComputer(null))));toolbar.Children.Add(B("传书设置",()=>_=Safe(PigeonSettings)));
        // Keep both columns reachable on narrow windows using a wrapping stack instead of compressing the conversation.
        var content=new StackPanel{Spacing=14};content.Children.Add(T("飞鸽传书",24));content.Children.Add(toolbar);
        if(Bounds.Width<650){content.Children.Add(peerList);content.Children.Add(chatHost);}else{var layout=new Grid{ColumnDefinitions=new ColumnDefinitions("175,*")};peerList.Margin=new Thickness(0,0,14,0);layout.Children.Add(peerList);Grid.SetColumn(chatHost,1);layout.Children.Add(chatHost);content.Children.Add(layout);}
        host.Content=Scroll(content);RefreshChat();
    }
    private async Task PairComputer(DiscoveredPeer? discovered)
    {
        var ip=new TextBox{Watermark="对方 IPv4 地址",Text=discovered?.Address};var port=new TextBox{Text=(discovered?.Computer.Port??48620).ToString()};var auto=new CheckBox{Content="允许自动接收这台电脑的文件"};
        if(!await Dialog("添加电脑",[T("对方需同时运行 EasyAppKeeper / AppKeeper。请在双方界面核对校验码。",12),ip,port,auto]))return;
        if(!int.TryParse(port.Text,out var number))throw new ArgumentException("请输入有效端口。");
        var added=await pigeon.Pair(ip.Text??"",number,discovered?.Computer.Key,auto.IsChecked==true);await Alert("配对校验码："+pigeon.Code(added)+"\n请与对方核对并由对方确认。");RefreshChat();
    }
    private async Task ChoosePeer(PigeonPeer chosen)
    {
        if(chosen.State=="incoming")
        {
            var auto=new CheckBox{Content="允许自动接收这台电脑的文件"};var reject=B("拒绝此设备",()=>{pigeon.Reject(chosen.Key);RefreshChat();});
            if(await Dialog("确认配对",[T(chosen.Name,18),T("请与对方核对校验码："+pigeon.Code(chosen)),auto,reject]))pigeon.Allow(chosen.Key,auto.IsChecked==true);
            RefreshChat();return;
        }
        if(chosen.State!="paired"){await Alert("等待对方确认 · 校验码 "+pigeon.Code(chosen));return;}
        peer=chosen.Key;chatStamp="";chatMessages=new StackPanel{Spacing=9};composer=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Watermark="输入消息，最多 2000 字符",MinHeight=65,MaxHeight=160};
        var length=T("0 / 2000",11);composer.TextChanged+=(_,_)=>length.Text=$"{PigeonService.MessageLength(composer.Text??"")} / 2000";
        var actions=new WrapPanel();actions.Children.Add(B("发送",()=>{pigeon.SendText(chosen.Key,composer.Text??"");composer.Text="";RefreshChat();}));actions.Children.Add(B("发文件",()=>_=Safe(()=>SendFiles(false))));actions.Children.Add(B("发文件夹",()=>_=Safe(()=>SendFiles(true))));actions.Children.Add(length);
        composer.KeyDown+=(_,e)=>{if(e.Key==Avalonia.Input.Key.Enter&&e.KeyModifiers==Avalonia.Input.KeyModifiers.None){e.Handled=true;Run(()=>{pigeon.SendText(chosen.Key,composer.Text??"");composer.Text="";RefreshChat();});}};
        chatHost!.Content=Stack(T(chosen.Name,20),B("返回电脑列表",()=>{peer=null;pigeon.SetReadingPeer(null);chatHost.Content=T("选择电脑查看消息");RefreshChat();}),chatMessages,composer,actions);
        Avalonia.Input.DragDrop.SetAllowDrop(chatHost,true);chatHost.AddHandler(Avalonia.Input.DragDrop.DropEvent,(_,e)=>{var destination=peer;if(destination==null)return;_=Safe(async()=>{foreach(var f in e.Data.GetFiles()??[])if(f.TryGetLocalPath() is {} p)await pigeon.SendPath(destination,p);RefreshChat();});e.Handled=true;});RefreshChat();
    }
    private void RefreshChat()
    {
        if(section!="飞鸽传书"||peerList==null||refreshing)return;refreshing=true;
        try
        {
            var unread=pigeon.UnreadCounts();peerList.Children.Clear();
            foreach(var computer in pigeon.Peers())peerList.Children.Add(B(computer.Name+"\n"+(computer.State=="paired"?(pigeon.Online(computer.Key)?"在线 · 直连":"离线 · 可排队"):computer.State=="incoming"?"配对请求":"等待确认")+(unread.GetValueOrDefault(computer.Key)>0?$" 🔴{PigeonUnread.Label(unread[computer.Key])}":""),()=>_=Safe(()=>ChoosePeer(computer))));
            foreach(var found in pigeon.Discovered().Where(d=>!pigeon.Peers().Any(p=>p.Key==d.Computer.Key)))peerList.Children.Add(B(found.Computer.Name+"\n局域网发现",()=>_=Safe(()=>PairComputer(found))));
            if(peerList.Children.Count==0)peerList.Children.Add(T("暂无电脑，可手动添加。",12));
            if(peer==null||chatMessages==null)return;
            var snapshot=pigeon.History(peer);var stamp=JsonSerializer.Serialize(snapshot.Select(i=>i with{Unread=false}));
            if(stamp!=chatStamp)
            {
                chatStamp=stamp;
                var existing=chatMessages.Children.OfType<Border>().Where(b=>b.Tag is PigeonItem).ToDictionary(b=>((PigeonItem)b.Tag!).Id);
                for(var index=0;index<snapshot.Length;index++)
                {
                    var item=snapshot[index];var view=existing.GetValueOrDefault(item.Id);
                    if(view?.Tag is not PigeonItem old||(old with{Unread=false})!=(item with{Unread=false}))view=ChatMessage(item);
                    if(index<chatMessages.Children.Count&&ReferenceEquals(chatMessages.Children[index],view))continue;
                    chatMessages.Children.Remove(view);chatMessages.Children.Insert(index,view);
                }
                while(chatMessages.Children.Count>snapshot.Length)chatMessages.Children.RemoveAt(snapshot.Length);
            }
            var reading=IsVisible&&IsActive&&WindowState!=WindowState.Minimized;
            pigeon.SetReadingPeer(reading?peer:null);if(reading)pigeon.MarkRead(peer,snapshot.Select(i=>i.Id));
        }
        catch(Exception e){pigeon.SetReadingPeer(null);Status(e.Message);}
        finally{refreshing=false;}
    }
    private Border ChatMessage(PigeonItem item)
    {
        var body=new StackPanel{Spacing=5};
        if(item.Kind=="text")body.Children.Add(new TextBox{Text=item.Value,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0)});
        else{var file=B(item.Value,()=>{if(item.LocalFile==null)throw new IOException("文件接收完成后可打开。");MacPlatform.Open(item.LocalFile);});body.Children.Add(file);}
        var caption=item.State switch{"saved"=>"已接收","sent"=>"已送达","pending"=>"等待接收","receiving"=>"接收中 / 等待重连","queued"=>"排队中","rejected"=>"已拒绝","failed"=>"源文件不可用",_=>item.State};
        body.Children.Add(T($"{item.At.ToLocalTime():MM-dd HH:mm} · {caption}"+(item.Size>0?$" · {item.Progress}/{item.Size} 字节":""),11));
        var buttons=new WrapPanel();if(item.State=="pending"){buttons.Children.Add(B("接收",()=>{pigeon.Answer(item.Id,true);RefreshChat();}));buttons.Children.Add(B("拒绝",()=>{pigeon.Answer(item.Id,false);RefreshChat();}));}
        if(item.Kind=="file"&&item.State is "queued" or "receiving")buttons.Children.Add(B("取消",()=>{pigeon.Cancel(item.Id);RefreshChat();}));
        if(item.State=="failed")buttons.Children.Add(B("重试",()=>{pigeon.Retry(item.Id);RefreshChat();}));
        if(item.LocalFile is {} path)buttons.Children.Add(B("打开所在位置",()=>MacPlatform.Open(path,true)));body.Children.Add(buttons);
        var border=new Border{Child=body,Background=Soft,CornerRadius=new CornerRadius(10),Padding=new Thickness(10),Margin=new Thickness(item.Outgoing?20:0,0,item.Outgoing?0:20,0),Tag=item};
        var menu=new ContextMenu();menu.Items.Add(Menu(item.Kind=="text"?"复制全文":"复制文件",()=>_=Safe(async()=>{if(item.Kind=="text")await Clipboard!.SetTextAsync(item.Value);else{if(item.LocalFile is not {} f||!File.Exists(f))throw new IOException("文件尚未接收完成或已移动 / 删除。");await MacPlatform.Native("--copy-file",f);}})));
        if(item.Kind=="text"&&body.Children[0] is TextBox text){menu.Items.Insert(0,Menu("复制选中内容",()=>_=Safe(async()=>{if(!string.IsNullOrEmpty(text.SelectedText))await Clipboard!.SetTextAsync(text.SelectedText);})));text.ContextMenu=menu;}
        border.ContextMenu=menu;return border;
    }
    private async Task SendFiles(bool folders)
    {
        var destination=peer??throw new InvalidOperationException("请先选择电脑。");
        var items=folders?(IEnumerable<IStorageItem>)await StorageProvider.OpenFolderPickerAsync(new(){AllowMultiple=true}):await StorageProvider.OpenFilePickerAsync(new(){AllowMultiple=true});
        foreach(var item in items)if(item.TryGetLocalPath() is {} p)await pigeon.SendPath(destination,p);RefreshChat();
    }
    private async Task PigeonSettings()
    {
        var settings=pigeon.Settings;var port=new TextBox{Text=settings.Port.ToString()};var share=new TextBox{Text=settings.Share};var limit=new TextBox{Text=(settings.Limit/1_000_000_000m).ToString()};
        var chosen=pigeon.Peers().FirstOrDefault(p=>p.Key==peer);var auto=new CheckBox{Content="允许自动接收当前电脑的文件",IsChecked=chosen?.Auto,IsEnabled=chosen!=null};var ip=new TextBox{Text=chosen?.Address,Watermark="当前电脑 IPv4"};var remotePort=new TextBox{Text=chosen?.Port.ToString(),Watermark="对方端口"};
        var clear=B("清空当前电脑已结束记录",()=>_=Safe(async()=>{if(chosen!=null&&await Dialog("清空会话？",[T("已接收的文件会保留。")])){pigeon.ClearHistory(chosen.Key);RefreshChat();}}));
        if(await Dialog("传书设置",[T("本机端口"),port,T("接收目录"),share,B("选择接收目录",()=>_=Safe(async()=>{var dirs=await StorageProvider.OpenFolderPickerAsync(new());if(dirs.FirstOrDefault()?.TryGetLocalPath() is {} p)share.Text=p;})),T("自动接收上限（GB）"),limit,auto,ip,remotePort,clear]))
        {
            if(!int.TryParse(port.Text,out var number)||!decimal.TryParse(limit.Text,out var gb)||gb<0||gb>1000000)throw new ArgumentException("请填写有效端口和大小上限。");
            pigeon.Configure(number,share.Text??"share",(long)(gb*1_000_000_000m),chosen==null?null:chosen with{Auto=auto.IsChecked==true,Address=ip.Text??chosen.Address,Port=int.Parse(remotePort.Text??"")});
        }
    }
    private void Agenda(int tab=0)
    {
        var rows=new StackPanel{Spacing=8};
        var navigation=new WrapPanel();foreach(var item in new[]{("待办",0),("月历",1),("已完成",2)})navigation.Children.Add(B(item.Item1,()=>Agenda(item.Item2)));
        navigation.Children.Add(B("连接手机",()=>_=Safe(Phone)));navigation.Children.Add(B("导出 Markdown",()=>_=Safe(async()=>{var save=await StorageProvider.SaveFilePickerAsync(new(){SuggestedFileName="日程.md"});if(save!=null){await using var stream=await save.OpenWriteAsync();await using var writer=new StreamWriter(stream);await writer.WriteAsync(agenda.Markdown(DateTime.Today.AddYears(-10),DateTime.Today.AddYears(10)));}})));
        var panel=Stack(T("日程管理",24),navigation);DateTime? selected=null;
        if(tab==1){var calendar=new Calendar{SelectedDate=DateTime.Today};calendar.SelectedDatesChanged+=(_,_)=>Draw(calendar.SelectedDate);panel.Children.Add(calendar);selected=DateTime.Today;}
        panel.Children.Add(rows);
        void Draw(DateTime? day)
        {
            rows.Children.Clear();var tasks=agenda.Tasks().Where(t=>tab==2?t.Completed!=null:t.Completed==null).Where(t=>tab!=1||day!=null&&t.On(day.Value)).OrderBy(t=>t.Due??DateTimeOffset.MaxValue);
            foreach(var task in tasks){var actions=new WrapPanel();if(task.Completed==null){actions.Children.Add(B("完成",()=>_=Safe(async()=>{await CompleteTask(task);Draw(day);})));actions.Children.Add(B(task.Actual==null?"开始":"撤销开始",()=>{if(task.Actual==null)agenda.Append("start",task.Id,new{actualStartedAt=AgendaStore.Iso(DateTimeOffset.Now)});else agenda.Append("edit",task.Id,new{actualStartedAt=(string?)null});Draw(day);}));}else actions.Children.Add(B("恢复待办",()=>{agenda.Append("reopen",task.Id,new{});Draw(day);}));
                actions.Children.Add(B("删除",()=>_=Safe(async()=>{if(await Dialog("删除事项？",[T(task.Title)])){agenda.Append("delete",task.Id,new{});Draw(day);}})));
                rows.Children.Add(new Border{Background=Soft,CornerRadius=new CornerRadius(10),Padding=new Thickness(12),Child=Stack(B(task.Title,()=>_=Safe(async()=>{await EditTask(task);Draw(day);})),T(task.Tag+(task.Due.HasValue?" · 截止 "+AgendaStore.Format(task.Due,task.DueTime):"")+(task.Actual!=null?" · 已开始":"")+(task.Completed!=null?" · 已完成":""),12),actions)});
            }
            if(rows.Children.Count==0)rows.Children.Add(T("暂无事项",12));
        }
        var quick=new TextBox{Watermark="输入待办事项"};panel.Children.Add(quick);panel.Children.Add(B("快速添加",()=>{if(string.IsNullOrWhiteSpace(quick.Text))return;agenda.Append("create",Guid.NewGuid().ToString(),new{title=quick.Text.Trim(),category=""});quick.Text="";Draw(selected);}));panel.Children.Add(B("添加时间与标签",()=>_=Safe(async()=>{await EditTask(null);Draw(selected);})));
        host.Content=Scroll(panel);Draw(selected);
    }
    private async Task EditTask(AgendaTask? task)
    {
        var title=new TextBox{Watermark="事项名称",Text=task?.Title};var category=new TextBox{Watermark="标签",Text=task?.Tag};var due=new DatePicker{SelectedDate=task?.Due};var time=new TimePicker{SelectedTime=task?.Due?.TimeOfDay};var start=new DatePicker{SelectedDate=task?.Start};
        if(await Dialog("事项详情",[title,category,T("计划日期（可留空）"),start,T("截止日期（可留空）"),due,T("截止时间（可留空）"),time]))
        {
            if(string.IsNullOrWhiteSpace(title.Text))throw new ArgumentException("请填写事项名称。");
            DateTimeOffset? deadline=due.SelectedDate.HasValue?new DateTimeOffset(due.SelectedDate.Value.Date+(time.SelectedTime??TimeSpan.Zero)):null;
            agenda.Append(task==null?"create":"edit",task?.Id??Guid.NewGuid().ToString(),new{title=title.Text.Trim(),category=category.Text??"",plannedStart=AgendaStore.Iso(start.SelectedDate),startHasTime=false,deadline=AgendaStore.Iso(deadline),deadlineHasTime=time.SelectedTime.HasValue});
        }
    }
    private async Task CompleteTask(AgendaTask task)
    {
        var note=new TextBox{Watermark="完成反馈",AcceptsReturn=true,MinHeight=70};var seconds=new TextBox{Watermark="投入分钟（可留空）"};var rating=new ComboBox{ItemsSource=new[]{"不评分","1","2","3","4","5"},SelectedIndex=0};
        if(await Dialog("完成并记录反馈",[T(task.Title),seconds,rating,note]))
        {
            int? effort=null;if(!string.IsNullOrWhiteSpace(seconds.Text)){if(!int.TryParse(seconds.Text,out var minutes)||minutes<0||minutes>1000000)throw new ArgumentException("请输入有效投入分钟。");effort=minutes*60;}
            agenda.Append("complete",task.Id,new{completedAt=AgendaStore.Iso(DateTimeOffset.Now),effortSeconds=effort,rating=rating.SelectedIndex==0?(int?)null:rating.SelectedIndex,note=note.Text??""});
        }
    }
    private async Task Phone()
    {
        var snapshot=sync.Snapshot();var panel=new StackPanel{Spacing=9};panel.Children.Add(T(snapshot.Status));var port=new TextBox{Text=snapshot.Port.ToString()};var enabled=new CheckBox{Content="启用手机局域网同步",IsChecked=snapshot.Enabled};
        panel.Children.Add(T("通信端口"));panel.Children.Add(port);panel.Children.Add(enabled);
        foreach(var address in AgendaLanSync.Addresses())panel.Children.Add(T(address.Name+" · "+address.Address,12));
        if(snapshot.Serving)
        {
            var session=sync.CreateSession();using var generator=new QRCodeGenerator();using var data=generator.CreateQrCode(JsonSerializer.Serialize(session.Offer,AgendaSyncProtocol.Json),QRCodeGenerator.ECCLevel.M);using var qr=new PngByteQRCode(data);
            panel.Children.Add(new Image{Source=new Bitmap(new MemoryStream(qr.GetGraphic(5))),Width=230,Height=230});panel.Children.Add(T("确认码："+session.Code,22));panel.Children.Add(T("原 MarkDo Android 手机端扫码，或输入 IP、端口与确认码。五分钟有效。",12));
        }
        foreach(var phone in snapshot.Phones)panel.Children.Add(B(phone.Name+" · 撤销配对",()=>{sync.Revoke(phone.Id);Status("已撤销 "+phone.Name);}));
        if(await Dialog("连接手机",[panel])){if(!int.TryParse(port.Text,out var number))throw new ArgumentException("请输入有效端口。");await sync.ConfigureAsync(enabled.IsChecked==true,number);}
    }
    internal void Preferences()=>_=Safe(async()=>
    {
        var ball=new CheckBox{Content="显示贴边桌面浮球",IsChecked=state.Settings.Ball};var record=new CheckBox{Content="记录通过本应用打开的入口",IsChecked=state.Settings.RecordVisits};
        var login=new CheckBox{Content="登录时启动",IsChecked=await MacPlatform.Native("--login","status")=="true"};
        uint code=state.Settings.HotkeyCode,modifiers=state.Settings.HotkeyModifiers;var hotkey=new TextBox{IsReadOnly=true,Watermark="点此按下快捷键",Text=$"当前快捷键：{HotkeyName(code,modifiers)}"};
        hotkey.KeyDown+=(_,e)=>{if(TryHotkey(e.Key,e.KeyModifiers,out var c,out var m)){code=c;modifiers=m;hotkey.Text=HotkeyName(c,m);e.Handled=true;}};
        var export=B("导出布局",()=>_=Safe(async()=>{var f=await StorageProvider.SaveFilePickerAsync(new(){SuggestedFileName="EasyAppKeeper布局.json"});if(f?.TryGetLocalPath() is {} p)state.Store.Export(p,state.Data);}));
        var import=B("导入布局",()=>_=Safe(async()=>{var files=await StorageProvider.OpenFilePickerAsync(new());if(files.FirstOrDefault()?.TryGetLocalPath() is {} p){state.Import(p);if(section=="快捷应用")Apps();}}));
        var license=B("第三方许可",()=>_=Safe(async()=>{using var stream=typeof(MacWindow).Assembly.GetManifestResourceStream("AppKeeper.THIRD-PARTY-NOTICES.txt");if(stream!=null)await Alert(await new StreamReader(stream).ReadToEndAsync());}));
        if(await Dialog("偏好设置",[T("全局快捷键（Option / Command / Control 加字母或空格）",12),hotkey,ball,record,login,export,import,B("打开数据目录",()=>MacPlatform.Open(state.Root)),license]))
        {
            await MacPlatform.Native("--login",login.IsChecked==true?"enable":"disable");state.Settings.Ball=ball.IsChecked==true;state.Settings.RecordVisits=record.IsChecked==true;state.Settings.HotkeyCode=code;state.Settings.HotkeyModifiers=modifiers;state.Save();PreferencesSaved?.Invoke();
        }
    });
    private static readonly Dictionary<Avalonia.Input.Key,uint> MacKeys=new(){[Avalonia.Input.Key.Space]=49,[Avalonia.Input.Key.A]=0,[Avalonia.Input.Key.B]=11,[Avalonia.Input.Key.C]=8,[Avalonia.Input.Key.D]=2,[Avalonia.Input.Key.E]=14,[Avalonia.Input.Key.F]=3,[Avalonia.Input.Key.G]=5,[Avalonia.Input.Key.H]=4,[Avalonia.Input.Key.I]=34,[Avalonia.Input.Key.J]=38,[Avalonia.Input.Key.K]=40,[Avalonia.Input.Key.L]=37,[Avalonia.Input.Key.M]=46,[Avalonia.Input.Key.N]=45,[Avalonia.Input.Key.O]=31,[Avalonia.Input.Key.P]=35,[Avalonia.Input.Key.Q]=12,[Avalonia.Input.Key.R]=15,[Avalonia.Input.Key.S]=1,[Avalonia.Input.Key.T]=17,[Avalonia.Input.Key.U]=32,[Avalonia.Input.Key.V]=9,[Avalonia.Input.Key.W]=13,[Avalonia.Input.Key.X]=7,[Avalonia.Input.Key.Y]=16,[Avalonia.Input.Key.Z]=6};
    private static bool TryHotkey(Avalonia.Input.Key key,Avalonia.Input.KeyModifiers flags,out uint code,out uint modifiers){modifiers=0;if(flags.HasFlag(Avalonia.Input.KeyModifiers.Alt))modifiers|=2048;if(flags.HasFlag(Avalonia.Input.KeyModifiers.Meta))modifiers|=256;if(flags.HasFlag(Avalonia.Input.KeyModifiers.Control))modifiers|=4096;var valid=modifiers!=0;if(flags.HasFlag(Avalonia.Input.KeyModifiers.Shift))modifiers|=512;return MacKeys.TryGetValue(key,out code)&&valid;}
    private static string HotkeyName(uint code,uint modifiers)=>((modifiers&256)!=0?"⌘ ":"")+((modifiers&2048)!=0?"⌥ ":"")+((modifiers&4096)!=0?"⌃ ":"")+((modifiers&512)!=0?"⇧ ":"")+(MacKeys.FirstOrDefault(p=>p.Value==code).Key.ToString());
}
