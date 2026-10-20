namespace Massdex.SyncShark.V2;

/// <summary>
/// Decision 64's CLI surface: decisions list / decisions resolve. A first-sync tie leaves a needs-decision notice
/// open; the list shows both sides' freshly observed state and a version token that pins the exact notice, and the
/// resolve applies the chosen side and closes that notice only on evidence (Astra round 2).
/// </summary>
internal static partial class RunCliChecks
{
    private static readonly DateTimeOffset TieBase = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static async Task RunDecisionChecksAsync()
    {
        await Cli_DecisionsList_ShowsTieAsync();
        await Cli_DecisionsResolve_KeepAppliesAndClosesAsync();
        await Cli_DecisionsResolve_StaleTokenRefusedAsync();
        await Cli_DecisionsResolve_UnknownPathRefusedAsync();
        await Cli_DecisionsResolve_ChosenSideMissingStaysOpenAsync();
        await Cli_DecisionsResolve_ExcludedPathStaysOpenAsync();
        await Cli_DecisionsResolve_CaseVariantClosesExactNoticeAsync();
        await Cli_DecisionsResolve_FolderRefusedAsync();
    }

    private static async Task Cli_DecisionsList_ShowsTieAsync()
    {
        var (fx, noticeId, token) = await TieAfterFirstSyncAsync();
        var runtime = Runtime(fx);
        var list = await runtime.LoadDecisionsAsync(fx.Pair.PairId, 50, 0, CancellationToken.None);
        var item = list.Items.Single();
        Require(list.TotalOpen == 1 && list.Returned == 1 && !list.HasMore &&
            item.RelativePath == "t.txt" && item.Rule == 5 && item.ReasonKey == "needs-decision" &&
            item.SizeA == 8 && item.SizeB == 8 && item.ModifiedA == TieBase && item.ModifiedB == TieBase.AddSeconds(1) &&
            item.Token == token && token.StartsWith(noticeId + ":", StringComparison.Ordinal) &&
            token.Length == noticeId.Length + 1 + 16,
            "decisions list shows the tie with both sides' state and a token that pins the notice id and version");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "decisions list makes no file effects");
        Console.WriteLine("PASS Cli_DecisionsList_ShowsTie");
    }

    // The reviewed tie: resolving with side A copies A's version onto B and closes the notice.
    private static async Task Cli_DecisionsResolve_KeepAppliesAndClosesAsync()
    {
        var (fx, _, token) = await TieAfterFirstSyncAsync();
        var runtime = Runtime(fx);
        var result = await DispatchAsync(runtime, ["--cli", "decisions", "resolve", "--pair", fx.Pair.PairId,
            "--path", "t.txt", "--keep", "a", "--token", token, "--yes"]);
        Require(result is SyncCliDecisionResolveData { NoticeClosed: true, Applied: 1 } decided &&
            decided.Side == "Local" && decided.Detail is null,
            "decisions resolve applies the chosen side and reports the closed notice");
        var a = await fx.A.ObserveAsync("t.txt", CancellationToken.None);
        var b = await fx.B.ObserveAsync("t.txt", CancellationToken.None);
        Require(a is not null && b is not null && a.Hash == b.Hash,
            "the chosen side's version lands on the other side");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).All(notice => notice.Kind != NoticeKind.UserDecision),
            "the decided notice is closed in the store");
        Console.WriteLine("PASS Cli_DecisionsResolve_KeepAppliesAndCloses");
    }

    // A real change on disk (the endpoint, not the store) after the list is refused before any effect.
    private static async Task Cli_DecisionsResolve_StaleTokenRefusedAsync()
    {
        var (fx, _, token) = await TieAfterFirstSyncAsync();
        fx.A.Seed("t.txt", "local v2", mtime: TieBase.AddMinutes(5));
        var runtime = Runtime(fx);
        var refusal = await RefusalAsync(() => DispatchAsync(runtime, ["--cli", "decisions", "resolve", "--pair",
            fx.Pair.PairId, "--path", "t.txt", "--keep", "a", "--token", token, "--yes"]));
        Require(refusal is SyncCliReviewException { Message: var message } &&
            message.Contains("changed since you looked", StringComparison.Ordinal),
            "a resolution whose token no longer matches the endpoint is a review error");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "a stale-token refusal makes no file effects");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Kind == NoticeKind.UserDecision),
            "the notice stays open after a stale-token refusal");
        Console.WriteLine("PASS Cli_DecisionsResolve_StaleTokenRefused");
    }

    private static async Task Cli_DecisionsResolve_UnknownPathRefusedAsync()
    {
        var (fx, _, token) = await TieAfterFirstSyncAsync();
        var runtime = Runtime(fx);
        var refusal = await RefusalAsync(() => DispatchAsync(runtime, ["--cli", "decisions", "resolve", "--pair",
            fx.Pair.PairId, "--path", "missing.txt", "--keep", "a", "--token", token, "--yes"]));
        Require(refusal is SyncCliReviewException, "an unknown decision path is refused");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "an unknown-path refusal makes no file effects");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Kind == NoticeKind.UserDecision),
            "the tie notice stays open after an unknown-path refusal");
        Console.WriteLine("PASS Cli_DecisionsResolve_UnknownPathRefused");
    }

    // The chosen side is gone: nothing can be copied, so the notice stays open and reports the reason.
    private static async Task Cli_DecisionsResolve_ChosenSideMissingStaysOpenAsync()
    {
        var (fx, _, _) = await TieAfterFirstSyncAsync();
        await fx.A.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.A, "t.txt", null, null, null,
            false, null), CancellationToken.None);
        var runtime = Runtime(fx);
        var list = await runtime.LoadDecisionsAsync(fx.Pair.PairId, 50, 0, CancellationToken.None);
        var result = await DispatchAsync(runtime, ["--cli", "decisions", "resolve", "--pair", fx.Pair.PairId,
            "--path", "t.txt", "--keep", "a", "--token", list.Items.Single().Token, "--yes"]);
        Require(result is SyncCliDecisionResolveData { NoticeClosed: false } decided &&
            decided.Detail is not null && decided.Detail.StartsWith("decision could not be applied", StringComparison.Ordinal),
            "a decision whose chosen side is missing is reported and left open");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Kind == NoticeKind.UserDecision),
            "the notice stays open when the chosen side is missing");
        var b = await fx.B.ObserveAsync("t.txt", CancellationToken.None);
        Require(b is not null && b.Size == 8, "nothing is copied back to the missing side");
        Console.WriteLine("PASS Cli_DecisionsResolve_ChosenSideMissingStaysOpen");
    }

    // An excluded path cannot be synced, so its decision cannot be applied and the notice stays open.
    private static async Task Cli_DecisionsResolve_ExcludedPathStaysOpenAsync()
    {
        var (fx, _, token) = await TieAfterFirstSyncAsync();
        fx.Store.SavePair(fx.Pair with { Excluded = ["t.txt"] });
        var runtime = Runtime(fx);
        var result = await DispatchAsync(runtime, ["--cli", "decisions", "resolve", "--pair", fx.Pair.PairId,
            "--path", "t.txt", "--keep", "a", "--token", token, "--yes"]);
        Require(result is SyncCliDecisionResolveData { NoticeClosed: false, Applied: 0 },
            "a decision on an excluded path is not applied");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Kind == NoticeKind.UserDecision),
            "the notice stays open when the path is excluded");
        Console.WriteLine("PASS Cli_DecisionsResolve_ExcludedPathStaysOpen");
    }

    // Two case-variant notices at the same path: the token resolves the exact one it pins and closes only that one.
    private static async Task Cli_DecisionsResolve_CaseVariantClosesExactNoticeAsync()
    {
        var (fx, tieNoticeId, _) = await TieAfterFirstSyncAsync();
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 29, NoticeKind.UserDecision, "T.txt", "kind clash",
            "needs-decision")]);
        var runtime = Runtime(fx);
        var list = await runtime.LoadDecisionsAsync(fx.Pair.PairId, 50, 0, CancellationToken.None);
        Require(list.Items.Count == 2, "two case-variant notices are listed");
        var clash = list.Items.Single(candidate => candidate.Rule == 29);
        var result = await DispatchAsync(runtime, ["--cli", "decisions", "resolve", "--pair", fx.Pair.PairId,
            "--path", "T.txt", "--keep", "a", "--token", clash.Token, "--yes"]);
        Require(result is SyncCliDecisionResolveData { NoticeClosed: true },
            "the token resolves the exact notice it pins");
        var open = fx.Store.OpenNotices(fx.Pair.PairId).Where(notice => notice.Kind == NoticeKind.UserDecision).ToArray();
        Require(open.Length == 1 && open[0].NoticeId == tieNoticeId,
            "only the pinned notice closes; the other case-variant notice stays open");
        Console.WriteLine("PASS Cli_DecisionsResolve_CaseVariantClosesExactNotice");
    }

    // A folder decision is shown in the list (marked folder) but cannot be resolved yet (owner choice, Astra round 3).
    private static async Task Cli_DecisionsResolve_FolderRefusedAsync()
    {
        var fx = Build();
        fx.A.Seed("d", string.Empty, kind: ItemKind.Folder);
        fx.B.Seed("d", string.Empty, kind: ItemKind.Folder);
        await FirstSyncAsync(fx);
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 5, NoticeKind.UserDecision, "d", "held for you",
            "needs-decision")]);
        var runtime = Runtime(fx);
        var list = await runtime.LoadDecisionsAsync(fx.Pair.PairId, 50, 0, CancellationToken.None);
        Require(list.Items.Single() is { IsFolder: true }, "decisions list marks a folder decision as a folder");
        var refusal = await RefusalAsync(() => DispatchAsync(runtime, ["--cli", "decisions", "resolve", "--pair",
            fx.Pair.PairId, "--path", "d", "--keep", "a", "--token", list.Items.Single().Token, "--yes"]));
        Require(refusal is SyncCliReviewException { Message: var message } &&
            message.Contains("folder decisions can't be resolved yet", StringComparison.Ordinal),
            "resolving a folder decision is refused as a review error");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "a folder-decision refusal makes no file effects");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Kind == NoticeKind.UserDecision),
            "the folder decision notice stays open after the refusal");
        Console.WriteLine("PASS Cli_DecisionsResolve_FolderRefused");
    }

    private static V2CliRuntime Runtime(Fixture fx) =>
        new(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");

    private static async Task<(Fixture Fx, string NoticeId, string Token)> TieAfterFirstSyncAsync()
    {
        var fx = Build();
        fx.A.Seed("t.txt", "local v1", mtime: TieBase);
        fx.B.Seed("t.txt", "cloud v1", mtime: TieBase.AddSeconds(1));   // within the 2 s tie window -> rule 5 Hold
        await FirstSyncAsync(fx);
        var item = (await Runtime(fx).LoadDecisionsAsync(fx.Pair.PairId, 50, 0, CancellationToken.None)).Items.Single(candidate =>
            candidate.RelativePath == "t.txt");
        var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(candidate =>
            candidate.Kind == NoticeKind.UserDecision && candidate.Path == "t.txt");
        return (fx, notice.NoticeId!, item.Token);
    }
}
