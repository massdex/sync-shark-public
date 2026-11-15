using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

// L99 probe: a replace-by-rename (write Item.tmp, rename it onto Item) over two saved bindings, run through the
// ordinary AutoSync Syncer on the real store with the store's own live-coverage admission. Each check asserts the
// correct end state: within a fixed number of sync calls Item holds the latest content under one honest binding (the
// tmp binding settled) or an actionable notice names the paths; SelectionDestinationOccupied never repeats unbounded,
// no binding is rewritten without matching both sides, and the side that made the change keeps its content.
public static partial class ChangeRunnerChecks
{
    private const string Replaced = "Parent/Item";
    private const string ReplacedTemp = Replaced + ".tmp";
    private const string SecondTempId = "local-temp-2";
    // One call to act, one retry, and one that must show the refusal no longer repeats.
    private const int ReplacePasses = 3;
    private const byte OldContent = 1;
    private const byte TempContent = 5;
    private const byte SecondContent = 6;

    private sealed record ReplaceExpected(string Path, string? Id, byte Content);

    public static void ReplaceByRenameSingle()
    {
        // The NTFS replace also journals the overwritten identity's deletion; without it, only the Renamed work.
        foreach (var overwriteDelete in new[] { false, true })
            ReplaceByRenameLocalAsync("single" + (overwriteDelete ? "+delete" : ""), overwriteDelete, null)
                .GetAwaiter().GetResult();
    }

    public static void ReplaceByRenameTwice() =>
        ReplaceByRenameLocalAsync("twice", true, "twice").GetAwaiter().GetResult();

    public static void ReplaceByRenameRecreatedTemp() =>
        ReplaceByRenameLocalAsync("recreated", true, "recreated").GetAwaiter().GetResult();

    public static void ReplaceByRenameDropbox() => ReplaceByRenameDropboxAsync().GetAwaiter().GetResult();

