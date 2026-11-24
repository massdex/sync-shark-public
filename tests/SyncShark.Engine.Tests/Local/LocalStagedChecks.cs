using Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

public static class LocalStagedChecks
{
    public static async Task RunAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target=Path.Combine(root,"target");
            foreach(var armed in new[]{false,true})
            {
                var name=StageNames.For(target); var moved=Path.Combine(root,"moved");
                Check.Require(StageNames.IsStage(Path.GetFileName(name)) && !StageNames.IsStage(Path.GetFileName(target)) &&
                    !StageNames.IsStage(Path.GetFileName(name)+".bak"),"stage grammar");
                File.WriteAllBytes(name,[1]);
                using var handle=Open(name,0xC0010000,FileShare.ReadWrite|FileShare.Delete,IntPtr.Zero,3,0x00300000,IntPtr.Zero);
                using var file=new FileStream(handle,FileAccess.ReadWrite); using var hash=new ContentHash(); hash.Append(new byte[]{1});
                var id=new byte[24];Check.Require(ReadId(handle,18,id,24),"stage fixture identity unavailable");
                var culture=System.Globalization.CultureInfo.InvariantCulture;
                var proof=new StageProof(name,BitConverter.ToUInt64(id).ToString(culture),BitConverter.ToInt64(id,8).ToString(culture),1,hash.Finish());
                StageProof? asked=null;
                Task<bool> Owned(StageProof p){asked=p; return Task.FromResult(armed && p==proof);}
                var stage=(LocalStaged)Activator.CreateInstance(typeof(LocalStaged),System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,
                    null,[target,proof,file,(Func<StageProof,Task<bool>>)Owned],null)!;
                File.Move(name,moved); File.WriteAllBytes(name,[2]);
                await stage.DisposeAsync(); await stage.DisposeAsync();
                Check.Require(asked==proof && handle.IsClosed && File.Exists(moved)==armed && File.ReadAllBytes(name).SequenceEqual(new byte[]{2}),
                    "stage disposal did not apply armed ownership to its actual held file");
                if(armed) Check.Require(File.ReadAllBytes(moved).SequenceEqual(new byte[]{1}),"armed stage version lost");
                File.Delete(name);
            }
        }
        finally { Directory.Delete(root,true); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32",EntryPoint="CreateFileW",ExactSpelling=true,CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path,uint access,FileShare share,IntPtr security,uint creation,uint flags,IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)]
    private static extern bool ReadId(Microsoft.Win32.SafeHandles.SafeFileHandle handle,int kind,[System.Runtime.InteropServices.Out] byte[] data,uint size);
}
