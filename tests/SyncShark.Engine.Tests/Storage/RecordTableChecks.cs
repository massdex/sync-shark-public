using Massdex.SyncShark.Engine.Storage;

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

        // A projection is published only after the parent move settles. Reading the old footprint must never rebase it.
        if(mapped.LocalPath!="a/x" || mapped.DropboxPath!="b/x") throw new InvalidOperationException("Unconfirmed parent move advanced its child");
        await new Committer().ProjectSubtreeAsync(db,"p",new Scope("b","b"),new Scope("B","B"),default);
        var independent=await records.FindAsync(db,"p",Side.Dropbox,"B/X",default);
        var linked=await records.FindAsync(db,"p",Side.Local,"B/Y",default);
        if(independent!= (mapped with { DropboxPath="B/x" }) || linked?.LocalPath!="B/y" || linked.DropboxPath!="B/y" || linked.DropboxContentHash!="opaque")
            throw new InvalidOperationException("Confirmed parent move changed unrelated content, missed spelling, or moved the independent endpoint");
    }
}
