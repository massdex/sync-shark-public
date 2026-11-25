using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Real-store composition of the folder-spend expiry: a confirmed fresh folder link retires a timeless
/// spent name (so the next Dropbox delete wins again) but never a dated file deletion, and a rejected insert retires nothing.</summary>
public static class SpentMarkerChecks
{
    public static async Task RunAsync()
    {
        var directory=Directory.CreateTempSubdirectory();
        try
        {
            var image=Path.Combine(directory.FullName,"fixture.sqlite");
            using var con=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=image,Pooling=false}.ToString());
            await con.OpenAsync();
            con.CreateFunction<string,string>("pathkey",PathNames.Key,isDeterministic:true);
            using var seed=con.CreateCommand(); seed.CommandText=Schema.Create+$"PRAGMA user_version={Schema.Version};"; await seed.ExecuteNonQueryAsync();
            using var store=new Store(image,false,ItemChecks.Settings);
            var pair=ItemChecks.Pair(); await store.SavePairAsync(pair,default);

            // Publish one deleted-name acquisition so the durable block holds these names, exactly as an ordinary Repair's history does.
            var scan=Guid.NewGuid(); store.BeginScan(pair.PairId,scan);
            static async IAsyncEnumerable<Listed> Deleted()
            {
                await Task.CompletedTask;
                yield return new("spentfolder",null);
                yield return new("controlfolder",null);
                yield return new("datedfolder",null);
                yield return new("occupied",null);
            }
            await store.AddScanAsync(pair.PairId,scan,Side.Dropbox,Deleted(),()=> "marker",default);
            await store.PublishDeletedHistoryAsync(pair.PairId,scan,default);

            string xKey=PathNames.DropboxKey("spentfolder"), yKey=PathNames.DropboxKey("controlfolder"), tKey=PathNames.DropboxKey("datedfolder"), dKey=PathNames.DropboxKey("occupied");
            static SyncedEntry Folder(string path,string id)=>SyncedEntries.From(ItemChecks.File(Side.Local,path,0,id,true),ItemChecks.File(Side.Dropbox,path,0,id+"d",true));

            // J1 (positive) + Record variant: D1 folder removal spends the name timelessly (Effects settles after=null with the folder key);
            // the fresh folder link written when X is restored and recorded retires that spend, so a later D2 is visible again.
            var xb=Folder("spentfolder","fx");
            await store.CommitAsync(pair.PairId,null,xb,null,null,default);
            await store.CommitAsync(pair.PairId,xb,null,null,xKey,default);
            Check.Require(!await store.DeletedNameAsync(pair.PairId,null,xKey,true,default),"the D1 folder spend did not mask the restored name");
            await store.CommitAsync(pair.PairId,null,xb,null,null,default);
            Check.Require(await store.DeletedNameAsync(pair.PairId,null,xKey,true,default),"the fresh folder link did not retire the timeless spend (D2 would stay hidden)");
            Check.Require(await store.ConsumedDeletionAsync(pair.PairId,xKey,default)==null,"a retired timeless spend left a resident date");

            // J2 (no-signal control in the same fixture): with no fresh binding in between, the timeless spend stays and keeps masking.
            var yb=Folder("controlfolder","fy");
            await store.CommitAsync(pair.PairId,null,yb,null,null,default);
            await store.CommitAsync(pair.PairId,yb,null,null,yKey,default);
            Check.Require(!await store.DeletedNameAsync(pair.PairId,null,yKey,true,default),"the no-signal control lost its timeless spend with no binding in between");

            // J3 (dated file deletion never retired): a fresh folder link at a file-spent key keeps the exact consumed time.
            var when=DateTimeOffset.UnixEpoch.AddDays(3);
            await store.CommitAsync(pair.PairId,null,null,null,tKey,default,when);
            await store.CommitAsync(pair.PairId,null,Folder("datedfolder","ft"),null,null,default);
            Check.Require(await store.ConsumedDeletionAsync(pair.PairId,tKey,default)==when,"a fresh folder link erased a dated file deletion");

            // Rejected insert retention: an occupied Dropbox key blocks a fresh folder insert and the timeless spend survives it.
            await store.CommitAsync(pair.PairId,null,Folder("occupied","fd"),null,null,default);
            await store.CommitAsync(pair.PairId,null,null,null,dKey,default);
            bool rejected=false;
            try { await store.CommitAsync(pair.PairId,null,SyncedEntries.From(ItemChecks.File(Side.Local,"collide",0,"fc",true),ItemChecks.File(Side.Dropbox,"occupied",0,"fcd",true)),null,null,default); }
            catch(Exception) { rejected=true; }
            Check.Require(rejected,"a fresh folder insert onto an occupied Dropbox key was not rejected");
            Check.Require(!await store.DeletedNameAsync(pair.PairId,null,dKey,true,default),"a rejected fresh folder insert retired the timeless spend");
        }
        finally { directory.Delete(true); }
    }
}
