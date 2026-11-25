namespace AppKeeper;

internal static class MacStartup
{
    internal static FileStream? TryAcquire(string root)
    {
        Directory.CreateDirectory(root);
        try{return new FileStream(Path.Combine(root,"instance.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){return null;}
    }
    internal static void RequestActivation(string root)=>File.WriteAllText(Path.Combine(root,"activate-main.request"),"");
    internal static bool ConsumeActivation(string root)
    {
        var file=Path.Combine(root,"activate-main.request");if(!File.Exists(file))return false;File.Delete(file);return true;
    }
}
