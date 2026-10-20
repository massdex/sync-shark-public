using System.ComponentModel;
using System.Net;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task BlockerChecksAsync()
    {
        foreach (var (code, expected) in new[] { (5, "permissions"), (32, "Close the application"),
            (3, "Locate the moved"), (1167, "Reconnect"), (112, "Free space"), (123, "Rename"), (206, "Shorten") })
            Require(BlockerDiagnostic.Describe(new IOException("identity check failed", new Win32Exception(code)))
                .Contains(expected, StringComparison.Ordinal), "wrapped native repair " + code);
        // Quarantine owns these codes; never accidentally classify them as absence or ordinary permission.
        foreach (var code in new[] { 225, 226 })
            Require(!BlockerDiagnostic.Describe(new Win32Exception(code)).Contains("Repair:", StringComparison.Ordinal),
                "quarantine is not reclassified by the blocker catalog");
        foreach (var tag in new[] { "insufficient_space", "no_write_permission", "operation_suppressed", "malformed_path", "team_folder" })
        {
            var raw = new DropboxApiException(HttpStatusCode.Conflict, "provider refusal") { WriteErrorTag = tag };
            var mapped = DropboxItems.MapWriteError(new IOException("wrapped", raw));
            Require(mapped is ProviderWriteBlockedException blocked && blocked.Code == tag,
                "structured provider refusal remains distinct from target mismatch: " + tag);
            Require(ReferenceEquals(mapped, DropboxItems.MapWriteError(mapped)), "write mapping is idempotent");
        }
        var unknown = new DropboxApiException(HttpStatusCode.Conflict, "unknown") { WriteErrorTag = "future_reason" };
        Require(ReferenceEquals(unknown, DropboxItems.MapWriteError(unknown)), "unknown 409 remains a genuine retryable failure");
        Require(DropboxItems.MapWriteError(new DropboxApiException(HttpStatusCode.Conflict, "changed")
            { WriteErrorTag = "conflict" }) is ExpectedTargetMismatchException, "exact conflict remains conditional mismatch");
        var paper = DropboxItems.ToItem(new PilotRemoteEntry("Document.paper", PilotEntryKind.File, "id:document", "revision", 0, null)
            { RequiresExport = true });
        Require(paper.Flags.HasFlag(ItemFlags.RequiresExport) && paper.Flags.HasFlag(ItemFlags.Unreadable),
            "non-downloadable metadata survives mapping without turning into deletion evidence");

        var root = Path.Combine(Path.GetTempPath(), "blocker-check-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var pair = fx.Pair.PairId;
        store.AddNotices([new(pair, 30, NoticeKind.Info, "changing.txt", "content not confirmed", "content-unconfirmed")]);
        store.AddNotices([new(pair, 40, NoticeKind.Info, "changing.txt", "exact executor failure", "sync-retry")]);
        store.AddNotices([new(pair, 30, NoticeKind.Info, "changing.txt", "content not confirmed", "content-unconfirmed")]);
        var single = store.OpenNotices(pair).Single();
        Require(single.ReasonKey == "sync-retry" && single.Detail == "exact executor failure", "one notice preserves the specific diagnosis");
        var run = fx.Run();
        await run.ApplyRetryNoticeChoiceAsync(pair, single.NoticeId!, NoticeChoices.RetryNow, default);
        Require(store.SyncRetryAt(pair, single.Path)?.RetryAfter == DateTimeOffset.MinValue,
            "targeted retry creates work even when no previous retry row exists");
        Require(store.OpenNotices(pair).Count == 1, "requesting retry does not falsely close the notice");
        var guidance = await run.ApplyRetryNoticeChoiceAsync(pair, single.NoticeId!, NoticeChoices.RepairHelp, default);
        Require(guidance.Contains("exact executor failure", StringComparison.Ordinal) && store.OpenNotices(pair).Count == 1,
            "guided repair is shared and does not acknowledge success");

        var host = new V2SyncHost(store, run, new ObservedExecutor(fx.Executor), null!, Settings, TimeProvider.System,
            Path.Combine(root, "state.db"));
        var guiResult = await host.ResolveConflictSelectionCommandAsync(
            [new(new(single.NoticeId!, pair, single.Path, 0), SyncAuthorityConflictResolutionKind.PreserveBoth,
                NoticeChoices.RepairHelp)]);
        Require(guiResult.Items.Single().Result!.Decisions.Single().Reason.Contains("exact executor failure", StringComparison.Ordinal),
            "GUI host uses shared repair guidance");
        var cli = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), root);
        await cli.ResolveConflictAsync(pair, single.Path, NoticeChoices.RepairHelp, single.NoticeId, 0, null, default);
        Require(store.OpenNotices(pair).Count == 1, "CLI repair guidance leaves the same notice open");

        store.AddNotices([new(pair, 40, NoticeKind.Info, "unknown.txt", "new undiscovered blocker", "future-blocker")]);
        var generic = store.OpenNotices(pair).Single(n => n.Path == "unknown.txt");
        Require(NoticeChoices.Options(generic).Any(o => o.Choice == NoticeChoices.RetryNow) &&
            NoticeChoices.Options(generic).Any(o => o.Choice == NoticeChoices.Ignore), "unknown item blocker retains generic options");
        await run.ApplyRetryNoticeChoiceAsync(pair, generic.NoticeId!, NoticeChoices.Ignore, default);
        Require(store.Pair(pair)!.Excluded.Contains("unknown.txt") && fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "Ignore excludes the exact item without deleting anything");
        Require(store.OpenNotices(pair).All(n => n.Path != "unknown.txt"), "explicit Ignore closes only the selected notice");
        Console.WriteLine("PASS blocker prerequisites, structured repairs, dedupe, targeted retry, generic ignore");
    }
}
