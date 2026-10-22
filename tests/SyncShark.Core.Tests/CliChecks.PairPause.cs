using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static partial class CliChecks
{
    internal static async Task PairPauseChecksAsync(string root)
    {
        var noState = Path.Combine(root, "no-state");
        try
        {
            using var unexpected = SqliteStateStore.OpenPauseControl(noState);
            throw new InvalidOperationException("Pause control must not create a missing store");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("No state yet", StringComparison.Ordinal)) { }
        if (Directory.Exists(noState)) throw new InvalidOperationException("Pause control created a missing state directory");
        using var owner = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        owner.SavePair(new PairDef("pause-pair", "Pause fixture",
            new RootDef("a", "Local", null, root, "a"),
            new RootDef("b", "Dropbox", "acc", "/fixture", "id:b"), [], [], true));
        foreach (var verb in new[] { "pause", "resume" })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var code = await SyncCli.ExecuteAsync(["--cli", "pairs", verb, "--pair", "pause-pair", "--json"],
                output, error, () => new V2CliRuntime(null,
                    SqliteStateStore.OpenPauseControl(root), null!, null!, root), CancellationToken.None);
            if (code != 0 || owner.PairPaused("pause-pair") != (verb == "pause") || !owner.Pair("pause-pair")!.Enabled)
                throw new InvalidOperationException($"pairs {verb} did not update only the durable pause flag: {output} {error}");
        }
        var opened = false;
        using var missingOutput = new StringWriter();
        using var missingError = new StringWriter();
        var missing = await SyncCli.ExecuteAsync(["--cli", "pairs", "pause"], missingOutput, missingError,
            () => { opened = true; throw new InvalidOperationException("Must not open state"); }, CancellationToken.None);
        if (missing == 0 || opened) throw new InvalidOperationException("Pause requires an exact pair before opening state");
        Console.WriteLine("CLI pause/resume and state-free missing-pair validation passed");
    }
}
