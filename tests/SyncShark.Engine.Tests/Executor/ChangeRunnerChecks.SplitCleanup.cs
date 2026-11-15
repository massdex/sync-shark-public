using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    private static async Task SplitCleanupPreparationAsync()
    {
        foreach (var mode in new[] { "empty", "child", "armed", "chosen", "evidence", "foreign", "mount", "replaced", "absent" })
        {
            using var fixture = new Fixture(realStore: true);
            var (context, row) = SplitCleanupContext(fixture);
            var root = row.Dropbox!;
            var initial = mode == "evidence" ? row with { Local = FolderEntry(root.Path, "source-evidence") } : row;
            string existing;
            using (var plan = await new FolderPlanBuilder(context).BuildAsync(initial, Side.Dropbox,
                new HashSet<string>(), true, default))
                existing = fixture.Inner.BeginFolderRemoval(PairId, plan,
                    mode == "chosen" ? FolderRemovalAuthority.Chosen : FolderRemovalAuthority.Inferred,
                    fixture.Settings.StoragePageSize, default);
            if (mode == "armed")
                fixture.Inner.ArmFolderSubmission(PairId, fixture.Inner.ReadyFolderEffects(PairId, existing, -1,
                    fixture.Settings.StoragePageSize).ToArray());
            if (mode == "child") fixture.Dropbox.Seed(FolderEntry(PathNames.Child(root.Path, "Child"), "child-id"), null);
            if (mode == "mount") fixture.Dropbox.Seed(root with { SharedRoot = true }, null);
            if (mode is "foreign" or "replaced") fixture.Dropbox.Seed(root with { Id = "replacement-id" }, null);
            if (mode == "absent") await fixture.Dropbox.DeleteAsync(root, null, default);
            var before = fixture.Inner.FolderRemoval(PairId, existing);
            var request = new FolderRemovalRequest(row, Side.Dropbox, FolderRemovalRoute.SplitCleanup)
            {
                ExpectedRootId = mode == "foreign" ? "replacement-id" : root.Id,
                Live = true, Accepts = FolderRemovalRequest.EmptyPlan
            };
            var folders = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
                RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0)).Items.Folders;
            var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
            var retired = fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval));
            var result = await folders.PrepareCheckedAsync(request, default);
            var after = fixture.Inner.FolderRemoval(PairId, existing);
            var superseded = mode == "empty";
            var reused = mode is "armed" or "chosen" or "evidence";
            var expected = mode switch
            {
                "child" => FolderRemovalCheck.NotAccepted,
                "foreign" => FolderRemovalCheck.ForeignOperation,
                "mount" => FolderRemovalCheck.Mount,
                "replaced" => FolderRemovalCheck.Replaced,
                "absent" => FolderRemovalCheck.Absent,
                _ => FolderRemovalCheck.Passed
            };
            Check.Require(result.Check == expected &&
                fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) - begun == (superseded ? 1 : 0) &&
                fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) - retired == (superseded ? 1 : 0) &&
                (superseded ? result.OperationId is not null && result.OperationId != existing &&
                    after is { Active: false, RetirementDisposition: FolderRetirementDisposition.NoEffect, Cause: null }
                    : after == before && result.OperationId == (reused ? existing : null)),
                "split cleanup preparation preserves custody until its own empty plan is accepted: " + mode +
                "; check=" + result.Check + "; operation=" + after);
            if (superseded)
            {
                var next = fixture.Inner.FolderRemoval(PairId, result.OperationId!);
                Check.Require(next is { Authority: FolderRemovalAuthority.Inferred, SourceEvidence: null } &&
                    Recovery.Members(fixture.Inner, PairId, next.OperationId, fixture.Settings.StoragePageSize).Count() == 1 &&
                    fixture.Inner.Issues(PairId).Count == 0,
                    "an accepted cleanup freezes only the empty root and retires its predecessor without a notice");
            }
        }
    }

    private static async Task SplitCleanupMixedMembersAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        var root = row.Dropbox!;
        var branches = Enumerable.Range(0, 3).Select(index =>
            FolderEntry(PathNames.Child(root.Path, "Branch" + index), "branch-" + index)).ToArray();
        var survivor = FolderEntry(PathNames.Child(root.Path, "Survivor"), "survivor-id");
        foreach (var child in branches.Append(survivor)) fixture.Dropbox.Seed(child, null);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = branches.Select(child => SyncedEntries.From(child with { Id = "local-" + child.Id }, child)).ToArray()
        });
        string existing;
        using (var plan = await new FolderPlanBuilder(context).BuildAsync(row, Side.Dropbox,
            new HashSet<string>(), true, default))
            existing = fixture.Inner.BeginFolderRemoval(PairId, plan, FolderRemovalAuthority.Inferred,
                fixture.Settings.StoragePageSize, default);
        var effects = fixture.Inner.ReadyFolderEffects(PairId, existing, -1, branches.Length).ToArray();
        Check.Require(effects.Length == branches.Length, "a survivor leaves three independently selected branches");
        fixture.Inner.ArmFolderSubmission(PairId, [effects[^1]]);
        var members = Recovery.Members(fixture.Inner, PairId, existing, fixture.Settings.StoragePageSize).ToArray();
        Check.Require(members.Count(member => member.EffectState == FolderRemovalItem.EffectStatus.Ready) == 2 &&
            members.Count(member => member.EffectState == FolderRemovalItem.EffectStatus.Armed) == 1,
            "two proven-unsent branches precede one possibly-sent branch");
        // Make a successor's empty-plan proof possible, so only the mixed custody prevents supersession.
        foreach (var child in branches.Append(survivor)) await fixture.Dropbox.DeleteAsync(child, null, default);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            ForgetSelf = branches.Select(child => (Side.Dropbox, PathNames.DropboxKey(child.Path))).ToArray()
        });
        var before = fixture.Inner.FolderRemoval(PairId, existing);
        var folders = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0)).Items.Folders;
        var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        var retired = fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval));
        var result = await folders.PrepareCheckedAsync(new FolderRemovalRequest(row, Side.Dropbox, FolderRemovalRoute.SplitCleanup)
        {
            ExpectedRootId = root.Id, Live = true, Accepts = FolderRemovalRequest.EmptyPlan
        }, default);
        Check.Require(result.Check == FolderRemovalCheck.Passed && result.OperationId == existing &&
            fixture.Inner.FolderRemoval(PairId, existing) == before &&
            Recovery.Members(fixture.Inner, PairId, existing, fixture.Settings.StoragePageSize).SequenceEqual(members) &&
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun &&
            fixture.Hooks.Count(nameof(IStateStore.RetireFolderRemoval)) == retired && fixture.Inner.Issues(PairId).Count == 0,
            "one possibly-sent member preserves the whole mixed operation without retirement, successor or notice");
        var disposition = FolderRetirementDisposition.NoEffect;
        var walk = await SqlStatementsAsync(fixture, () =>
        {
            disposition = FolderRetirement.For(Recovery.Members(fixture.Inner, PairId, existing,
                fixture.Settings.StoragePageSize));
            return Task.CompletedTask;
        });
        var throughArmed = members.TakeWhile(member => member.EffectState != FolderRemovalItem.EffectStatus.Armed).Count() + 1;
        Check.Require(disposition == FolderRetirementDisposition.Acknowledged &&
            walk.Total == 2 * ((throughArmed + fixture.Settings.StoragePageSize - 1) / fixture.Settings.StoragePageSize),
            "the mixed-member walk stops on the page containing its first possibly-sent member");
    }

    private static async Task SplitCleanupMountReuseAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var (context, row) = SplitCleanupContext(fixture);
        string existing;
        using (var plan = await new FolderPlanBuilder(context).BuildAsync(row, Side.Dropbox,
            new HashSet<string>(), true, default))
            existing = fixture.Inner.BeginFolderRemoval(PairId, plan, FolderRemovalAuthority.Chosen,
                fixture.Settings.StoragePageSize, default);
        Check.Require(fixture.Inner.ReadyFolderEffects(PairId, existing, -1, fixture.Settings.StoragePageSize).Any(),
            "the reused operation has a genuinely ready effect before the mount transition");
        var root = row.Dropbox! with { SharedRoot = true };
        fixture.Dropbox.Seed(root, null);
        var submits = 0;
        context = context with
        {
            Dropbox = IdentitySide.Truthful(new ProxySide(fixture.Dropbox) { OnSubmit = _ => submits++ }, fixture.Dropbox)
        };
        var folders = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0)).Items.Folders;
        var begun = fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval));
        var result = await folders.RemoveCheckedAsync(new FolderRemovalRequest(row, Side.Dropbox, FolderRemovalRoute.SplitCleanup)
        {
            ExpectedRootId = root.Id, Live = true, Accepts = FolderRemovalRequest.EmptyPlan
        }, default);
        Check.Require(result.Check == FolderRemovalCheck.Passed && result.Removal?.Status == FolderRemovalStatus.Retained &&
            submits == 0 && await fixture.Dropbox.ReadOneAsync(root.Path, default) == root &&
            fixture.Hooks.Count(nameof(IStateStore.BeginFolderRemoval)) == begun,
            "a non-supersedable ready operation is reused, but its changed SharedRoot refuses a new send");
    }

    // M1 crosses two full pages; M2 distinguishes a linear walk from per-member re-walks. The root-only
    // case also measures the cleanup's own unsent operation. Trace counts exclude plan scratch and SQL VM work.
    private static async Task SplitCleanupCostAsync()
    {
        int pageSize;
        using (var sizing = new Fixture()) pageSize = sizing.Settings.StoragePageSize;
        var sizes = new[] { 1, 2 * pageSize + 1, 4 * (2 * pageSize + 1) };
        var counts = new List<(int Members, int Walk, int Retire, int Freeze)>();
        foreach (var members in sizes)
        foreach (var sent in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            var (context, row) = SplitCleanupContext(fixture);
            var children = Enumerable.Range(0, members - 1).Select(index =>
                FolderEntry(PathNames.Child(row.Dropbox!.Path, "Child" + index), "child-" + index)).ToArray();
            foreach (var child in children) fixture.Dropbox.Seed(child, null);
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Record = children.Select(child => SyncedEntries.From(child with { Id = "local-" + child.Id }, child)).ToArray()
            });
            string existing;
            using (var plan = await new FolderPlanBuilder(context).BuildAsync(row, Side.Dropbox,
                new HashSet<string>(), true, default))
                existing = fixture.Inner.BeginFolderRemoval(PairId, plan, FolderRemovalAuthority.Inferred, pageSize, default);
            Check.Require(Recovery.Members(fixture.Inner, PairId, existing, pageSize).Count() == members,
                "the measured operation contains exactly the stated member population");
            if (sent) fixture.Inner.ArmFolderSubmission(PairId,
                fixture.Inner.ReadyFolderEffects(PairId, existing, -1, pageSize).ToArray());
            var disposition = FolderRetirementDisposition.NoEffect;
            var walk = await SqlStatementsAsync(fixture, () =>
            {
                disposition = FolderRetirement.For(Recovery.Members(fixture.Inner, PairId, existing, pageSize));
                return Task.CompletedTask;
            });
            var expectedWalk = sent ? 2 : 2 * ((members + pageSize - 1) / pageSize + 1);
            Check.Require(walk.Total == expectedWalk && disposition == (sent
                    ? FolderRetirementDisposition.Acknowledged : FolderRetirementDisposition.NoEffect),
                "split cleanup walks all unsent members but stops at the first possibly-sent member: M=" + members +
                "; sent=" + sent + "; statements=" + walk.Total);
            if (sent) continue;
            foreach (var child in children) await fixture.Dropbox.DeleteAsync(child, null, default);
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                ForgetSelf = children.Select(child => (Side.Dropbox, PathNames.DropboxKey(child.Path))).ToArray()
            });
            using var empty = await new FolderPlanBuilder(context).BuildAsync(row, Side.Dropbox,
                new HashSet<string>(), true, default);
            Check.Require(FolderRemovalRequest.EmptyPlan(empty, default), "only an accepted empty plan may retire custody");
            var generation = fixture.Inner.FolderRemoval(PairId, existing).Generation;
            var retire = await SqlStatementsAsync(fixture, () =>
            {
                fixture.Inner.RetireFolderRemoval(PairId, existing, generation, FolderRetirementDisposition.NoEffect,
                    StateChange.Empty);
                return Task.CompletedTask;
            });
            var freeze = await SqlStatementsAsync(fixture, () =>
            {
                fixture.Inner.BeginFolderRemoval(PairId, empty, FolderRemovalAuthority.Inferred, pageSize, default);
                return Task.CompletedTask;
            });
            // Retirement is M member updates plus seven fixed statements. Empty freeze is BEGIN, pair, operation,
            // root member, counterpart's empty range and COMMIT; no rename linkage and no active scan partition.
            Check.Require(retire.Total == members + 7 && freeze.Total == 6,
                "split cleanup retirement and successor freeze have absolute SQL budgets: M=" + members +
                "; retirement=" + retire.Total + "; freeze=" + freeze.Total);
            counts.Add((members, walk.Total, retire.Total, freeze.Total));
            Console.WriteLine("Split cleanup SQL: M=" + members + "; P=" + pageSize + "; walk=" + walk.Total +
                "; retirement=" + retire.Total + "; freeze=" + freeze.Total);
        }
        var small = counts[^2];
        var large = counts[^1];
        Check.Require(large.Retire - small.Retire == large.Members - small.Members && large.Freeze == small.Freeze &&
            large.Walk - small.Walk == 2 * ((large.Members + pageSize - 1) / pageSize -
                (small.Members + pageSize - 1) / pageSize), "split cleanup costs grow only with the old operation's members");
    }

    private static (ExecutorContext Context, Row Row) SplitCleanupContext(Fixture fixture)
    {
        RepairDone(fixture);
        ClearObligations(fixture);
        var selection = Selected(fixture, "Unrelated", LocalEntry("Unrelated", "unrelated-id"));
        var root = FolderEntry("Parent", "source-id");
        var saved = SyncedEntries.From(root with { Id = "local-source-id" }, root);
        fixture.Dropbox.Seed(root, null);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var context = fixture.Context(selection) with
        {
            Local = IdentitySide.Truthful(fixture.Local, fixture.Local),
            Dropbox = IdentitySide.Truthful(fixture.Dropbox, fixture.Dropbox),
            CaptureFolderCursorAsync = _ => Task.FromResult(Cursor),
            ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
        };
        return (context, Row.ForOneSide(Side.Dropbox, saved.LocalKey, root, saved, [], null, RowContext.Neutral));
    }
}
