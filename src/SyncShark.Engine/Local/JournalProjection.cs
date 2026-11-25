using System.Collections.Immutable;

namespace Massdex.SyncShark.Engine.Local;

/// <summary>Projects one native page into a member's ordered changes; name halves, holds and failures go back to the reader.</summary>
internal sealed class JournalProjection
{
    // USN_REASON bits from winioctl.h; only the three data-change bits establish a content write.
    internal const uint Content=7, Create=0x100, Delete=0x200, Old=0x1000, New=0x2000, Links=0x10000, Reparse=0x100000, Close=0x80000000;
    internal readonly PairConfig pair;
    internal readonly JournalReader.Member m;
    internal readonly JournalPosition from;
    internal readonly string volume;
    internal readonly ImmutableDictionary<ulong,JournalReader.Half>.Builder old;
    internal readonly ImmutableDictionary<ulong,JournalReader.Hop> physical;
    internal readonly CancellationToken ct;
    internal readonly bool firstOnly;
    internal readonly List<JournalReader.Hop>? paired;
    internal readonly List<ulong>? starts;
    internal readonly Action<ulong,ulong>? defer;
    internal readonly PathScope scope;
    internal readonly IStore store;
    internal readonly ulong root;
    internal readonly List<Change> result=new();
    internal readonly Dictionary<ulong,(string? Path,IReadOnlyList<string>? Links)> locations=new();
    private readonly Dictionary<string,List<SyncedEntry>> saved=new();
    internal readonly Dictionary<ulong,string> movedParents=new();
    internal RemovalProof? victim;
    internal ulong? failedUsn;

    private JournalProjection(PairConfig pair,JournalReader.Member m,JournalPosition from,string volume,ImmutableDictionary<ulong,JournalReader.Half>.Builder old,
        ImmutableDictionary<ulong,JournalReader.Hop> physical,CancellationToken ct,bool firstOnly,List<JournalReader.Hop>? paired,List<ulong>? starts,Action<ulong,ulong>? defer)
    {
        (this.pair,this.m,this.from,this.volume,this.old,this.physical,this.ct)=(pair,m,from,volume,old,physical,ct);
        (this.firstOnly,this.paired,this.starts,this.defer)=(firstOnly,paired,starts,defer);
        scope=m.Scope!(); store=m.Store!; root=pair.Roots!.Local.FileId;
    }

    /// <summary>Project the records before end. A first-only probe stops at the first relevant change and keeps no half, hold or failure.</summary>
    internal static async Task<(List<Change> Changes,bool Uncertain,ulong? FailedUsn)> Project(PairConfig pair,JournalReader.Member m,JournalPosition from,string volume,IReadOnlyList<UsnRecord> records,ulong end,
        ImmutableDictionary<ulong,JournalReader.Half>.Builder old,ImmutableDictionary<ulong,JournalReader.Hop> physical,CancellationToken ct,bool firstOnly=false,List<JournalReader.Hop>? paired=null,List<ulong>? starts=null,Action<ulong,ulong>? defer=null)
    {
        var page=new JournalProjection(pair,m,from,volume,old,physical,ct,firstOnly,paired,starts,defer);
        page.victim=(await page.store.ArmedAsync(pair.PairId,ct).ConfigureAwait(false))?.Victim.Proof;
        foreach(var record in records)
        {
            ct.ThrowIfCancellationRequested(); if (record.Usn>=end || firstOnly && page.result.Count!=0) break;
            var flags=record.Reason;
            if ((flags & (Content|Create|Delete|Old|New|Links|Reparse|Close))==0) continue;
            if (record.FileId==page.root)
            {
                if ((flags&(Delete|Old|New|Reparse))!=0) throw new JournalRootChangedException(pair.LocalRoot);
                continue;
            }
            var id=LocalIdentity.FileIdText(record.FileId);
            var parent=LocalIdentity.FileIdText(record.ParentId);
            var rows=await page.BindingsAsync(id).ConfigureAwait(false);
            string? history=null;
            if (record.Name is {Length:>0} name)
            {
                if (record.ParentId==page.root) history=name;
                else if (page.movedParents.TryGetValue(record.ParentId,out var folderPath)) history=PathNames.Child(folderPath,name);
                else if (physical.TryGetValue(record.ParentId,out var parentHop)) history=PathNames.Child(parentHop.FolderPath!,name);
                else if (await page.BindingsAsync(parent).ConfigureAwait(false) is [var binding]) history=PathNames.Child(binding.LocalPath,name);
            }
            await new JournalRecordProjection(page,record,id,parent,rows,history).ProjectAsync().ConfigureAwait(false);
        }
        return (page.result,page.failedUsn.HasValue,page.failedUsn);
    }

    internal static bool MetadataFailure(Exception e) => e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ChangedUnderneathException or ItemFailedException;
    internal static bool BoundRootChanged(Exception error,PairConfig pair) => error is ChangedUnderneathException changed && PathNames.Same(changed.Path,pair.LocalRoot);

    internal bool Excluded(string path,bool folder) => scope.Excludes(path,null,folder) &&
        !(scope.Exclusions.Any(e=>!e.Keep && e.Side==Side.Local && PathNames.IsSelfOrUnder(path,e.Path)) && !scope.Configured.Excludes(path,null,folder));

    internal async Task<List<SyncedEntry>> BindingsAsync(string id)
    {
        if (saved.TryGetValue(id,out var bindings)) return bindings;
        bindings=[]; await foreach(var row in store.ByIdAsync(pair.PairId,Side.Local,id,ct)) bindings.Add(row);
        return saved[id]=bindings;
    }
}
