using System.Diagnostics;
using System.Xml.Linq;

namespace AppKeeper;

internal static class MacFinderSelfTest
{
    internal static async Task Run(string root)
    {
        var services=Path.Combine(root,"test-services");var executable=Path.Combine(root,"fake app's executable");
        MacFinderIntegration.Apply(executable,true,services);
        var contents=Path.Combine(services,MacFinderIntegration.Title+".workflow","Contents");
        var document=XDocument.Load(Path.Combine(contents,"document.wflow"));
        if(!document.Descendants("string").Any(e=>e.Value=="com.apple.Automator.fileSystemObject"))throw new Exception("Finder service does not accept file objects.");
        var script=MacFinderIntegration.Script(executable);if(!script.Contains("\"$@\"")||!script.Contains("'\"'\"'"))throw new Exception("Finder action loses quoted file arguments.");
        if(OperatingSystem.IsMacOS())
        {
            var output=Path.Combine(root,"finder-arguments.txt");var sample=Path.Combine(root,"中文 ' & 空格.txt");File.WriteAllText(sample,"unchanged");
            File.WriteAllText(executable,"#!/bin/bash\n/usr/bin/printf '%s\\n' \"$@\" > "+MacFinderIntegration.ShellQuote(output)+"\n");
            File.SetUnixFileMode(executable,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            var start=new ProcessStartInfo("/usr/bin/automator"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
            foreach(var arg in new[]{"-i",sample,Path.GetDirectoryName(contents)!})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try{await process.WaitForExitAsync(timeout.Token);}catch{process.Kill(true);throw;}
            var log=await stdout+await stderr;if(process.ExitCode!=0)throw new Exception("Automator workflow failed: "+log);
            for(var i=0;i<40&&!File.Exists(output);i++)await Task.Delay(50);
            if(!File.Exists(output)||!File.ReadAllLines(output).SequenceEqual(["--add",sample]))throw new Exception("Automator lost literal Unicode/space/quote arguments: "+log);
            Console.WriteLine("PASS: native Automator file Quick Action executes with literal Unicode/space/quote arguments");
        }
        MacFinderIntegration.Apply(executable,false,services);
        if(Directory.Exists(Path.GetDirectoryName(contents)))throw new Exception("Finder integration was not removed.");
    }
}
