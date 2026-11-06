namespace Massdex.SyncShark;

/// <summary>The AutoSync controls the Engine already owns, mapped onto their Engine commands (retirement map step 3).
/// Enablement and the pair wake move here, because the Engine host is their single owner: <c>--pair</c> edits that
/// pair and <c>--all</c> loops every pair through the same per-pair owner.
/// <c>interval</c> is retired rather than mapped (no Engine owner); <c>start</c>/<c>stop</c> must keep addressing
/// the one legacy host together, and <c>stop</c> also releases an owned supervisor when no host answers;
/// <c>status</c> is already answered from the Engine host by the existing read route.
/// Engine code holds no command text: the AutoSync names live in the registry and the Engine words, flags and
/// ownership come from the catalog, so this file is one mapping and nothing else.</summary>
public static class AutoSyncEngineRoute
{
    /// <summary>Returns the Engine argument list for an owned AutoSync row, or null when the row keeps its legacy
    /// route or does not parse. The AutoSync row is parsed first, so its own usage rules still decide what is a
    /// valid invocation; the Engine row's --yes is supplied here because the AutoSync row already carries the
    /// explicit target.</summary>
    public static string[]? Map(string[] args)
    {
        SyncCliInvocation invocation;
        try { invocation = SyncCliRegistry.Parse(args); }
        catch (SyncCliException) { return null; } // the caller's own parse reports the exact usage error
        if (SyncCliRegistry.EngineEquivalent(invocation.Command) is not { } key) return null;
        var row = EngineCliCatalog.Rows.Single(candidate => EngineCliCatalog.CommandKey(candidate.Command) == key);
        var result = new List<string>(args.Length) { args[0] };
        result.AddRange(row.Command.Split());
        if (invocation.PairId is { } pair)
        {
            result.Add(EngineCliCatalog.Flag(EngineOptionKey.Pair));
            result.Add(pair);
        }
        else result.Add(EngineCliCatalog.Flag(EngineOptionKey.All));
        result.Add(EngineCliCatalog.Flag(EngineOptionKey.Yes));
        if (invocation.Json) result.Add(SyncCli.JsonSwitch);
        return [.. result];
    }
}
