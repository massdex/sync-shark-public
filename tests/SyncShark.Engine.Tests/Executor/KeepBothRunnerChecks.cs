// HANDOFF: StateChange.ClearPending was removed from the landed contract, so both "does not clear pending" clauses were superseded and dropped; the remaining forget/rename assertions are unchanged.
using System.Text;
using Massdex.SyncShark.Engine.Edge;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for KeepBothRunner (`--engine-checks syncer`), written by KeepBothRunner's writer.</summary>
public static class KeepBothRunnerChecks
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        foreach (var reuseLocal in new[] { false, true })
        foreach (var reuseDropbox in new[] { false, true })
            await CopiesAsync(reuseLocal, reuseDropbox);
        foreach (var keptSide in new[] { Side.Local, Side.Dropbox })
        foreach (var occupant in new[] { "absent", "matching", "different" })
            await RetainedSideAsync(keptSide, occupant);
        await MissingLabelsAsync();
        await DifferentContentsAsync();
        await LazyHashAsync();
        await DifferentSizesAsync();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var mutation in new[] { "content", "size", "folder", "skipped", "missing" })
        foreach (var retainedOnly in new[] { false, true })
            await ChangedReuseAsync(side, mutation, retainedOnly);
        await LabelUsabilityAsync();
        await FolderOccupantsAsync();
        await SkippedOccupantsAsync();
        await TimeTieKeepsBothAsync();
        await CurrentStampLabelsAsync();
        await DownloadHashMismatchAsync();
        await LocalSpaceRefusalAsync();
        await DownloadCancellationAsync();
        await SourceReadFailureAsync();
        await PartialLegAsync();
        await CancellationAsync();
    }

    private static async Task CopiesAsync(bool reuseLocal, bool reuseDropbox)
    {
        var context = Context();
        using var store = context.Store;
        var admission = (FakeAdmission)context.Admission;
        var space = new Space(Plenty);
        context = context with { ReserveDownloadAsync = space.ReserveAsync };
        await context.Local.EnsureFolderAsync("local", CancellationToken.None);
        await context.Dropbox.EnsureFolderAsync("remote", CancellationToken.None);
        var local = await PutAsync(context.Local, "local/item.txt", "local bytes");
        var dropbox = await PutAsync(context.Dropbox, "remote/other.txt", "other bytes");
        var row = RowFor(local, dropbox);
        if (reuseLocal)
        {
            var saved = SyncedEntries.From(local, dropbox);
            store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
            row = row with { Synced = saved };
        }
        var runner = new KeepBothRunner(context, new Transfers(context));
        var labels = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(labels.DropboxLabel == Label(context, dropbox.Path, Side.Local, local.Modified),
            "The upload label uses the real Dropbox path and local version's label.");
        Check.Require(labels.LocalLabel == Label(context, local.Path, Side.Dropbox, dropbox.Modified),
            "The download label uses the real local path and Dropbox version's label.");
        Check.Require(!labels.ReuseDropbox && !labels.ReuseLocal, "Absent labels are not reused.");
        Entry? priorLocal = null;
        Entry? priorDropbox = null;
        if (reuseLocal) priorLocal = await CopyAsync(context.Dropbox, dropbox, context.Local, labels.LocalLabel!);
        if (reuseDropbox) priorDropbox = await CopyAsync(context.Local, local, context.Dropbox, labels.DropboxLabel!);
        labels = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(labels.ReuseLocal == reuseLocal && labels.ReuseDropbox == reuseDropbox,
            "Each completed label is independently reused by content hash.");

        var localCalls = new RecordingSide(context.Local);
        var dropboxCalls = new RecordingSide(context.Dropbox);
        context = context with { Local = localCalls, Dropbox = dropboxCalls };
        runner = new KeepBothRunner(context, new Transfers(context));
        var change = await runner.RunAsync(row, labels, CancellationToken.None);
        foreach (var side in new[] { localCalls, dropboxCalls })
        {
            var reused = labels.On(side.Side).Reused;
            var source = side.Side == Side.Local ? dropbox : local;
            var other = side.Side == Side.Local ? dropboxCalls : localCalls;
            Check.Require(side.Calls.Count(call => call.Operation == nameof(ISide.StageAsync)) == (reused ? 0 : 1)
                && side.Calls.Count(call => call.Operation == nameof(ISide.PublishAsync)) == (reused ? 0 : 1)
                && other.Calls.Count(call => call.Operation == nameof(ISide.OpenReadAsync)) == (reused ? 0 : 1)
                && side.StagedBytes == (reused ? 0 : source.Size),
                "Each normal keep-both leg transfers exact bytes once, or nothing when reused.");
        }
        Check.Require(space.Requests.Count == (labels.ReuseLocal ? 0 : 1)
            && space.Requests.All(request => request.Bytes == dropbox.Size && request.Target == labels.LocalLabel),
            "Only a fresh local download reserves its declared source bytes once; a reused copy reserves nothing.");
        Check.Require(admission.Admitted == 0 && store.Pending(context.Pair.PairId) is NoPending,
            "Keep-both takes no admission permit and never owns the pair's replace-pending slot.");
        Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, PathNames.Key(local.Path)) == row.Synced,
            "KeepBoth returns a change without committing it.");
        Check.Require(change.Record.Count == 2 && change.ForgetSelf.Count == (row.Synced is null ? 0 : 1),
            "One change records two crossed links and forgets the prior row only when present.");
        Check.Require(change.Record.Any(x => x.LocalPath == local.Path && x.DropboxPath == labels.DropboxLabel)
            && change.Record.Any(x => x.LocalPath == labels.LocalLabel && x.DropboxPath == dropbox.Path),
            "Both originals are cross-linked to the opposite-side copies.");
        Check.Require(change.Raise.Count == 1 && change.Raise[0].Kind == IssueKind.Info
            && change.Raise[0].Cause == TextKey.InfoKeptBoth
            && change.Raise[0].Subject.Items.SequenceEqual(new IssueItem[]
            {
                new(Side.Local, local.Path, local.Id), new(Side.Dropbox, dropbox.Path, dropbox.Id),
                new(Side.Dropbox, labels.DropboxLabel!, (await context.Dropbox.ReadOneAsync(labels.DropboxLabel!, default))!.Id),
                new(Side.Local, labels.LocalLabel!, (await context.Local.ReadOneAsync(labels.LocalLabel!, default))!.Id)
            }), "The InfoKeptBoth subject contains originals followed by actual copy identities.");
        Check.Require(new[] { local.Path, dropbox.Path }.All(path => change.Close.Any(close =>
            close.Path == path && close.Kinds.Contains(IssueKind.Failure) && close.Kinds.Contains(IssueKind.Tie))),
            "Failures and ties on both originals are closed.");
        Check.Require(change.ForgetSubtrees.Count == 0 && change.Renames.Count == 0,
            "Keeping both neither replaces, renames nor forgets a subtree.");
        store.Commit(context.Pair.PairId, change);
        await VerifyCopiesAsync(context, local, dropbox, labels);
        var notice = store.Issues(context.Pair.PairId).Single();
        Check.Require(notice.Subject.Items.SequenceEqual(change.Raise[0].Subject.Items) &&
            !IssuePolicy.Holds(notice.Kind, notice.Cause) &&
            IssuePresentation.Options(notice).Select(option => option.Choice).SequenceEqual(
                new[] { ResolutionChoice.Dismiss.ToString(), ResolutionChoice.Ignore.ToString() }),
            "The enriched stored notice remains non-holding with Dismiss and visibility choices.");
        store.Commit(context.Pair.PairId, StateChange.Empty with { Raise = change.Raise });
        Check.Require(store.Issues(context.Pair.PairId).Single().Id == notice.Id,
            "Repeated enriched notices deduplicate.");
        foreach (var binding in change.Record)
        foreach (var missing in new[] { Side.Local, Side.Dropbox })
        foreach (var edited in new[] { false, true })
        {
            var remainingLocal = missing == Side.Local ? null : await context.Local.ReadOneAsync(binding.LocalPath, default);
            var remainingDropbox = missing == Side.Dropbox ? null : await context.Dropbox.ReadOneAsync(binding.DropboxPath, default);
            if (edited && remainingLocal is not null) remainingLocal = remainingLocal with { Size = remainingLocal.Size + 1 };
            if (edited && remainingDropbox is not null) remainingDropbox = remainingDropbox with { ContentHash = "edited" };
            var linked = row with { Local = remainingLocal, Dropbox = remainingDropbox, Synced = binding };
            var action = Decide.For(linked, context.Pair.FirstRun, DateTimeOffset.UtcNow, false);
            var expected = missing == Side.Local
                ? (edited ? ActionKind.Download : ActionKind.DeleteDropbox)
                : (edited ? ActionKind.Upload : ActionKind.DeleteLocal);
            Check.Require(action.Kind == expected && action.InfoAfter == (edited ? TextKey.InfoEditBeatsDelete : null),
                "Crossed bindings preserve deletion propagation and edited-counterpart restoration.");
        }
        if (priorLocal is not null)
            Check.Require(await context.Local.ReadOneAsync(labels.LocalLabel!, CancellationToken.None) == priorLocal,
                "Reused local copies retain their identity.");
        if (priorDropbox is not null)
            Check.Require(await context.Dropbox.ReadOneAsync(labels.DropboxLabel!, CancellationToken.None) == priorDropbox,
                "Reused Dropbox copies retain their identity.");
    }

    private static async Task RetainedSideAsync(Side keptSide, string occupant)
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "local.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "remote.txt", "BBBBBB");
        var saved = SyncedEntries.From(local, dropbox);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var row = RowFor(local, dropbox) with { Synced = saved };
        var target = context.For(keptSide);
        var source = keptSide == Side.Local ? dropbox : local;
        var sourceSide = keptSide == Side.Local ? context.Dropbox : context.Local;
        var original = keptSide == Side.Local ? local : dropbox;
        var firstLabel = Label(context, original.Path, sourceSide.Side, source.Modified);
        Entry? prior = occupant switch
        {
            "matching" => await CopyAsync(sourceSide, source, target, firstLabel),
            "different" => await PutAsync(target, firstLabel, new string('X', (int)source.Size)),
            _ => null
        };
        var localCalls = new RecordingSide(context.Local);
        var dropboxCalls = new RecordingSide(context.Dropbox);
        context = context with { Local = localCalls, Dropbox = dropboxCalls };
        var runner = new KeepBothRunner(context, new Transfers(context));
        var labels = await runner.ChooseLabelsAsync(row, CancellationToken.None, keptSide);
        var (label, reused) = labels.On(keptSide);
        Check.Require(label == Label(context, original.Path, sourceSide.Side, source.Modified,
                occupant == "different" ? 2 : 1) && reused == (occupant == "matching"),
            "The retained side alone chooses a label, reusing only equal contents.");
        Check.Require(labels.On(sourceSide.Side) == (null, false),
            "The excluded side has no label and no reuse flag.");
        var change = await runner.RunAsync(row, labels, CancellationToken.None);
        var keptCalls = keptSide == Side.Local ? localCalls : dropboxCalls;
        var excludedCalls = keptSide == Side.Local ? dropboxCalls : localCalls;
        Check.Require(excludedCalls.Calls.All(call =>
                call.Path == source.Path && call.Operation is nameof(ISide.ReadOneAsync)
                    or nameof(ISide.ContentHashAsync) or nameof(ISide.OpenReadAsync)),
            "The omitted leg performs no label lookup, validation, listing or mutation.");
        Check.Require(keptCalls.Calls.Count(call => call.Operation == nameof(ISide.StageAsync)) == (reused ? 0 : 1)
            && keptCalls.Calls.Count(call => call.Operation == nameof(ISide.PublishAsync)) == (reused ? 0 : 1)
            && excludedCalls.Calls.Count(call => call.Operation == nameof(ISide.OpenReadAsync)) == (reused ? 0 : 1)
            && keptCalls.Calls.All(call => call.Operation is nameof(ISide.ReadOneAsync)
                or nameof(ISide.ContentHashAsync) or nameof(ISide.StageAsync) or nameof(ISide.PublishAsync))
            && keptCalls.StagedBytes == (reused ? 0 : source.Size) && excludedCalls.StagedBytes == 0,
            "Only the retained leg transfers its exact bytes, and reused contents transfer nothing.");
        var copy = await target.ReadOneAsync(label!, CancellationToken.None);
        Check.Require(copy is not null && change.Record.Count == 1
            && change.Record[0] == SyncedEntries.From(keptSide, copy!, source),
            "Exactly one crossed link records the retained copy and its source.");
        Check.Require(change.ForgetSelf.SequenceEqual(new[] { (Side.Local, saved.LocalKey) })
            && change.ForgetSubtrees.Count == 0 && change.Renames.Count == 0,
            "The returned change forgets only the old binding, with no removal or replacement effects.");
        Check.Require(change.Raise.Count == 1 && change.Raise[0].Kind == IssueKind.Info
            && change.Raise[0].Cause == TextKey.InfoKeptBoth
            && change.Raise[0].Subject.Items.SequenceEqual(new IssueItem[]
            {
                new(Side.Local, local.Path, local.Id), new(Side.Dropbox, dropbox.Path, dropbox.Id),
                new(keptSide, copy!.Path, copy.Id)
            }), "The one-leg InfoKeptBoth subject contains originals followed by its actual retained copy.");
        var notice = new Issue(1, IssueKind.Info, TextKey.InfoKeptBoth, local.Path,
            change.Raise[0].Subject, DateTimeOffset.UnixEpoch, false);
        var text = IssuePresentation.Render(notice, new(RetryPresentationState.Unavailable));
        Check.Require(!text.Contains(source.Path, StringComparison.Ordinal) &&
            text.Contains(original.Path, StringComparison.Ordinal) && text.Contains(copy!.Path, StringComparison.Ordinal) &&
            !text.Contains("counterpart", StringComparison.Ordinal) && !text.Contains("Linked", StringComparison.Ordinal),
            "A one-leg notice names only kept-side locations and makes no cross-side link claim.");
        Check.Require(new[] { local.Path, dropbox.Path }.All(path => change.Close.Any(close =>
            close.Path == path && close.Kinds.Contains(IssueKind.Failure) && close.Kinds.Contains(IssueKind.Tie))),
            "One-leg keep-both closes failures and ties on both originals.");
        Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved
            && store.Issues(context.Pair.PairId).Count == 0,
            "The runner does not commit the one-leg change itself.");
        Check.Require(await target.ContentHashAsync(copy!, CancellationToken.None)
                == await sourceSide.ContentHashAsync(source, CancellationToken.None)
            && await context.Local.ReadOneAsync(local.Path, CancellationToken.None) == local
            && await context.Dropbox.ReadOneAsync(dropbox.Path, CancellationToken.None) == dropbox,
            "The retained copy has exact source content and both originals remain unchanged.");
        if (prior is not null)
            Check.Require(await target.ReadOneAsync(firstLabel, CancellationToken.None) == prior,
                "Matching labels retain identity and different-content occupants remain untouched.");
    }

    private static async Task MissingLabelsAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "BBBB");
        var localCalls = new RecordingSide(context.Local);
        var dropboxCalls = new RecordingSide(context.Dropbox);
        context = context with { Local = localCalls, Dropbox = dropboxCalls };
        var rejected = false;
        try
        {
            await new KeepBothRunner(context, new Transfers(context)).RunAsync(RowFor(local, dropbox),
                new KeepBothLabels(null, false, null, false), CancellationToken.None);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Check.Require(rejected && localCalls.Calls.Count == 0 && dropboxCalls.Calls.Count == 0,
            "Two absent labels are rejected before any provider effects.");
    }

    private static async Task DifferentContentsAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "BBBB");
        var row = RowFor(local, dropbox) with
        {
            LocalHash = await context.Local.ContentHashAsync(local, CancellationToken.None)
        };
        var runner = new KeepBothRunner(context, new Transfers(context));
        var first = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        var wrongLocal = await PutAsync(context.Local, first.LocalLabel!, "CCCC");
        var wrongDropbox = await PutAsync(context.Dropbox, first.DropboxLabel!, "DDDD");
        Check.Require(wrongLocal.Size == dropbox.Size && wrongLocal.Modified == dropbox.Modified
            && wrongDropbox.Size == local.Size && wrongDropbox.Modified == local.Modified,
            "The colliding labels have identical size and time but different bytes.");
        var next = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(!next.ReuseLocal && !next.ReuseDropbox
            && next.LocalLabel == Label(context, local.Path, Side.Dropbox, dropbox.Modified, 2)
            && next.DropboxLabel == Label(context, dropbox.Path, Side.Local, local.Modified, 2),
            "Different bytes advance the label attempt on both sides.");
        var matchingLocal = await CopyAsync(context.Dropbox, dropbox, context.Local, next.LocalLabel!);
        var matchingDropbox = await CopyAsync(context.Local, local, context.Dropbox, next.DropboxLabel!);
        var reused = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(reused == next with { ReuseLocal = true, ReuseDropbox = true },
            "Matching contents are reused after bypassing different-content labels.");
        var change = await runner.RunAsync(row, reused, CancellationToken.None);
        Check.Require(change.Record.Any(x => x.LocalId == matchingLocal.Id)
            && change.Record.Any(x => x.DropboxId == matchingDropbox.Id), "Reused entries supply the saved identities.");
        Check.Require(await context.Local.ReadOneAsync(first.LocalLabel!, CancellationToken.None) == wrongLocal
            && await context.Dropbox.ReadOneAsync(first.DropboxLabel!, CancellationToken.None) == wrongDropbox,
            "Unrelated colliding copies are untouched.");
    }

    private static async Task LazyHashAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "BBBB");
        var runner = new KeepBothRunner(context, new Transfers(context));
        var first = await runner.ChooseLabelsAsync(RowFor(local, dropbox), CancellationToken.None);
        await CopyAsync(context.Local, local, context.Dropbox, first.DropboxLabel!);
        var occupied = await runner.ChooseLabelsAsync(RowFor(local, dropbox), CancellationToken.None);
        Check.Require(occupied.ReuseDropbox && occupied.DropboxLabel == first.DropboxLabel,
            "An occupied label is hashed and reused when its content matches the original.");

        var freeLocal = await PutAsync(context.Local, "free.txt", "CCCC");
        var freeDropbox = await PutAsync(context.Dropbox, "free.txt", "DDDD");
        ((FakeSide)context.Local).FailNext(freeLocal.Path,
            new InvalidOperationException("A free label must not hash the original."));
        var free = await runner.ChooseLabelsAsync(RowFor(freeLocal, freeDropbox), CancellationToken.None);
        Check.Require(free.DropboxLabel == Label(context, freeDropbox.Path, Side.Local, freeLocal.Modified)
            && free.LocalLabel == Label(context, freeLocal.Path, Side.Dropbox, freeDropbox.Modified),
            "Free labels are chosen without hashing the original local file.");
    }

    private static async Task DifferentSizesAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "BBBB");
        var runner = new KeepBothRunner(context, new Transfers(context));
        var first = await runner.ChooseLabelsAsync(RowFor(local, dropbox), CancellationToken.None);
        var wrongLocal = dropbox with { Path = first.LocalLabel!, Size = dropbox.Size + 1 };
        var wrongDropbox = local with
        {
            Path = first.DropboxLabel!, Size = local.Size + 1,
            ContentHash = await context.Local.ContentHashAsync(local, CancellationToken.None)
        };
        ((FakeSide)context.Local).Seed(wrongLocal, null);
        ((FakeSide)context.Dropbox).Seed(wrongDropbox, null);
        ((FakeSide)context.Local).FailNext(local.Path,
            new InvalidOperationException("A wrong-size label must not hash the source."));
        var next = await runner.ChooseLabelsAsync(RowFor(local, dropbox), CancellationToken.None);
        Check.Require(!next.ReuseLocal && !next.ReuseDropbox &&
            next.LocalLabel == Label(context, local.Path, Side.Dropbox, dropbox.Modified, 2) &&
            next.DropboxLabel == Label(context, dropbox.Path, Side.Local, local.Modified, 2),
            "Wrong-size labels are skipped without hashing the unreadable candidate or the source.");
    }

    private static async Task ChangedReuseAsync(Side side, string mutation, bool retainedOnly)
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "BBBB");
        var saved = SyncedEntries.From(local, dropbox);
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var row = RowFor(local, dropbox) with { Synced = saved };
        var runner = new KeepBothRunner(context, new Transfers(context));
        Side? keptSide = retainedOnly ? side : null;
        var labels = await runner.ChooseLabelsAsync(row, CancellationToken.None, keptSide);
        var target = (FakeSide)context.For(side);
        var copy = side == Side.Local
            ? await CopyAsync(context.Dropbox, dropbox, target, labels.LocalLabel!)
            : await CopyAsync(context.Local, local, target, labels.DropboxLabel!);
        labels = await runner.ChooseLabelsAsync(row, CancellationToken.None, keptSide);
        Check.Require(side == Side.Local ? labels.ReuseLocal : labels.ReuseDropbox,
            "The label is verified reusable before the external change.");
        var changed = false;
        async Task ChangeAsync()
        {
            changed = true;
            if (mutation == "missing")
            {
                await target.DeleteAsync(copy, null, CancellationToken.None);
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(mutation == "size" ? "CCCCC" : "CCCC");
            var edited = mutation switch
            {
                "folder" => copy with { IsFolder = true },
                "skipped" => copy with { Skip = TextKey.SkipOnlineOnly.ToString() },
                _ => copy with { Size = bytes.LongLength, ContentHash = DropboxContentHash.Compute(bytes) }
            };
            target.Seed(edited, mutation is "folder" or "skipped" ? null : bytes);
        }
        if (side == Side.Local && !retainedOnly)
        {
            context = context with { Dropbox = new RecordingSide(context.Dropbox, ChangeAsync) };
        }
        else
        {
            await ChangeAsync();
        }
        runner = new KeepBothRunner(context, new Transfers(context));
        var rejected = false;
        try
        {
            var change = await runner.RunAsync(row, labels, CancellationToken.None);
            store.Commit(context.Pair.PairId, change);
        }
        catch (ChangedUnderneathException error)
        {
            rejected = error.Path == copy.Path;
        }
        Check.Require(changed && rejected, "A changed reused label is rejected at its path, not recorded.");
        Check.Require(store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved &&
            store.SyncedByKey(context.Pair.PairId, side, PathNames.Key(copy.Path)) is null &&
            store.Issues(context.Pair.PairId).Count == 0,
            "Rejected reuse preserves the prior row and creates neither a crossed link nor InfoKeptBoth.");
        if (side == Side.Local && !retainedOnly)
        {
            Check.Require(await context.Dropbox.ReadOneAsync(labels.DropboxLabel!, CancellationToken.None) is not null,
                "The upload completes before the edited local label is rejected; the partial copy is preserved.");
        }
    }

    private static async Task LabelUsabilityAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "BBBB");
        var row = RowFor(local, dropbox);
        var runner = new KeepBothRunner(context, new Transfers(context));
        var fresh = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(!fresh.ReuseLocal && !fresh.ReuseDropbox &&
            await runner.AreUsableAsync(row, fresh, CancellationToken.None),
            "Free label paths authorize a fresh create and are usable.");
        await CopyAsync(context.Dropbox, dropbox, context.Local, fresh.LocalLabel!);
        await CopyAsync(context.Local, local, context.Dropbox, fresh.DropboxLabel!);
        var reused = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(reused.ReuseLocal && reused.ReuseDropbox &&
            await runner.AreUsableAsync(row, reused, CancellationToken.None),
            "Copies holding the reused content make the whole label set usable.");
        var occupant = (await context.Local.ReadOneAsync(reused.LocalLabel!, CancellationToken.None))!;
        // The seeded skipped occupant keeps its bytes: the former preflight hashes a reuse label regardless of Skip.
        ((FakeSide)context.Local).Seed(occupant with { Skip = TextKey.SkipOnlineOnly.ToString() }, Utf8("BBBB"));
        // EX-06 is a move: preflight keeps its former answer for a skipped reuse label, and execution still
        // refuses it at the label path with no effect and no notice.
        Check.Require(await runner.AreUsableAsync(row, reused, CancellationToken.None),
            "Preflight keeps the former semantics: a skipped reuse label with matching content is not refused early.");
        ChangedUnderneathException? refused = null;
        try { await runner.RunAsync(row, reused, CancellationToken.None); }
        catch (ChangedUnderneathException error) { refused = error; }
        Check.Require(refused?.Path == reused.LocalLabel &&
            store.Issues(context.Pair.PairId).Count == 0 &&
            await context.Dropbox.ReadOneAsync(reused.DropboxLabel!, CancellationToken.None) is not null,
            "Execution still refuses the raced skipped label at its path, records nothing and raises no notice.");
    }

    private static async Task FolderOccupantsAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "BBBB");
        var row = RowFor(local, dropbox);
        var runner = new KeepBothRunner(context, new Transfers(context));
        var first = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        await context.Local.EnsureFolderAsync(first.LocalLabel!, CancellationToken.None);
        await context.Dropbox.EnsureFolderAsync(first.DropboxLabel!, CancellationToken.None);
        var next = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(!next.ReuseLocal && !next.ReuseDropbox
            && next.LocalLabel != first.LocalLabel && next.DropboxLabel != first.DropboxLabel,
            "A folder is not a reusable content copy.");
    }

    private static async Task SkippedOccupantsAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "BBBB");
        var row = RowFor(local, dropbox);
        var runner = new KeepBothRunner(context, new Transfers(context));
        var first = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        var skippedLocal = dropbox with { Path = first.LocalLabel!, Skip = TextKey.SkipOnlineOnly.ToString() };
        var skippedDropbox = local with
        {
            Path = first.DropboxLabel!, Skip = TextKey.SkipOnlineOnly.ToString(),
            ContentHash = await context.Local.ContentHashAsync(local, CancellationToken.None)
        };
        ((FakeSide)context.Local).Seed(skippedLocal, null);
        ((FakeSide)context.Dropbox).Seed(skippedDropbox, null);
        var next = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(!next.ReuseLocal && !next.ReuseDropbox &&
            next.LocalLabel == Label(context, local.Path, Side.Dropbox, dropbox.Modified, 2) &&
            next.DropboxLabel == Label(context, dropbox.Path, Side.Local, local.Modified, 2),
            "Skipped labels advance the attempt without reading content or reusing a matching cached hash.");
        await runner.RunAsync(row, next, CancellationToken.None);
        Check.Require(await context.Local.ReadOneAsync(first.LocalLabel!, CancellationToken.None) == skippedLocal &&
            await context.Dropbox.ReadOneAsync(first.DropboxLabel!, CancellationToken.None) == skippedDropbox,
            "Keep-both leaves skipped label occupants untouched.");
        await VerifyCopiesAsync(context, local, dropbox, next);
    }

    private static async Task TimeTieKeepsBothAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "local");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "other");
        Check.Require(local.Modified == dropbox.Modified && local.Size == dropbox.Size
            && local.ContentHash != dropbox.ContentHash,
            "The tie case has equal size and time but different bytes, so no single winner exists.");
        var row = RowFor(local, dropbox);
        var runner = new KeepBothRunner(context, new Transfers(context));
        var labels = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        var change = await runner.RunAsync(row, labels, CancellationToken.None);
        store.Commit(context.Pair.PairId, change);
        await VerifyCopiesAsync(context, local, dropbox, labels);
        var issues = store.Issues(context.Pair.PairId);
        Check.Require(issues.Count == 1 && issues[0].Kind == IssueKind.Info
            && issues[0].Cause == TextKey.InfoKeptBoth,
            "An unresolvable time tie keeps both copies and raises InfoKeptBoth exactly once.");
    }

    private static async Task CurrentStampLabelsAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "BBBB");
        var saved = SyncedEntries.From(local, dropbox) with
        {
            LocalModifiedTicks = local.Modified.AddDays(-30).UtcTicks
        };
        var row = RowFor(local, dropbox) with { Synced = saved };
        var labels = await new KeepBothRunner(context, new Transfers(context))
            .ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(labels.DropboxLabel == Label(context, dropbox.Path, Side.Local, local.Modified)
            && labels.LocalLabel == Label(context, local.Path, Side.Dropbox, dropbox.Modified),
            "Labels follow the current observation, never the old saved local stamp.");
    }

    private static async Task DownloadHashMismatchAsync()
    {
        var settings = Settings();
        using var pair = new RealPair(settings);
        using var store = new InMemoryStateStore();
        var dropbox = new FakeSide(Side.Dropbox);
        var space = new Space(Utf8("BBBB").LongLength);
        var context = Context(settings: settings, store: store, local: pair.Local, dropbox: dropbox,
            pair: pair.Pair, space: space);
        File.WriteAllText(Path.Combine(pair.Folder, "item.txt"), "AAAA");
        var local = (await pair.Local.ReadOneAsync("item.txt", CancellationToken.None))!;
        var dropboxEntry = Seed(dropbox, "item.txt", "BBBB");
        var row = RowFor(local, dropboxEntry);
        var runner = new KeepBothRunner(context, new Transfers(context));
        var labels = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        // Same length, different bytes: the stage must reject what the source metadata promised.
        dropbox.Seed(dropboxEntry, Encoding.UTF8.GetBytes("GGGG"));
        ItemFailedException? failed = null;
        try
        {
            await runner.RunAsync(row, labels, CancellationToken.None);
        }
        catch (ItemFailedException error)
        {
            failed = error;
        }
        Check.Require(failed is { Cause: TextKey.DownloadHashMismatch } && failed.Path == labels.LocalLabel,
            "A verified download rejects same-length bytes that do not match the source content hash.");
        var localFiles = Directory.GetFiles(pair.Folder, "*", SearchOption.AllDirectories);
        Check.Require(localFiles.Length == 1 && Path.GetFileName(localFiles[0]) == "item.txt"
            && !File.Exists(Path.Combine(pair.Folder, labels.LocalLabel!)),
            "A rejected download leaves no final or staged local bytes.");
        var uploaded = await dropbox.ReadOneAsync(labels.DropboxLabel!, CancellationToken.None);
        Check.Require(uploaded is not null && uploaded.ContentHash == DropboxContentHash.Compute(Utf8("AAAA")),
            "The completed upload leg is preserved, not rolled back, when the download is rejected.");
        Check.Require(store.Issues(context.Pair.PairId).Count == 0 && store.Pending(context.Pair.PairId) is NoPending,
            "A failed keep-both commits no notice, record or pending note itself.");
        Check.Require(space.Requests.Count == 1 && space.Requests[0].Bytes == dropboxEntry.Size,
            "The rejected download reserved its declared source size exactly once.");

        // The failed attempt's grant must be retired: with free space equal to the size, only a released lease admits again.
        dropbox.Seed(dropboxEntry, Utf8("BBBB"));
        labels = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(labels.ReuseDropbox && !labels.ReuseLocal,
            "The preserved upload leg is reused instead of being repeated.");
        store.Commit(context.Pair.PairId, await runner.RunAsync(row, labels, CancellationToken.None));
        Check.Require(space.Requests.Count == 2 && space.Requests[1].Bytes == dropboxEntry.Size,
            "Exactly one reservation per download attempt; the rejected attempt's grant was retired.");
        var downloaded = await pair.Local.ReadOneAsync(labels.LocalLabel!, CancellationToken.None);
        Check.Require(downloaded is not null
            && await pair.Local.ContentHashAsync(downloaded, CancellationToken.None) == dropboxEntry.ContentHash,
            "The retried download publishes the source's exact bytes.");
        Check.Require(store.Issues(context.Pair.PairId).Count == 1
            && store.Issues(context.Pair.PairId)[0].Cause == TextKey.InfoKeptBoth,
            "The successful retry raises InfoKeptBoth once.");
    }

    private static async Task LocalSpaceRefusalAsync()
    {
        var settings = Settings();
        using var pair = new RealPair(settings);
        using var store = new InMemoryStateStore();
        var dropbox = new RecordingSide(new FakeSide(Side.Dropbox));
        var space = new Space(Utf8("BBBB").LongLength - 1);
        var context = Context(settings: settings, store: store, local: pair.Local, dropbox: dropbox,
            pair: pair.Pair, space: space);
        File.WriteAllText(Path.Combine(pair.Folder, "item.txt"), "AAAA");
        var local = (await pair.Local.ReadOneAsync("item.txt", CancellationToken.None))!;
        var dropboxEntry = Seed((FakeSide)dropbox.Inner, "item.txt", "BBBB");
        var row = RowFor(local, dropboxEntry);
        var runner = new KeepBothRunner(context, new Transfers(context));
        var labels = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        var openedBefore = dropbox.Calls.Count(call => call.Operation == nameof(ISide.OpenReadAsync));
        ItemFailedException? refused = null;
        try
        {
            await runner.RunAsync(row, labels, CancellationToken.None);
        }
        catch (ItemFailedException error)
        {
            refused = error;
        }
        Check.Require(refused is { Cause: TextKey.FailLocalNoSpace },
            "A destination without room refuses the download with the local no-space cause.");
        Check.Require(dropbox.Calls.Count(call => call.Operation == nameof(ISide.OpenReadAsync)) == openedBefore
            && dropbox.Calls.Count(call => call.Operation == nameof(ISide.StageAsync)) == 1
            && dropbox.Calls.Count(call => call.Operation == nameof(ISide.PublishAsync)) == 1
            && !File.Exists(Path.Combine(pair.Folder, labels.LocalLabel!))
            && Directory.GetFiles(pair.Folder, "*", SearchOption.AllDirectories).Length == 1,
            "A refused download opens no source, creates no stage and leaves the destination clean.");
        Check.Require(await dropbox.ReadOneAsync(labels.DropboxLabel!, CancellationToken.None) is not null,
            "The upload leg completed before the local refusal and is retained.");

        space.Available = Utf8("BBBB").LongLength;
        labels = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        store.Commit(context.Pair.PairId, await runner.RunAsync(row, labels, CancellationToken.None));
        Check.Require(space.Requests.Count == 2 && space.Requests.All(request => request.Bytes == dropboxEntry.Size),
            "Each attempt reserves once against a fresh observation; the later grant succeeds.");
        Check.Require(store.Issues(context.Pair.PairId).Count == 1,
            "The retried keep-both records its single InfoKeptBoth notice.");
    }

    private static async Task DownloadCancellationAsync()
    {
        var settings = Settings();
        using var pair = new RealPair(settings);
        using var store = new InMemoryStateStore();
        using var cancellation = new CancellationTokenSource();
        var inner = new FakeSide(Side.Dropbox);
        var dropbox = new RecordingSide(inner, duringOpen: () =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });
        var space = new Space(Plenty);
        var context = Context(settings: settings, store: store, local: pair.Local, dropbox: dropbox,
            pair: pair.Pair, space: space);
        File.WriteAllText(Path.Combine(pair.Folder, "item.txt"), "AAAA");
        var local = (await pair.Local.ReadOneAsync("item.txt", CancellationToken.None))!;
        var dropboxEntry = Seed(inner, "item.txt", "BBBB");
        var row = RowFor(local, dropboxEntry);
        var runner = new KeepBothRunner(context, new Transfers(context));
        var labels = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        var cancelled = false;
        try
        {
            await runner.RunAsync(row, labels, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Check.Require(cancelled, "Cancellation during the download propagates instead of becoming an item failure.");
        Check.Require(Directory.GetFiles(pair.Folder, "*", SearchOption.AllDirectories).Length == 1
            && await dropbox.ReadOneAsync(labels.DropboxLabel!, CancellationToken.None) is not null,
            "Cancellation leaves no local stage, keeps the originals and keeps the completed upload leg.");
        Check.Require(store.Issues(context.Pair.PairId).Count == 0
            && store.Pending(context.Pair.PairId) is NoPending,
            "A cancelled keep-both records no notice and no pending note.");
    }

    private static async Task SourceReadFailureAsync()
    {
        var settings = Settings();
        using var pair = new RealPair(settings);
        using var store = new InMemoryStateStore();
        var dropbox = new FakeSide(Side.Dropbox);
        var space = new Space(Plenty);
        var context = Context(settings: settings, store: store, local: pair.Local, dropbox: dropbox,
            pair: pair.Pair, space: space);
        File.WriteAllText(Path.Combine(pair.Folder, "item.txt"), "AAAA");
        var local = (await pair.Local.ReadOneAsync("item.txt", CancellationToken.None))!;
        var dropboxEntry = Seed(dropbox, "item.txt", "BBBB");
        var row = RowFor(local, dropboxEntry);
        var runner = new KeepBothRunner(context, new Transfers(context));
        var labels = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        var failure = new IOException("source unavailable");
        dropbox.FailNext(dropboxEntry.Path, failure);
        Exception? observed = null;
        try
        {
            await runner.RunAsync(row, labels, CancellationToken.None);
        }
        catch (IOException error)
        {
            observed = error;
        }
        Check.Require(ReferenceEquals(observed, failure),
            "A source read failure keeps its own identity instead of becoming a content failure.");
        Check.Require(Directory.GetFiles(pair.Folder, "*", SearchOption.AllDirectories).Length == 1
            && await pair.Local.ReadOneAsync("item.txt", CancellationToken.None) == local
            && await dropbox.ReadOneAsync(dropboxEntry.Path, CancellationToken.None) == dropboxEntry,
            "A failed source read closes the stage, removes partial bytes and preserves both originals.");
        Check.Require(space.Requests.Count == 1 && store.Issues(context.Pair.PairId).Count == 0,
            "The failed source read reserved once and recorded nothing.");
    }

    private static async Task PartialLegAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "BBBB");
        var saved = SyncedEntries.From(local, dropbox) with
        {
            LocalModifiedTicks = local.Modified.AddDays(-1).UtcTicks, DropboxContentHash = "previous-content"
        };
        var row = RowFor(local, dropbox) with { Synced = saved };
        store.Commit(context.Pair.PairId, StateChange.Empty with { Record = [saved] });
        var runner = new KeepBothRunner(context, new Transfers(context));
        var labels = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        var occupant = await PutAsync(context.Local, labels.LocalLabel!, "CCCC");
        var localCalls = new RecordingSide(context.Local);
        var dropboxCalls = new RecordingSide(context.Dropbox);
        var countedContext = context with { Local = localCalls, Dropbox = dropboxCalls };
        runner = new KeepBothRunner(countedContext, new Transfers(countedContext));
        var failed = false;
        try
        {
            await runner.RunAsync(row, labels, CancellationToken.None);
        }
        catch (PathOccupiedException)
        {
            failed = true;
        }
        Check.Require(failed, "The second create fails when its selected label becomes occupied.");
        Check.Require(localCalls.StagedBytes == dropbox.Size && dropboxCalls.StagedBytes == local.Size
            && new[] { localCalls, dropboxCalls }.All(side =>
                side.Calls.Count(call => call.Operation == nameof(ISide.OpenReadAsync)) == 1
                && side.Calls.Count(call => call.Operation == nameof(ISide.StageAsync)) == 1
                && side.Calls.Count(call => call.Operation == nameof(ISide.PublishAsync)) == 1
                && side.Calls.All(call => call.Operation is nameof(ISide.OpenReadAsync)
                    or nameof(ISide.StageAsync) or nameof(ISide.PublishAsync))),
            "Partial failure attempts each leg once with exact bytes and never cleans up the completed copy.");
        var completedCopy = await context.Dropbox.ReadOneAsync(labels.DropboxLabel!, CancellationToken.None);
        Check.Require(completedCopy is not null
            && store.SyncedByKey(context.Pair.PairId, Side.Local, saved.LocalKey) == saved
            && store.Issues(context.Pair.PairId).Count == 0,
            "The first copy survives, but a partial keep-both returns no committed state or notice.");
        await context.Local.DeleteAsync(occupant, null, CancellationToken.None);
        var retry = await runner.ChooseLabelsAsync(row, CancellationToken.None);
        Check.Require(retry.ReuseDropbox && !retry.ReuseLocal && retry.DropboxLabel == labels.DropboxLabel,
            "Retry selects the completed copy instead of making a new label.");
        Check.Require(string.CompareOrdinal(PathNames.DropboxKey(labels.DropboxLabel!), saved.LocalKey) < 0,
            "The partial-repair regression must retain a completed label that sorts before its original.");
    }

    private static async Task CancellationAsync()
    {
        var context = Context();
        using var store = context.Store;
        var local = await PutAsync(context.Local, "item.txt", "AAAA");
        var dropbox = await PutAsync(context.Dropbox, "item.txt", "BBBB");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            await new KeepBothRunner(context, new Transfers(context))
                .ChooseLabelsAsync(RowFor(local, dropbox), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Check.Require(cancelled, "Label selection propagates cancellation.");
    }

    private static ExecutorContext Context(InMemoryStateStore? store = null, EngineSettings? settings = null,
        ISide? local = null, ISide? dropbox = null, PairConfig? pair = null, FakeAdmission? admission = null,
        Space? space = null)
    {
        store ??= new InMemoryStateStore();
        settings ??= Settings();
        pair ??= new PairConfig("pair", "Pair A", Path.GetTempPath(), "/remote", "account", [], [],
            FirstRunPolicy.NewestWins, true, null);
        return new ExecutorContext(
            Local: local ?? new FakeSide(Side.Local), Dropbox: dropbox ?? new FakeSide(Side.Dropbox),
            Recovery: new FakeRecovery(), Store: store, Pair: pair, Scope: new PathScope(pair, [pair]),
            Settings: settings, Runtime: new ReplaceTurn(), Admission: admission ?? new FakeAdmission(),
            Priority: false, Events: _ => { }, ReserveDownloadAsync: (space ?? new Space(Plenty)).ReserveAsync,
            Time: TimeProvider.System,
            ReadDeletionEvidenceAsync: (_, _, _) => throw new NotSupportedException(),
            BeginDownloadAttempt: source => new RequestAttempt(source, AttemptOwner.Transfer),
            CompleteDownloadAttemptAsync: (_, _) => Task.FromResult(false),
            UploadAdmissionIdentity: (AccountId: "account", RouteName: "route", SourceIdentity: "source"),
            ItemAttempts: new UnusedItemAttempts(), Opportunity: new object(), ExpectedGeneration: 1);
    }

    private static EngineSettings Settings()
    {
        using var stream = typeof(EngineSettings).Assembly.GetManifestResourceStream(ResourceNames.Settings)!;
        using var reader = new StreamReader(stream);
        return SettingsParser.Parse(reader.ReadToEnd(), null);
    }

    private static Row RowFor(Entry local, Entry dropbox) => new(PathNames.Key(local.Path), local, dropbox,
        null, [], null, new RowContext(false, false, false, null, false, false, false, false));

    private static string Label(ExecutorContext context, string path, Side source, DateTimeOffset modified,
        int attempt = 1)
    {
        var rule = context.Settings.ConflictLabel;
        return KeepBothLabel.For(path, source == Side.Local ? rule.NameA : rule.NameB, modified, rule, attempt);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<Entry> PutAsync(ISide side, string path, string text)
    {
        var bytes = Utf8(text);
        var source = new Entry(path, false, bytes.LongLength, DateTimeOffset.UnixEpoch, "", "", "", null);
        using var content = new MemoryStream(bytes);
        await using var staged = await side.StageAsync(path, content, source, CancellationToken.None);
        return (await side.PublishAsync(staged, null, null, CancellationToken.None)).Final!;
    }

    private static Entry Seed(FakeSide side, string path, string text)
    {
        var bytes = Utf8(text);
        var entry = new Entry(path, false, bytes.LongLength, DateTimeOffset.UnixEpoch, "id", "rev",
            DropboxContentHash.Compute(bytes), null);
        side.Seed(entry, bytes);
        return entry;
    }

    private static async Task<Entry> CopyAsync(ISide source, Entry entry, ISide target, string path)
    {
        await using var content = await source.OpenReadAsync(entry, null, CancellationToken.None);
        await using var staged = await target.StageAsync(path, content, entry, CancellationToken.None);
        return (await target.PublishAsync(staged, null, null, CancellationToken.None)).Final!;
    }

    private static async Task VerifyCopiesAsync(ExecutorContext context, Entry local, Entry dropbox,
        KeepBothLabels labels)
    {
        Check.Require(await context.Local.ReadOneAsync(local.Path, CancellationToken.None) == local
            && await context.Dropbox.ReadOneAsync(dropbox.Path, CancellationToken.None) == dropbox,
            "Both originals remain at their original names and identities.");
        var copiedLocal = await context.Local.ReadOneAsync(labels.LocalLabel!, CancellationToken.None);
        var copiedDropbox = await context.Dropbox.ReadOneAsync(labels.DropboxLabel!, CancellationToken.None);
        Check.Require(copiedLocal is not null && copiedDropbox is not null, "Both copies exist.");
        Check.Require(await context.Local.ContentHashAsync(copiedLocal!, CancellationToken.None) == dropbox.ContentHash
            && copiedDropbox!.ContentHash == await context.Local.ContentHashAsync(local, CancellationToken.None),
            "Each side holds the other original's exact contents.");
    }

    private const long Plenty = 1024 * 1024;

    /// <summary>The real local-space owner over one synthetic volume, recording every admission request.</summary>
    private sealed class Space
    {
        private const uint SyntheticSerial = 1;
        private readonly DownloadSpace space;

        public Space(long available)
        {
            Available = available;
            space = new DownloadSpace(_ => SyntheticSerial, _ => Available);
        }

        public long Available { get; set; }
        public List<(string Target, long Bytes)> Requests { get; } = [];

        public ValueTask<IAsyncDisposable> ReserveAsync(string localRoot, string target, long bytes,
            CancellationToken ct)
        {
            Requests.Add((target, bytes));
            return space.ReserveAsync(localRoot, target, bytes, ct);
        }
    }

    /// <summary>One pair rooted in a fresh temporary folder, so downloads run the production LocalSide.</summary>
    private sealed class RealPair : IDisposable
    {
        public RealPair(EngineSettings settings)
        {
            Folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(Folder);
            Pair = new PairConfig("pair", "Pair A", Folder, "/remote", "account", [], [],
                FirstRunPolicy.NewestWins, true, null);
            var scope = new PathScope(Pair, [Pair]);
            Local = new LocalSide(Pair, scope, settings,
                new PendingNotes(new NoPendingStore(), Pair.PairId, settings.StoragePageSize, mutation: true),
                Path.Combine(Folder, "stages"), (_, _, _, _) => { });
        }

        public string Folder { get; }
        public PairConfig Pair { get; }
        public LocalSide Local { get; }

        public void Dispose()
        {
            try { Directory.Delete(Folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class UnusedItemAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) => throw new NotSupportedException();

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode, Action<Issue, bool>? settledIssue = null, Action<Issue>? committedIssue = null) => throw new NotSupportedException();

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingSide(ISide inner, Func<Task>? duringUpload = null, Func<Task>? duringOpen = null)
        : ISide
    {
        public List<(string Operation, string Path)> Calls { get; } = [];
        public long StagedBytes { get; private set; }
        public ISide Inner => inner;
        public Side Side => inner.Side;
        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct)
        {
            Calls.Add((nameof(ISide.ReadAllAsync), under));
            return inner.ReadAllAsync(under, options, ct);
        }
        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
        {
            Calls.Add((nameof(ISide.ReadOneAsync), path));
            return inner.ReadOneAsync(path, ct);
        }
        public async Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct)
        {
            Calls.Add((nameof(ISide.OpenReadAsync), entry.Path));
            if (duringOpen is not null) await duringOpen();
            return await inner.OpenReadAsync(entry, attempt, ct);
        }
        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
        {
            Calls.Add((nameof(ISide.ContentHashAsync), entry.Path));
            return inner.ContentHashAsync(entry, ct);
        }
        public async Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
        {
            Calls.Add((nameof(ISide.StageAsync), path));
            if (duringUpload is not null) await duringUpload();
            // Position delta is exact: Transfers forwards Position to FakeSide's seekable MemoryStream.
            var start = content.Position;
            var staged = await inner.StageAsync(path, content, source, ct);
            StagedBytes += content.Position - start;
            return staged;
        }
        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct)
        {
            Calls.Add((nameof(ISide.PublishAsync), staged.Path));
            return inner.PublishAsync(staged, expected, selection, ct);
        }
        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
        {
            Calls.Add((nameof(ISide.EnsureFolderAsync), path));
            return inner.EnsureFolderAsync(path, ct);
        }
        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
        {
            Calls.Add((nameof(ISide.DeleteAsync), expected.Path));
            return inner.DeleteAsync(expected, selection, ct);
        }
        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
        {
            Calls.Add((nameof(ISide.MoveAsync), newPath));
            return inner.MoveAsync(expected, newPath, ct);
        }
        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct)
        {
            Calls.Add((nameof(ISide.SubmitFolderRemovalAsync), submission.SubmissionId));
            return inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        }
        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct)
        {
            Calls.Add((nameof(ISide.ReadFolderRemovalAsync), jobId));
            return inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
        }
    }
}
