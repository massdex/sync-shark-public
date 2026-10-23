namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    private static async Task Cli_RetainedNoticeChoicesAsync()
    {
        Cli_RetainedNoticeGenerationParsing();
        await Cli_RetryScopesAsync();
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            var loser = fx.A.Seed("choice.txt", "older");
            var winner = fx.B.Seed("choice.txt", "newer");
            store.AddNotices([new Notice(fx.Pair.PairId, 4, NoticeKind.AutoConflict, "choice.txt",
                "Newer version chosen; older version retained.", PlanNotices.AutoConflict.Key,
                RetainedVersion: new RetainedNoticeVersion("retained-action", Side.A, loser, winner))]);
            var runtime = Runtime(fx, root);
            var list = (SyncCliConflictListData)await DispatchAsync(runtime,
                ["--cli", "conflicts", "list", "--pair", fx.Pair.PairId]);
            var notice = list.Items.Single();
            Require(notice.Options.Select(option => option.Choice).SequenceEqual(new[]
                { NoticeChoices.UseOtherVersion, NoticeChoices.SaveOtherVersionAsCopy, NoticeChoices.Dismiss }),
                "persisted retained notice exposes all three explicit actions in CLI");
            Require(store.OpenNotices(fx.Pair.PairId).Single().RetainedVersion?.ActionId == "retained-action",
                "SQLite roundtrip retains exact action provenance");
            foreach (var choice in notice.Options.Select(option => option.Choice))
                Require(SyncCliParser.Parse(["--cli", "conflicts", "resolve", "--pair", fx.Pair.PairId,
                    "--path", "choice.txt", "--choice", choice, "--key", notice.ConflictId,
                    "--generation", "0", "--yes"]).Choice == choice, "notice action parses unchanged");
            var rejectedOrdinaryChoice = false;
            try
            {
                await runtime.ResolveConflictAsync(fx.Pair.PairId, "choice.txt", "KeepLocal",
                    notice.ConflictId, 0, null, CancellationToken.None);
            }
            catch (SyncCliUsageException) { rejectedOrdinaryChoice = true; }
            Require(rejectedOrdinaryChoice && store.OpenNotices(fx.Pair.PairId).Count == 1,
                "direct runtime cannot use generation zero for an ordinary conflict choice");
            await DispatchAsync(runtime, ["--cli", "conflicts", "resolve", "--pair", fx.Pair.PairId,
                "--path", "choice.txt", "--choice", NoticeChoices.Dismiss, "--key", notice.ConflictId,
                "--generation", "0", "--yes"]);
            Require(store.OpenNotices(fx.Pair.PairId).Count == 0, "dismiss closes the selected notice");
            Require(fx.A.WriteCalls == 0 && fx.B.WriteCalls == 0,
                "dismiss cannot write or delete endpoint content");
        }
        finally { Cleanup(root); }
        Console.WriteLine("PASS Cli_RetainedNoticeChoices");
    }

    public static async Task ConflictNoticeHandlerChecksAsync()
    {
        Cli_RetainedNoticeGenerationParsing();
        foreach (var choice in new[] { "rename-incoming=Renamed.txt", NoticeChoices.ReplaceExisting, NoticeChoices.Dismiss })
        {
            var root = TempRoot();
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            var a = fx.A.Seed("choice.txt", "incoming");
            var b = fx.B.Seed("choice.txt", "occupant");
            store.AddNotices([new(fx.Pair.PairId, 14, NoticeKind.UserDecision, a.Path, "incoming collision", "incoming-name-clash",
                ConflictVersions: new(a, b, Incoming: new("draft", 14, ActionKind.CopyNew, Side.B, b.Path, null, a, null, false, null)))]);
            var notice = store.OpenNotices(fx.Pair.PairId).Single();
            await DispatchAsync(Runtime(fx, root), ["--cli", "conflicts", "resolve", "--pair", fx.Pair.PairId,
                "--path", a.Path, "--choice", choice, "--key", notice.NoticeId!, "--generation", "0", "--yes"]);
            Require(store.OpenNotices(fx.Pair.PairId).Count == 0, "CLI exact notice handler completes explicit choice");
            var afterA = await fx.A.ObserveAsync(choice.StartsWith("rename") ? "Renamed.txt" : a.Path, CancellationToken.None);
            var afterB = await fx.B.ObserveAsync(b.Path, CancellationToken.None);
            Require(afterA?.Hash == a.Hash && afterB?.Hash == (choice == NoticeChoices.ReplaceExisting ? a.Hash : b.Hash),
                "CLI handler preserves incoming content and targets only chosen occupant");
            if (choice == NoticeChoices.Dismiss) Require(fx.A.WriteCalls == 0 && fx.B.WriteCalls == 0, "CLI dismiss is effect-free");
        }
        Console.WriteLine("PASS ConflictNoticeHandlerChecks");
    }

    private static void Cli_RetainedNoticeGenerationParsing()
    {
        static string[] Resolve(string choice, string generation) =>
            ["--cli", "conflicts", "resolve", "--pair", "p", "--path", "choice.txt",
                "--choice", choice, "--key", "notice-id", "--generation", generation, "--yes"];
        static void Reject(string[] args)
        {
            try { SyncCliParser.Parse(args); }
            catch (SyncCliUsageException) { return; }
            throw new InvalidOperationException("Invalid zero/negative generation was accepted.");
        }
        foreach (var choice in new[] { NoticeChoices.UseOtherVersion,
                     NoticeChoices.SaveOtherVersionAsCopy, NoticeChoices.Dismiss, NoticeChoices.Compare,
                     NoticeChoices.Merge + "=Merged.txt", NoticeChoices.KeepA, NoticeChoices.KeepB,
                     NoticeChoices.KeepBoth, NoticeChoices.RenameIncoming + "=NewName.txt", NoticeChoices.ReplaceExisting })
        {
            Require(SyncCliParser.Parse(Resolve(choice, "0")).Generation == 0,
                "retained notice accepts generation zero");
            Reject(Resolve(choice, "-1"));
            Reject(Resolve(choice, "not-a-number"));
        }
        foreach (var choice in new[] { "KeepLocal", "KeepDropbox", "PreserveBoth", "arbitrary-choice" })
        {
            Reject(Resolve(choice, "0"));
            Require(SyncCliParser.Parse(Resolve(choice, "1")).Generation == 1,
                "ordinary conflict choices retain positive generation parsing");
        }
        Reject(["--cli", "first-sync", "apply", "--pair", "p", "--generation", "0", "--yes"]);
        Require(SyncCliParser.Parse(["--cli", "first-sync", "apply", "--pair", "p",
            "--generation", "1", "--yes"]).Generation == 1,
            "reviewed apply retains positive generation parsing");
    }
}
