using Microsoft.Win32.SafeHandles;
using static Massdex.SyncShark.Engine.Local.LocalNative;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Holds every directory from a volume root down to one parent open, so no pinned name can be replaced.</summary>
internal sealed class ParentHandles : IDisposable
{
    private readonly List<SafeFileHandle> handles=[];
    internal SafeFileHandle Last=>handles[^1];
    internal ParentHandles(string directory)
    {
        var chain=new Stack<string>();
        for(var d=new DirectoryInfo(directory);d is not null;d=d.Parent) chain.Push(d.FullName);
        try
        {
            foreach(var name in chain) Pin(name);
        }
        catch { Dispose(); throw; }
    }
    internal void PinDescendants(string root,string parent)
    {
        var relative=Path.GetRelativePath(root,parent);
        var current=root;
        if(relative!=".") foreach(var name in relative.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar))
        { current=Path.Combine(current,name); Pin(current); }
    }
    internal void Pin(string name)
    {
        var h=Open(name,ReadAttributes|0x20,FileShare.ReadWrite); handles.Add(h);
        var info=Info(h);
        if(!info.Folder || (IsLink(info.Attributes) && Classify(h,info.Attributes) is not null)) throw new ChangedUnderneathException(name);
    }
    public void Dispose() { for(int i=handles.Count-1;i>=0;i--) handles[i].Dispose(); }
}
