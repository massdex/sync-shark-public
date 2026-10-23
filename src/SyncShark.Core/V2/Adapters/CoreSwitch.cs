namespace Massdex.SyncShark.V2;

/// <summary>Selects the v1 or v2 core from the <c>--core</c> switch or the <c>SYNCSHARK_CORE</c> setting.</summary>
public static class CoreSwitch
{
    public const string LegacyNotice = "Sync Shark: legacy engine selected explicitly (--core v1 or SYNCSHARK_CORE=1); use --core v2 for the product engine.";

    /// <summary>The compatibility environment setting; legacy selection requires an explicit switch.</summary>
    public const string EnvironmentVariable = "SYNCSHARK_CORE";

    private const string CliOption = "--cli";
    private const string CoreOption = "--core";
    private const string V2Token = "v2";
    private const string V1Token = "v1";

    /// <summary>Returns the arguments without the switch; a non-null error is a usage error (exit 2).</summary>
    /// <remarks>
    /// The CLI form is <c>--cli --core v2</c>; the app form is <c>--core v2</c>. Both remove the switch and its
    /// token and otherwise behave the same, so the CLI and the app select the core one way.
    /// </remarks>
    public static (bool UseV2, string[] Args, string? Error) Select(string[] args, string? environmentValue)
    {
        ArgumentNullException.ThrowIfNull(args);
        var coreIndex = args.Length >= 2 && args[0] == CliOption && args[1] == CoreOption ? 1
            : args.Length >= 1 && args[0] == CoreOption ? 0
            : -1;
        if (coreIndex >= 0)
        {
            if (args.Length <= coreIndex + 1)
                return (false, args, "The " + CoreOption + " switch requires " + V1Token + " or " + V2Token + ".");
            var token = args[coreIndex + 1];
            if (token == V2Token) return (true, Remove(args, coreIndex), null);
            if (token == V1Token) return (false, Remove(args, coreIndex), null);
            return (false, args, "The " + CoreOption + " switch must be " + V1Token + " or " + V2Token + ", not '" +
                token + "'.");
        }
        if (environmentValue is null or "" or "2") return (true, args, null);
        if (environmentValue == "1") return (false, args, null);
        return (false, args, "The " + EnvironmentVariable + " setting must be 1 or 2, not '" + environmentValue + "'.");
    }

    private static string[] Remove(string[] args, int coreIndex)
    {
        var remaining = new List<string>(args.Length - 2);
        for (var index = 0; index < args.Length; index++)
            if (index != coreIndex && index != coreIndex + 1) remaining.Add(args[index]);
        return remaining.ToArray();
    }
}
