using Massdex.SyncShark;
using Massdex.SyncShark.V2;

// Focused registry and parser checks for the core v2 Recovery selection surface. They pin the published
// command contract only: no engine, store, endpoint, real exe or user data is touched here.
internal static partial class CliChecks
{
    private static void RecoverySelectionRegistryChecks()
    {
        var row = SyncCliRegistry.Rows.Single(item => item.Command == "recovery restore");
        foreach (var option in new[] { "--id", "--as-copy", "--dry-run", "--since", "--path-prefix", "--side",
                     "--action-id", "--after", "--key", "--copy", "--yes" })
            Require(row.Options.Split(',').Contains(option), "recovery restore publishes " + option);
        Require(row.Mutating && row.RequiresYes && row.RequiresPair,
            "recovery restore stays a mutating, confirmation-gated, pair-scoped command");
        Require(row.Usage.Contains("--id <recovery-id>", StringComparison.Ordinal) &&
                row.Usage.Contains("--dry-run", StringComparison.Ordinal) &&
                row.Usage.Contains("--as-copy", StringComparison.Ordinal) &&
                row.Usage.Contains("legacy --key", StringComparison.Ordinal),
            "recovery restore documents the exact id, dry run, copy alias and legacy aliases");

        var list = SyncCliRegistry.Rows.Single(item => item.Command == "recovery list");
        foreach (var option in new[] { "--since", "--path-prefix", "--side", "--action-id", "--after", "--limit" })
            Require(list.Options.Split(',').Contains(option), "recovery list publishes " + option);

        var filtered = SyncCliRegistry.Parse(["--cli", "recovery", "list", "--pair", "pair-1",
            "--since", "2026-10-20T00:00:00Z", "--path-prefix", "docs", "--side", "local",
            "--action-id", "act-1", "--after", "r0001", "--limit", "500"]);
        Require(filtered.Value("--since") == "2026-10-20T00:00:00Z" &&
                filtered.Value("--path-prefix") == "docs" && filtered.Value("--side") == "local" &&
                filtered.Value("--action-id") == "act-1" && filtered.Value("--after") == "r0001" &&
                filtered.Limit == 500,
            "the parser preserves every bounded Recovery list filter and the engine's 500-row maximum");

        var preview = SyncCliRegistry.Parse(["--cli", "recovery", "restore", "--pair", "pair-1",
            "--id", "r0001"]);
        Require(preview.Command == "recovery restore" && preview.Value("--id") == "r0001" &&
                !preview.Yes && !preview.Flag("--dry-run") && !preview.Copy,
            "recovery restore parses an exact id without --yes, so it defaults to a dry run");
        Require(!SyncCliRegistry.MutatesProductState(preview),
            "an unconfirmed recovery restore is not treated as a product-state mutation");

        var confirmed = SyncCliRegistry.Parse(["--cli", "recovery", "restore", "--pair", "pair-1",
            "--id", "r0001", "--as-copy", "--yes"]);
        Require(confirmed.Yes && confirmed.Copy, "--as-copy and --yes reach the invocation");
        Require(SyncCliRegistry.MutatesProductState(confirmed),
            "a confirmed recovery restore is a product-state mutation");

        var overridden = SyncCliRegistry.Parse(["--cli", "recovery", "restore", "--pair", "pair-1",
            "--id", "r0001", "--dry-run", "--yes"]);
        Require(overridden.Yes && overridden.Flag("--dry-run"),
            "an explicit dry run is retained alongside --yes so it can override the confirmation");
        Require(!SyncCliRegistry.MutatesProductState(overridden),
            "--dry-run overrides --yes and keeps the command read-only");

        var legacy = SyncCliRegistry.Parse(["--cli", "recovery", "restore", "--pair", "pair-1",
            "--key", "k0001", "--copy", "--yes"]);
        Require(legacy.Key == "k0001" && legacy.Copy && legacy.Yes,
            "the legacy --key and --copy aliases keep their exact meaning");

        var filterRestore = SyncCliRegistry.Parse(["--cli", "recovery", "restore", "--pair", "pair-1",
            "--since", "2026-10-20T00:00:00+00:00", "--path-prefix", "docs", "--limit", "500"]);
        Require(!filterRestore.Yes && filterRestore.Value("--since") == "2026-10-20T00:00:00+00:00" &&
                filterRestore.Limit == 500,
            "a filtered recovery restore also defaults to a dry run and accepts one bounded page");

        var providerRevision = SyncCliRegistry.Parse(["--cli", "recovery", "restore", "--pair", "pair-1",
            "--account", "acc-1", "--path", "docs/r.txt", "--key", "v0001", "--yes"]);
        Require(providerRevision.AccountId == "acc-1" && providerRevision.Path == "docs/r.txt" &&
                providerRevision.Key == "v0001" && providerRevision.Yes,
            "the legacy v1 provider-revision shape still parses for its confirmed route");

        Refused(["--cli", "recovery", "restore", "--pair", "pair-1"],
            "a bare recovery restore is a usage error and never selects everything");
        Refused(["--cli", "recovery", "restore", "--pair", "pair-1", "--id", "r1", "--key", "r2"],
            "an exact id and its legacy alias may not name different items");
        Refused(["--cli", "recovery", "restore", "--pair", "pair-1", "--id", "r1", "--since",
                "2026-10-20T00:00:00Z"],
            "an exact id may not be mixed with a filter selection");
        Refused(["--cli", "recovery", "restore", "--pair", "pair-1", "--id", "r1", "--after", "r0"],
            "an exact id is a single page and does not take a continuation cursor");
        Refused(["--cli", "recovery", "restore", "--pair", "pair-1", "--since", "2026-10-20T00:00:00"],
            "a recovery timestamp without Z or an explicit offset is refused");
        Refused(["--cli", "recovery", "list", "--pair", "pair-1", "--side", "both"],
            "the recovery side vocabulary is local|dropbox, not the rescan a|b|both vocabulary");
        Refused(["--cli", "recovery", "list", "--pair", "pair-1", "--limit", "501"],
            "a Recovery page may not exceed the engine's 500-row bound");

        var localLocal = new PairDef("pair-ll", "pair-ll",
            new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:ra"),
            new RootDef("rb", SyncProviders.Local, null, "C:\\b", "id:rb"), [], [], true);
        Require(V2CliRuntime.RecoverySide(localLocal, "local") is null,
            "both local roots mean --side local selects both sides instead of refusing a valid filter");
        var reversed = new PairDef("pair-rev", "pair-rev",
            new RootDef("ra", SyncProviders.Dropbox, "acc", "/a", "id:ra"),
            new RootDef("rb", SyncProviders.Local, null, "C:\\b", "id:rb"), [], [], true);
        Require(V2CliRuntime.RecoverySide(reversed, "local") == Side.B,
            "a reversed pair maps --side local to the actual local root, never assuming A is local");
        var refusedSide = false;
        try { _ = V2CliRuntime.RecoverySide(localLocal, "dropbox"); }
        catch (SyncCliUsageException) { refusedSide = true; }
        Require(refusedSide, "a provider with no root in the pair is refused clearly");
    }

    private static void Refused(string[] arguments, string description)
    {
        var refused = false;
        try { _ = SyncCliRegistry.Parse(arguments); }
        catch (SyncCliUsageException) { refused = true; }
        Require(refused, description);
    }
}
