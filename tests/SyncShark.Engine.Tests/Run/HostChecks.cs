using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Host startup and read-only commands never create an absent store.</summary>
public static class HostChecks
{
    public static async Task RunAsync()
    {
        var opens = new List<bool>();
        var host = new Host((create,_) => {opens.Add(create);return Task.FromResult<IStore?>(null);},
            (_,_,_,_)=>throw new InvalidOperationException(),ItemChecks.Settings,null!,TimeProvider.System,
            _=>Task.FromResult("connection"),(_,_)=>Task.CompletedTask);
        Check.Require((await host.StartAsync(default)).Count==0,"Empty machine acquired a volume");
        await foreach(var _ in host.PairsAsync(default)) throw new InvalidDataException("Invented pair");
        Check.Require(opens.Count>0 && !opens.Contains(true),"Read-only command requested store creation");
        await host.PauseAsync(null,default); await host.ResumeAsync("pair",default);
        Check.Require(host.IsPaused("pair"),"Pair resume undid global pause");
        await host.PauseAsync("other",default); await host.ResumeAsync(null,default);
        Check.Require(!host.IsPaused("pair") && host.IsPaused("other"),"Global resume lost a pair pause");
        var before=opens.Count;
        try { await host.SetExclusionAsync("pair",new(Side.Local,"../outside",false),false,true,default); throw new InvalidDataException("Accepted an escaping exclusion"); }
        catch(EngineRequestException e) when(e.Key==TextKey.ExclusionPathInvalid) { }
        Check.Require(before==opens.Count,"Invalid exclusion reached storage");
        host.SetStartupMessage("switch refused",true);
        Check.Require(await host.StatusAsync(null,default)=="switch refused" && (await host.StartAsync(default)).Count==0 && before==opens.Count,"Switch refusal opened the store");
        var stop=host.Stopping; await host.StopAsync(default); Check.Require(stop.IsCancellationRequested,"Host did not notify the pipe owner of stop");
    }
}
