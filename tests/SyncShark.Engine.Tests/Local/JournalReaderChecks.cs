using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>A paused member retains its own prefix; settling another member must not acknowledge it.</summary>
public static class JournalReaderChecks
{
    public static async Task RunAsync()
    {
        var dir=Directory.CreateTempSubdirectory();
        try
        {
            var journal=new UsnJournal(); var start=journal.Current(dir.FullName);
            var identity=new RootIdentity(dir.FullName,"/a","connection",new(1,1,null),new("a","b","c","d"));
            var a=new PairConfig("a","Pair A",dir.FullName,"/a","connection",[],[],FirstRunPolicy.NewestWins,true,null,identity);
            var b=a with { PairId="b",Name="Pair B" };
            using var store=new InMemoryStateStore();
            await store.SavePositionAsync("1",start,default);
            var reader=new JournalReader(); var scope=new PathScope(a,[a],DriveType.Fixed); var settings=SettingsParser.Load();
            foreach(var pair in new[]{a,b}) reader.Attach(pair,store,()=>scope,(_,_,_)=>(null,null),_=>null);
            reader.Membership(b,true);
            File.WriteAllBytes(Path.Combine(dir.FullName,"change"),[1]);
            var end=journal.Current(dir.FullName);
            Check.Require(end.NextUsn>start.NextUsn,"Fixture did not create journal input");
            async Task ReadToEnd(PairConfig pair)
            {
                ChangePage page; do { page=await reader.ReadPageAsync(pair,start,end,settings,default); } while(!page.CaughtUp);
                Check.Require(page.Position==end && !page.ContinuityLost,"Finite read did not reach its own boundary");
                await reader.SettleAsync(pair,end,store,default);
            }
            await ReadToEnd(a);
            reader.Attach(b);
            Check.Require(await store.PositionAsync("1",default)==start,"Paused or reattached member lost its unsettled prefix");
            reader.Membership(b,false); await ReadToEnd(b);
            Check.Require(await store.PositionAsync("1",default)==end,"Common settled prefix did not advance without another read");
        }
        finally { dir.Delete(true); }
    }
}
