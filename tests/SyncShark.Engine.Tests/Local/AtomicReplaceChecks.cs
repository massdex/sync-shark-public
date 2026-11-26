using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>An unelevated host reads a name-redacted journal: an unbound tmp renamed over a bound file is a change, a bound rename still owes Repair.</summary>
public static class AtomicReplaceChecks
{
    private sealed class Source : UsnJournal
    {
        public UsnRecord[] Records=[];
        public override (ulong First,JournalPosition Tail) Retained(string root)=>(0,new(1,1000));
        public override Task<NtfsUsnPage> ReadAsync(string root,JournalPosition from,JournalPosition? end,int bytes,CancellationToken ct)
        {
            var records=from.NextUsn==10 ? Records : [];
            return Task.FromResult(new NtfsUsnPage(NtfsUsnPageStatus.Complete,records.Length==0 ? from.NextUsn+10 : records.Max(r=>r.Usn)+1,null,records));
        }
    }
    const uint Extend=2, Create=0x100, Delete=0x200, Old=0x1000, New=0x2000, Close=0x80000000;
    const ulong Root=11, Dir=40, Target=50, Tmp=60;

    private static UsnRecord R(ulong usn,uint reason,ulong id)=>new(id,Dir,usn,reason,FileAttributes.Normal,null);

    // Windows order for File.Move(tmp,name,overwrite): the old file's delete comes before the rename.
    public static async Task RedactedAtomicReplaceAsync()
    {
        var (changes,revision,settled,uncertain)=await Read([R(11,Create,Tmp),R(12,Create|Extend,Tmp),R(13,Create|Extend|Close,Tmp),R(14,Delete|Close,Target),R(15,Old,Tmp),R(16,New,Tmp),R(17,New|Close,Tmp)],moved:false);
        Check.Require(changes.Any(c=>c is {Kind:ChangeKind.Changed,Id:"60",Path:"dir/name"}),"atomic replace lost the change at its target path");
        Check.Require(revision==0 && uncertain==0 && settled==18,"redacted atomic replace of an unbound tmp owed Repair or held the journal position");
    }

    public static async Task RedactedBoundRenameAsync()
    {
        var (_,revision,settled,uncertain)=await Read([R(11,Old,Target),R(12,New,Target),R(13,New|Close,Target)],moved:true);
        Check.Require(revision==1 && uncertain!=0 && settled==11,"redacted rename of a bound file no longer goes to Repair");
    }

    private static async Task<(List<Change> Changes,long Revision,ulong? Settled,int Uncertain)> Read(UsnRecord[] records,bool moved)
    {
        using var store=new InMemoryStateStore(); var src=new Source{Records=records}; var reader=new JournalReader(src);
        var root=Path.Join(Path.GetTempPath(),"Pair A"); var identity=new RootIdentity(root,"/a","test",new(1,Root,null),new("a","b","c","d"));
        var a=new PairConfig("a","Pair A",root,"/a","test",[],[],FirstRunPolicy.NewestWins,true,null,identity);
        var scope=new PathScope(a,[a],DriveType.Fixed); var settings=SettingsParser.Load();
        var start=new JournalPosition(1,10); await store.SavePositionAsync("1",start,default);
        await store.CommitAsync("a",null,new(PathNames.Key("dir"),"dir","dir","dir",true,Dir.ToString(),0,0,"d",""),null,null,default);
        await store.CommitAsync("a",null,new(PathNames.Key("dir/name"),"dir/name","dir/name","dir/name",false,Target.ToString(),0,0,"c",""),null,null,default);
        await store.SavePairAsync(a,default);
        reader.Attach(a,store,()=>scope,(id,_,_)=>id switch {
                Tmp=>(Path.Join(root,"dir","name"),null),
                Target=>moved ? (Path.Join(root,"dir","renamed"),null) : (null,null),
                Dir=>(Path.Join(root,"dir"),null), _=>(null,null)},
            p=>p=="dir/name" ? new(p,false,5,DateTimeOffset.UnixEpoch,Tmp.ToString(),"","",null) : p=="dir" ? new(p,true,0,DateTimeOffset.UnixEpoch,Dir.ToString(),"","",null) : null);
        var page=await reader.ReadPageAsync(a,start,new(1,records.Max(r=>r.Usn)+1),settings,default);
        if(page.Position!=null) await reader.SettleAsync(a,page.Position,store,default);
        var debt=await store.ReadRepairAsync("a",Side.Local,default);
        return (page.Changes.ToList(),debt.Revision,(await reader.SettledJournalAsync(a,default))?.NextUsn,page.Uncertain.Count);
    }
}
