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
            var file=Path.Combine(dir.FullName,"change"); File.WriteAllBytes(file,[1]);
            using var held=File.OpenHandle(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var (fileId,_)=UsnJournalChecks.FileRecord(held);
            var id=unchecked((long)fileId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var entry=new Entry("change",false,1,DateTimeOffset.UnixEpoch,id,"","",null);
            foreach(var pair in new[]{a,b}) reader.Attach(pair,store,()=>scope,(key,_,_)=>key==fileId ? (file,null) : (null,null),_=>entry);
            reader.Membership(b,true);
            var end=journal.Current(dir.FullName);
            Check.Require(end.NextUsn>start.NextUsn,"Fixture did not create journal input");
            ulong? firstRelevant=null;
            async Task ReadToEnd(PairConfig pair)
            {
                ChangePage page; bool projected=false;
                do
                {
                    page=await reader.ReadPageAsync(pair,start,end,settings,default);
                    foreach(var c in page.Changes.Where(c=>c.Id==id)) firstRelevant??=c.Usn;
                    projected |= page.Changes.Any(c=>c.Path==entry.Path && c.Id==id && c.LocalContentDirty && c.JournalId==start.JournalId);
                } while(!page.CaughtUp);
                Check.Require(projected,"Active or resumed member missed its ordered content-write record");
                Check.Require(page.Position==end && !page.ContinuityLost,"Finite read did not reach its own boundary");
                await reader.SettleAsync(pair,end,store,default);
            }
            await ReadToEnd(a);
            reader.Attach(b);
            Check.Require(await store.PositionAsync("1",default)==new JournalPosition(start.JournalId,firstRelevant!.Value),"Paused or reattached member lost its unsettled prefix");
            reader.Membership(b,false); await ReadToEnd(b);
            Check.Require(await store.PositionAsync("1",default)==end,"Common settled prefix did not advance without another read");
        }
        finally { dir.Delete(true); }
    }
}
