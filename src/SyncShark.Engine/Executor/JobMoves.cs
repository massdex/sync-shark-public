namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Settle one saved move job member: at its destination, or by following the observed arrival. An unexplained arrival is reported, never guessed.</summary>
internal static class JobMoves
{
    internal static async Task<bool> SettleAsync(Unfinished owner,Unfinished.Call call,JobMember member,string id,string destination,SyncedEntry? binding,Scope scope)
    {
        (IdentityLocation Location,bool InScope)? where;
        try {where=await call.Dropbox.ReadByIdAsync(id,call.Ct);}
        catch(ItemFailedException) {return false;}
        if(where is {InScope:true} arrived)
        {
            if(arrived.Location.Entry.Id!=id || arrived.Location.Entry.Skip!=null) return false;
            if(!PathNames.Same(arrived.Location.Entry.Path,destination) || !PathNames.SameName(arrived.Location.Entry.Path,destination))
            {
                if(binding==null || PathNames.Same(arrived.Location.Entry.Path,member.Path)) return false;
                var peer=await call.Local.ReadByIdAsync(binding.LocalId,call.Ct);
                if(peer is {InScope:true} pc && pc.Location.Entry.Id==binding.LocalId && pc.Location.Entry.Skip==null && arrived.Location.Entry.Skip==null)
                {
                    var moves=new List<RenameCandidate>();if(pc.Location.Entry.Path!=binding.LocalPath) moves.Add(new(Side.Local,pc.Location.Entry,binding));
                    moves.Add(new(Side.Dropbox,arrived.Location.Entry,binding));
                    if(!await RowExecution.ExecuteAsync(call.Pair,call.Settings,call.Events,call.Paths,null,moves,false,call.Local,call.Dropbox,call.Store,call.Populate,call.CompareOldBinding,call.Ct,call.HeldInput,null,false,coversThrough:owner.CoversThrough)) return false;
                }
                else await UnknownMove(call,member,id,destination,binding);
                return true;
            }
            if(binding==null) return false;
            await call.CompareOldBinding(binding,scope,new(scope.LocalPath,arrived.Location.Entry.Path),call.Ct);
        }
        else if(binding!=null)
        {
            var peer=await call.Local.ReadByIdAsync(binding.LocalId,call.Ct);
            if(peer is {InScope:true} present && present.Location.Entry.Skip==null && present.Location.Entry.Path!=binding.LocalPath)
            {
                if(!await RowExecution.ExecuteAsync(call.Pair,call.Settings,call.Events,call.Paths,null,[new(Side.Local,present.Location.Entry,binding)],false,call.Local,call.Dropbox,call.Store,call.Populate,call.CompareOldBinding,call.Ct,call.HeldInput,null,false,coversThrough:owner.CoversThrough)) return false;
            }
            else await UnknownMove(call,member,id,destination,binding);
        }
        else return false;
        return true;
    }

    private static Task UnknownMove(Unfinished.Call call,JobMember member,string id,string destination,SyncedEntry binding)=>call.Store.SaveNoticeAsync(call.Pair.PairId,new(0,NoticeKind.Failure,TextKey.FailDropboxOutcomeUnknown,member.Path,
        new IssueSubject([new(Side.Dropbox,member.Path,id)],Side.Dropbox){Move=new(id,member.Path,destination,null,binding.IsFolder)},DateTimeOffset.UtcNow,false),call.Ct);
}
