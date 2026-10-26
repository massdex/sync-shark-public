namespace Massdex.SyncShark.V2;

/// <summary>CLI first-sync choice checks (first-sync-choices.md): parsing, refusals, binding and legacy maps.</summary>
internal static partial class RunCliChecks
{
    private const string RefusalSuffix = "No sync action was applied.";

    private static async Task RunFirstSyncChoiceChecksAsync()
    {
        await Cli_Choices_RemoveAndCopyByPrefixAsync();
        await Cli_Choices_ApplyIsBoundToPrepareAsync();
        await Cli_Choices_InvalidAreUsageErrorsAsync();
        await Cli_Choices_RepeatedPropertiesRefusedAsync();
        await Cli_Choices_LegacyMapStillWorksAsync();
        await Cli_Choices_LegacyReservedKeysAsync();
        await Cli_Choices_FromAbsoluteFileAsync();
        await RunFirstSyncChoiceEffectChecksAsync();
        await RunFirstSyncBindingChecksAsync();
        await RunReviewDeleteChecksAsync();
    }

    // A per-folder remove and a per-folder copy in one document: the removed file is archived recovery-first
    // on the side that held it, the copied file lands on the other side, and the first sync completes.
    private static async Task Cli_Choices_RemoveAndCopyByPrefixAsync()
    {
        var fx = Build();
        fx.A.Seed("a/x.txt", "one");
        fx.A.Seed("b/y.txt", "two");
        var root = TempRoot();
        try
        {
            await PrepareThenApplyAsync(fx, root, """
                {"oneSided":[{"prefix":"a/","side":"Local","action":"remove"},
                             {"prefix":"b/","side":"Local","action":"copy"}]}
                """);
            Require(await fx.A.ObserveAsync("a/x.txt", CancellationToken.None) is null,
                "the remove choice deleted the file on the side that held it");
            Require(await fx.B.ObserveAsync("b/y.txt", CancellationToken.None) is not null,
                "the copy choice put the file on the other side");
            Require(fx.Store.Recoveries(fx.Pair.PairId).Count == 1,
                "the remove was recovery-first, leaving one recovery record");
            Require(fx.Store.FirstSyncDone(fx.Pair.PairId), "the applied first sync is marked done");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_RemoveAndCopyByPrefix");
    }

    // Decision 54: an apply is bound to the reviewed prepare; a different document or a missing prepare is a
    // review error and never reaches a writer.
    private static async Task Cli_Choices_ApplyIsBoundToPrepareAsync()
    {
        var fx = Build();
        fx.A.Seed("b/y.txt", "two");
        var root = TempRoot();
        const string copy = """{"oneSided":[{"prefix":"b/","side":"Local","action":"copy"}]}""";
        const string remove = """{"oneSided":[{"prefix":"b/","side":"Local","action":"remove"}]}""";
        try
        {
            var runtime = Runtime(fx, root);
            Require(await RefusedApplyAsync(runtime, fx, copy), "an apply with no prepare is refused");
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, copy);
            Require(await RefusedApplyAsync(runtime, fx, remove),
                "an apply whose choices differ from the last prepare is refused");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "no refused apply makes a writer call");
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, copy, false, null, _ => { },
                CancellationToken.None);
            Require(result.ChoiceEffects is [{ Action: "copy", Files: 1 }],
                "the matching apply reports the prepared choice's effect");
            Require(fx.WriterB.Puts == 1 && await fx.B.ObserveAsync("b/y.txt", CancellationToken.None) is not null,
                "the matching apply copies the file once");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_ApplyIsBoundToPrepare");
    }

    // Every malformed or contradictory choices document is a usage error (exit 2) for prepare AND for apply,
    // before any planning, so no file effect can reach either endpoint.
    private static async Task Cli_Choices_InvalidAreUsageErrorsAsync()
    {
        string[] invalid =
        [
            "{",
            "[1]",
            """{"paths":[],"oneSided":1}""",
            """{"paths":{"x.txt":"upload"},"bogus":1}""",
            """{"x.txt":"sideways"}""",
            """{"x.txt":"upload","X.TXT":"download"}""",
            """{"x.txt":{"upload":true}}""",
            """{"oneSided":{"prefix":"a"}}""",
            """{"oneSided":[1]}""",
            """{"oneSided":[{"prefix":"a","side":"Local"}]}""",
            """{"oneSided":[{"prefix":"a","side":"Local","action":"keep"}]}""",
            """{"oneSided":[{"prefix":"a","side":"Nowhere","action":"remove"}]}""",
            """{"oneSided":[{"prefix":"a","side":"Local","action":"remove","bogus":1}]}""",
            """{"oneSided":[{"prefix":"/a","side":"Local","action":"remove"}]}""",
            """{"oneSided":[{"prefix":"a\\b","side":"Local","action":"remove"}]}""",
            """{"oneSided":[{"prefix":"..","side":"Local","action":"remove"}]}""",
            """{"oneSided":[{"prefix":"a//b","side":"Local","action":"remove"}]}""",
            """{"oneSided":[{"prefix":"a","side":"Local","action":"remove"},{"prefix":"a/","side":"local","action":"copy"}]}"""
        ];
        var accepted = new List<string>();
        var leaked = false;
        foreach (var json in invalid)
        {
            var fx = Build();
            var root = TempRoot();
            try
            {
                var runtime = Runtime(fx, root);
                if (!await RefusedPrepareAsync(runtime, fx, json)) accepted.Add("prepare " + json);
                if (!await RefusedApplyUsageAsync(runtime, fx, json)) accepted.Add("apply " + json);
                leaked |= fx.WriterA.Calls != 0 || fx.WriterB.Calls != 0;
            }
            finally
            {
                Cleanup(root);
            }
        }
        Require(accepted.Count == 0, "every invalid choices document is refused: " + string.Join(" | ", accepted));
        Require(!leaked, "no refused choices document reaches a writer");
        Console.WriteLine("PASS Cli_Choices_InvalidAreUsageErrors");
    }

    // A repeated root property or entry field is refused, so a duplicate cannot slip past the one seen-set.
    private static async Task Cli_Choices_RepeatedPropertiesRefusedAsync()
    {
        string[] repeated =
        [
            """{"oneSided":[{"prefix":"a","side":"Local","action":"remove"}],"oneSided":[{"prefix":"a","side":"Local","action":"remove"}]}""",
            """{"paths":{},"paths":{}}""",
            """{"x.txt":"upload","x.txt":"download"}""",
            """{"oneSided":[{"prefix":"a","prefix":"b","side":"Local","action":"remove"}]}""",
            """{"oneSided":[{"prefix":"a","side":"Local","side":"Local","action":"remove"}]}"""
        ];
        var accepted = new List<string>();
        foreach (var json in repeated)
        {
            var fx = Build();
            var root = TempRoot();
            try
            {
                if (!await RefusedPrepareAsync(Runtime(fx, root), fx, json)) accepted.Add(json);
            }
            finally
            {
                Cleanup(root);
            }
        }
        Require(accepted.Count == 0, "every repeated choices property is refused: " + string.Join(" | ", accepted));
        Console.WriteLine("PASS Cli_Choices_RepeatedPropertiesRefused");
    }

    // The legacy flat map still works. "no-change" proves the entry was parsed as a choice: the file is left alone.
    private static async Task Cli_Choices_LegacyMapStillWorksAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        var root = TempRoot();
        try
        {
            await PrepareThenApplyAsync(fx, root, """{"x.txt":"no-change"}""");
            Require(await fx.B.ObserveAsync("x.txt", CancellationToken.None) is null,
                "the legacy no-change choice left the file alone");
            Require(fx.Store.FirstSyncDone(fx.Pair.PairId), "the legacy choice completes the first sync");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_LegacyMapStillWorks");
    }

    // "paths" and "oneSided" with STRING values are ordinary legacy path entries, not the structured form.
    private static async Task Cli_Choices_LegacyReservedKeysAsync()
    {
        var upload = Build();
        upload.A.Seed("paths", "one");
        var uploadRoot = TempRoot();
        var download = Build();
        download.B.Seed("oneSided", "two");
        var downloadRoot = TempRoot();
        try
        {
            await PrepareThenApplyAsync(upload, uploadRoot, """{"paths":"upload"}""");
            Require(await upload.B.ObserveAsync("paths", CancellationToken.None) is not null,
                "a string 'paths' value is a legacy upload entry");
            await PrepareThenApplyAsync(download, downloadRoot, """{"oneSided":"download"}""");
            Require(await download.A.ObserveAsync("oneSided", CancellationToken.None) is not null,
                "a string 'oneSided' value is a legacy download entry");
        }
        finally
        {
            Cleanup(uploadRoot);
            Cleanup(downloadRoot);
        }
        Console.WriteLine("PASS Cli_Choices_LegacyReservedKeys");
    }

    // --choices accepts an absolute file as well as inline JSON, through the shared reader.
    private static async Task Cli_Choices_FromAbsoluteFileAsync()
    {
        var fx = Build();
        fx.A.Seed("a/x.txt", "bytes");
        var root = TempRoot();
        var path = Path.Combine(root, "choices.json");
        try
        {
            await File.WriteAllTextAsync(path,
                """{"oneSided":[{"prefix":"a/","side":"Local","action":"remove"}]}""");
            var runtime = Runtime(fx, root);
            var data = await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, path);
            Require(data.ChoiceEffects is [{ Files: 1 }], "an absolute choices file is read and its effect reported");
            Require(data.ChoicesDigest is { Length: 64 }, "prepare reports the digest of the normalized choices");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "reading a choices file makes no writer call");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Choices_FromAbsoluteFile");
    }

    private static async Task<bool> RefusedPrepareAsync(V2CliRuntime runtime, Fixture fx, string choices)
    {
        try
        {
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
        }
        catch (SyncCliUsageException error)
        {
            return error.Message.Contains(RefusalSuffix, StringComparison.Ordinal);
        }
        return false;
    }

    private static async Task<bool> RefusedApplyAsync(V2CliRuntime runtime, Fixture fx, string choices)
    {
        try
        {
            await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, CancellationToken.None);
        }
        catch (SyncCliReviewException)
        {
            return true;
        }
        return false;
    }

    private static async Task<bool> RefusedApplyUsageAsync(V2CliRuntime runtime, Fixture fx, string choices)
    {
        try
        {
            await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, CancellationToken.None);
        }
        catch (SyncCliUsageException error)
        {
            return error.Message.Contains(RefusalSuffix, StringComparison.Ordinal);
        }
        return false;
    }

    private static async Task<SyncCliFirstSyncResultData> PrepareThenApplyAsync(Fixture fx, string root, string choices)
    {
        var runtime = Runtime(fx, root);
        await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
        return await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, CancellationToken.None);
    }

    private static V2CliRuntime Runtime(Fixture fx, string productRoot) =>
        new(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), productRoot);

    private static string TempRoot()
    {
        // Keep fixture state (including any SQLite database) under the test root when the harness names one, and
        // never in the live product store.
        var parent = Environment.GetEnvironmentVariable("SYNCSHARK_TEST_ROOT") is { Length: > 0 } testRoot
            ? Path.Combine(testRoot, "TEMP")
            : Path.GetTempPath();
        var root = Path.Combine(parent, "syncshark-choices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover scratch directory is harmless; no check depends on it being gone.
        }
    }
}
