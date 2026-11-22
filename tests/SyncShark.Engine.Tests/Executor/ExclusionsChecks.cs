using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Contract checks for Exclusions that depend only on the group's own code: the read-only preview and the
/// protected-content refusal, which never authorizes a removal. Real copy/removal outcomes are INTEGRATION-CHECKS
/// (need W01 Planner.Decide and W17 Effects.ApplyAsync bodies; R29).</summary>
public static class ExclusionsChecks
{
    /// <summary>Verifies the Exclusions contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var settings = Scenarios.ItemChecks.Settings;
        var exclusion = new Exclusion(Side.Local, "dir", false);
        var configured = pair with { Exclusions = [exclusion] };

        // A clean folder preview: the one child is a potential copy and a removal, nothing is blocked, no effect runs.
        var clean = new TestDoubles.FakeSide(Side.Local);
        clean.Seed(Scenarios.ItemChecks.File(Side.Local, "dir", 0, "folder", folder: true), []);
        clean.Seed(Scenarios.ItemChecks.File(Side.Local, "dir/a", 1, "a"), [1]);
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var scope = new PathScope(configured, [configured], DriveType.Fixed);
            var preview = await new Exclusions().PreviewAsync(configured, exclusion, clean,
                new TestDoubles.FakeSide(Side.Dropbox), store, scope, default);
            Check.Require(preview is { CopyFiles: 1, CopyBytes: 1, RemoveFiles: 1, RemoveBytes: 1, BlockedByProtected: false } &&
                preview.RecoveryPlace == TextKey.RecoveryRecycleBin, "Clean preview counts or recovery place");
            Check.Require(clean.Mutations.Count == 0 && clean.Requests[nameof(ISide.DeleteAsync)] == 0 &&
                clean.Requests[nameof(ISide.PublishAsync)] == 0, "Preview changed or removed content");
        }

        // Skipped content blocks the application: refused, the required notice raised, and the whole source retained.
        var protectedSide = new TestDoubles.FakeSide(Side.Local);
        protectedSide.Seed(Scenarios.ItemChecks.File(Side.Local, "dir", 0, "folder", folder: true), []);
        protectedSide.Seed(Scenarios.ItemChecks.File(Side.Local, "dir/a", 1, "a"), [1]);
        protectedSide.Seed(Scenarios.ItemChecks.File(Side.Local, "dir/b", 2, "b") with
            { Skip = nameof(TextKey.SkipOnlineOnly) }, [2]);
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var scope = new PathScope(configured, [configured], DriveType.Fixed);
            var outcome = await new Exclusions().ApplyAsync(configured, settings, _ => { }, scope, exclusion, protectedSide,
                new TestDoubles.FakeSide(Side.Dropbox), store, null!, null!, default);
            Check.Require(outcome == ExclusionOutcome.Refused, "Blocked exclusion was not refused");
            Check.Require(store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure && n.Cause == TextKey.FailExcludeHoldsProtected),
                "Blocked application raised no protected-content notice");
            Check.Require(protectedSide.Mutations.Count == 0 && protectedSide.Items.ContainsKey("dir") &&
                protectedSide.Items.ContainsKey("dir/b"), "Blocked application removed protected or root content");
        }

        // Keep exclusions and an absent target are already applied and touch nothing.
        var untouched = new TestDoubles.FakeSide(Side.Local);
        untouched.Seed(Scenarios.ItemChecks.File(Side.Local, "dir", 0, "folder", folder: true), []);
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var scope = new PathScope(configured, [configured], DriveType.Fixed);
            var exclusions = new Exclusions();
            var kept = await exclusions.ApplyAsync(configured, settings, _ => { }, scope, new Exclusion(Side.Local, "dir", true),
                untouched, new TestDoubles.FakeSide(Side.Dropbox), store, null!, null!, default);
            var absent = await exclusions.ApplyAsync(configured, settings, _ => { }, scope,
                new Exclusion(Side.Local, "missing", false), untouched, new TestDoubles.FakeSide(Side.Dropbox), store,
                null!, null!, default);
            Check.Require(kept == ExclusionOutcome.Applied && absent == ExclusionOutcome.Applied &&
                untouched.Mutations.Count == 0, "Keep or absent exclusion was not applied without effect");
        }
    }
}
