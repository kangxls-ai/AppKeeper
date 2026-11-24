using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AppKeeper;

internal static class EntryInboxSelfTest
{
    internal static void Run(string root)
    {
        var inbox=new EntryInbox(Path.Combine(root,"shell-queue"));
        var folder=Directory.CreateDirectory(Path.Combine(root,"中文 空格 ' & 资料")).FullName;
        var file=Path.Combine(folder,"测试文档.txt");File.WriteAllText(file,"unchanged");
        Parallel.For(0,8,_=>inbox.Enqueue([file,folder+Path.DirectorySeparatorChar,file]));
        var batch=inbox.Read();Check(batch.Files.Length==8&&batch.Paths.SequenceEqual([file,folder]),"concurrent batches are atomic and deduplicated");
        Check(new EntryInbox(Path.Combine(root,"shell-queue")).Read().Files.Length==8,"requests survive process restart before acknowledgment");
        inbox.Enqueue([folder]);inbox.Complete(batch);Check(inbox.Read().Files.Length==1,"acknowledgment retains requests arriving during dialog");
        inbox.Complete(inbox.Read());Check(inbox.Read().Paths.Length==0,"explicit cancellation clears only its batch");
        File.WriteAllText(Path.Combine(inbox.DirectoryPath,"bad.json"),"{bad");Check(inbox.Read().Paths.Length==0&&File.Exists(Path.Combine(inbox.DirectoryPath,"bad.json.invalid")),"malformed request quarantined");
        Check(EntryInbox.Arguments(["--add",file]).SequenceEqual([file])&&EntryInbox.Arguments([file]).Length==0,"explicit add switch preserves literal file names");
        Check(EntryInbox.Scope(root)!=EntryInbox.Scope(Path.Combine(root,"other")),"editions and installations use distinct instance scopes");
        Check(File.ReadAllText(file)=="unchanged","source file is never modified");
    }
    private static void Check(bool value,string label){if(!value)throw new Exception("Entry inbox: "+label);}
}
