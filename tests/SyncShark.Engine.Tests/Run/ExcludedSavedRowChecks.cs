using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.Engine.Tests.Executor;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Checks excluded saved-only rows stay out of sync scope. Pending exclusions retain failures;
/// applied, Keep and ignored resolutions retire them (`--engine-checks repair`). The prefixed-file cases
/// cover the P3 8.5 saved-row matrix: a FileNamePrefix rule never makes an ignored file a transfer, delete,
/// hash or read target, and its counterpart and baseline survive.</summary>
public static class ExcludedSavedRowChecks
{
    // Matches the shipped engine-settings.json default; a hand-built EngineSettings must supply it explicitly.
    private const int LogBufferCharacters = 262144;

    private static readonly EngineSettings Settings = new("{name} ({side} {time}){ext}", "yyyyMMddHHmmss",
        "Local", "Dropbox", ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", LogBufferCharacters, ScanTestSettings.BatchRows);

    // Registered separately so the runner reports each verdict even when an earlier one fails.
    public static void KeptEdit() => KeptLocalEditIsNotUploadedAsync().GetAwaiter().GetResult();
    public static void DeletedCopy() => DeletedExcludedCopyKeepsCounterpartAsync().GetAwaiter().GetResult();
    public static void UnchangedReads() => UnchangedExcludedFilesAreNotReadAsync().GetAwaiter().GetResult();
    public static void BlockedNotice() => BlockedExclusionHistoryAsync().GetAwaiter().GetResult();
    public static void PrefixFirstRun() => PrefixFirstRunSkipsIgnoredAsync().GetAwaiter().GetResult();
    public static void PrefixUnchanged() => PrefixUnchangedNotReadAsync().GetAwaiter().GetResult();
    public static void PrefixChangedNeighbor() => PrefixChangedUploadsOnlyOrdinaryAsync().GetAwaiter().GetResult();
    public static void PrefixSavedOnly() => PrefixSavedOnlyKeepsCounterpartAsync().GetAwaiter().GetResult();
    public static void PrefixBothAbsent() => PrefixBothAbsentRetainsHistoryAsync().GetAwaiter().GetResult();
    public static void PrefixExactName() => PrefixComparedToExactName();
    public static void PrefixPerPair() => PrefixPerPairRules();

    // (a) A local edit under a local keep exclusion must not be uploaded.
    private static Task KeptLocalEditIsNotUploadedAsync() => WithFolder("keep-edit", async folder =>
    {
        using var rig = NewRig(folder, Pair(exclusions: [new Exclusion(Side.Local, "keep", true)]));
        await rig.Local.EnsureFolderAsync("keep", default);
        await rig.Dropbox.EnsureFolderAsync("keep", default);
        var local = await PutAsync(rig.Local, "keep/a.txt", [1]);
        var dropbox = await PutAsync(rig.Dropbox, "keep/a.txt", [1]);
        RecordLinked(rig, local, dropbox);
        await PutAsync(rig.Local, "keep/a.txt", [2, 3], local);

        var localSide = new ScopedSide(rig.Local, rig.Scope);
        var dropboxSide = new ScopedSide(rig.Dropbox, rig.Scope);
        using var log = Log(rig, folder, "run-1");
        await ScanAsync(rig, log, localSide, dropboxSide);

        var calls = dropboxSide.Calls.ToArray();
        var writes = dropboxSide.Mutations.ToArray();
        var kept = await ReadBytesAsync(rig.Dropbox, "keep/a.txt");
        Check.Require(!calls.Contains("keep/a.txt") && kept.SequenceEqual(new byte[] { 1 }),
            "an edit inside a local keep exclusion makes no Dropbox call for keep/a.txt (calls: " +
            string.Join(",", calls) + "; writes: " + string.Join(",", writes) + "; Dropbox content now " +
            kept.Length + " bytes: " + string.Join(",", kept) + ")");
    });

    // (b) Deleting the local copy under a local keep exclusion must not delete its Dropbox counterpart.
    private static Task DeletedExcludedCopyKeepsCounterpartAsync() => WithFolder("keep-delete", async folder =>
    {
        using var rig = NewRig(folder, Pair(exclusions: [new Exclusion(Side.Local, "keep", true)]));
        await rig.Local.EnsureFolderAsync("keep", default);
        await rig.Dropbox.EnsureFolderAsync("keep", default);
        var local = await PutAsync(rig.Local, "keep/a.txt", [1]);
        var dropbox = await PutAsync(rig.Dropbox, "keep/a.txt", [1]);
        RecordLinked(rig, local, dropbox);
        await rig.Local.DeleteAsync(local, null, CancellationToken.None);

        var localSide = new ScopedSide(rig.Local, rig.Scope);
        var dropboxSide = new ScopedSide(rig.Dropbox, rig.Scope);
        using var log = Log(rig, folder, "run-1");
        await ScanAsync(rig, log, localSide, dropboxSide);

        Check.Require(await rig.Dropbox.ReadOneAsync("keep/a.txt", CancellationToken.None) is not null,
            "the kept Dropbox counterpart survives a deleted local copy inside a keep exclusion");
        Check.Require(!dropboxSide.Mutations.Contains("keep/a.txt"),
            "deleting the local copy inside a keep exclusion makes no Dropbox write for keep/a.txt (observed: " +
            string.Join(",", dropboxSide.Mutations) + ")");
    });

    // (c) An unchanged Repair must make no per-item read inside an exclusion.
    private static Task UnchangedExcludedFilesAreNotReadAsync() => WithFolder("keep-reads", async folder =>
    {
        using var rig = NewRig(folder, Pair(exclusions: [new Exclusion(Side.Local, "keep", true)]));
        await rig.Local.EnsureFolderAsync("keep", default);
        await rig.Dropbox.EnsureFolderAsync("keep", default);
        for (var index = 0; index < 3; index++)
        {
            var local = await PutAsync(rig.Local, "keep/f" + index + ".txt", [1]);
            var dropbox = await PutAsync(rig.Dropbox, "keep/f" + index + ".txt", [1]);
            RecordLinked(rig, local, dropbox);
        }

        var localSide = new ScopedSide(rig.Local, rig.Scope);
        var dropboxSide = new ScopedSide(rig.Dropbox, rig.Scope);
        using var log = Log(rig, folder, "run-1");
        await ScanAsync(rig, log, localSide, dropboxSide);

        var reads = localSide.Calls.Concat(dropboxSide.Calls)
            .Count(path => path.StartsWith("keep/", StringComparison.Ordinal));
        Check.Require(reads == 0,
            "an unchanged Repair makes no per-item read inside an exclusion (observed " + reads + ")");
    });

    // (P3 8.5) First run: a FileNamePrefix match is out of scope and never becomes a transfer, read or delete.
    private static Task PrefixFirstRunSkipsIgnoredAsync() => WithFolder("prefix-first-run", async folder =>
    {
        using var rig = NewRig(folder, Pair(ignoreRules: ["FileNamePrefix:~$"]));
        await PutAsync(rig.Local, "~$draft.docx", [1]);

        var localSide = new ScopedSide(rig.Local, rig.Scope);
        var dropboxSide = new ScopedSide(rig.Dropbox, rig.Scope);
        using var log = Log(rig, folder, "run-1");
        await ScanAsync(rig, log, localSide, dropboxSide);

        Check.Require(!localSide.Calls.Contains("~$draft.docx") && !dropboxSide.Calls.Contains("~$draft.docx"),
            "first run: a prefixed file is never opened, hashed or read (calls: " +
            string.Join(",", localSide.Calls.Concat(dropboxSide.Calls)) + ")");
        Check.Require(!dropboxSide.Mutations.Contains("~$draft.docx"),
            "first run: a prefixed file is not uploaded (writes: " + string.Join(",", dropboxSide.Mutations) + ")");
        Check.Require(await rig.Dropbox.ReadOneAsync("~$draft.docx", CancellationToken.None) is null,
            "first run: the prefixed file is not copied to Dropbox");
    });

    // (P3 8.5) Unchanged run: a saved prefixed row is out of scope, so Repair makes no per-item read.
    private static Task PrefixUnchangedNotReadAsync() => WithFolder("prefix-unchanged", async folder =>
    {
        using var rig = NewRig(folder, Pair(ignoreRules: ["FileNamePrefix:~$"]));
        var local = await PutAsync(rig.Local, "~$notes.tmp", [1]);
        var dropbox = await PutAsync(rig.Dropbox, "~$notes.tmp", [1]);
        RecordLinked(rig, local, dropbox);

        var localSide = new ScopedSide(rig.Local, rig.Scope);
        var dropboxSide = new ScopedSide(rig.Dropbox, rig.Scope);
        using var log = Log(rig, folder, "run-1");
        await ScanAsync(rig, log, localSide, dropboxSide);

        Check.Require(!localSide.Calls.Contains("~$notes.tmp") && !dropboxSide.Calls.Contains("~$notes.tmp"),
            "unchanged run: a prefixed file is never opened, hashed or read (calls: " +
            string.Join(",", localSide.Calls.Concat(dropboxSide.Calls)) + ")");
        Check.Require(localSide.Mutations.IsEmpty && dropboxSide.Mutations.IsEmpty,
            "unchanged run: a prefixed file causes no provider write");
    });

    // (P3 8.5) Changed run: the prefixed edit is skipped while its ordinary neighbour is uploaded.
    private static Task PrefixChangedUploadsOnlyOrdinaryAsync() => WithFolder("prefix-changed", async folder =>
    {
        using var rig = NewRig(folder, Pair(ignoreRules: ["FileNamePrefix:~$"]));
        var ordinaryLocal = await PutAsync(rig.Local, "note.txt", [1]);
        var ordinaryDropbox = await PutAsync(rig.Dropbox, "note.txt", [1]);
        var prefixedLocal = await PutAsync(rig.Local, "~$note.docx", [1]);
        var prefixedDropbox = await PutAsync(rig.Dropbox, "~$note.docx", [1]);
        RecordLinked(rig, ordinaryLocal, ordinaryDropbox);
        RecordLinked(rig, prefixedLocal, prefixedDropbox);
        using (var first = Log(rig, folder, "run-1"))
            await ScanAsync(rig, first, new ScopedSide(rig.Local, rig.Scope),
                new ScopedSide(rig.Dropbox, rig.Scope));
        await PutAsync(rig.Local, "note.txt", [2, 3], ordinaryLocal);
        await PutAsync(rig.Local, "~$note.docx", [2, 3], prefixedLocal);

        var localSide = new ScopedSide(rig.Local, rig.Scope);
        var dropboxSide = new ScopedSide(rig.Dropbox, rig.Scope);
        using var log = Log(rig, folder, "run-2");
        await ScanAsync(rig, log, localSide, dropboxSide);

        Check.Require(dropboxSide.Mutations.Contains("note.txt"),
            "changed run: an ordinary neighbour is still uploaded (writes: " +
            string.Join(",", dropboxSide.Mutations) + ")");
        Check.Require(!dropboxSide.Mutations.Contains("~$note.docx") && !dropboxSide.Calls.Contains("~$note.docx"),
            "changed run: the prefixed edit is not uploaded or read (writes: " +
            string.Join(",", dropboxSide.Mutations) + ")");
        Check.Require((await ReadBytesAsync(rig.Dropbox, "~$note.docx")).SequenceEqual(new byte[] { 1 }),
            "changed run: the prefixed Dropbox counterpart keeps its original content");
    });

    // (P3 8.5) Saved-only: with one prefixed side missing, the counterpart and baseline are preserved.
    private static Task PrefixSavedOnlyKeepsCounterpartAsync() => WithFolder("prefix-saved-only", async folder =>
    {
        using var rig = NewRig(folder, Pair(ignoreRules: ["FileNamePrefix:~$"]));
        var local = await PutAsync(rig.Local, "~$a.txt", [1]);
        var dropbox = await PutAsync(rig.Dropbox, "~$a.txt", [1]);
        RecordLinked(rig, local, dropbox);
        await rig.Dropbox.DeleteAsync(dropbox, null, CancellationToken.None);

        var localSide = new ScopedSide(rig.Local, rig.Scope);
        var dropboxSide = new ScopedSide(rig.Dropbox, rig.Scope);
        using var log = Log(rig, folder, "run-1");
        await ScanAsync(rig, log, localSide, dropboxSide);

        Check.Require((await ReadBytesAsync(rig.Local, "~$a.txt")).SequenceEqual(new byte[] { 1 }),
            "saved-only: the surviving local prefixed copy is not deleted");
        Check.Require(dropboxSide.Mutations.IsEmpty && localSide.Mutations.IsEmpty,
            "saved-only: a missing prefixed side is not evidence of deletion (writes: " +
            string.Join(",", dropboxSide.Mutations.Concat(localSide.Mutations)) + ")");
        Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("~$a.txt")) is not null,
            "saved-only: the prefixed baseline is retained");
    });

    // (P3 8.5) Both absence: a fully absent prefixed row keeps its saved history and triggers no effect.
    private static Task PrefixBothAbsentRetainsHistoryAsync() => WithFolder("prefix-both-absent", async folder =>
    {
        using var rig = NewRig(folder, Pair(ignoreRules: ["FileNamePrefix:~$"]));
        var local = await PutAsync(rig.Local, "~$gone.txt", [1]);
        var dropbox = await PutAsync(rig.Dropbox, "~$gone.txt", [1]);
        RecordLinked(rig, local, dropbox);
        await rig.Local.DeleteAsync(local, null, CancellationToken.None);
        await rig.Dropbox.DeleteAsync(dropbox, null, CancellationToken.None);

        var localSide = new ScopedSide(rig.Local, rig.Scope);
        var dropboxSide = new ScopedSide(rig.Dropbox, rig.Scope);
        using var log = Log(rig, folder, "run-1");
        var summary = await ScanAsync(rig, log, localSide, dropboxSide);

        Check.Require(!summary.Refused, "both absent: the Repair completes");
        Check.Require(localSide.Mutations.IsEmpty && dropboxSide.Mutations.IsEmpty,
            "both absent: absence of a prefixed file causes no transfer or delete");
        Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("~$gone.txt")) is not null,
            "both absent: the prefixed baseline history is retained");
    });

    // (P3 8.5) A prefix matches longer names, while an exact FileName rule matches only that name.
    private static void PrefixComparedToExactName()
    {
        var exact = new PathScope(Pair(ignoreRules: ["FileName:~$"]), []);
        Check.Require(exact.Excludes("~$", null, false) && !exact.Excludes("~$Report.docx", null, false),
            "FileName:~$ matches only the exact name, not a longer prefixed name");

        var prefix = new PathScope(Pair(ignoreRules: ["FileNamePrefix:~$"]), []);
        Check.Require(prefix.Excludes("~$", null, false) && prefix.Excludes("~$Report.docx", null, false),
            "FileNamePrefix:~$ matches both the exact name and longer prefixed names");
        Check.Require(!prefix.Excludes("Report.docx", null, false) && !prefix.Excludes("~$Folder", null, true),
            "a prefixed rule leaves ordinary files and same-named directories eligible");
    }

    // (P3 8.5) Two pairs with different extra rules each ignore only their own prefix.
    private static void PrefixPerPairRules()
    {
        var tilde = Pair("pair-a", ignoreRules: ["FileNamePrefix:~$"]);
        var dot = Pair("pair-b", ignoreRules: ["FileNamePrefix:.~"]);
        var tildeScope = new PathScope(tilde, [tilde]);
        var dotScope = new PathScope(dot, [dot]);

        Check.Require(tildeScope.Excludes("~$a.tmp", null, false) && !tildeScope.Excludes(".~b.tmp", null, false),
            "pair-a ignores its tilde prefix and leaves the dot prefix eligible");
        Check.Require(dotScope.Excludes(".~b.tmp", null, false) && !dotScope.Excludes("~$a.tmp", null, false),
            "pair-b ignores its dot prefix and leaves the tilde prefix eligible");
    }

    private static async Task BlockedExclusionHistoryAsync()
    {
        foreach (var resolution in new[] { "applied", "kept", "ignored" })
            await ExclusionHistoryAsync(resolution);
    }

    // Folder captures must exercise the durable planner, not a second implementation in the test double.
    private static Task ExclusionHistoryAsync(string resolution) => WithFolder("blocked-" + resolution, async folder =>
    {
        using var rig = NewRig(folder, Pair(exclusions: [new Exclusion(Side.Local, "guarded", false)]),
            StateStore.Open(Path.Combine(folder, "state.db")));
        var localFolder = await rig.Local.EnsureFolderAsync("guarded", CancellationToken.None);
        var dropboxFolder = await rig.Dropbox.EnsureFolderAsync("guarded", CancellationToken.None);
        var localChild = await PutAsync(rig.Local, "guarded/online.txt", [1]);
        var dropboxChild = await PutAsync(rig.Dropbox, "guarded/online.txt", [1]);
        RecordLinked(rig, localFolder, dropboxFolder);
        RecordLinked(rig, localChild, dropboxChild);
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailExcludeHoldsProtected, localChild.Path,
                new IssueSubject([new IssueItem(Side.Local, localChild.Path, localChild.Id),
                    new IssueItem(Side.Dropbox, dropboxChild.Path, dropboxChild.Id)], null))]
        });
        var childFailure = rig.Store.Issues(rig.Pair.PairId).Single();

        var online = localChild with { Skip = nameof(TextKey.SkipOnlineOnly) };
        var localSide = new ScopedSide(rig.Local, rig.Scope) { Extra = [online] };
        var dropboxSide = new ScopedSide(rig.Dropbox, rig.Scope);
        Issue? rootFailure = null;
        foreach (var runId in new[] { "pending-1", "pending-2" })
        {
            using var log = Log(rig, folder, runId);
            var summary = await ScanAsync(rig, log, localSide, dropboxSide);
            Check.Require(!summary.Refused, resolution + ": a blocked exclusion does not abort Repair");
            Check.Require(Lines(log.Path).Any(fields => fields[1] == nameof(RunEventKind.Issue) &&
                    fields[3] == "guarded" && fields[5] == nameof(TextKey.FailExcludeHoldsProtected)),
                resolution + ": each blocked attempt reports its protective failure");
            var issue = rig.Store.Issues(rig.Pair.PairId).Single(item => item.Kind == IssueKind.Failure &&
                item.Cause == TextKey.FailExcludeHoldsProtected && item.Path == "guarded");
            Check.Require(rootFailure is null || issue.Id == rootFailure.Id && issue.At == rootFailure.At,
                resolution + ": a pending exclusion retains its continuing failure identity and timestamp");
            rootFailure = issue;
            var remainingChild = rig.Store.IssueById(rig.Pair.PairId, childFailure.Id);
            Check.Require(remainingChild is not null && remainingChild.Kind == childFailure.Kind &&
                    remainingChild.Cause == childFailure.Cause && remainingChild.Path == childFailure.Path &&
                    remainingChild.At == childFailure.At && remainingChild.Dismissed == childFailure.Dismissed &&
                    remainingChild.Subject.HasSameItems(childFailure.Subject),
                resolution + ": a historical child failure survives excluded saved-only rows");
            Check.Require(localSide.Mutations.IsEmpty && dropboxSide.Mutations.IsEmpty,
                resolution + ": a protected exclusion makes no provider changes");
            foreach (var entry in new[] { localFolder, localChild })
                Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key(entry.Path)) is not null,
                    resolution + ": a pending exclusion retains the saved binding for " + entry.Path);
        }

        var pair = resolution switch
        {
            "kept" => rig.Pair with { Exclusions = [new Exclusion(Side.Local, "guarded", true)] },
            "ignored" => rig.Pair with { Exclusions = [], IgnoreRules = ["FolderName:guarded"] },
            _ => rig.Pair
        };
        rig.Store.SavePair(pair);
        var resolved = rig with { Pair = pair, Scope = new PathScope(pair, [pair]) };
        var resolvedLocal = new ScopedSide(rig.Local, resolved.Scope);
        var resolvedDropbox = new ScopedSide(rig.Dropbox, resolved.Scope);
        using (var log = Log(resolved, folder, "resolved"))
        {
            var summary = await ScanAsync(resolved, log, resolvedLocal, resolvedDropbox);
            Check.Require(!summary.Refused, resolution + ": the follow-up Repair completes");
        }
        Check.Require(!rig.Store.Issues(pair.PairId).Any(issue => issue.Kind == IssueKind.Failure),
            resolution + ": completed or configured-out-of-scope exclusion failures retire");
        Check.Require((await rig.Local.ReadOneAsync("guarded", CancellationToken.None) is null) ==
                (resolution == "applied"), resolution + ": only an applied removal deletes the excluded copy");
        Check.Require((await ReadBytesAsync(rig.Dropbox, dropboxChild.Path)).SequenceEqual(new byte[] { 1 }),
            resolution + ": the counterpart content survives");
        foreach (var entry in new[] { localFolder, localChild })
            Check.Require((rig.Store.SyncedByKey(pair.PairId, Side.Local, PathNames.Key(entry.Path)) is null) ==
                    (resolution == "applied"), resolution + ": only applied removal forgets " + entry.Path);
        Check.Require(resolvedDropbox.Mutations.IsEmpty,
            resolution + ": already preserved counterpart content is not recopied");
        if (resolution != "applied")
            Check.Require(resolvedLocal.Calls.IsEmpty && resolvedDropbox.Calls.IsEmpty &&
                    resolvedLocal.Mutations.IsEmpty,
                resolution + ": scope retirement requires no per-item provider calls or effects");
    });

    private static PairConfig Pair(string id = "pair-a", IReadOnlyList<Exclusion>? exclusions = null,
        IReadOnlyList<string>? ignoreRules = null) =>
        new(id, id, @"C:\ProbeFixture", "/ProbeFixture", "connection-a", ignoreRules ?? [], exclusions ?? [],
            FirstRunPolicy.NewestWins, true, null, new RootBinding(@"C:\ProbeFixture", "/ProbeFixture",
                "connection-a", new DirectoryIdentity(1, 1, null),
                new DropboxRootIdentity("account-a", "root-a", "home-a", "folder-a")));

    private static Rig NewRig(string folder, PairConfig pair, IStateStore? store = null)
    {
        store ??= StateStore.Open(Path.Combine(folder, "state.db"));
        store.SavePair(pair);
        SourceFixture.AcquireJournal(store, pair);
        return new Rig(pair, store, new FakeSide(Side.Local), new FakeSide(Side.Dropbox),
            new FakeRecovery { Evidence = new DriveEvidence(1, 1, 1) },
            new FakeAdmission { Level = 3 }, new PathScope(pair, [pair]));
    }

    private static Task ScanAsync(Rig rig, RunLog log, ISide local, ISide dropbox) =>
        ScanPassChecks.RunScanAsync(new ScanPassChecks.Rig(rig.Pair, rig.Store, rig.Local, rig.Dropbox,
            rig.Recovery, rig.Admission, rig.Scope), log, local, dropbox);

    private static RunLog Log(Rig rig, string folder, string runId) =>
        RunLog.Open(folder, rig.Pair.PairId, runId, _ => { }, LogBufferCharacters);

    private static void RecordLinked(Rig rig, Entry local, Entry dropbox) =>
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, dropbox)] });

    private static string[][] Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(RunLog.FieldSeparator)).ToArray();
    }

    private static async Task<byte[]> ReadBytesAsync(FakeSide side, string path)
    {
        var entry = await side.ReadOneAsync(path, CancellationToken.None) ?? throw new InvalidOperationException(
            "missing probe entry: " + path);
        await using var stream = await side.OpenReadAsync(entry, null, CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static async Task<Entry> PutAsync(FakeSide side, string path, byte[] bytes, Entry? expected = null)
    {
        var source = new Entry(path, false, bytes.Length, DateTimeOffset.UnixEpoch, path, "", "", null);
        using var content = new MemoryStream(bytes);
        await using var stage = await side.StageAsync(path, content, source, CancellationToken.None);
        return (await side.PublishAsync(stage, expected, null, CancellationToken.None)).Final
            ?? throw new InvalidOperationException("probe publish produced no entry: " + path);
    }

    private static async Task WithFolder(string name, Func<string, Task> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-probe-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { await body(folder); }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record Rig(PairConfig Pair, IStateStore Store, FakeSide Local, FakeSide Dropbox,
        FakeRecovery Recovery, FakeAdmission Admission, PathScope Scope) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    // The production listings drop entries the scope excludes; FakeSide does not, so this wrapper applies the same
    // filter (LocalListing.Walk / DropboxListing.Skipped) and records every per-item call and write.
    private sealed class ScopedSide(ISide inner, PathScope scope) : ISide
    {
        public IReadOnlyList<Entry> Extra { get; init; } = [];
        public ConcurrentBag<string> Calls { get; } = new();
        public ConcurrentBag<string> Mutations { get; } = new();

        public Side Side => inner.Side;

        public async IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var yielded = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var item in inner.ReadAllAsync(under, options, ct))
            {
                if (item.Entry is { } entry) yielded.Add(entry.Path);
                if (item.Entry is { } scanned && !options.IncludeOutOfScope && Excluded(scanned.Path, scanned.IsFolder))
                    continue;
                yield return Replaced(item);
            }
            foreach (var entry in Extra)
                if (!yielded.Contains(entry.Path) && IsUnder(entry.Path, under))
                {
                    if (!options.IncludeOutOfScope && Excluded(entry.Path, entry.IsFolder)) continue;
                    yield return new Listed(entry.Path, entry);
                }
        }

        public async Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
        {
            Calls.Add(path);
            return Replace(path) ?? await inner.ReadOneAsync(path, ct);
        }

        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct)
        {
            Calls.Add(entry.Path);
            return inner.OpenReadAsync(entry, attempt, ct);
        }

        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct)
        {
            Calls.Add(entry.Path);
            return inner.ContentHashAsync(entry, ct);
        }

        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
        {
            Calls.Add(path);
            return inner.StageAsync(path, content, source, ct);
        }

        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct)
        {
            Calls.Add(staged.Path);
            Mutations.Add(staged.Path);
            return inner.PublishAsync(staged, expected, selection, ct);
        }

        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct)
        {
            Calls.Add(path);
            Mutations.Add(path);
            return inner.EnsureFolderAsync(path, ct);
        }

        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct)
        {
            Calls.Add(expected.Path);
            Mutations.Add(expected.Path);
            return inner.DeleteAsync(expected, selection, ct);
        }

        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct)
        {
            Calls.Add(expected.Path);
            Calls.Add(newPath);
            Mutations.Add(expected.Path);
            Mutations.Add(newPath);
            return inner.MoveAsync(expected, newPath, ct);
        }

        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct)
        {
            // Only the selected effects are submitted; the submission shell itself carries no path entry.
            foreach (var effect in effects)
                if (effect.Expected is { } entry)
                {
                    Calls.Add(entry.Path);
                    Mutations.Add(entry.Path);
                }
            return inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);
        }

        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
            RequestAttempt attempt, CancellationToken ct) =>
            // Polling observes progress and carries no mutation or path entry.
            inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);

        private bool Excluded(string path, bool isFolder) =>
            Side == Side.Local ? scope.Excludes(path, null, isFolder) : scope.Excludes(null, path, isFolder);

        private static bool IsUnder(string path, string under) => under.Length == 0 ||
            path.StartsWith(under + PathNames.PathSeparator, StringComparison.Ordinal);

        private Entry? Replace(string path) => Extra.FirstOrDefault(candidate => candidate.Path == path);

        private Listed Replaced(Listed item) =>
            item.Entry is { } entry && Replace(entry.Path) is { } skipped
                ? new Listed(entry.Path, skipped)
                : item;
    }
}
