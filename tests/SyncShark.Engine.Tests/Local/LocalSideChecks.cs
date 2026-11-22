using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks the evidence carried across the local publication and listing boundaries.</summary>
public static class LocalSideChecks
{
    public static async Task RunAsync()
    {
        // A selected non-readable entry must be refused before even resolving a data filename.
        var pair = new PairConfig("pair","Pair","invalid\0root","/root","account",[],[],FirstRunPolicy.NewestWins,true,null);
        var side = LocalSide.Factory()(pair,()=>throw new InvalidOperationException(),SettingsParser.Load(),new TestDoubles.InMemoryStateStore());
        var wrong = new List<string>();
        foreach (var reason in new[]{TextKey.SkipOnlineOnly,TextKey.SkipLink,TextKey.SkipSpecial})
        {
            var entry=new Entry("file",false,1,DateTimeOffset.UnixEpoch,"1","","",reason.ToString());
            try { using var stream=await side.OpenReadAsync(entry,default); wrong.Add($"{reason}: returned a content stream"); }
            catch(ChangedUnderneathException) { }
            catch(Exception) { wrong.Add($"{reason}: content open reached the filesystem"); }
            var impossible=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"file");
            try { if(await side.RecoverAsync(new("1",impossible,entry),entry,default) is not null) wrong.Add($"{reason}: offered recovery bytes"); }
            catch(ChangedUnderneathException) { }
            catch(Exception) { wrong.Add($"{reason}: recovery reached the filesystem"); }
        }
        Check.Require(wrong.Count==0,string.Join("; ",wrong));
    }

    // Registered separately: the cross-group checks must stay NOT IMPLEMENTED until their owners land.
    public static Task LateReadFailureAsync()
    {
        // A throwing exception filter would otherwise rethrow the underlying IOException and mask a missing classifier.
        Check.Require(LocalErrors.IsFileFailure(new IOException()), "late I/O is a file failure");
        using var reader = new LocalReadStream(new FailingRead(), "broken-item");
        try { reader.ReadByte(); throw new InvalidDataException("read did not report failure"); }
        catch (ItemFailedException e) { Check.Require(e.Path == "broken-item" && e.Cause == TextKey.ReadLocalFailed, "late read lost its item"); }
        return Task.CompletedTask;
    }
    public static async Task ExcludedFirstAliasAsync()
    {
        var root=Directory.CreateTempSubdirectory().FullName;
        var path=Path.Combine(root,"one"); var alias=Path.Combine(root,"two");
        try
        {
            var (identity,drive)=DriveIdentity.Read(root);
            File.WriteAllText(path,"version");
            if(!CreateHardLinkW(alias,path,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
            var observed=DirectoryListing.One(root,"one") ?? throw new InvalidDataException("missing fixture");
            var id=unchecked((ulong)long.Parse(observed.Id,System.Globalization.CultureInfo.InvariantCulture));
            var (_,links)=IdentityPaths.ResolveLinksById(root,identity,id,true,default);
            var names=links?.Select(p=>PathNames.RelativeUnderLocalRoot(root,p)).OfType<string>().ToArray();
            if(names is not {Length:2}) throw new InvalidDataException("incomplete alias fixture");
            var pair=new PairConfig("pair","Pair",root,"/root","account",[],[new Exclusion(Side.Local,names[0],true)],
                FirstRunPolicy.NewestWins,true,null,new(root,"/root","account",identity,new("account","root","home","folder")));
            var scope=new PathScope(pair,[pair],drive);
            var side=LocalSide.Factory()(pair,()=>scope,SettingsParser.Load(),new TestDoubles.InMemoryStateStore());
            var actual=await side.ReadByIdAsync(observed.Id,default);
            Check.Require(actual is { InScope:true } && actual.Value.Location.Entry.Id==observed.Id &&
                PathNames.Same(actual.Value.Location.Entry.Path,names[1]),"excluded first alias concealed eligible second alias");
        }
        finally { File.Delete(alias); File.Delete(path); Directory.Delete(root); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32",CharSet=System.Runtime.InteropServices.CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern bool CreateHardLinkW(string alias,string existing,IntPtr security);
    private sealed class FailingRead : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException();
    }
}
