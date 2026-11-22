using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Contract checks for Exclusions: the side-effect-free preview and the applied/pending result use one
/// eligibility over skipped, other-excluded and nested content (F040, M10, product-0247/0250).</summary>
public static class ExclusionsChecks
{
    /// <summary>Verifies the Exclusions contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var exclusion = new Exclusion(Side.Local, "dir", false);
        var configured = pair with { Exclusions = [exclusion] };

        // A clean folder: the one child is a potential copy and a removal, nothing is blocked, no effect runs.
        var clean = new TestDoubles.FakeSide(Side.Local);
        clean.Seed(Scenarios.ItemChecks.File(Side.Local, "dir", 0, "folder", folder: true), []);
        clean.Seed(Scenarios.ItemChecks.File(Side.Local, "dir/a", 1, "a"), [1]);
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var scope = new PathScope(configured, [configured], DriveType.Fixed);
            var preview = await new Exclusions().PreviewAsync(configured, exclusion, clean,
                new TestDoubles.FakeSide(Side.Dropbox), store, scope, default);
            Check.Require(preview.CopyFiles == 1 && preview.CopyBytes == 1 && preview.RemoveFiles == 1 &&
                preview.RemoveBytes == 1 && !preview.BlockedByProtected, "Clean preview counts");
            Check.Require(preview.RecoveryPlace == TextKey.RecoveryRecycleBin, "Wrong PC recovery place");
            Check.Require(clean.Requests[nameof(ISide.ReadOneAsync)] > 0 && clean.Mutations.Count == 0 &&
                clean.Requests[nameof(ISide.DeleteAsync)] == 0 && clean.Requests[nameof(ISide.PublishAsync)] == 0,
                "Preview changed or removed content");
        }

        // Skipped content refuses the removal; it is still counted as a removal but never as a copy.
        var protectedSide = new TestDoubles.FakeSide(Side.Local);
        protectedSide.Seed(Scenarios.ItemChecks.File(Side.Local, "dir", 0, "folder", folder: true), []);
        protectedSide.Seed(Scenarios.ItemChecks.File(Side.Local, "dir/a", 1, "a"), [1]);
        protectedSide.Seed(Scenarios.ItemChecks.File(Side.Local, "dir/b", 2, "b") with
            { Skip = nameof(TextKey.SkipOnlineOnly) }, [2]);
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var scope = new PathScope(configured, [configured], DriveType.Fixed);
            var preview = await new Exclusions().PreviewAsync(configured, exclusion, protectedSide,
                new TestDoubles.FakeSide(Side.Dropbox), store, scope, default);
            Check.Require(preview.BlockedByProtected && preview.CopyFiles == 1 && preview.RemoveFiles == 2,
                "Protected content did not block the removal or was copied");
        }

        // Keep exclusions do nothing, and an absent target is already applied; neither touches files.
        var untouched = new TestDoubles.FakeSide(Side.Local);
        untouched.Seed(Scenarios.ItemChecks.File(Side.Local, "dir", 0, "folder", folder: true), []);
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var scope = new PathScope(configured, [configured], DriveType.Fixed);
            var keep = new Exclusions();
            var keepPreview = await keep.PreviewAsync(configured, new Exclusion(Side.Local, "dir", true), untouched,
                new TestDoubles.FakeSide(Side.Dropbox), store, scope, default);
            Check.Require(keepPreview is { CopyFiles: 0, RemoveFiles: 0, BlockedByProtected: false }, "Keep preview");
            var kept = await keep.ApplyAsync(configured, Scenarios.ItemChecks.Settings, _ => { }, scope,
                new Exclusion(Side.Local, "dir", true), untouched, new TestDoubles.FakeSide(Side.Dropbox), store,
                null!, null!, default);
            var absent = await keep.ApplyAsync(configured, Scenarios.ItemChecks.Settings, _ => { }, scope,
                new Exclusion(Side.Local, "missing", false), untouched, new TestDoubles.FakeSide(Side.Dropbox), store,
                null!, null!, default);
            Check.Require(kept == ExclusionOutcome.Applied && absent == ExclusionOutcome.Applied &&
                untouched.Mutations.Count == 0, "Keep or absent exclusion was not applied without effect");
        }
    }
}
