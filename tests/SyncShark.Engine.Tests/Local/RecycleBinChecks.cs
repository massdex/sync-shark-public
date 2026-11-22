using Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

public static class RecycleBinChecks
{
    public static async Task RunAsync()
    {
        Check.Require(RecycleBin.BinName("first.txt",false,1)=="first.txt" && RecycleBin.BinName("first.txt",false,2)=="first (2).txt" &&
            RecycleBin.BinName("group.a",true,3)=="group.a (3)","bin collisions preserve both items");
        var untouched = new Entry("file",false,0,DateTimeOffset.UnixEpoch,"1","","",null);
        try { await new RecycleBin().RemoveAsync("invalid\0root",DriveType.Removable,untouched,null,default);
            throw new InvalidDataException("removal accepted no exact victim"); }
        catch (ChangedUnderneathException) { }
        await TrimContinuesAsync();
        // Non-removable drives must not touch even a similarly named directory.
        await new RecycleBin().TrimAsync("invalid\0root",DriveType.Fixed,DateTimeOffset.UnixEpoch,default);
    }
    private static async Task TrimContinuesAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        var drive=Enumerable.Range('Q',10).Reverse().Select(c=>((char)c)+":").First(d=>!Directory.Exists(d+Path.DirectorySeparatorChar));
        try
        {
            Subst(drive,root);
            try
            {
                var bin=Directory.CreateDirectory(Path.Combine(root,ResourceNames.RootBinFolder)).FullName;
                // Never run a removable-bin operation until its synthetic volume root is proven to be ours.
                var marker=Guid.NewGuid().ToString("N"); File.WriteAllText(Path.Combine(root,marker),marker);
                Check.Require(File.ReadAllText(Path.Combine(drive+Path.DirectorySeparatorChar,marker))==marker,"drive is not the fixture");
                var first=Path.Combine(bin,"a"); var later=Path.Combine(bin,"z");
                File.WriteAllText(first,"held"); File.WriteAllText(later,"expired");
                File.SetCreationTimeUtc(first,DateTime.UtcNow.AddDays(-40)); File.SetCreationTimeUtc(later,DateTime.UtcNow.AddDays(-40));
                using var handle=File.Open(first,FileMode.Open,FileAccess.Read,FileShare.None);
                bool reported=false;
                try { await new RecycleBin().TrimAsync(drive+Path.DirectorySeparatorChar,DriveType.Removable,DateTimeOffset.UtcNow,default); }
                catch(IOException) { reported=true; }
                Check.Require(File.Exists(first) && !File.Exists(later) && reported,"failed trim item prevented sibling cleanup or failure reporting");
            }
            finally { Subst(drive,"/D"); }
        }
        finally { Directory.Delete(root,true); }
    }
    private static void Subst(string drive,string argument)
    {
        var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"subst.exe")){UseShellExecute=false};
        start.ArgumentList.Add(drive); start.ArgumentList.Add(argument);
        using var command=System.Diagnostics.Process.Start(start)!; command.WaitForExit();
        Check.Require(command.ExitCode==0,"could not prepare or release synthetic volume");
    }
}
