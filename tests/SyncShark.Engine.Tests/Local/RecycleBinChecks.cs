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
        await SubstituteVersionAsync();
        await TrimContinuesAsync();
        // Non-removable drives must not touch even a similarly named directory.
        await new RecycleBin().TrimAsync("invalid\0root",DriveType.Fixed,DateTimeOffset.UnixEpoch,default);
    }
    private static async Task SubstituteVersionAsync()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(path,[11]);
            var before=DateTime.UnixEpoch.AddYears(35); File.SetLastWriteTimeUtc(path,before);
            using var metadata=File.OpenHandle(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var id=new byte[24];
            Check.Require(GetFileInformationByHandleEx(metadata,18,id,(uint)id.Length),"missing NTFS fixture identity");
            var culture=System.Globalization.CultureInfo.InvariantCulture;
            var first=new IdentityLocation(BitConverter.ToUInt64(id).ToString(culture),path,
                new Entry("victim",false,1,before,BitConverter.ToInt64(id,8).ToString(culture),"","",null));
            // The shell has identified the object, but another open can still edit it before custody of its bytes.
            byte[] newest=[22,33,44]; var after=before.AddDays(1);
            File.WriteAllBytes(path,newest); File.SetAttributes(path,FileAttributes.Normal); File.SetLastWriteTimeUtc(path,after);
            var receipt=await RecycleBin.ReadSubstituteReceiptAsync(first,metadata,default);
            using var digest=new ContentHash(); digest.Append(newest);
            Check.Require(receipt.Outcome==RemovalOutcome.Retained && receipt.RecycledVictim==
                (first with {Entry=first.Entry with {Size=newest.Length,Modified=after,ContentHash=digest.Finish()}}),
                "substitute receipt mixed earlier metadata with the later content digest");
        }
        finally { File.Delete(path); }
    }
    [System.Runtime.InteropServices.DllImport("kernel32",SetLastError=true)]
    private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h,int kind,
        [System.Runtime.InteropServices.Out] byte[] data,uint bytes);
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
