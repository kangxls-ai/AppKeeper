using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace AppKeeper;

internal sealed partial class MacWindow
{
    private async Task ExternalEntriesSmoke()
    {
        var root=Directory.CreateDirectory(Path.Combine(state.Root,"external-add-test")).FullName;
        var file=Path.Combine(root,"中文 ' & 文件.txt");File.WriteAllText(file,"unchanged");
        var folder=Directory.CreateDirectory(Path.Combine(root,"中文 文件夹")).FullName;
        var inbox=new EntryInbox(state.Root);inbox.Enqueue([file,folder]);await Task.Delay(550);
        var adding=DrainExternalEntries();await Task.Delay(100);
        var dialog=OwnedWindows.Single(w=>w.Title=="添加到 EasyAppKeeper");
        dialog.GetVisualDescendants().OfType<TextBox>().Single(t=>t.Watermark?.StartsWith("新建标签")==true).Text="右键测试";
        var output=Environment.GetEnvironmentVariable("APPKEEPER_MAC_UI_DIR");
        if(!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(output);using var bitmap=new RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width,(int)dialog.Bounds.Height));bitmap.Render(dialog);bitmap.Save(Path.Combine(output,"添加入口.png"));
        }
        dialog.Close(true);await adding;
        if(state.Data.Entries.Count(e=>e.Target==file||e.Target==folder)!=2||state.Data.Entries.Where(e=>e.Target==file||e.Target==folder).Any(e=>!e.Tags.Contains("右键测试")||string.IsNullOrEmpty(e.Icon)))throw new Exception("右键批量入口、图标或新建标签未保存。");
        inbox.Enqueue([file,folder]);await Task.Delay(550);adding=DrainExternalEntries();await Task.Delay(80);
        dialog=OwnedWindows.Single(w=>w.Title=="添加到 EasyAppKeeper");
        dialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex=state.Data.Tags.IndexOf("右键测试")+1;
        // A later request must survive confirmation of the already displayed batch.
        inbox.Enqueue([file]);dialog.Close(true);await adding;
        if(state.Data.Entries.Count(e=>e.Target==file||e.Target==folder)!=2||inbox.Read().Files.Length!=1)throw new Exception("重复添加产生副本或误消费稍后的请求。");
        await Task.Delay(550);adding=DrainExternalEntries();await Task.Delay(80);
        dialog=OwnedWindows.Single(w=>w.Title=="添加到 EasyAppKeeper");dialog.Close(false);await adding;
        if(inbox.Read().Files.Length!=0||new MacState(state.Root).Data.Entries.Count(e=>e.Target==file||e.Target==folder)!=2||File.ReadAllText(file)!="unchanged")throw new Exception("取消或持久化验收失败。");
        Console.WriteLine("PASS: Mac external-add real dialog, new/existing tags, file/folder icons, dedup, cancellation, concurrent request acknowledgment and persistence");
    }
}