    private static async Task ReplaceByRenameLocalAsync(string variant, bool overwriteDelete, string? then)
    {
        using var fixture = new Fixture(realStore: true);
        SeedFolders(fixture, "Parent");
        var item = LocalEntry(Replaced, "local-item");
        var temp = LocalEntry(ReplacedTemp, "local-temp");
        var remoteItem = RemoteEntry(Replaced, "remote-item");
        var remoteTemp = RemoteEntry(ReplacedTemp, "remote-temp") with
            { ContentHash = DropboxContentHash.Compute(new[] { TempContent }) };
        fixture.Dropbox.Seed(remoteItem, [OldContent]);
        fixture.Dropbox.Seed(remoteTemp, [TempContent]);
        var saved = new Dictionary<string, SyncedEntry>(StringComparer.Ordinal)
        {
            [Replaced] = SyncedEntries.From(item, remoteItem),
            [ReplacedTemp] = SyncedEntries.From(temp, remoteTemp)
        };
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [.. saved.Values] });
        // The saved tmp identity now sits at Item; the saved Item file it replaced is gone.
        var renamed = temp with { Path = Replaced };
        fixture.Local.Seed(renamed, [TempContent]);
        var page = 1L;
        var notices = new List<ChangeNotice>();
        if (overwriteDelete) notices.Add(Deleting(Replaced, item.Id));
        notices.Add(new ChangeNotice(Side.Local, ChangeKind.Renamed, Replaced, ReplacedTemp, temp.Id, renamed,
            false, false));
        var expected = new[]
        {
            new ReplaceExpected(Replaced, temp.Id, TempContent), new ReplaceExpected(ReplacedTemp, null, 0)
        };
        var second = LocalEntry(ReplacedTemp, SecondTempId) with { Modified = Stamp.AddMinutes(1) };
        if (then is not null)
        {
            // A second write creates a fresh tmp identity before the first replacement is processed.
            notices.Add(new ChangeNotice(Side.Local, ChangeKind.Changed, ReplacedTemp, null, second.Id, second,
                true, false));
            if (then == "recreated")
            {
                fixture.Local.Seed(second, [SecondContent]);
                expected = [new ReplaceExpected(Replaced, temp.Id, TempContent),
                    new ReplaceExpected(ReplacedTemp, second.Id, SecondContent)];
            }
            else
            {
                // The fresh tmp replaces Item again; an unsaved identity's rename journals as a change at its new path.
                var final = second with { Path = Replaced };
                fixture.Local.Seed(final, [SecondContent]);
                notices.Add(Deleting(Replaced, temp.Id));
                notices.Add(new ChangeNotice(Side.Local, ChangeKind.Changed, Replaced, null, second.Id, final,
                    false, false));
                expected = [new ReplaceExpected(Replaced, second.Id, SecondContent),
                    new ReplaceExpected(ReplacedTemp, null, 0)];
            }
        }
        WorkSelection? first = null;
        foreach (var notice in notices)
        {
            var seeded = Seed(fixture, notice, page++);
            first ??= seeded;
        }
        RepairDone(fixture);
        ClearObligations(fixture);
        fixture.LocalView = new HookedSide(fixture.Local);
        await ReplacePassesAsync(variant, fixture, first!, Side.Local, saved, expected);
    }

    private static async Task ReplaceByRenameDropboxAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        SeedFolders(fixture, "Parent");
        var item = LocalEntry(Replaced, "local-item");
        var temp = LocalEntry(ReplacedTemp, "local-temp");
        var remoteItem = RemoteEntry(Replaced, "remote-item");
        var remoteTemp = RemoteEntry(ReplacedTemp, "remote-temp") with
            { ContentHash = DropboxContentHash.Compute(new[] { TempContent }) };
        fixture.Local.Seed(item, [OldContent]);
        fixture.Local.Seed(temp, [TempContent]);
        var saved = new Dictionary<string, SyncedEntry>(StringComparer.Ordinal)
        {
            [Replaced] = SyncedEntries.From(item, remoteItem),
            [ReplacedTemp] = SyncedEntries.From(temp, remoteTemp)
        };
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [.. saved.Values] });
        // Dropbox moved the saved tmp identity onto Item; production feed forms: the tombstone has no id, the
        // metadata no old path.
        var moved = remoteTemp with { Path = Replaced };
        fixture.Dropbox.Seed(moved, [TempContent]);
        var tombstone = new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, ReplacedTemp, null, null, null, false, false);
        var (work, epoch, domain) = AcquireRemotePage(fixture, tombstone,
            new ChangeNotice(Side.Dropbox, ChangeKind.Changed, Replaced, null, moved.Id, moved, false, false));
        var first = new WorkSelection(PairId, Generation, epoch, Side.Dropbox, domain, null,
            PathNames.KeyFor(Side.Dropbox, ReplacedTemp), work - 1, work - 1);
        RepairDone(fixture);
        await ReplacePassesAsync("dropbox", fixture, first, Side.Dropbox, saved,
            [new ReplaceExpected(Replaced, moved.Id, TempContent), new ReplaceExpected(ReplacedTemp, null, 0)]);
    }

    private static async Task ReplacePassesAsync(string variant, Fixture fixture, WorkSelection first, Side source,
        IReadOnlyDictionary<string, SyncedEntry> saved, IReadOnlyList<ReplaceExpected> expected)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Diagnosis only: which admitted selection each boundary refusal belongs to.
        var admitted = new Dictionary<Exception, WorkSelection>(ReferenceEqualityComparer.Instance);
        Boundary? boundary = null;
        boundary = new Boundary(request =>
        {
            try { fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint); }
            catch (LiveAdmissionStaleException error)
            {
                admitted[error] = request.Selection;
                throw;
            }
            return new Lease { AllowsRelease = true };
        })
        {
            // A refusal inside the admitted execution passes the boundary's own catch.
            Refused = error => admitted[error] = boundary!.Requests[^1].Single().Selection
        };
        var occupied = new int[ReplacePasses];
        var refusals = new int[ReplacePasses];
        for (var pass = 0; pass < ReplacePasses; pass++)
        {
            var events = new List<RunEvent>();
            var syncer = LiveSyncer(fixture, first, boundary, events);
            boundary.Capture = (chosen, _, _) => Task.FromResult(fixture.Inner.CaptureLiveCoverage(chosen,
                Coverage(chosen).Local, Coverage(chosen).Dropbox));
            await syncer.SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
            refusals[pass] = events.Count(item => item.Error is LiveAdmissionStaleException);
            occupied[pass] = events.Count(item => item.Error is LiveAdmissionStaleException
                { Reason: LiveAdmissionStaleReason.SelectionDestinationOccupied });
            foreach (var refusal in events.Where(item => item.Error is LiveAdmissionStaleException))
                Console.WriteLine(RefusalLine(variant, pass + 1, refusal,
                    admitted.GetValueOrDefault(refusal.Error!)));
            Console.WriteLine($"L99-PROBE variant={variant} pass={pass + 1} occupied={occupied[pass]} " +
                $"refusals={refusals[pass]} effects={string.Join(",", events.Where(item => item.EffectKind is not null)
                    .Select(item => item.EffectKind + ":" + item.Path))}");
        }
        var paths = expected.Select(item => item.Path).ToArray();
        var settled = !PendingAt(fixture, Side.Local, paths).Any() && !PendingAt(fixture, Side.Dropbox, paths).Any();
        var honest = true;
        var kept = true;
        var states = new List<string>();
        foreach (var want in expected)
        {
            var local = await fixture.Local.ReadOneAsync(want.Path, default);
            var remote = await fixture.Dropbox.ReadOneAsync(want.Path, default);
            var binding = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(want.Path));
            var localByte = await ByteOn(fixture.Local, local);
            var remoteByte = await ByteOn(fixture.Dropbox, remote);
            settled &= want.Id is null
                ? local is null && remote is null && binding is null
                : localByte == want.Content && remoteByte == want.Content && binding is { } bound &&
                  bound.IdOn(source) == want.Id && bound.LocalId == local!.Id && bound.DropboxId == remote!.Id;
            // A binding is either the untouched saved row or matches both sides at its path.
            honest &= binding is null || saved.TryGetValue(want.Path, out var original) && binding == original ||
                binding.LocalId == local?.Id && binding.DropboxId == remote?.Id;
            // The changing side keeps its latest content; the other side keeps a file at a path that stays present.
            var changing = source == Side.Local ? localByte : remoteByte;
            var other = source == Side.Local ? remote : local;
            kept &= want.Id is null || changing == want.Content && other is not null;
            states.Add($"{want.Path}: local={local?.Id}:{localByte} dropbox={remote?.Id}:{remoteByte} " +
                $"binding={binding?.LocalId}/{binding?.DropboxId}");
        }
        var issues = fixture.Inner.Issues(PairId);
        var noticed = issues.Any(issue => paths.Any(path => PathNames.Same(issue.Path, path)));
        Check.Require((settled || noticed) && honest && kept && occupied[^1] == 0,
            $"L99 {variant}: a replace-by-rename settles or raises a notice within {ReplacePasses} sync calls, " +
            "without repeating SelectionDestinationOccupied, rewriting a binding blindly or losing content; " +
            $"occupied per pass=[{string.Join(",", occupied)}]; refusals per pass=[{string.Join(",", refusals)}]; " +
            $"settled={settled}; noticed={noticed}; honest={honest}; kept={kept}; {string.Join("; ", states)}; " +
            $"issues={string.Join(",", issues.Select(issue => issue.Path + ":" + issue.Cause))}; " +
            $"pending={PendingAt(fixture, Side.Local, paths).Count}/{PendingAt(fixture, Side.Dropbox, paths).Count}");
    }

    // One refusal: exception, reason and detail, the reported row, the admitted selection (when it passed the
    // boundary) and the innermost frames with file:line, so CI names the guard that refused.
    private static string RefusalLine(string variant, int pass, RunEvent refusal, WorkSelection? selection)
    {
        var error = (LiveAdmissionStaleException)refusal.Error!;
        var frames = new System.Diagnostics.StackTrace(error, true).GetFrames()
            .Take(4).Select(frame => frame.GetMethod()?.DeclaringType?.Name + "." + frame.GetMethod()?.Name + "@" +
                Path.GetFileName(frame.GetFileName()) + ":" + frame.GetFileLineNumber());
        return $"L99-REFUSAL variant={variant} pass={pass} type={error.GetType().Name} reason={error.Reason} " +
            $"detail={error.Detail} waitKey={error.WaitKey} holder={error.Holder?.Path}:{error.Holder?.Cause} " +
            $"superseded={error.Superseded?.LaterWorkId} newerEpisode={error.NewerEpisode is not null} " +
            $"cause={refusal.Cause} row={refusal.Side}:{refusal.Path}:{refusal.WorkId} " +
            $"selection={(selection is null ? "not-admitted" : selection.Side + ":" + selection.Key + ":" +
                (selection.Identity ?? "-") + ":" + selection.AnchorWorkId + "-" + selection.ThroughWorkId)} " +
            $"stack={string.Join(" < ", frames)}";
    }

    // The one byte the fixtures write, read from the inner fake so it is outside any counted provider read.
    private static async Task<int?> ByteOn(FakeSide side, Entry? entry)
    {
        if (entry is null) return null;
        var bytes = new MemoryStream();
        await using (var stream = await side.OpenReadAsync(entry, null, default))
            await stream.CopyToAsync(bytes);
        return bytes.Length == 1 ? bytes.ToArray()[0] : -(int)bytes.Length;
    }
}
