using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks actual v16 encodings, mapped keys, hard-link pages and the publication boundary.</summary>
public static class RecordTableChecks
{
    /// <summary>All three read paths recover the same saved bytes and boundary without crossing the side's footprint.</summary>
    public static async Task RunAsync()
    {
        using var db=PairTableChecks.Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="""
            INSERT INTO synced_entry(pair_id,local_path,dropbox_key_delta,dropbox_path_delta,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash,local_published_journal,local_published_usn) VALUES
              (1,'a/x','B/X','b/x',0,7,1,2,'x',zeroblob(32),9,12),
              (1,'b/y',NULL,NULL,0,7,1,2,'y','opaque',NULL,NULL),
              (1,'beyond',NULL,NULL,0,'07',1,2,'z','opaque',NULL,NULL)
            """;
        cmd.ExecuteNonQuery(); var records=new RecordTable();
        var mapped=await records.FindAsync(db,"p",Side.Dropbox,"B/X",default);
        if(mapped is null || mapped.LocalPublished!=new JournalPosition(9,12) || mapped.LocalId!="7" || mapped.DropboxContentHash!=new string('0',64)) throw new InvalidOperationException("Lost compact binding or publication");
        if(await records.FindAsync(db,"p",Side.Dropbox,"A/X",default)!=null) throw new InvalidOperationException("Mapped row inherited its old key");
        var aliases=new List<SyncedEntry>(); await foreach(var row in records.ByIdAsync(db,"p",Side.Local,"7",1,default)) aliases.Add(row);
        if(aliases.Count!=2 || !aliases.Contains(mapped)) throw new InvalidOperationException("Indexed ID lost a page or matched opaque 07");
        var children=new List<SyncedEntry>(); await foreach(var row in records.DescendantsAsync(db,"p",Side.Dropbox,"B",1,default)) children.Add(row);
        if(children.Count!=2 || !children.Contains(mapped) || !children.Any(x=>x.DropboxId=="y" && x.LocalPublished==null)) throw new InvalidOperationException("Mapped and inherited descendants differ");

        foreach(var outcome in new[]{RemovalOutcome.NoEffect,RemovalOutcome.Unknown,RemovalOutcome.Published})
            await MoveSettlement(outcome);
    }

    private static async Task MoveSettlement(RemovalOutcome outcome)
    {
        var root=Directory.CreateTempSubdirectory();
        try
        {
            var settings=ItemChecks.Settings; var pair=ItemChecks.Pair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
            using var store=await Store.OpenAsync(root.FullName,true,settings,default) ?? throw new InvalidDataException();
            await store.SavePairAsync(pair,default);
            var pc=new FakeSide(Side.Local); var cloud=new FakeSide(Side.Dropbox); var queue=new WorkQueue();
            const string before="Parent/old", after="Parent/OLD";
            var left=ItemChecks.File(Side.Local,before,1,"10",true); var right=ItemChecks.File(Side.Dropbox,before,1,"folder",true);
            var parent=SyncedEntries.From(left,right); await store.CommitAsync(pair.PairId,null,parent,null,null,default);
            pc.Seed(left with {Path=after},[]);cloud.Seed(right,[]);
            var stamp=new JournalPosition(23,41);
            async Task<SyncedEntry> Child(string local,string actualLocal,string remote,string lid,string did)
            {
                var a=ItemChecks.File(Side.Local,local,2,lid); var b=ItemChecks.File(Side.Dropbox,remote,2,did);
                pc.Seed(a with {Path=actualLocal},[2]); cloud.Seed(b,[2]); var record=SyncedEntries.From(a,b);
                await store.CommitAsync(pair.PairId,null,record,stamp,null,default);
                return record with {LocalPublished=stamp};
            }
            var linked=await Child(before+"/child",after+"/child",before+"/child","11","child");
            var independent=await Child("Elsewhere/file","Elsewhere/file",before+"/file","12","mapped");
            if(outcome!=RemovalOutcome.Published) cloud.MoveOutcomes.Enqueue(new(outcome,null,TextKey.FailChanged));
            var row=new Row(parent.LocalKey,left with {Path=after},right,parent,[],null,RowContext.Neutral);
            var action=new SyncAction(ActionKind.MoveDropbox,row,Target:after);
            var result=await new Effects().ApplyAsync(pair,settings,_=>{},paths,action,pc,cloud,store,ItemChecks.Population(pair,paths,pc,cloud,store),ItemChecks.Hop(pair,paths,queue,pc,cloud,store),default);
            Check.Require(cloud.Requests[nameof(ISide.MoveAsync)]>0 && ((result==EffectOutcome.Applied)==(outcome==RemovalOutcome.Published)),"Folder move outcome was not exercised or was settled incorrectly");
            foreach(var record in new[]{linked,independent})
            {
                var expected=outcome==RemovalOutcome.Published ? record with {LocalPath=record==linked?after+"/child":record.LocalPath,DropboxPath=after+"/"+PathNames.Name(record.DropboxPath)} : record;
                var actual=await store.FindAsync(pair.PairId,Side.Local,record.LocalKey,default);
                Check.Require(actual==expected,$"Folder move {outcome} changed an unconfirmed path or an unrelated binding fact");
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();root.Delete(true); }
    }
}
