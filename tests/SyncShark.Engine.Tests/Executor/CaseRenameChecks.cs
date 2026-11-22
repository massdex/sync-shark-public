using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Check both folder hops against actual physical endpoints and the unchanged logical binding.</summary>
public static class CaseRenameChecks
{
    public static async Task RunAsync()
    {
        var side = new FakeSide(Side.Dropbox);
        var folder = ItemChecks.File(Side.Dropbox,"old",0,"folder",true);
        side.Seed(folder,[]);
        var binding = SyncedEntries.From(folder with {Id="pc"},folder);
        var start = new Scope("OLD",folder.Path); var goal = new Scope("OLD","OLD");
        var temporary = goal with {DropboxPath=PathNames.CaseRenamePath(goal.DropboxPath,ItemChecks.Settings.CaseRenameSuffix)};
        var hops = new List<(Scope Before, Scope After)>(); var events = new List<RunEvent>();
        foreach (var refusal in new[]{RemovalOutcome.NoEffect,RemovalOutcome.Unknown})
        {
            side.MoveOutcomes.Enqueue(new(refusal,null,TextKey.FailChanged));
            var refused = await new CaseRename().MoveAsync(side,folder,goal.DropboxPath,ItemChecks.Settings.CaseRenameSuffix,binding,start,(_,_,_,_)=>throw new InvalidDataException("Unconfirmed hop was compared"),default,events.Add);
            Check.Require(refused.Outcome==refusal && events.Count==0 && side.Items.Values.Single().Entry==folder,"Unconfirmed hop was logged, advanced or settled");
        }
        var blocked = await new CaseRename().MoveAsync(side,folder,goal.DropboxPath,ItemChecks.Settings.CaseRenameSuffix,binding,start,(_,_,_,_)=>
        { side.MoveOutcomes.Enqueue(new(RemovalOutcome.NoEffect,null,TextKey.FailChanged)); return Task.CompletedTask; },default);
        Check.Require(blocked.Outcome==RemovalOutcome.NoEffect && blocked.Final?.Id==folder.Id && blocked.Final.Path==temporary.DropboxPath,"Second leg refusal lost the confirmed temporary source");
        await side.MoveAsync(side.Items[temporary.DropboxPath].Entry,folder.Path,default);
        var result = await new CaseRename().MoveAsync(side,folder,goal.DropboxPath,ItemChecks.Settings.CaseRenameSuffix,binding,start,(saved,before,after,ct) =>
        {
            Check.Require(saved == binding && side.Items[after.DropboxPath].Entry.Path == after.DropboxPath,"Handoff lost binding or ran before physical movement");
            hops.Add((before,after)); return Task.CompletedTask;
        },default,events.Add);
        Check.Require(result.Final?.Id == folder.Id && hops.SequenceEqual(new[]{(start,temporary),(temporary,goal)}),"Both physical folder ranges must be compared in order");
        Check.Require(events.Select(e=>e.Path).SequenceEqual(new[]{temporary.DropboxPath,goal.DropboxPath}),"Physical legs not logged individually");
        using var store = new InMemoryStateStore(); var pair = ItemChecks.Pair();
        var unlinked = new Row("old",null,result.Final,null,[],null,RowContext.Neutral);
        events.Clear();
        var outcome = await new Effects().ApplyAsync(pair,ItemChecks.Settings,events.Add,new(pair,[pair],DriveType.Fixed),new(ActionKind.MoveUnlinked,unlinked,Target:"old",Side:Side.Dropbox),new FakeSide(Side.Local),side,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(outcome==EffectOutcome.Applied && events.Count(e=>e.What==nameof(ISide.MoveAsync))==2 && store.Records.Count==0,"Unlinked case resolution bypassed its two physical legs");
        var lone=ItemChecks.File(Side.Dropbox,"chosen",9,"lone"); var occupant=lone with {Path="taken",Id="occupant"};
        side.Seed(lone,[9]); side.Seed(occupant,[9]); var untouched=new FakeSide(Side.Local);
        try
        {
            await new Effects().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),new(ActionKind.MoveUnlinked,new(lone.Path,null,lone,null,[],null,RowContext.Neutral),Target:occupant.Path,Side:Side.Dropbox),untouched,side,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
            throw new InvalidDataException("Occupied unlinked resolution ran a bilateral collision");
        }
        catch(PathOccupiedException ex) when(ex.Path==occupant.Path) { }
        Check.Require(untouched.Items.Count==0 && store.Records.Count==0 && side.Items[lone.Path].Entry==lone && side.Items[occupant.Path].Entry==occupant,"Unlinked resolution touched another endpoint or binding");
        var pc=new FakeSide(Side.Local); var settledPc=folder with {Path="OLD",Id="pc"}; pc.Seed(settledPc,[]);
        var old=binding with {LocalPath="before",LocalKey=PathNames.Key("before"),DropboxPath="before",DropboxKey=PathNames.DropboxKey("before")};
        await store.CommitAsync(pair.PairId,null,old,null,null,default);
        var actual=side.Items["old"].Entry; var replay=new Row(old.LocalKey,settledPc,actual,old,[],null,RowContext.Neutral);
        var compared=false;
        try
        {
            await new Effects().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),new(ActionKind.Record,replay),pc,side,store,(_,_,_,_)=>throw new InvalidOperationException(),(saved,before,after,_)=>
            {compared=saved==old && before==new Scope("before","before") && after==new Scope("OLD","old");throw new IOException("Unread hop range");},default);
            throw new InvalidDataException("Already-at-goal folder move settled without comparing its old bindings");
        }
        catch(IOException ex) when(ex.Message=="Unread hop range") { }
        Check.Require(compared && store.Records.Values.Contains(old),"Failed replay lost old folder binding");
    }
}
