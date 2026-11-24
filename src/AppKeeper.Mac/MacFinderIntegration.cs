using System.Xml.Linq;

namespace AppKeeper;

internal static class MacFinderIntegration
{
    internal const string Title="添加到 EasyAppKeeper";
    private static XElement Value(object value)=>value switch
    {
        bool b=>new XElement(b?"true":"false"),int n=>new XElement("integer",n),
        Dictionary<string,object> d=>new XElement("dict",d.SelectMany(p=>new[]{new XElement("key",p.Key),Value(p.Value)})),
        object[] array=>new XElement("array",array.Select(Value)),_=>new XElement("string",value.ToString())
    };
    internal static string ShellQuote(string value)=>"'"+value.Replace("'","'\"'\"'")+"'";
    internal static string Script(string executable)=>"/usr/bin/nohup "+ShellQuote(executable)+" --add \"$@\" >/dev/null 2>&1 &\n";
    internal static void Apply(string executable,bool enabled,string? servicesRoot=null)
    {
        var services=servicesRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Library","Services");
        var workflow=Path.Combine(services,Title+".workflow");var contents=Path.Combine(workflow,"Contents");
        // Remove only our own two files; do not recursively delete a user-created workflow.
        if(!enabled)
        {
            foreach(var name in new[]{"Info.plist","document.wflow"}){var file=Path.Combine(contents,name);if(File.Exists(file))File.Delete(file);}
            if(Directory.Exists(contents)&&!Directory.EnumerateFileSystemEntries(contents).Any())Directory.Delete(contents);
            if(Directory.Exists(workflow)&&!Directory.EnumerateFileSystemEntries(workflow).Any())Directory.Delete(workflow);
            return;
        }
        Dictionary<string,object> types(string type)=>new(){["Container"]="List",["Types"]=new object[]{type}};
        var action=new Dictionary<string,object>
        {
            ["AMAccepts"]=types("com.apple.cocoa.string"),["AMProvides"]=types("com.apple.cocoa.string"),
            ["AMActionVersion"]="2.0.3",["AMApplication"]=new object[]{"Automator"},["AMParameterProperties"]=new Dictionary<string,object>(),
            ["ActionBundlePath"]="/System/Library/Automator/Run Shell Script.action",["ActionName"]="Run Shell Script",
            ["ActionParameters"]=new Dictionary<string,object>{["COMMAND_STRING"]=Script(executable),["CheckedForUserDefaultShell"]=true,["inputMethod"]=1,["shell"]="/bin/bash",["source"]=""},
            ["BundleIdentifier"]="com.apple.RunShellScript",["Class Name"]="RunShellScriptAction",["UUID"]="991BA349-77B0-4300-A0EA-1497E480F941",
            ["InputUUID"]="5A6A4DB3-4781-49CF-9B7D-89517C728243",["OutputUUID"]="8B9A2E15-09F3-421F-9E5D-682507F807BC",["isViewVisible"]=false
        };
        var metadata=new Dictionary<string,object>
        {
            ["serviceInputTypeIdentifier"]="com.apple.Automator.fileSystemObject",["serviceOutputTypeIdentifier"]="com.apple.Automator.nothing",
            ["serviceApplicationBundleID"]="com.apple.finder",["serviceApplicationPath"]="/System/Library/CoreServices/Finder.app",["serviceProcessesInput"]=0,
            ["useAutomaticInputType"]=false,["workflowTypeIdentifier"]="com.apple.Automator.servicesMenu"
        };
        var document=new Dictionary<string,object>{["AMApplicationBuild"]="523",["AMApplicationVersion"]="2.10",["AMDocumentVersion"]="2",["actions"]=new object[]{new Dictionary<string,object>{["action"]=action}},["connectors"]=Array.Empty<object>(),["workflowMetaData"]=metadata};
        var service=new Dictionary<string,object>{["NSMenuItem"]=new Dictionary<string,object>{["default"]=Title},["NSMessage"]="runWorkflowAsService",["NSSendFileTypes"]=new object[]{"public.item"}};
        var info=new Dictionary<string,object>{["CFBundleIdentifier"]="io.kangxls.EasyAppKeeper.add-service",["NSServices"]=new object[]{service}};
        Directory.CreateDirectory(contents);
        Write(Path.Combine(contents,"document.wflow"),document);Write(Path.Combine(contents,"Info.plist"),info);
    }
    private static void Write(string path,Dictionary<string,object> data)
    {
        var text=new XDocument(new XDeclaration("1.0","UTF-8",null),new XElement("plist",new XAttribute("version","1.0"),Value(data))).ToString();
        if(!File.Exists(path)||File.ReadAllText(path)!=text)File.WriteAllText(path,text);
    }
}
