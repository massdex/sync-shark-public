namespace Massdex.SyncShark;

/// <summary>The AutoSync controls the Engine already owns, mapped onto their Engine commands (retirement map step 3).
/// Per-pair enablement and the pair wake move here, because the Engine host is their single owner.
/// Deliberately not mapped, each with its own reason: the persisted global switch behind <c>--all</c> and
/// <c>interval</c> have no Engine equivalent; <c>start</c>/<c>stop</c> must keep addressing the one legacy host
/// together, and <c>stop</c> also releases an owned supervisor when no host answers; <c>status</c> is already
/// answered from the Engine host by the existing read route.</summary>
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
        if (!invocation.Command.StartsWith("autosync ", StringComparison.Ordinal)) return null;
        var command = invocation.Command["autosync ".Length..];
        var pair = invocation.PairId;
        string[] engineArgs;
        if (command is "enable" or "disable" && pair is not null && !invocation.Flag("--all"))
            engineArgs = ["engine", "pairs", command, "--pair", pair, "--yes"];
        else if (command == "now" && pair is not null)
            engineArgs = ["engine", "host", "wake", "--pair", pair, "--yes"];
        else return null;
        var result = new List<string>(args.Length + 2) { args[0] };
        result.AddRange(engineArgs);
        if (invocation.Json) result.Add(SyncCli.JsonSwitch);
        return [.. result];
    }
}
