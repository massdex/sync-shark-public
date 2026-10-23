using Massdex.SyncShark;

// Focused RC639-6 check for the retained-control recovery command surface. It exercises the REAL command
// registry, capability projection and argument parser only: no authority database, no provider, no fixture and
// no live effect. The handler against a real retained pair is deliberately not exercised here and is listed
// as planned/unverified in the result coverage rather than implied by these assertions.
internal static class RetainedControlRecoveryCliChecks
{
    private static int _assertions;

    internal static Task RunAsync()
    {
        _assertions = 0;
        const string command = "pairs recover-control";

        var row = SyncCliRegistry.Rows.SingleOrDefault(item => item.Command == command);
        Assert(row is not null, "the registry exposes the pairs recover-control command");
        Assert(row!.RequiresYes, "the command is confirmation-gated");
        Assert(row.Mutating, "the command is declared mutating");
        Assert(row.RequiresPair, "the command requires an exact pair");
        var options = row.Options.Split(',').Select(value => value.Trim())
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        Assert(options.SequenceEqual(new[] { "--draft", "--local-root", "--pair", "--source", "--source-sha256",
                "--yes" }),
            "the declared option set is the pair, pinned source, source hash, held root, optional current draft and confirmation");
        Assert(row.Usage.Contains("--source-sha256 <64-hex-lowercase>", StringComparison.Ordinal),
            "the usage names the pinned lowercase 64-hex source hash");
        Assert(row.Usage.Contains("--local-root <held-local-root>", StringComparison.Ordinal),
            "the usage names the held local root");
        Assert(row.Usage.Contains("[--draft <current-pair-json>]", StringComparison.Ordinal),
            "the usage names the optional current-pair draft for an unpublished initial candidate");
        Assert(row.Effect.Contains("no old database or policy is imported", StringComparison.Ordinal),
            "the declared effect states that no old database or policy is imported");
        Assert(row.Effect.Contains("stays disabled", StringComparison.Ordinal),
            "the declared effect states that the pair stays disabled for fresh review");

        var capabilities = SyncCliRegistry.Capabilities();
        Assert(SyncCliRegistry.RenderCapabilities(capabilities).Contains(command, StringComparison.Ordinal),
            "the rendered capability surface includes the command");

        var sha = new string('a', 64);
        var invocation = SyncCliRegistry.Parse(["--cli", "pairs", "recover-control", "--pair", "pair-x", "--source",
            "C:\\fixture\\original.db", "--source-sha256", sha, "--local-root", "C:\\fixture\\root", "--yes"]);
        Assert(invocation.Command == command, "the parser resolves the exact command");
        Assert(invocation.PairId == "pair-x", "the parser reads the exact pair");
        Assert(invocation.Value("--source") == "C:\\fixture\\original.db",
            "the parser reads the pinned source path");
        Assert(invocation.Value("--source-sha256") == sha,
            "the parser reads the pinned source hash as a value option");
        Assert(invocation.Value("--local-root") == "C:\\fixture\\root",
            "the parser reads the held local root as a value option");
        Assert(invocation.Flag("--yes"), "the parser reads the confirmation flag");
        var draftInvocation = SyncCliRegistry.Parse(["--cli", "pairs", "recover-control", "--pair", "pair-x",
            "--source", "C:\\fixture\\original.db", "--source-sha256", sha, "--local-root", "C:\\fixture\\root",
            "--draft", "{\"Id\":\"pair-x\"}", "--yes"]);
        Assert(draftInvocation.Value("--draft") == "{\"Id\":\"pair-x\"}",
            "the parser reads the optional current-pair draft as a value option");

        foreach (var (arguments, label) in new (string[], string)[]
                 {
                     (["--cli", "pairs", "recover-control", "--pair", "pair-x", "--source", "C:\\a.db",
                         "--source-sha256"], "a pinned source hash without a value"),
                     (["--cli", "pairs", "recover-control", "--pair", "pair-x", "--local-root"],
                         "a held local root without a value"),
                     (["--cli", "pairs", "recover-control", "--pair", "pair-x", "--source"],
                         "a pinned source without a value")
                 })
            Refuses(arguments, label);

        const string publication = "pairs recover-publication";
        var publicationRow = SyncCliRegistry.Rows.SingleOrDefault(item => item.Command == publication);
        Assert(publicationRow is not null, "the registry exposes the pairs recover-publication command");
        Assert(publicationRow!.RequiresYes && publicationRow.Mutating && publicationRow.RequiresPair,
            "pairs recover-publication is confirmation-gated, mutating and requires an exact pair");
        Assert(publicationRow.Options.Split(',').Select(value => value.Trim())
                .OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(new[] { "--pair", "--yes" }),
            "pairs recover-publication declares only the exact pair and confirmation");
        Assert(SyncCliRegistry.RenderCapabilities(SyncCliRegistry.Capabilities())
                .Contains(publication, StringComparison.Ordinal),
            "the rendered capability surface includes pairs recover-publication");
        var publicationInvocation = SyncCliRegistry.Parse(["--cli", "pairs", "recover-publication",
            "--pair", "pair-x", "--yes"]);
        Assert(publicationInvocation.Command == publication && publicationInvocation.PairId == "pair-x" &&
                publicationInvocation.Flag("--yes"),
            "the parser resolves the exact pairs recover-publication command, its pair and confirmation");

        Console.WriteLine("PASS retained control recovery CLI: " + _assertions +
            " assertions; the real registry, capability projection and parser only.");
        return Task.CompletedTask;
    }

    private static void Refuses(string[] arguments, string label)
    {
        try
        {
            SyncCliRegistry.Parse(arguments);
        }
        catch (Exception error) when (error is SyncCliUsageException or InvalidOperationException or
                                          ArgumentException)
        {
            Assert(!string.IsNullOrWhiteSpace(error.Message), "the parser refuses " + label);
            return;
        }
        throw new InvalidDataException("Expected a usage refusal for " + label);
    }

    private static void Assert(bool value, string label)
    {
        _assertions++;
        if (!value) throw new InvalidDataException("Retained control recovery CLI: " + label);
    }
}
