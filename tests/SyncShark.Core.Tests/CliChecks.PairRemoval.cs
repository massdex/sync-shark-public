using Massdex.SyncShark;

// Focused registry and parser checks for `pairs remove`. The storage verb is exercised in StorageChecks; the
// dispatch handler itself belongs to the CLI change, so these checks pin the published command contract.
internal static partial class CliChecks
{
    private static void PairRemoveRegistryChecks()
    {
        const string command = "pairs remove";
        var row = SyncCliRegistry.Rows.SingleOrDefault(item => item.Command == command);
        Require(row is not null, "the registry exposes the pairs remove command");
        var registered = row!;
        Require(registered.RequiresYes && registered.Mutating && registered.RequiresPair,
            "pairs remove is confirmation-gated, mutating and requires an exact pair");
        Require(registered.Usage.Contains("--pair <exact-id>", StringComparison.Ordinal) &&
                registered.Usage.Contains("--archive <directory>", StringComparison.Ordinal) &&
                registered.Usage.Contains("--yes", StringComparison.Ordinal),
            "pairs remove documents its exact pair, archive directory and confirmation");
        var options = registered.Options.Split(',').Select(value => value.Trim())
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Require(options.SequenceEqual(new[] { "--archive", "--drop-unfinished", "--pair", "--yes" }),
            "pairs remove declares only the exact pair, archive directory and confirmation");
        Require(registered.Effect.Contains("archive", StringComparison.OrdinalIgnoreCase) &&
                registered.Effect.Contains("root", StringComparison.OrdinalIgnoreCase) &&
                registered.Effect.Contains("retain", StringComparison.OrdinalIgnoreCase),
            "the declared effect states that root data is retained after archiving the pair");
        Require(SyncCliRegistry.RenderCapabilities(SyncCliRegistry.Capabilities())
                .Contains(command, StringComparison.Ordinal),
            "the rendered capability surface includes pairs remove");

        var invocation = SyncCliRegistry.Parse(["--cli", "pairs", "remove", "--pair", "pair-x",
            "--archive", "C:\\fixture\\archive", "--yes", "--json"]);
        Require(invocation.Command == command && invocation.PairId == "pair-x" &&
                invocation.Value("--archive") == "C:\\fixture\\archive" && invocation.Flag("--yes"),
            "the parser resolves pairs remove, its exact pair, archive directory and confirmation");
        Require(!invocation.Flag("--drop-unfinished"), "unfinished effects are not dropped by default");
        var explicitDrop = SyncCliRegistry.Parse(["--cli", "pairs", "remove", "--pair", "pair-x",
            "--archive", "C:\\fixture\\archive", "--drop-unfinished", "--yes", "--json"]);
        Require(explicitDrop.Flag("--drop-unfinished"), "the parser preserves explicit unfinished-effect consent");
    }
}
