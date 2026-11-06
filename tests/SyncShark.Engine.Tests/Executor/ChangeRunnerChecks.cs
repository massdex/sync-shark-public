using System.Reflection;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks for SelectionPass (F062): the captured selection generation reaches every admitted sync route
/// unchanged, a stale admission refuses without an effect or settlement, SaveBefore precedes coverage capture, and
/// the committed effective Issue reaches the exact selection-bound residual continuation.</summary>
public static class ChangeRunnerChecks
{
    private const string PairId = "pair-a";
    // The pair's local root serial (1) is the journal domain the executor's evidence reads.
    private const string VolumeKey = "1";
    private const long Generation = 0;
    private const string Item = "item";
    private const string Folder = "folder";
    private const string OldPath = "old-item";
    private const string SourcePath = "src-item";
    private const string DestinationPath = "dst-item";
    private const string LocalId = "local-id";
    private const string DropboxId = "dropbox-id";
    private const string OccupantId = "occupant-id";
    private const string Cursor = "cursor-a";
    private const int LogBufferCharacters = 256;
    private const string CommitFault = "commit-fixture";
    private const string AttachFault = "attach-fixture";
    private const int OccupantReadOrdinal = 3;

    private static readonly DateTimeOffset Stamp = DateTimeOffset.UnixEpoch.AddDays(1);

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    public static void RunPeers()
    {
        PeerCasesAsync().GetAwaiter().GetResult();
        HeldRepeatedNoticeAsync().GetAwaiter().GetResult();
        PeerRetentionCasesAsync().GetAwaiter().GetResult();
        PeerPreimageAdvance();
        FailedSelectionReleasesClaim();
        FolderFallbackPeerAsync().GetAwaiter().GetResult();
        foreach (var move in new[] { false, true })
            FolderRecordReplacementScopeAsync(move).GetAwaiter().GetResult();
    }

    private static async Task FolderRecordReplacementScopeAsync(bool move)
    {
        using var fixture = new Fixture();
        var local = FolderEntry(Folder, LocalId);
        var remote = FolderEntry(Folder, DropboxId);
        var saved = move ? null : SyncedEntries.From(local, remote);
        fixture.Local.Seed(local, null);
        fixture.Local.Seed(LocalEntry(Plain, MemberId), [2]);
        if (saved is not null) fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null,
            LocalId, local, false, false));
        var row = new Row(PathNames.Key(Folder), local, remote, saved, [], null, RowContext.Neutral);
        var prepared = new Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.Record, row);
        var scopes = LockKeys.ForScopes(fixture.Pair, LockKeys.ForLiveAction(prepared, fixture.Pair, null));
        var lease = new Lease { AllowedScopes = scopes, AllowedKeys = scopes.Select(scope => scope.Key).ToArray() };
        var changed = move ? remote with { Path = Folder.ToUpperInvariant() } :
            RemoteEntry(Folder, DropboxId) with { Modified = Stamp.AddDays(1) };
        fixture.Dropbox.Seed(changed, move ? null : [1]);
        var boundary = new Boundary(_ => lease);
        var jobs = SyncJobs.Create(fixture.Context(selected), new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 0));
        var error = await ThrowsAsync<LiveAdmissionStaleException>(() => jobs.Items.RunAdmittedAsync(row, default,
            preparedAction: prepared, execution: lease, selection: selected));
        Check.Require(error.Reason == LiveAdmissionStaleReason.FootprintChanged && lease.ScopeCalls == 1 &&
            (await fixture.Local.ReadOneAsync(Folder, default)) is { IsFolder: true } &&
            await fixture.Local.ReadOneAsync(Plain, default) is not null && fixture.Inner.SyncedByKey(PairId, Side.Local, row.Key) == saved &&
            await fixture.Dropbox.ReadOneAsync(Folder, default) == changed,
            "RP-02/RP-04: folder Record reobserved as replacement or case-only Move refuses exact lease without effects; move=" + move);
    }

    private static async Task FolderFallbackPeerAsync()
    {
        using var fixture = new Fixture(realStore: true);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var source = fixture.Inner.ReadSource(PairId, side)!.Value;
            fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor, null);
        }
        var local = FolderEntry(Folder, LocalId);
        var remote = FolderEntry(Folder, DropboxId);
        var before = SyncedEntries.From(local, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [before] });
        fixture.Dropbox.Seed(remote, null);
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, Folder, null,
            LocalId, null, false, true));
        var peer = SeedRemote(fixture, remote);
        fixture.Inner.BeginSelection(peer);
        var admissions = 0;
        var boundary = new Boundary(request =>
        {
            fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
            // Folder timestamps do not trigger Record; changed spelling does, at the same join key.
            if (++admissions == 2) fixture.Local.Seed(local with { Path = Folder.ToUpperInvariant() }, null);
            return new Lease { AllowedKeys = LockKeys.ForScopes(fixture.Pair, request.Footprint).Select(scope => scope.Key).ToArray() };
        });
        Check.Require(await Runner(fixture, selected, boundary, (_, _, _) => Task.FromResult(Coverage(selected)))
            .RunAsync(selected, default), "PR-01: reappeared folder completes its deferred-plan fallback");
        var saved = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Folder));
        Check.Require(admissions == 2 && saved != before && fixture.Inner.ReadWork(peer).Single().Before == saved &&
            fixture.Hooks.CommittedSelections.Any(work => work.Peer == boundary.Requests[0][0].Coverage.Peer),
            "PR-01: fallback Record advances the exact peer preimage atomically; admissions=" + admissions +
            "; changed=" + (saved != before) + "; peerMatches=" + (fixture.Inner.ReadWork(peer).Single().Before == saved) +
            "; commits=" + fixture.Hooks.CommittedSelections.Count);
        await RunPeerSelectionAsync(fixture, peer);
        Check.Require(!PendingWork.ByIdentity(fixture.Inner, Side.Dropbox, peer.Domain, DropboxId).Any(),
            "PR-01: kept folder peer settles in its next selection without Repair");
    }

    private static void PeerPreimageAdvance()
    {
        foreach (var mode in new[] { "matching", "different", "later" })
        {
            using var fixture = new Fixture(realStore: true);
            var local = LocalEntry(Item, LocalId);
            var remote = RemoteEntry(Item, DropboxId);
            var before = SyncedEntries.From(local, remote);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [before] });
            var own = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null,
                LocalId, local, true, false));
            fixture.Inner.BeginSelection(own);
            var watermark = fixture.Inner.ReadWorkWatermark();
            var other = SeedRemote(fixture, remote);
            var peerBefore = mode == "different" ? SyncedEntries.From(local, remote with { ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) }) : before;
            if (mode == "different") fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [peerBefore] });
            fixture.Inner.BeginSelection(other);
            if (mode == "different") fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [before] });
            var selected = own with { Peer = new SelectionPeer(PathNames.KeyFor(Side.Dropbox, Item),
                mode == "later" ? watermark : fixture.Inner.ReadWorkWatermark()) };
            var after = SyncedEntries.From(local, remote with { ContentHash = DropboxContentHash.Compute(new byte[] { 2 }) });
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [after] }, selected, null);
            Check.Require(fixture.Inner.ReadWork(other).Single().Before == (mode == "matching" ? after : peerBefore),
                "G18: only admitted matching peer preimage advances atomically: " + mode);
            fixture.Inner.CompleteWork(selected, selected.Peer);
            Check.Require(PendingWork.ByIdentity(fixture.Inner, Side.Dropbox, other.Domain, DropboxId).Any() == (mode != "matching"),
                "G18: different preimage and later peer stay unconsumed: " + mode);
        }
    }

    private static async Task PeerRetentionCasesAsync()
    {
        var failures = new List<Exception>();
        foreach (var mode in new[] { "held", "failed", "skipped", "out-of-scope", "residuals", "folder" })
        {
            try { await PeerRetentionAsync(mode); }
            catch (Exception error) { failures.Add(new InvalidOperationException(mode, error)); }
        }
        foreach (var foreign in new[] { false, true })
        {
            try { await FolderPeerOnDiskAsync(foreign); }
            catch (Exception error) { failures.Add(new InvalidOperationException("folder-on-disk " + foreign, error)); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task FolderPeerOnDiskAsync(bool foreign)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, Folder));
            await File.WriteAllBytesAsync(Path.Combine(root, Plain), [2]);
            using var fixture = new Fixture(realStore: true, localRoot: root);
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                var source = fixture.Inner.ReadSource(PairId, side)!.Value;
                fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                    source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor, null);
            }
            var remote = FolderEntry(Folder, DropboxId);
            fixture.Dropbox.Seed(remote, null);
            var selected = SeedRemote(fixture, remote);
            var local = new Massdex.SyncShark.Engine.Local.LocalSide(fixture.Pair,
                new PathScope(fixture.Pair, [fixture.Pair]), fixture.Context(selected).Settings,
                new PendingNotes(fixture.Inner, fixture.Pair.PairId, 100, mutation: false),
                root, (_, _, _, _) => throw new NotSupportedException());
            fixture.LocalView = local;
            var folder = (await local.ReadOneAsync(Folder, default))!;
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, folder.Id, folder, false, false));
            await RunPeerSelectionAsync(fixture, selected);
            var kept = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, folder.Id).Single();
            if (foreign)
            {
                var injected = false;
                fixture.LocalView = new ProxySide(local)
                {
                    OnRead = path =>
                    {
                        if (path != Plain || injected) return;
                        injected = true;
                        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null,
                            null, folder, false, false), 2);
                    }
                };
                var error = await ThrowsAsync<LiveAdmissionStaleException>(() => RunPeerSelectionAsync(fixture, kept));
                Check.Require(injected && error.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
                    await fixture.Dropbox.ReadOneAsync(Plain, default) is null,
                    "G18: foreign folder notice arriving before child refuses without uploading");
                return;
            }
            await RunPeerSelectionAsync(fixture, kept);
            Check.Require((await fixture.Dropbox.ReadOneAsync(Plain, default))?.ContentHash ==
                DropboxContentHash.Compute(new byte[] { 2 }) &&
                !PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, folder.Id).Any(),
                "G18-03: Local disk walk uploads an unannounced child and settles without Repair");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task PeerRetentionAsync(string mode)
    {
        using var fixture = new Fixture(realStore: true);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var source = fixture.Inner.ReadSource(PairId, side)!.Value;
            fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor, null);
        }
        var folder = mode == "folder";
        var path = folder ? Folder : Item;
        var local = folder ? FolderEntry(path, LocalId) : LocalEntry(path, LocalId);
        var remote = folder ? FolderEntry(path, DropboxId) : RemoteEntry(path, DropboxId);
        fixture.Local.Seed(local, folder ? null : [2]);
        fixture.Dropbox.Seed(remote, folder ? null : [1]);
        if (folder) fixture.Local.Seed(LocalEntry(Plain, MemberId), [2]);
        else fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
        var observedLocal = new ProxySide(fixture.Local);
        fixture.LocalView = observedLocal;
        var localWork = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, path, null,
            LocalId, local, !folder, false));
        if (mode is "held" or "residuals")
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, path,
                    new IssueSubject([new IssueItem(Side.Local, path, LocalId)], null))]
            });
        if (mode == "residuals") await RunPeerSelectionAsync(fixture, localWork);
        var remoteWork = SeedRemote(fixture, remote);
        if (mode == "failed") fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            BlockPublish = (_, _) => throw new ItemFailedException(path, TextKey.FailDropboxNoPermission, null)
        };
        if (mode == "skipped") fixture.Local.Seed(local with { Skip = "fixture-skip" }, [2]);
        var context = fixture.Context(remoteWork);
        if (mode == "out-of-scope")
            context = context with { Scope = new PathScope(fixture.Pair with { IgnoreRules = ["FileName:" + Item] }, [fixture.Pair]) };
        await RunPeerSelectionAsync(fixture, remoteWork, context);
        if (mode == "residuals")
        {
            fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(path, new[] { IssueKind.Failure })] });
            var residual = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
            var hashes = observedLocal.Hashes;
            await RunPeerSelectionAsync(fixture, residual);
            Check.Require(observedLocal.Hashes > hashes &&
                !PendingWork.ByIdentity(fixture.Inner, Side.Dropbox, remoteWork.Domain, DropboxId).Any() &&
                !PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Any(),
                "G18-02: one released residual selection hashes the dirty Local edit and settles both sides");
            return;
        }
        Check.Require(PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Any(),
            "G18-01/G18-03: unconsumed Local evidence remains reachable: " + mode);
        if (mode is "held" or "failed")
        {
            var kept = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
            await RunPeerSelectionAsync(fixture, kept);
            var residual = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
            Check.Require(fixture.Inner.ReadWork(residual).Any(work => work.Notice.LocalContentDirty) &&
                fixture.Inner.Issues(PairId).Any(issue => issue.Kind == IssueKind.Failure),
                "G18-01: the kept dirty row reaches its own visible holder");
            fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(path, new[] { IssueKind.Failure })] });
        }
        fixture.DropboxView = null;
        if (mode == "skipped") fixture.Local.Seed(local, [2]);
        var next = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
        await RunPeerSelectionAsync(fixture, next);
        Check.Require((await fixture.Dropbox.ReadOneAsync(folder ? Plain : path, default))!.ContentHash ==
            DropboxContentHash.Compute(new byte[] { 2 }),
            "G18-01/G18-03: kept evidence uploads the edit or discovers the child without Repair: " + mode);
        Check.Require(!PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Any() &&
            !PendingWork.ByIdentity(fixture.Inner, Side.Dropbox, remoteWork.Domain, DropboxId).Any(),
            "G18-01/G18-03: both sides settle without spinning: " + mode);
    }

    public static void FailedSelectionReleasesClaim() => FailedSelectionReleasesClaimAsync().GetAwaiter().GetResult();

    private static async Task FailedSelectionReleasesClaimAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var local = LocalEntry(Item, LocalId);
        fixture.Local.Seed(local, [2]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed,
            Item, null, LocalId, local, false, false));
        var peer = new SelectionPeer(PathNames.DropboxKey(Item), selection.ThroughWorkId);
        var failure = new InvalidOperationException(AttachFault);
        var context = fixture.Context(selection) with
        {
            Events = _ =>
            {
                fixture.Inner.ClaimSelectionPeer(selection, peer);
                throw failure;
            }
        };
        Check.Require(ReferenceEquals(await ThrowsAsync<InvalidOperationException>(
            () => RunPeerSelectionAsync(fixture, selection, context)), failure),
            "the executor propagates the injected mid-selection failure");
        await ThrowsAsync<LiveAdmissionStaleException>(() =>
        {
            fixture.Inner.ClaimSelectionPeer(selection, peer);
            return Task.CompletedTask;
        });
        Check.Require(fixture.Inner.ReadWork(selection).Single().WorkId == selection.AnchorWorkId,
            "executor failure releases its claim without retiring unfinished work");
    }

    private static async Task RunPeerSelectionAsync(Fixture fixture, WorkSelection selection,
        ExecutorContext? context = null)
    {
        var boundary = new Boundary(request =>
        {
            fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
            return new Lease { AllowedKeys = LockKeys.ForScopes(fixture.Pair, request.Footprint).Select(scope => scope.Key).ToArray() };
        });
        context ??= fixture.Context(selection);
        var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 0));
        boundary.Capture = (_, _, _) => Task.FromResult(Coverage(selection));
        var runner = new SelectionPass(context, jobs.Items, jobs.Ancestors, boundary, jobs.Actions);
        Check.Require(await runner.RunAsync(selection, default), "G18: production selection completes");
    }

    private static async Task HeldRepeatedNoticeAsync()
    {
        using var fixture = new Fixture();
        var local = LocalEntry(Item, LocalId);
        fixture.Local.Seed(local, [2]);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, Item,
                new IssueSubject([new IssueItem(Side.Local, Item, LocalId)], null))]
        });
        var first = new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, null, false, false);
        var selection = Seed(fixture, first);
        var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
        var position = fixture.Inner.ReadJournal(VolumeKey)!;
        var through = fixture.Inner.AcquireJournalPage(VolumeKey, position, position,
            [(PairId, Generation, source.Epoch, source.Revision, false, false)],
            [(PairId, first with { LocalContentDirty = true })])!.Value;
        selection = selection with { ThroughWorkId = through };
        Check.Require(await Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => throw new InvalidOperationException("a held branch needs no coverage"))
            .RunAsync(selection, default), "two notices for a held item attach without stopping the opportunity");
        var residual = PendingAt(fixture, Side.Local, Item).Single();
        Check.Require(residual.AnchorWorkId > through && fixture.Inner.ReadLocalDirty(PairId, Generation,
            source.Epoch, VolumeKey, LocalId, PathNames.Key(Item), null).Dirty,
            "one residual preserves the dirty fact from either held notice");
    }

    private static async Task PeerCasesAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var tie in new[] { false, true })
        {
            using var fixture = new Fixture();
            var local = LocalEntry(Item, LocalId) with { Modified = Stamp.AddDays(-2) };
            var remote = RemoteEntry(Item, DropboxId) with { Modified = local.Modified };
            if (!tie)
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            if (tie)
            {
                local = local with { Modified = DateTimeOffset.UnixEpoch };
                remote = remote with { Modified = local.Modified, ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) };
            }
            fixture.Local.Seed(local, [2]);
            fixture.Dropbox.Seed(remote, tie ? [3] : [1]);
            var localWork = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null,
                LocalId, local, true, false));
            var remoteWork = SeedRemote(fixture, remote);
            var selection = side == Side.Local ? localWork : remoteWork;
            var watermark = fixture.Inner.ReadWorkWatermark();
            var boundary = new Boundary(_ => new Lease());
            Check.Require(await Runner(fixture, selection, boundary, (_, _, _) =>
                Task.FromResult(Coverage(selection))).RunAsync(selection, default),
                "A41/A42/A72: one bilateral selection settles the observed peer");
            Check.Require(boundary.Requests.Single().Single().Coverage.Peer ==
                new SelectionPeer(PathNames.KeyFor(PathNames.Other(side), Item), watermark),
                "the peer carries the exact other-side path and pre-observation watermark");
            if (!tie)
                Check.Require(fixture.Hooks.CommittedSelections.Any(committed => committed with { Peer = null } == selection &&
                    committed.Peer == boundary.Requests.Single().Single().Coverage.Peer),
                    "G18: commit carries the exact observed peer without changing opportunity identity");
            Check.Require(PendingAt(fixture, Side.Local, Item)
                .All(work => work.AnchorWorkId > watermark), "the original Local acquired rows retire");
            if (!tie || side == Side.Dropbox)
                Check.Require(!fixture.Inner.ReadLocalDirty(PairId, Generation, localWork.SourceEpoch, VolumeKey,
                    LocalId, PathNames.Key(Item), null).Dirty, "consumed Local dirty evidence retires");
            if (tie)
            {
                Check.Require(fixture.Inner.Issues(PairId).Single().Kind == IssueKind.Tie &&
                    fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
                    "A41: both edits raise and attach the Tie without refusing: " +
                    string.Join(",", fixture.Inner.Issues(PairId).Select(issue => issue.Kind + ":" + issue.Cause)) +
                    "; attachments=" + fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)));
            }
            else
            {
                Check.Require((await fixture.Dropbox.ReadOneAsync(Item, default))!.ContentHash ==
                    DropboxContentHash.Compute(new byte[] { 2 }),
                    "A72: the Local peer with identical id, size and time is hashed and uploaded");
                var echo = SeedRemote(fixture, (await fixture.Dropbox.ReadOneAsync(Item, default))!);
                Check.Require(await Runner(fixture, echo, new Boundary(_ => new Lease()), (_, _, _) =>
                    Task.FromResult(Coverage(echo))).RunAsync(echo, default),
                    "A42: the next upload echo is processed normally");
                Check.Require(!PendingAt(fixture, Side.Dropbox, Item).Any(),
                    "the echo and consumed peer rows retire");
            }
        }
    }

    private static WorkSelection SeedRemote(Fixture fixture, Entry remote)
    {
        var source = fixture.Inner.ReadSource(PairId, Side.Dropbox)!.Value;
        var roots = fixture.Pair.Roots!.Dropbox;
        var provenance = source.Provenance ?? new SourceProvenance(roots.AccountId, roots.RootNamespaceId,
            roots.FolderId, fixture.Pair.DropboxRoot, true, true, true, true, Generation, source.Epoch);
        if (source.Provenance is null)
        {
            fixture.Inner.AdvanceSource(PairId, Side.Dropbox, Generation, source.Epoch, source.Revision,
                null, source.Cursor, source.Epoch, source.Revision, provenance, source.Cursor, source.Obligation);
            source = fixture.Inner.ReadSource(PairId, Side.Dropbox)!.Value;
        }
        var work = fixture.Inner.AcquireDropboxPage(PairId, Generation, source.Epoch, source.Revision,
            provenance, source.Cursor,
            [new ChangeNotice(Side.Dropbox, ChangeKind.Changed, remote.Path, null, remote.Id, remote, false, false)], Cursor);
        return new WorkSelection(PairId, Generation, source.Epoch, Side.Dropbox, provenance.NamespaceId,
            remote.Id, PathNames.KeyFor(Side.Dropbox, remote.Path), work, work);
    }

    // PendingAncestors omits held work unless a coverage watermark is supplied; 0 is below every work id, so the
    // in-memory fixture sees exactly the acquired rows the retired identity-keyed read returned.
    private static IReadOnlyList<WorkSelection> PendingAt(Fixture fixture, Side side, params string[] paths) =>
        fixture.Inner.PendingAncestors(PairId, Generation, side,
            paths.Select(path => PathNames.KeyFor(side, path)).ToArray(), 0).ToArray();

    public static void RunLiveReplaceSafety() => LiveFolderReplaceOverUnreadableChildAsync().GetAwaiter().GetResult();

    public static void RunRaisedHoldSafety() => RaisedHoldCoversSelectionChildrenAsync().GetAwaiter().GetResult();

    public static void RunHeldRenameSafety() => RenameUnderHeldFolderHoldsAsync().GetAwaiter().GetResult();

    private static void TrustEchoSources(Fixture fixture)
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var source = fixture.Inner.ReadSource(PairId, side)!.Value;
            var root = fixture.Pair.Roots!.Dropbox;
            var provenance = side == Side.Local ? source.Provenance : new SourceProvenance(root.AccountId,
                root.RootNamespaceId, root.FolderId, fixture.Pair.DropboxRoot, true, true, true, true, Generation, source.Epoch);
            fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, provenance,
                side == Side.Dropbox ? Cursor : source.Cursor, null);
        }
    }

    private static async Task LocalEchoCasesAsync()
    {
        foreach (var mode in new[] { "echo", "queued-remote", "later-remote", "completion-remote", "local-edit", "dirty", "engine-write", "deleted",
            "cursor", "provenance", "local-obligation", "remote-obligation", "issue", "ancestor", "unrelated", "retry" })
        {
            using var fixture = new Fixture(realStore: true);
            TrustEchoSources(fixture);
            var path = Folder + "/" + Item;
            fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
            fixture.Dropbox.Seed(FolderEntry(Folder, "remote-parent"), null);
            var local = LocalEntry(path, LocalId);
            var remote = RemoteEntry(path, DropboxId);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            fixture.Dropbox.Seed(remote, [1]);
            if (mode != "deleted") fixture.Local.Seed(local, mode == "local-edit" ? [2] : [1]);
            // UsnPaths resolves the stage id at its final name after MoveStage; content-write reasons stay dirty.
            var selected = Seed(fixture, new ChangeNotice(Side.Local,
                mode == "deleted" ? ChangeKind.Deleted : ChangeKind.Changed, path, null, LocalId,
                mode == "deleted" ? null : local, mode is "local-edit" or "dirty" or "engine-write", mode == "deleted"));
            if (mode == "queued-remote")
            {
                var changed = remote with { ContentHash = DropboxContentHash.Compute(new byte[] { 2 }), Revision = "rev-2" };
                fixture.Dropbox.Seed(changed, [2]);
                SeedRemote(fixture, changed);
            }
            if (mode is "cursor" or "provenance" or "local-obligation" or "remote-obligation")
            {
                var side = mode == "local-obligation" ? Side.Local : Side.Dropbox;
                var source = fixture.Inner.ReadSource(PairId, side)!.Value;
                fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                    source.Provenance, source.Cursor, source.Epoch, source.Revision,
                    mode == "provenance" ? null : source.Provenance, mode is "cursor" or "provenance" ? null : source.Cursor,
                    mode.EndsWith("obligation", StringComparison.Ordinal) ? SourceObligation.NormalRepair : null);
            }
            if (mode is "issue" or "ancestor" or "unrelated" or "retry")
            {
                var issuePath = mode == "ancestor" ? Folder.ToUpperInvariant() : mode == "unrelated" ? "elsewhere" : path;
                fixture.Inner.Commit(PairId, StateChange.Empty with
                {
                    Raise = [(mode == "retry" ? IssueKind.Failure : IssueKind.Info,
                        mode == "retry" ? TextKey.RequiredRepairRetry : TextKey.InfoNewestWins, issuePath,
                        new IssueSubject([], null))]
                });
            }
            if (mode == "completion-remote") fixture.Hooks.BeforeCall = method =>
            {
                if (method.Name != nameof(IStateStore.CompleteWork)) return;
                fixture.Hooks.BeforeCall = null;
                SeedRemote(fixture, remote);
            };
            var reads = 0;
            fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: _ => reads++);
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)fixture.Inner.GetType().GetField("_connection",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Inner)!;
            var statements = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => statements++), null);
            try { await RunSelectionAsync(fixture, selected); }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
            Console.WriteLine("90a mode=" + mode + "; sql=" + statements + "; providerReads=" + reads);
            var shortcut = mode is "echo" or "later-remote" or "completion-remote" or "unrelated";
            if (mode == "completion-remote")
                Check.Require(PendingAt(fixture, Side.Dropbox, path).Any(),
                    "90a Local-only completion never retires an unobserved Dropbox arrival");
            Check.Require(shortcut ? reads == 0 : reads > 0, "90a provider-read guard: " + mode + "; reads=" + reads);
            Check.Require(!PendingAt(fixture, Side.Local, path).Any(), "90a Local notice retires: " + mode);
            if (mode == "queued-remote")
                Check.Require(await fixture.Local.ContentHashAsync((await fixture.Local.ReadOneAsync(path, default))!, default) ==
                    DropboxContentHash.Compute(new byte[] { 2 }), "90A-S1: pre-acquired remote B reaches Local");
            if (mode == "local-edit")
                Check.Require((await fixture.Dropbox.ReadOneAsync(path, default))!.ContentHash ==
                    DropboxContentHash.Compute(new byte[] { 2 }), "90a genuine Local edit reaches Dropbox");
            if (mode == "deleted")
                Check.Require(await fixture.Dropbox.ReadOneAsync(path, default) is null, "90a deletion is not an echo");
            if (mode == "later-remote")
            {
                var changed = remote with { ContentHash = DropboxContentHash.Compute(new byte[] { 2 }), Revision = "rev-2" };
                fixture.Dropbox.Seed(changed, [2]);
                var later = SeedRemote(fixture, changed);
                await RunSelectionAsync(fixture, later);
                Check.Require(reads > 0 && await fixture.Local.ContentHashAsync(
                    (await fixture.Local.ReadOneAsync(path, default))!, default) == DropboxContentHash.Compute(new byte[] { 2 }),
                    "90a a remote edit acquired after Local completion remains reachable");
            }
        }
    }

    private static async Task LocalEchoResponsibilityAsync()
    {
        foreach (var mode in new[] { "pending", "folder", "alias", "retry", "children" })
        {
            var attempt = Guid.NewGuid();
            using var fixture = new Fixture(realStore: true, registeredAttempt: attempt);
            TrustEchoSources(fixture);
            var path = Folder + "/" + Item;
            var local = LocalEntry(path, LocalId);
            var saved = SyncedEntries.From(local, RemoteEntry(path, DropboxId));
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
            var notice = new ChangeNotice(Side.Local, ChangeKind.Changed, path, null, LocalId, local, false, false);
            var selected = Seed(fixture, notice);
            fixture.Inner.BeginSelection(selected);
            var unheld = fixture.Inner.CreateLocalEchoProbe(selected);
            if (mode == "pending")
            {
                var probe = fixture.Inner.CreateLocalEchoProbe(selected);
                Check.Require(probe(saved, selected.ThroughWorkId), "90a initially no pending effect");
                var pending = new PendingEffect(1, "operation", 0, PendingKind.LocalDelete, PendingPhase.Armed,
                    path, new RemovalProof(local, VolumeKey, saved.DropboxContentHash), saved,
                    null, null, null, null, false, null);
                fixture.Inner.ArmPending(PairId, pending);
                Check.Require(!probe(saved, selected.ThroughWorkId), "90a pending effect is checked fresh after snapshot");
                fixture.Inner.SettlePending(PairId, pending, StateChange.Empty);
                Check.Require(probe(saved, selected.ThroughWorkId), "90a settled pending effect permits echo");
                continue;
            }
            if (mode == "folder")
            {
                var root = FolderEntry(Folder, DropboxId);
                using var plan = fixture.Inner.OpenFolderPlan(PairId,
                    Row.ForOneSide(Side.Dropbox, PathNames.Key(Folder), root, null, [], null, RowContext.Neutral), Side.Dropbox);
                await plan.CaptureAsync(fixture.Dropbox.ReadAllAsync(Folder,
                    new ListOptions(true, false, false), default), default);
                foreach (var child in plan.Children(default))
                    plan.MarkClassified(child.Key, child.NativeKey,
                        new FolderChild(child.Entry, child.Synced, FolderVerdict.Removed), false, default);
                plan.Complete(true, default);
                fixture.Inner.BeginFolderRemoval(PairId, plan, 1, default);
            }
            else if (mode == "retry")
            {
                var subject = new IssueSubject([new IssueItem(Side.Local, path, LocalId)], null);
                var episode = fixture.Inner.RecordInUseFailure(PairId, Generation, selected.SourceEpoch, path,
                    subject, TextKey.FailLocalInUse, selected, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
                Check.Require(!unheld(saved, selected.ThroughWorkId),
                    "90a newly raised retry responsibility is checked fresh after snapshot");
                Check.Require(!fixture.Inner.CreateLocalEchoProbe(selected)(saved, selected.ThroughWorkId),
                    "90a real in-use episode prevents settlement");
                Check.Require(fixture.Inner.TryClaimItemRetry(episode, attempt), "90a retry is claimed by registered attempt");
                fixture.Inner.CompleteItemAttempt(episode, attempt, StateChange.Empty, null, selected);
                Check.Require(fixture.Inner.ReadItemRetryState(PairId, [episode.Token.IssueId])[episode.Token.IssueId] is null,
                    "90a successful retry leaves no automatic episode");
                continue;
            }
            else
            {
                fixture.Inner.Commit(PairId, StateChange.Empty with { Raise =
                    [(mode == "alias" ? IssueKind.Info : IssueKind.Failure, TextKey.FailChanged, "elsewhere",
                        new IssueSubject([new IssueItem(Side.Dropbox, path.ToUpperInvariant(), DropboxId)], null))] });
                if (mode == "children")
                {
                    var issue = fixture.Inner.Issues(PairId).Single();
                    fixture.Inner.AttachIssueWork(selected, issue, [(notice, false, saved)]);
                    Check.Require(!unheld(saved, selected.ThroughWorkId),
                        "90a acquired child responsibility blocks even with a pre-issue snapshot");
                }
            }
            Check.Require(!fixture.Inner.CreateLocalEchoProbe(selected)(saved, selected.ThroughWorkId),
                "90a responsibility guard: " + mode);
            if (mode == "children")
            {
                fixture.Inner.CompleteWork(selected, null);
                var residual = PendingAt(fixture, Side.Local, path).Single();
                Check.Require(!fixture.Inner.CreateLocalEchoProbe(residual)(saved, residual.ThroughWorkId),
                    "90a residual issue work is not an echo");
            }
        }
    }

    private static async Task LocalEchoReadFailureAsync()
    {
        using var fixture = new Fixture(realStore: true);
        TrustEchoSources(fixture);
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        var saved = SyncedEntries.From(local, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        fixture.Local.Seed(local with { Size = 2 }, [1, 2]);
        fixture.Dropbox.FailNext(Item, new ItemFailedException(Item, Text.ReadFailedOn(Side.Dropbox), null));
        var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId,
            local, true, false));
        Check.Require(await RunSelectionAsync(fixture, selected), "90a read failure retains its original responsibility");
        var residual = fixture.Inner.ReadWork(PendingAt(fixture, Side.Local, Item).Single()).Single();
        Check.Require(residual.Notice.Observed == local && residual.Before == saved,
            "90a failed bilateral read retains notice observation and work.Before, not partial Local observation");
    }

    private static async Task LocalEchoFolderAsync()
    {
        // Two siblings distinguish subtree discovery from a single-item shortcut; the walk itself is unchanged.
        foreach (var guarded in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: true);
            TrustEchoSources(fixture);
            var local = FolderEntry(Folder, LocalId);
            var remote = FolderEntry(Folder, DropboxId);
            fixture.Local.Seed(local, null);
            fixture.Dropbox.Seed(remote, null);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
            foreach (var name in new[] { "first", "second" })
            {
                var child = LocalEntry(Folder + "/" + name, LocalId + name);
                var counterpart = RemoteEntry(child.Path, DropboxId + name);
                fixture.Local.Seed(child, [1]);
                fixture.Dropbox.Seed(counterpart, [1]);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(child, counterpart)] });
            }
            if (guarded) fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Info, TextKey.InfoNewestWins, (Folder + "/first").ToUpperInvariant(), new IssueSubject([], null))]
            });
            var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, LocalId,
                local, false, false));
            var reads = new List<string>();
            fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: path => reads.Add(path));
            await RunSelectionAsync(fixture, selected);
            Check.Require(guarded ? reads.Contains(Folder) && reads.Contains(Folder + "/first") &&
                !reads.Contains(Folder + "/second") : reads.Count == 0,
                "90a touched folder and unchanged discovered children skip provider reads except overlapping issues");
        }
    }

    private static void LocalEchoIssueCounts()
    {
        // A small fixture distinguishes one streamed snapshot from repeated per-item scans.
        foreach (var count in new[] { 1, 3 })
        {
            using var fixture = new Fixture(realStore: true);
            TrustEchoSources(fixture);
            var local = LocalEntry(Item, LocalId);
            var remote = RemoteEntry(Item, DropboxId);
            var saved = SyncedEntries.From(local, remote);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved],
                Raise = Enumerable.Range(0, count).Select(index => (IssueKind.Info, TextKey.InfoNewestWins,
                    "unrelated/" + index, new IssueSubject([], null))).ToArray() });
            var selected = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, local, false, false));
            fixture.Inner.BeginSelection(selected);
            const int repetitions = 3; // Repeated callers in one pass, not a timing benchmark.
            fixture.Inner.SelectWork(PairId, Generation, new PathScope(fixture.Pair, [fixture.Pair]));
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)fixture.Inner.GetType().GetField("_connection",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Inner)!;
            var snapshots = 0;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, sql) =>
            {
                if (sql.utf8_to_string().StartsWith("SELECT path,subject_json,NULL ", StringComparison.Ordinal)) snapshots++;
            }), null);
            try
            {
                for (var repeat = 0; repeat < repetitions; repeat++)
                    Check.Require(fixture.Inner.CreateLocalEchoProbe(selected)(saved, selected.ThroughWorkId),
                        "90a finite pass reuses responsibility snapshot");
                Check.Require(snapshots == 1, "90a responsibility streams once per finite pass");
                fixture.Inner.EndWorkPass(PairId);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Raise =
                    [(IssueKind.Info, TextKey.InfoNewestWins, Item, new IssueSubject([], null))] });
                fixture.Inner.SelectWork(PairId, Generation, new PathScope(fixture.Pair, [fixture.Pair]));
                Check.Require(!fixture.Inner.CreateLocalEchoProbe(selected)(saved, selected.ThroughWorkId),
                    "90a the next pass observes newly raised responsibility");
                Check.Require(snapshots == 2, "90a next pass refreshes responsibility snapshot");
                Console.WriteLine("90a snapshot openIssues=" + count + "; firstPassCalls=" + repetitions +
                    "; passes=2; snapshots=" + snapshots);
            }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
        }
    }

    private static async Task RunAsync()
    {
        await LocalEchoCasesAsync();
        await LocalEchoResponsibilityAsync();
        await LocalEchoReadFailureAsync();
        await LocalEchoFolderAsync();
        LocalEchoIssueCounts();
        await EmptyOpportunityAsync();
        await SelectedReadFailuresAsync();
        await ObservationReadOutcomesAsync();
        await GenerationForwardedAsync();
        await StaleGenerationRefusesAsync();
        await BeginSelectionBeforeCaptureAsync();
        await RetainedIssueAsync();
        await ProviderFreeNoticeRetainsAsync();
        await OccupantReadFailureStillRetainsAsync();
        await CommitFailureDoesNotRetainAsync();
        await AttachmentFailureRetainsParentAsync();
        await WrongSelectionRefusesAsync();
        await HeldBranchAttachesBeforeAdmissionAsync(closeBeforeAttach: false);
        await HeldBranchAttachesBeforeAdmissionAsync(closeBeforeAttach: true);
        RunMappedHolds();
        foreach (var rootState in new[] { RootState.Same, RootState.Replaced, RootState.Absent, RootState.Unreadable })
            await RetainedAncestorIdentityAsync(rootState);
        await PostRenameFailureRetainsAdvancedBeforeAsync();
        await OldAliasFailureRetainsAdvancedBeforeAsync();
        await EmptyBranchPreservesLaterArrivalAsync();
        await SeparateWalkWorkAsync();
        await OwnSelectionFenceAsync();
        await CounterpartAncestorFirstAsync();
        await RefusalMakesNoProviderCallsAsync();
        await FailedAncestorNoticeAsync();
        await FolderDeletionCoversItsChildrenAsync();
        var moveOutFailures = new List<Exception>();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
            try { await MovedOutOfDeletedFolderAsync(side); }
            catch (Exception error) { moveOutFailures.Add(error); }
        try { await FolderDeletionCoversItsChildrenAsync(refused: true); }
        catch (Exception error) { moveOutFailures.Add(error); }
        if (moveOutFailures.Count != 0) throw new AggregateException(moveOutFailures);
        await MappedAncestorFirstAsync();
        await OldPathAncestorFirstAsync();
        await DeletedDestinationFolderAsync();
        await NestedAncestorOrderAsync();
        await DependencyReadCountAsync();
        await UnreadableReleaseAsync();
        await WrappedWalkFailureAsync();
        await OrdinaryWalkFailureAsync();
        await FolderSurvivorFailureIdentityAsync();
        await LiveReobserveRevokesDirectAsync();
    }

    private static async Task EmptyOpportunityAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var store = new StoreCounters(fixture.Inner);
        var local = new CountingSide(fixture.Local);
        var remote = new CountingSide(fixture.Dropbox);
        var events = new List<RunEvent>();
        var opportunity = new WorkSelection(PairId, Generation, 0, Side.Local, VolumeKey, null,
            string.Empty, 0, 0);
        var context = fixture.Context(opportunity) with
        { Store = store, Local = local, Dropbox = remote, Events = events.Add };
        var syncer = new Syncer(context, new SyncPorts(new RecoveryBoundary(),
            RefusingExecutionBoundary.Instance, RefusingExecutionBoundary.Instance, 0));
        await syncer.SyncWorkAsync(default);
        Check.Require(store.MemberCalls.GetValueOrDefault(nameof(IStateStore.FirstRepairDone)) == 0 &&
            store.MemberCalls.GetValueOrDefault(nameof(IStateStore.SelectWork)) == 1 &&
            store.MemberCalls.GetValueOrDefault(nameof(IStateStore.BeginSelection)) == 0,
            "an empty opportunity selects once without reading FirstRepairDone or starting a selection");
        Check.Require(local.Reads + local.Listings + local.Opens + local.Hashes +
            remote.Reads + remote.Listings + remote.Opens + remote.Hashes == 0,
            "an empty opportunity performs no provider observation");
        Check.Require(events.Count(value => value.StageKey == TextKey.StageRows) == 2 &&
            events.Single(value => value.StageKey == TextKey.StageRows && value.Ended).Count == 0,
            "an empty opportunity closes its one row stage with zero completed branches");
    }

    // A live selection runs with no work scope of its own, so an unrelated sole single-file scope is the only way
    // SelectDirect could turn true; the reobserve turn must withdraw it so the replacement takes the session route.
    private static async Task LiveReobserveRevokesDirectAsync()
    {
        using var fixture = new Fixture();
        var network = new FakeNetworkSignal();
        network.SetAvailable(true);
        // This path never backs off; the values only satisfy the limiter's constructor.
        var limiter = new AdaptiveLimiter(1, 1, TimeProvider.System, network, _ => { },
            new FailureRetryOwner.BackoffSettings(1, 1, 0, 1));
        var folder = FolderEntry(Folder, FolderId);
        fixture.Local.Seed(folder, null);
        fixture.Dropbox.Seed(folder, null);
        await using var unrelated = await limiter.EnterWorkAsync(true, default);
        Check.Require(limiter.SelectDirect(1),
            "An unrelated sole single-file scope would otherwise let the replacement stage direct.");
        var selection = Selected(fixture, Folder, folder);
        var ready = await Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => Task.FromResult(Coverage(selection)), limiter).RunAsync(selection, default);
        Check.Require(ready, "The live reobserve completes.");
        Check.Require(!limiter.SelectDirect(1),
            "A live reobserve withdraws direct while an unrelated sole scope is outstanding.");
    }

    // Engine 8.7: descendants stream through the ordinary path, so a selection never refuses on its own work; a later
    // arrival at its path, or another selection's pending ancestor, still does.
    private static async Task SeparateWalkWorkAsync()
    {
        using var fixture = UnreadableFixture(withMemberFolder: false);
        var folder = FolderEntry(Folder, FolderId);
        var parent = Selected(fixture, Folder, folder);
        var child = Selected(fixture, Plain, LocalEntry(Plain, LocalId), page: 2);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, Folder,
                new IssueSubject([new IssueItem(Side.Local, Folder, FolderId)], null))]
        });
        Check.Require(await RunSelectionAsync(fixture, parent), "parent attaches its own held residual and completes");
        Check.Require(fixture.Inner.ReadWork(child).Single().Before is null,
            "a separately acquired walked child retains its own preimage while parent is held");
        fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(Folder, IssueClosure.AfterEffect)] });
        var released = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, FolderId).Single();
        Check.Require(await RunSelectionAsync(fixture, released), "released parent does not claim separately acquired child work");
        Check.Require(await RunSelectionAsync(fixture, child) && await SyncedAsync(fixture, Plain),
            "separately acquired child progresses after held parent releases");
    }

    private static async Task OwnSelectionFenceAsync()
    {
        var folder = FolderEntry(Folder, FolderId);
        using (var fixture = UnreadableFixture(withMemberFolder: false))
        {
            Check.Require(await RunNoticeAsync(fixture, Folder, folder) && await SyncedAsync(fixture, Plain),
                "A folder selection's child passes the fence on the selection's own work");
            var queries = fixture.Hooks.AncestorQueries;
            Check.Require(queries.Any(query => query.Through is not null) && queries.Any(query => query.Count == 0),
                "The fixture exercises both own-only and empty ancestor answers.");
            for (var index = 0; index < queries.Count; index++)
                if (queries[index].Through is not null)
                    Check.Require(index > 0 && queries[index - 1].Count > 0 &&
                        queries[index - 1].Side == queries[index].Side,
                        "Only a nonempty own-ancestor answer warrants a later-work query.");
        }
        using (var fixture = UnreadableFixture(withMemberFolder: false))
        {
            var selection = Selected(fixture, Folder, folder);
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, FolderId, folder, false, false), 2);
            var refused = await ThrowsAsync<LiveAdmissionStaleException>(() => RunSelectionAsync(fixture, selection));
            Check.Require(refused.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
                !await SyncedAsync(fixture, Plain), "Work arriving after the selection still refuses its descendants");
            var owners = fixture.Inner.PendingAncestors(PairId, Generation, Side.Local, [selection.Key]).ToArray();
            Check.Require(owners.Any(owner => owner.AnchorWorkId > selection.ThroughWorkId) &&
                fixture.Hooks.AncestorQueries.Any(query => query.Through is null && query.Count > 0),
                "After the own Record advances its preimage, the later arrival is a separate owner and the first query refuses it.");
        }
        using (var fixture = UnreadableFixture(withMemberFolder: false))
        {
            Selected(fixture, Folder, folder);
            var child = LocalEntry(Plain, LocalId);
            var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Plain, null, child.Id, child, false, false), 2);
            await ThrowsAsync<LiveAdmissionStaleException>(() => RunSelectionAsync(fixture, selection));
            Check.Require(!await SyncedAsync(fixture, Plain), "Another selection's pending ancestor still refuses");
            Check.Require(fixture.Hooks.AncestorQueries.All(query => query.Through is null),
                "A foreign anchor refuses without a second query.");
        }
    }

    private const string Member = Folder + "/member";
    private const string Plain = Folder + "/plain";
    private const string FolderId = "1";
    private const string MemberId = "2";

    // Bug 108: the fence checks both sides, so the ancestor must be run from both. The Local children sort ahead of
    // the Dropbox folder row by work id; each selection pre-runs that counterpart row before its own branch.
    private static async Task CounterpartAncestorFirstAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
        fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
        var children = new[] { Folder + "/a", Folder + "/b", Folder + "/c" };
        var selections = new List<WorkSelection>();
        for (var index = 0; index < children.Length; index++)
        {
            var entry = LocalEntry(children[index], "local-" + index);
            fixture.Local.Seed(entry, [(byte)index]);
            selections.Add(Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, children[index], null,
                entry.Id, entry, false, false), index + 1));
        }
        SeedRemote(fixture, FolderEntry(Folder, DropboxId));
        Check.Require(PendingAt(fixture, Side.Dropbox, Folder).Any(),
            "A108a: the fixture holds a pending counterpart-side ancestor");
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, selections[0], new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(events.All(item => item.Error is not LiveAdmissionStaleException),
            "A108a: a counterpart-side ancestor leaves no refusal behind");
        var firstAncestor = events.FindIndex(item => item.Path == Folder);
        var firstChild = events.FindIndex(item => item.Path == children[0]);
        Check.Require(firstAncestor >= 0 && firstChild > firstAncestor,
            "A108a: the counterpart-side ancestor runs before its children; ancestor=" + firstAncestor +
            ", child=" + firstChild + "; paths=" + string.Join(",", events.Where(item => item.Path.Length > 0)
                .Select(item => item.Kind + ":" + item.Path)));
        foreach (var child in children)
            Check.Require(await fixture.Dropbox.ReadOneAsync(child, default) is not null,
                "A108a: every child of the pre-run ancestor syncs, " + child);
        Check.Require(!PendingAt(fixture, Side.Dropbox, Folder).Any(),
            "A108a: the counterpart-side ancestor's own work retires");
    }

    // A fresh real store owes a source obligation; clearing both sides is what lets SelectWork offer seeded work.
    private static void ClearObligations(Fixture fixture)
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var source = fixture.Inner.ReadSource(PairId, side)!.Value;
            fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance,
                source.Cursor, null);
        }
    }

    // The fence now refuses a live branch before its observation, so a refusal costs no provider call.
    private static async Task RefusalMakesNoProviderCallsAsync()
    {
        using var fixture = new Fixture(realStore: true);
        fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
        var child = LocalEntry(Plain, LocalId);
        fixture.Local.Seed(child, [1]);
        var selection = Selected(fixture, Plain, child);
        fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
        SeedRemote(fixture, FolderEntry(Folder, DropboxId));
        var local = new CountingSide(fixture.Local);
        var remote = new CountingSide(fixture.Dropbox);
        fixture.LocalView = local;
        fixture.DropboxView = remote;
        var error = await ThrowsAsync<LiveAdmissionStaleException>(() =>
            RunSelectionAsync(fixture, selection));
        Check.Require(error.Reason == LiveAdmissionStaleReason.RelevantEvidenceChanged &&
            error.Detail?.Contains(PathNames.Key(Folder), StringComparison.Ordinal) == true,
            "A108b: the refusal names the blocking ancestor key");
        Check.Require(local.Reads + remote.Reads + local.Listings + remote.Listings +
            local.Work + remote.Work == 0,
            "A108b: a refused branch makes no provider call");
    }

    // An ancestor that fails leaves the user one deduplicated notice; the child is neither lost nor silently stuck.
    private static async Task FailedAncestorNoticeAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
        var child = LocalEntry(Plain, LocalId);
        fixture.Local.Seed(child, [1]);
        var selection = Selected(fixture, Plain, child);
        fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
        SeedRemote(fixture, FolderEntry(Folder, DropboxId));
        fixture.Dropbox.FailNext(Folder, new ItemFailedException(Folder, TextKey.FailDropboxNoPermission, null));
        var events = new List<RunEvent>();
        var boundary = new Boundary(_ => new Lease());
        await LiveSyncer(fixture, selection, boundary, events).SyncWorkAsync(default);
        await LiveSyncer(fixture, selection, boundary, events).SyncWorkAsync(default);
        var notices = fixture.Inner.Issues(PairId, [IssueKind.Failure]).Where(issue => issue.Path == Folder).ToArray();
        Check.Require(notices.Length == 1,
            "A108c: a failed ancestor leaves exactly one deduplicated notice; found " + notices.Length + " at " +
            string.Join(",", fixture.Inner.Issues(PairId, [IssueKind.Failure]).Select(issue => issue.Path + "/" + issue.Cause)));
        var synced = await SyncedAsync(fixture, Plain);
        var held = fixture.Inner.HeldCovers(PairId, PathNames.Key(Plain), null);
        var retained = PendingAt(fixture, Side.Local, Plain).Any();
        Check.Require(!synced && held && retained,
            "A108c: the child waits on that notice with its work retained; synced=" + synced + ", held=" + held +
            ", retained=" + retained);
    }

    // #109: a pending folder deletion is not reordered behind its children; it runs the survivor-aware folder plan,
    // which covers the descendants' pending evidence. The plan keeps changed content and removes unchanged content.
    private static async Task MovedOutOfDeletedFolderAsync(Side side)
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var localFolder = FolderEntry("Folder", "local-folder");
        var remoteFolder = FolderEntry("Folder", "remote-folder");
        var local = LocalEntry("Folder/File", LocalId);
        var remote = RemoteEntry("Folder/File", DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(localFolder, remoteFolder), SyncedEntries.From(local, remote)] });
        fixture.Local.Seed(side == Side.Local ? local with { Path = "Other/File" } : local, [1]);
        fixture.Dropbox.Seed(side == Side.Dropbox ? remote with { Path = "Other/File" } : remote, [1]);
        (side == Side.Local ? fixture.Dropbox : fixture.Local).Seed(side == Side.Local ? remoteFolder : localFolder, null);
        (side == Side.Local ? fixture.Local : fixture.Dropbox).Seed(FolderEntry("Other", "other-folder"), null);
        var entry = side == Side.Local ? local : remote;
        var notices = new[]
        {
            new ChangeNotice(side, ChangeKind.Renamed, "Other/File", "Folder/File", entry.Id,
                entry with { Path = "Other/File" }, false, false),
            new ChangeNotice(side, ChangeKind.Deleted, "Folder", null,
                side == Side.Local ? localFolder.Id : remoteFolder.Id, null, false, true)
        };
        WorkSelection selection;
        if (side == Side.Local)
        {
            selection = Seed(fixture, notices[0]);
            Seed(fixture, notices[1], 2);
        }
        else
        {
            var localSource = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
            fixture.Inner.AcquireJournalPage(VolumeKey, null, new JournalPosition(1, 1),
                [(PairId, Generation, localSource.Epoch, localSource.Revision, false, false)], []);
            selection = SeedRemote(fixture, entry with { Path = "Other/File" });
            var source = fixture.Inner.ReadSource(PairId, side)!.Value;
            fixture.Inner.AcquireDropboxPage(PairId, Generation, source.Epoch, source.Revision,
                source.Provenance!, source.Cursor, notices, Cursor);
        }
        var events = new List<RunEvent>();
        var boundary = new Boundary(request =>
        {
            fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
            return new Lease { AllowsRelease = true };
        });
        await LiveSyncer(fixture, selection, boundary, events,
            new FakeRecovery { Evidence = new DriveEvidence(1, 1, 1) }).SyncWorkAsync(default);
        Check.Require(!PendingAt(fixture, side, "Folder", "Folder/File", "Other/File").Any(),
            $"move-out {side}: both selections retire; " + string.Join(" | ", events.Select(e =>
                e.Path + ":" + e.Error + ":" + (e.Error as LiveAdmissionStaleException)?.Detail)) +
            "; issues=" + string.Join(" | ", fixture.Inner.Issues(PairId).Select(issue => issue.Path + ":" + issue.Cause)));
        foreach (var endpoint in new[] { fixture.Local, fixture.Dropbox })
        {
            var survivor = await endpoint.ReadOneAsync("Other/File", default);
            Check.Require(survivor is not null && await endpoint.ContentHashAsync(survivor, default) == remote.ContentHash &&
                await endpoint.ReadOneAsync("Folder", default) is null,
                $"move-out {side}: original bytes survive at new path on {endpoint.Side}, deleted folder stays gone");
        }
    }

    private static async Task FolderDeletionCoversItsChildrenAsync(bool refused = false)
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var remoteFolder = FolderEntry(Folder, DropboxId);
        fixture.Dropbox.Seed(remoteFolder, null);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(FolderEntry(Folder, FolderId), remoteFolder)] });
        var edited = Folder + "/edited";
        var unchanged = Folder + "/unchanged";
        var editedRemote = RemoteEntry(edited, "remote-edited");
        fixture.Inner.Commit(PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(LocalEntry(edited, "local-edited"), editedRemote)] });
        var editedNow = editedRemote with
        {
            Size = 2, Modified = Stamp.AddHours(1),
            ContentHash = DropboxContentHash.Compute(new byte[] { 9, 9 })
        };
        fixture.Dropbox.Seed(editedNow, [9, 9]);
        var siblingRemote = RemoteEntry(unchanged, "remote-sibling");
        fixture.Inner.Commit(PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(LocalEntry(unchanged, "local-sibling"), siblingRemote)] });
        fixture.Dropbox.Seed(siblingRemote, [1]);
        if (refused) fixture.DropboxView = new HookedSide(fixture.Dropbox, refuseRemoval: true);
        // Only the folder and the edited child have work: the unchanged sibling has no row of its own.
        var child = SeedRemote(fixture, editedNow);
        Seed(fixture, Deleting(Folder, FolderId), 1);
        var events = new List<RunEvent>();
        var boundary = new Boundary(request =>
        {
            fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
            return new Lease { AllowsRelease = true };
        });
        await LiveSyncer(fixture, child, boundary, events).SyncWorkAsync(default);
        if (refused)
        {
            Check.Require(PendingAt(fixture, Side.Local, Folder).Any(), "refused member retains folder work");
            var notices = fixture.Inner.Issues(PairId, [IssueKind.Failure]).ToArray();
            Check.Require(notices.Length == 1, "refused member raises one notice; measured=" + notices.Length);
            Check.Require(await fixture.Dropbox.ReadOneAsync(unchanged, default) is not null,
                "refused member leaves unchanged bytes untouched before retry");
            fixture.DropboxView = null;
            fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(Folder, new[] { IssueKind.Failure })] });
            await LiveSyncer(fixture, child, boundary, events).SyncWorkAsync(default);
        }
        Check.Require(await fixture.Dropbox.ReadOneAsync(edited, default) is not null &&
            await fixture.Local.ReadOneAsync(edited, default) is not null,
            "A109: the edited child survives on both sides");
        Check.Require(await fixture.Dropbox.ReadOneAsync(unchanged, default) is null,
            "A109: the unchanged sibling with no row of its own is deleted on Dropbox");
        Check.Require(!PendingAt(fixture, Side.Local, Folder).Any(),
            "A109: the folder selection retires; failures=" + string.Join(" | ", events
                .Where(item => item.Error is not null).Select(item => item.Path + ":" + item.Error + ":" +
                    (item.Error as LiveAdmissionStaleException)?.Detail)));
        events.Clear();
        await LiveSyncer(fixture, child, boundary, events).SyncWorkAsync(default);
        Check.Require(events.All(item => item.Error is not LiveAdmissionStaleException),
            "A109: the next pass has zero stale refusals");
        Check.Require(events.All(item => item.EffectKind is null) &&
            await fixture.Dropbox.ReadOneAsync(unchanged, default) is null &&
            await fixture.Dropbox.ReadOneAsync(edited, default) is not null,
            "A109: nothing comes back and nothing is restored on the next pass");
    }

    // #108 finding 1: the pre-run uses the fence's whole dependency key set, so a mapped counterpart ancestor and a
    // two-level chain of blockers run before the item instead of refusing it forever.
    private static async Task MappedAncestorFirstAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        fixture.Local.Seed(FolderEntry("A", "local-a"), null);
        fixture.Dropbox.Seed(FolderEntry("B", "remote-b"), null);
        // The folder mapping Local A <-> Dropbox B is saved, so the child's destination maps through it.
        fixture.Inner.Commit(PairId, StateChange.Empty with
        { Record = [SyncedEntries.From(FolderEntry("A", "local-a"), FolderEntry("B", "remote-b"))] });
        var local = LocalEntry("A/C", "local-c");
        fixture.Local.Seed(local, [1]);
        var mapped = new SyncedEntry(PathNames.Key("A/C"), "A/C", PathNames.DropboxKey("B/C"), "B/C", false,
            local.Id, 1, Stamp.UtcTicks, "remote-c", DropboxContentHash.Compute(new byte[] { 1 }));
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [mapped] });
        var child = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, "A/C", null, local.Id,
            local, false, false));
        SeedRemote(fixture, FolderEntry("B", "remote-b"));
        var localCalls = new CountingSide(fixture.Local);
        var remoteCalls = new CountingSide(fixture.Dropbox);
        fixture.LocalView = localCalls;
        fixture.DropboxView = remoteCalls;
        await ThrowsAsync<LiveAdmissionStaleException>(() => RunSelectionAsync(fixture, child));
        Check.Require(localCalls.Reads + remoteCalls.Reads + localCalls.Listings + remoteCalls.Listings +
            localCalls.Work + remoteCalls.Work == 0, "A108: mapped refusal makes zero provider calls");
        fixture.Inner.EndWorkPass(PairId);
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, child, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        var refused = events.Select(item => item.Error).OfType<LiveAdmissionStaleException>().FirstOrDefault();
        Check.Require(refused is null, "A108a: the mapped counterpart ancestor is run, not refused; " +
            refused?.Detail + "; trail=" + string.Join(" | ", events.Where(item => item.Path.Length > 0)
                .Select(item => item.Kind + ":" + item.Path + ":" + item.EffectKind)));
        var ancestorAt = events.FindIndex(item => item.Path == "B");
        var childAt = events.FindIndex(item => item.Path == "A/C");
        Check.Require(ancestorAt >= 0 && childAt > ancestorAt, "A108a: the mapped ancestor runs before its child");
        Check.Require(await fixture.Dropbox.ReadOneAsync("B", default) is not null,
            "A108a: the mapped ancestor was executed, not only observed");
    }

    private static async Task DeletedDestinationFolderAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var old = LocalEntry("Old", LocalId);
        var remote = RemoteEntry("Old", DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(old, remote)] });
        fixture.Local.Seed(FolderEntry("Target", FolderId), null);
        var moved = old with { Path = "Target/New" };
        fixture.Local.Seed(moved, [1]);
        fixture.Dropbox.Seed(remote, [1]);
        var folder = SeedRemote(fixture, FolderEntry("Target", "deleted-folder"));
        var source = fixture.Inner.ReadSource(PairId, Side.Dropbox)!.Value;
        var through = fixture.Inner.AcquireDropboxPage(PairId, Generation, source.Epoch, source.Revision,
            source.Provenance!, source.Cursor,
            [new ChangeNotice(Side.Dropbox, ChangeKind.Deleted, "Target", null, "deleted-folder", null, false, true)], Cursor);
        folder = folder with { ThroughWorkId = through };
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, moved.Path, "Old", moved.Id,
            moved, false, false));
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, folder, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(await fixture.Dropbox.ReadOneAsync("Target/New", default) is not null &&
            await fixture.Dropbox.ReadOneAsync("Old", default) is null &&
            await fixture.Local.ReadOneAsync("Target/New", default) is not null &&
            !PendingAt(fixture, Side.Dropbox, "Target").Any() &&
            events.All(item => item.Error is not LiveAdmissionStaleException),
            "CL03: folder selection drains before the incoming rename, preserving final contents");
    }

    private static async Task OldPathAncestorFirstAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var entry = LocalEntry("New/Child", LocalId);
        fixture.Local.Seed(FolderEntry("New", FolderId), null);
        fixture.Local.Seed(entry, [1]);
        var child = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, entry.Path,
            "Old/Child", entry.Id, entry, false, false));
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, "Earlier", null,
            "old-folder", FolderEntry("Earlier", "old-folder"), false, false), 2);
        // A later identity member is filtered by SelectWork; the exact old-path probe resolves its owner.
        Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, "Old", null,
            "old-folder", FolderEntry("Old", "old-folder"), false, false), 3);
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, child, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        Check.Require(!PendingAt(fixture, Side.Local, "Old", "Earlier").Any() &&
            events.FindIndex(item => item.Path == "Earlier") >= 0 &&
            events.FindIndex(item => item.Path == "Earlier") < events.FindIndex(item => item.Path == "New/Child") &&
            events.All(item => item.Error is not LiveAdmissionStaleException),
            "A108: old-path owner retires before the renamed child without stale refusal");
    }

    private static async Task DependencyReadCountAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        const string path = "A/B/C/D/E/F/File";
        var entry = LocalEntry(path, LocalId);
        fixture.Local.Seed(entry, [1]);
        var selected = Selected(fixture, path, entry);
        var syncer = LiveSyncer(fixture, selected, new Boundary(_ => new Lease()), []);
        var before = fixture.Hooks.Count(nameof(IStateStore.ReadWork));
        var probe = typeof(Syncer).GetMethod("PendingAncestor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check.Require(probe.Invoke(syncer, [selected]) is null, "depth-six fixture has no pending ancestor");
        var reads = fixture.Hooks.Count(nameof(IStateStore.ReadWork)) - before;
        Console.WriteLine($"ancestor-prerun depth=6 selections=1 ReadWork={reads}");
        Check.Require(reads == 1, "depth-six pre-run reads its selection once; measured=" + reads);
        await Task.CompletedTask;
    }

    private static async Task NestedAncestorOrderAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        fixture.Local.Seed(FolderEntry("A", "local-a"), null);
        fixture.Dropbox.Seed(FolderEntry("A", "remote-a"), null);
        fixture.Dropbox.Seed(FolderEntry("A/B", "remote-ab"), null);
        var local = LocalEntry("A/B/C", "local-c");
        fixture.Local.Seed(local, [1]);
        var child = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, "A/B/C", null, local.Id,
            local, false, false));
        SeedRemote(fixture, FolderEntry("A/B", "remote-ab"));
        SeedRemote(fixture, FolderEntry("A", "remote-a"));
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, child, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
        var nestedRefused = events.Select(item => item.Error).OfType<LiveAdmissionStaleException>().FirstOrDefault();
        Check.Require(nestedRefused is null, "A108a: both nested ancestors are run, not refused; " +
            nestedRefused?.Detail + "; trail=" + string.Join(" | ", events.Where(item => item.Path.Length > 0)
                .Select(item => item.Kind + ":" + item.Path + ":" + item.EffectKind)));
        var outer = events.FindIndex(item => item.Path == "A");
        var inner = events.FindIndex(item => item.Path == "A/B");
        var leaf = events.FindIndex(item => item.Path == "A/B/C");
        Check.Require(outer >= 0 && inner > outer && leaf > inner,
            "A108a: nested ancestors resolve outside-in before the child");
        Check.Require(await fixture.Dropbox.ReadOneAsync("A/B/C", default) is not null,
            "A108a: the nested child syncs");
    }

    private static ChangeNotice Deleting(string path, string id) =>
        new(Side.Local, ChangeKind.Deleted, path, null, id, null, false, true);

    private static Syncer LiveSyncer(Fixture fixture, WorkSelection selection, Boundary boundary,
        List<RunEvent> events, FakeRecovery? recovery = null)
    {
        boundary.Capture = (chosen, _, _) => Task.FromResult(Coverage(chosen));
        var context = fixture.Context(selection) with
        {
            Events = events.Add,
            // A live folder removal records its own Dropbox window before the first arm.
            CaptureFolderCursorAsync = _ => Task.FromResult(Cursor),
            ReadFolderChangesAsync = (_, ct) => NoFolderChangesAsync(ct)
        };
        if (recovery is not null) context = context with { Recovery = recovery };
        return new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 0));
    }

    private static async IAsyncEnumerable<Listed> NoFolderChangesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    private static Fixture UnreadableFixture(bool withMemberFolder = true)
    {
        var fixture = new Fixture(realStore: true);
        fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
        fixture.Local.Seed(LocalEntry(Plain, LocalId), [1]);
        if (withMemberFolder) fixture.Local.Seed(FolderEntry(Member, MemberId), null);
        return fixture;
    }

    private static async Task<bool> RunNoticeAsync(Fixture fixture, string path, Entry entry, CancellationToken ct = default,
        long page = 1)
    {
        return await RunSelectionAsync(fixture, Selected(fixture, path, entry, page), ct);
    }

    private static WorkSelection Selected(Fixture fixture, string path, Entry entry, long page = 1)
    {
        return Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, path, null, entry.Id, entry, false, false), page);
    }

    private static Task<bool> RunSelectionAsync(Fixture fixture, WorkSelection selection, CancellationToken ct = default) =>
        Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => Task.FromResult(Coverage(selection))).RunAsync(selection, ct);

    private static async Task FolderSurvivorFailureIdentityAsync()
    {
        foreach (var deletedSide in new[] { Side.Dropbox, Side.Local })
        {
            using var fixture = new Fixture(realStore: true);
            SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
            var localFolder = FolderEntry(Folder, FolderId);
            var remoteFolder = FolderEntry(Folder, DropboxId);
            var child = LocalEntry(Plain, LocalId);
            var remoteChild = RemoteEntry(Plain, "remote-child");
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Record = [SyncedEntries.From(localFolder, remoteFolder), SyncedEntries.From(child, remoteChild)]
            });
            var survivorSide = deletedSide == Side.Dropbox ? fixture.Local : fixture.Dropbox;
            survivorSide.Seed(deletedSide == Side.Dropbox ? localFolder : remoteFolder, null);
            survivorSide.Seed((deletedSide == Side.Dropbox ? child : remoteChild) with
                { Size = 2, ContentHash = DropboxContentHash.Compute(new byte[] { 2, 2 }) }, [2, 2]);
            var failedDestination = new ProxySide(deletedSide == Side.Dropbox ? fixture.Dropbox : fixture.Local)
            {
                BlockPublish = (_, _) => throw new ItemFailedException(Plain, TextKey.FailChanged, null)
            };
            if (deletedSide == Side.Dropbox) fixture.DropboxView = failedDestination;
            else fixture.LocalView = failedDestination;
            var selection = deletedSide == Side.Dropbox ? SeedRemote(fixture, remoteFolder) :
                Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Deleted, Folder, null, FolderId, null, false, true), page: 2);
            await RunSelectionAsync(fixture, selection);
            var last = fixture.Inner.ReadWorkWatermark();
            Check.Require(last > selection.ThroughWorkId, "F1: failed survivor actually registers new durable work");
            var residual = selection with { Identity = null, Key = PathNames.KeyFor(selection.Side, Plain),
                AnchorWorkId = last, ThroughWorkId = last };
            var work = fixture.Inner.ReadWork(residual).Single();
            Check.Require(work.Notice.Path == Plain && work.Notice.Side == selection.Side &&
                work.Notice.Id is null && work.Notice.Observed is null && work.Notice.Kind == ChangeKind.Changed && !work.Notice.IdentityDeleted,
                "F1/M4: survivor is reobserved at its counterpart path on the parent's source, never with the folder identity");
        }
    }

    private static async Task OrdinaryWalkFailureAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var fixture = new Fixture(realStore: true);
            var folder = FolderEntry(Folder, FolderId);
            var endpoint = side == Side.Local ? fixture.Local : fixture.Dropbox;
            var child = side == Side.Local ? LocalEntry(Plain, LocalId) : RemoteEntry(Plain, DropboxId);
            var cause = side == Side.Local ? TextKey.ReadLocalFailed : TextKey.ReadDropboxFailed;
            endpoint.Seed(folder, null);
            endpoint.Seed(child, [1]);
            endpoint.FailWalkAfter(Plain, new InventoryIncompleteException("fixture", new ItemFailedException(Folder, cause, null)));
            var selection = side == Side.Local
                ? Seed(fixture, new ChangeNotice(side, ChangeKind.Changed, Folder, null, FolderId, folder, false, false))
                : SeedRemote(fixture, folder);
            await RunSelectionAsync(fixture, selection);
            Check.Require(fixture.Inner.Issues(PairId).Any(issue => issue.Path == Folder && issue.Cause == cause) &&
                fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) > 0,
                "H5: ordinary walk failure retains exact provider cause and work instead of stopping the host: " + side);
        }
    }

    private static async Task WrappedWalkFailureAsync()
    {
        foreach (var peer in new[] { false, true })
        {
            using var fixture = UnreadableFixture();
            var folder = FolderEntry(Folder, FolderId);
            if (peer)
            {
                var remote = FolderEntry(Folder, DropboxId);
                fixture.Dropbox.Seed(remote, null);
                SeedRemote(fixture, remote);
            }
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            var failure = new InventoryIncompleteException("fixture",
                new ItemFailedException(Folder, TextKey.MoveFailed, null));
            if (peer)
                fixture.LocalView = new ProxySide(fixture.Local) { ListingGate = () => throw failure };
            else fixture.Local.FailWalkAfter(Plain, failure);
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(Touches(fixture, Member) && fixture.Inner.Issues(PairId).Any(issue =>
                issue.Path == Folder && issue.Cause == TextKey.MoveFailed),
                "AJ-05: retained enumeration failure preserves the typed provider cause and unreadable protection.");
            Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) > 0,
                "RP-01: failed walk retains its Failure even when opposite-side work owns the path");
        }
    }

    private static bool Touches(Fixture fixture, string path) =>
        fixture.Inner.UnreadableTouches(PairId, PathNames.Key(path), false);

    private static async Task<bool> SyncedAsync(Fixture fixture, string path) =>
        await fixture.Dropbox.ReadOneAsync(path, default) is not null;

    // Rev3b section 5 and rev3d: release through production selection and store, doubles truly walking.
    private static async Task UnreadableReleaseAsync()
    {
        var rootPath = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(rootPath, Member));
            await File.WriteAllTextAsync(Path.Combine(rootPath, Plain), "content");
            using var fixture = new Fixture(realStore: true, localRoot: rootPath);
            var rootId = WindowsDirectoryListing.List(rootPath).Single().FileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var selection = Selected(fixture, Folder, FolderEntry(Folder, rootId));
            var local = new Massdex.SyncShark.Engine.Local.LocalSide(fixture.Pair,
                new PathScope(fixture.Pair, [fixture.Pair]), fixture.Context(selection).Settings,
                new PendingNotes(fixture.Inner, fixture.Pair.PairId, 100, mutation: false),
                rootPath, (_, _, _, _) => throw new NotSupportedException());
            var member = (await local.ReadOneAsync(Member, default))!;
            UnreadableMember.Record(fixture.Inner, PairId, Member, member.Id);
            fixture.LocalView = new HookedSide(local, failDisposal: true);
            await RunSelectionAsync(fixture, selection);
            Check.Require(Touches(fixture, Member) && await SyncedAsync(fixture, Plain) &&
                fixture.Inner.Issues(PairId).Any(issue => issue is { Kind: IssueKind.Failure, Path: Folder,
                    Cause: TextKey.ReadLocalFailed }),
                "Real Local acquisition followed by outer disposal failure retains the member and ordinary sibling progress.");
        }
        finally { Directory.Delete(rootPath, true); }
        var folder = FolderEntry(Folder, FolderId);
        // One and two recorded keys expose repeated scans; extra unmatched directories hold r fixed while d grows.
        foreach (var recorded in new[] { 1, 2 })
        foreach (var misses in new[] { 1, 2 })
        {
            using var fixture = UnreadableFixture(withMemberFolder: false);
            for (var index = 0; index < recorded + misses; index++)
            {
                var path = Folder + "/directory" + index;
                fixture.Local.Seed(FolderEntry(path, "directory" + index), null);
                if (index < recorded) UnreadableMember.Record(fixture.Inner, PairId, path, "unmatched identity");
            }
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(fixture.Hooks.RecordedKeyVisits == recorded,
                $"Recorded membership is traversed once, not per callback: r={recorded}, misses={misses}, visits={fixture.Hooks.RecordedKeyVisits}.");
        }
        await fixtureCase(withMember: false, async fixture =>
        {
            // A47, A93: F recorded and enumerated: its member goes after the walk, children sync in the continuation.
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            Check.Require(await RunNoticeAsync(fixture, Folder, folder) && !Touches(fixture, Folder) &&
                await SyncedAsync(fixture, Plain) && fixture.Inner.Issues(PairId).Count == 0, "A47 release and sync");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A95: U protected, a ordinary: a syncs, U goes only on proof.
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            Check.Require(await RunNoticeAsync(fixture, Folder, folder) && !Touches(fixture, Member) &&
                await SyncedAsync(fixture, Plain), "A95 ordinary sibling and proved member");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A56: enumeration fails after a: member stays, a's completion stands, Failure on the walked folder.
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            fixture.Local.FailWalkAfter(Plain, new InventoryIncompleteException("fixture", null));
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(Touches(fixture, Member) && await SyncedAsync(fixture, Plain) &&
                fixture.Inner.Issues(PairId).Any(issue => issue is { Kind: IssueKind.Failure, Path: Folder,
                    Cause: TextKey.ReadLocalFailed }), "A56 failed walk releases nothing");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            fixture.LocalView = new HookedSide(fixture.Local, failDisposal: true);
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(Touches(fixture, Member) && await SyncedAsync(fixture, Plain) &&
                fixture.Inner.Issues(PairId).Any(issue => issue is { Kind: IssueKind.Failure, Path: Folder,
                    Cause: TextKey.ReadLocalFailed }), "Outer disposal failure retains protection and records the walked path.");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A57: cancellation stays cancellation and releases nothing.
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            var canceled = new OperationCanceledException();
            fixture.Local.FailWalkAfter(Plain, canceled);
            await ThrowsAsync<OperationCanceledException>(() => RunNoticeAsync(fixture, Folder, folder));
            Check.Require(Touches(fixture, Member), "A57 cancellation releases nothing");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A94: a link at the recorded path is not descended, so U stays while F's walk succeeds.
            fixture.Local.Seed(FolderEntry(Member, MemberId) with { Skip = nameof(TextKey.SkipLink) }, null);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(Touches(fixture, Member), "A94 an unopened directory proves nothing");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A96: root and descendant proved: both go, one continuation root.
            var counted = new CountingSide(fixture.Local);
            fixture.LocalView = counted;
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(!Touches(fixture, Folder) && !Touches(fixture, Member) && counted.Listings == 2,
                $"A96, A124: one walk plus one continuation, not one per member ({counted.Listings})");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A97: F proved under a still recorded P: F's member goes, its rows stay covered by P.
            fixture.Local.Seed(LocalEntry(Member + "/file", OccupantId), [1]);
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            await RunNoticeAsync(fixture, Member, FolderEntry(Member, MemberId));
            Check.Require(Touches(fixture, Folder) &&
                !fixture.Inner.UnreadableUnder(PairId, PathNames.Key(Folder)).Contains(PathNames.Key(Member)) &&
                !await SyncedAsync(fixture, Member + "/file"), "A97 an ancestor member keeps protecting");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A99, A124: a different item at the key: no release, no continuation, the row stays protected.
            var counted = new CountingSide(fixture.Local);
            fixture.LocalView = counted;
            UnreadableMember.Record(fixture.Inner, PairId, Member, "different");
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(Touches(fixture, Member) && counted.Listings == 1, "A99 a refused candidate has no continuation");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A48, A105, A106: a file notice under a member proves nothing: no effect, settled, notice stays, no growth.
            fixture.Local.Seed(LocalEntry(Member + "/file", OccupantId), [1]);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            foreach (var page in new long[] { 1, 2 })
                Check.Require(await RunNoticeAsync(fixture, Member + "/file", LocalEntry(Member + "/file", OccupantId), page: page) &&
                    Touches(fixture, Member) && !await SyncedAsync(fixture, Member + "/file") &&
                    fixture.Inner.Issues(PairId).Single().Kind == IssueKind.Skipped, "A48 no release from a file");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A11: release proves access only from a Local notice; a Dropbox notice at the same folder releases nothing.
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            var seeded = FolderEntry(Folder, FolderId);
            fixture.Dropbox.Seed(seeded, null);
            await RunSelectionAsync(fixture, SeedRemote(fixture, seeded));
            Check.Require(Touches(fixture, Folder), "A11 a Dropbox notice releases no recorded Local member");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A102: the released root is re-listed, while its link member is never opened and stays recorded; a later
            // dirty arrival under that member is then retained by the point-of-use exact-membership ask.
            fixture.Local.Seed(FolderEntry(Member, MemberId) with { Skip = nameof(TextKey.SkipLink) }, null);
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            fixture.Local.Seed(LocalEntry(Member + "/dirty", OccupantId), [1]);
            Check.Require(await RunNoticeAsync(fixture, Folder, folder) && !Touches(fixture, Folder) &&
                Touches(fixture, Member), "A102 the continuation retains the unopened member");
            await RunNoticeAsync(fixture, Member + "/dirty", LocalEntry(Member + "/dirty", OccupantId), page: 2);
            Check.Require(Touches(fixture, Member) && !await SyncedAsync(fixture, Member + "/dirty"),
                "A102 a later dirty arrival under a recorded member is retained by exact membership");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A104: the continuation fails: the release stands, the Failure is attached, the parent keeps the rest.
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            fixture.Local.Seed(LocalEntry(Member + "/file", OccupantId), [1]);
            fixture.Local.FailWalkAfter(Member + "/file", new InventoryIncompleteException("fixture", null), earlierWalks: 1);
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(!Touches(fixture, Member) && await SyncedAsync(fixture, Plain) &&
                fixture.Inner.Issues(PairId).Any(issue => issue is { Kind: IssueKind.Failure, Path: Member }),
                "A104 a failed continuation keeps the proved release");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A122: cancelled after the first commit: it stays released, the replay is ordinary, the second goes then.
            UnreadableMember.Record(fixture.Inner, PairId, Folder, FolderId);
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            using var cancel = new CancellationTokenSource();
            fixture.Hooks.BeforeCall = method =>
            {
                if (method.Name == nameof(IStateStore.ReleaseUnreadable)) cancel.Cancel();
            };
            var selection = Selected(fixture, Folder, folder);
            await ThrowsAsync<OperationCanceledException>(() => RunSelectionAsync(fixture, selection, cancel.Token));
            fixture.Hooks.BeforeCall = null;
            Check.Require(fixture.Inner.UnreadableUnder(PairId, PathNames.Key(Folder)).Count == 1, "A122 one committed");
            Check.Require(await RunSelectionAsync(fixture, selection) &&
                fixture.Inner.UnreadableUnder(PairId, PathNames.Key(Folder)).Count == 0, "A122 replay finishes the rest");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A101: a crash after release and continuation, before CompleteWork: the parent is reselected, no repeat.
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            var crash = new InvalidOperationException(CommitFault);
            fixture.Hooks.BeforeCall = method =>
            {
                if (method.Name == nameof(IStateStore.CompleteWork)) throw crash;
            };
            var selection = Selected(fixture, Folder, folder);
            await ThrowsAsync<InvalidOperationException>(() => RunSelectionAsync(fixture, selection));
            fixture.Hooks.BeforeCall = null;
            var revision = (await fixture.Dropbox.ReadOneAsync(Plain, default))!.Revision;
            Check.Require(await RunSelectionAsync(fixture, selection) && !Touches(fixture, Member) &&
                (await fixture.Dropbox.ReadOneAsync(Plain, default))!.Revision == revision, "A101 no repeated effect");
        });
        await fixtureCase(withMember: true, async fixture =>
        {
            // A123. The dismissed visibility question is OPEN and held for the owner: this asserts only today's outcome,
            // it does not approve it. Dismissed, access recovered, no event, no Repair: the member stays, no effect, and
            // no open notice shows it. Then an ordinary relevant event releases it safely.
            UnreadableMember.Record(fixture.Inner, PairId, Member, MemberId);
            fixture.Inner.DismissIssue(PairId, fixture.Inner.Issues(PairId).Single().Id);
            Check.Require(Touches(fixture, Member) && fixture.Inner.Issues(PairId).All(issue => issue.Dismissed) &&
                !fixture.Inner.IssueCounts(PairId).ContainsKey(IssueKind.Skipped), "A123 present outcome, open question");
            await RunNoticeAsync(fixture, Folder, folder);
            Check.Require(!Touches(fixture, Member), "A123 an ordinary event releases a dismissed member");
        });

        static async Task fixtureCase(bool withMember, Func<Fixture, Task> check)
        {
            using var fixture = UnreadableFixture(withMember);
            await check(fixture);
        }
    }

    // A13, A64, A65: an existing ancestor hold takes the branch before any capture or lease; a holder that closed
    // first leaves it unfinished and reachable; closing the notice brings the attached work back.
    private static async Task HeldBranchAttachesBeforeAdmissionAsync(bool closeBeforeAttach)
    {
        using var fixture = new Fixture();
        var folder = FolderEntry(Folder, LocalId + "-folder");
        var child = LocalEntry(Folder + "/child", LocalId);
        fixture.Local.Seed(folder, null);
        fixture.Local.Seed(child, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, Folder,
                new IssueSubject([new IssueItem(Side.Local, Folder, folder.Id)], null))]
        });
        void Close() => fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Close = [(Folder, new[] { IssueKind.NameClash })]
        });
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, child.Path, null, child.Id,
            child, false, false));
        if (closeBeforeAttach)
            fixture.Hooks.BeforeCall = method => { if (method.Name == nameof(IStateStore.AttachIssueWork)) Close(); };
        var captures = 0;
        var boundary = new Boundary(_ => new Lease());
        var settled = await Runner(fixture, selection, boundary, (_, _, _) =>
        {
            captures++;
            return Task.FromResult(Coverage(selection));
        }).RunAsync(selection, default);
        fixture.Hooks.BeforeCall = null;
        Check.Require(captures == 0 && boundary.Calls == 0, "A held branch asks for no coverage or lease.");
        Check.Require(settled != closeBeforeAttach, closeBeforeAttach
            ? "A holder closed before attachment leaves the selection unfinished."
            : "A branch attached to its holder lets the selection complete.");
        if (!closeBeforeAttach) Close();
        Check.Require(PendingAt(fixture, Side.Local, child.Path).Any(),
            "Work held by a notice stays reachable by its exact identity.");
    }

    public static void RunMappedHolds()
    {
        var failures = new List<Exception>();
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var missing in new[] { false, true })
        foreach (var changedHolder in new[] { false, true })
        {
            try { MappedHeldBranchAsync(side, missing, changedHolder).GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException($"mapped hold {side}, missing={missing}, changed={changedHolder}", error));
            }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static async Task MappedHeldBranchAsync(Side side, bool missing, bool changedHolder)
    {
        using var fixture = new Fixture(realStore: true);
        const string staging = "Staging";
        const string current = "Current";
        const string childName = "/Child";
        var sourceFolder = FolderEntry(staging, "source-folder");
        var targetFolder = FolderEntry(current, "target-folder");
        var source = side == Side.Local ? fixture.Local : fixture.Dropbox;
        var target = side == Side.Local ? fixture.Dropbox : fixture.Local;
        source.Seed(sourceFolder, null);
        target.Seed(targetFolder, null);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(side, sourceFolder, targetFolder)],
            Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, current,
                new IssueSubject([new IssueItem(PathNames.Other(side), current, targetFolder.Id)], null))]
        });
        var child = side == Side.Local ? LocalEntry(staging + childName, LocalId) :
            RemoteEntry(staging + childName, DropboxId);
        child = child with { Size = 2, Modified = Stamp.AddDays(1), ContentHash = DropboxContentHash.Compute(new byte[] { 1, 1 }) };
        source.Seed(child, [1, 1]);
        if (!missing) target.Seed(side == Side.Local ? RemoteEntry(current + childName, DropboxId) :
            LocalEntry(current + childName, LocalId), [2]);
        var selection = side == Side.Local ? Seed(fixture,
            new ChangeNotice(side, ChangeKind.Changed, child.Path, null, child.Id, child, false, false)) :
            SeedRemote(fixture, child);
        void Close() => fixture.Inner.Commit(PairId, StateChange.Empty with
        { Close = [(current, new[] { IssueKind.NameClash })] });
        if (changedHolder)
            fixture.Hooks.BeforeCall = method => { if (method.Name == nameof(IStateStore.AttachIssueWork)) Close(); };
        var boundary = new Boundary(_ => throw new InvalidOperationException("held work cannot be admitted"));
        var complete = await Runner(fixture, selection, boundary,
            (_, _, _) => throw new InvalidOperationException("held work cannot capture coverage"))
            .RunAsync(selection, default);
        fixture.Hooks.BeforeCall = null;
        Check.Require(complete != changedHolder && boundary.Calls == 0 &&
            fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
            "137: mapped/observed hold attaches before admission; a changed holder stays unfinished");
        Check.Require(await source.ReadOneAsync(child.Path, default) == child &&
            (await target.ReadOneAsync(current + childName, default) is null) == missing,
            "137: held source and counterpart are untouched");
        var sibling = LocalEntry("Sibling/Child", "sibling-id");
        fixture.Local.Seed(FolderEntry("Sibling", "sibling-folder"), null);
        fixture.Local.Seed(sibling, [3]);
        await RunNoticeAsync(fixture, sibling.Path, sibling, page: side == Side.Local ? 2 : 1);
        Check.Require(await fixture.Dropbox.ReadOneAsync(sibling.Path, default) is not null,
            "137: independent sibling continues");
        fixture.RestartStore();
        if (!changedHolder)
        {
            Check.Require(fixture.Inner.Issues(PairId).Single().Kind == IssueKind.NameClash,
                "137: occupied-name hold survives restart");
            Close();
        }
        var released = PendingAt(fixture, side, child.Path).Single();
        Check.Require(await RunSelectionAsync(fixture, released),
            "137: released work executes and completes after restart");
        var recorded = fixture.Inner.SyncedById(PairId, side, child.Id);
        Check.Require(recorded is not null &&
            (await target.ReadOneAsync(recorded.PathOn(PathNames.Other(side)), default))?.Size == child.Size &&
            !PendingAt(fixture, side, child.Path).Any(),
            "137: released work copies content, records the binding and retires");
    }

    private static async Task PostRenameFailureRetainsAdvancedBeforeAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var old = LocalEntry(OldPath, LocalId);
        var remote = RemoteEntry(OldPath, DropboxId);
        var saved = SyncedEntries.From(old, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        fixture.Local.Seed(old with { Path = Item, Size = 2 }, [2, 2]);
        fixture.Dropbox.Seed(remote, [1]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, Item, OldPath,
            LocalId, null, true, false));
        var failed = false;
        fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            BlockPublish = (_, _) =>
            {
                if (!failed)
                {
                    failed = true;
                    throw new ItemFailedException(Item, TextKey.FailDropboxNoPermission, null);
                }
                return Task.CompletedTask;
            }
        };
        var boundary = new Boundary(_ => new Lease());
        Check.Require(await Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)))
            .RunAsync(selection, default), "the post-rename failure is retained on its notice");
        var after = fixture.Inner.SyncedById(PairId, Side.Local, LocalId)!;
        Check.Require(after.LocalPath == Item && fixture.Inner.Issues(PairId).Any(issue => issue.Kind == IssueKind.Failure),
            "the rename commits before its ordinary branch fails");
        var released = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
        Check.Require(fixture.Inner.ReadWork(released).All(work => work.Before == after),
            "post-rename failure residual retains the advanced baseline before release");
        fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(Item, IssueClosure.AfterEffect)] });
        Check.Require(await Runner(fixture, released!, new Boundary(_ => new Lease()), (_, _, _) => Task.FromResult(Coverage(released!)))
            .RunAsync(released!, default), "released post-rename failure retries to completion");
    }

    private static async Task OldAliasFailureRetainsAdvancedBeforeAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var old = LocalEntry(OldPath, LocalId);
        var remote = RemoteEntry(OldPath, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(old, remote)] });
        fixture.Local.Seed(old with { Path = Item }, [1]);
        fixture.Dropbox.Seed(remote, [1]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, Item, OldPath,
            LocalId, null, false, false));
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name != nameof(IStateStore.Commit)) return;
            fixture.Hooks.BeforeCall = null;
            fixture.Local.Seed(LocalEntry(OldPath, OccupantId), [2]);
        };
        var failed = false;
        fixture.DropboxView = new ProxySide(fixture.Dropbox)
        {
            BlockPublish = (path, _) =>
            {
                if (path == OldPath && !failed)
                {
                    failed = true;
                    throw new ItemFailedException(path, TextKey.FailDropboxNoPermission, null);
                }
                return Task.CompletedTask;
            }
        };
        Check.Require(await Runner(fixture, selection, new Boundary(_ => new Lease()), (_, _, _) => Task.FromResult(Coverage(selection)))
            .RunAsync(selection, default), "F03: foreign old-alias failure is retained after the rename");
        Check.Require(failed, "F03: the foreign old-alias upload reached its failure");
        var after = fixture.Inner.SyncedById(PairId, Side.Local, LocalId)!;
        var residual = PendingWork.ByIdentity(fixture.Inner, Side.Local, VolumeKey, LocalId).Single();
        Check.Require(fixture.Inner.ReadWork(residual).All(work => work.Before == after),
            "F03: synthetic old-alias residual has the renamed identity's current binding before release");
        fixture.Inner.Commit(PairId, StateChange.Empty with { Close = [(OldPath, IssueClosure.AfterEffect)] });
        Check.Require(await Runner(fixture, residual, new Boundary(_ => new Lease()), (_, _, _) => Task.FromResult(Coverage(residual)))
            .RunAsync(residual, default), "F03: old-alias residual retries after release");
    }

    private static async Task EmptyBranchPreservesLaterArrivalAsync()
    {
        using var fixture = new Fixture(realStore: true);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, Item, OldPath,
            LocalId, null, false, false));
        WorkSelection? later = null;
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name != nameof(IStateStore.CompleteWork)) return;
            fixture.Hooks.BeforeCall = null;
            fixture.Local.Seed(LocalEntry(OldPath, OccupantId), [1]);
            later = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, OldPath, null,
                OccupantId, null, true, false), page: 2);
        };
        var boundary = new Boundary(_ => new Lease());
        Check.Require(await Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)))
            .RunAsync(selection, default), "empty primary and old-path branches complete without admission");
        Check.Require(boundary.Calls == 0 && later is not null && fixture.Inner.ReadWork(later).Single().Notice.Id == OccupantId,
            "a foreign old-path arrival after observation remains pending beyond selection completion");
    }

    private enum RootState { Same, Replaced, Absent, Unreadable }

    // A61, A62, A63: a covering first-run obligation binds only the ancestor it recorded, by a fresh Local read.
    private static async Task RetainedAncestorIdentityAsync(RootState state)
    {
        using var fixture = new Fixture();
        var folder = FolderEntry(Folder, LocalId + "-folder");
        var child = LocalEntry(Folder + "/child", LocalId);
        fixture.Local.Seed(child, [1]);
        if (state != RootState.Absent)
            fixture.Local.Seed(state == RootState.Replaced ? folder with { Id = OccupantId } : folder, null);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, Folder,
                new IssueSubject([new IssueItem(Side.Local, Folder, folder.Id)], null,
                    FirstRunDeletion: IssueSubject.FirstRunDeletionKind.UnscannedSubtree))]
        });
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, child.Path, null, child.Id,
            child, false, false));
        if (state == RootState.Unreadable)
            fixture.Local.FailNext(Folder, new ItemFailedException(Folder, TextKey.ReadLocalFailed, null));
        var captures = 0;
        var settled = await Runner(fixture, selection, new Boundary(_ => new Lease()), (_, _, _) =>
        {
            captures++;
            return Task.FromResult(Coverage(selection));
        }).RunAsync(selection, default);
        Check.Require(state switch
            {
                RootState.Same => captures == 0 && !settled,
                RootState.Unreadable => captures == 0,
                RootState.Absent => captures == 0 && settled,
                _ => captures == 1
            }, "A retained ancestor binds its child only while its identity holds: " + state + "; captures=" + captures);
        if (state == RootState.Unreadable)
            Check.Require(fixture.Inner.Issues(PairId).Any(issue => issue.Cause == TextKey.ReadLocalFailed),
                "A failed ancestor read is the row's Failure, never absence.");
    }

    // R1-03: each row/path/rename work shape must drive the admitted callback with the request's captured generation,
    // not a generation re-read from Storage after admission.
    private static async Task GenerationForwardedAsync()
    {
        foreach (var (route, notice) in Shapes(includeRename: false))
        {
            using var fixture = new Fixture();
            var local = LocalEntry(Item, LocalId);
            fixture.Local.Seed(local, [1, 2]);
            var selection = Seed(fixture, notice);
            var lease = new Lease();
            var boundary = new Boundary(_ => lease);
            var captures = 0;
            var runner = Runner(fixture, selection, boundary,
                (selected, footprint, _) =>
                {
                    captures++;
                    Check.Require(ReferenceEquals(selected, selection),
                        $"{route}: captureCoverage receives the exact selected work.");
                    Check.Require(footprint.Any(scope => scope.Side == Side.Local && scope.NativePath.Contains(Item)),
                        $"{route}: the footprint declares the selected Local path.");
                    return Task.FromResult(Coverage(selection));
                });

            var ready = await runner.RunAsync(selection, default);

            Check.Require(ready, $"{route}: a completed selection settles.");
            Check.Require(lease.RequireCalls == 1 && lease.Generation == selection.PairGeneration,
                $"{route}: the borrowed lease requires the captured request generation g.");
            Check.Require(captures == 1 && boundary.Calls == 1,
                $"{route}: one source capture and one admitted callback per valid admission.");
            Check.Require(await fixture.Dropbox.ReadOneAsync(Item, default) is not null,
                $"{route}: the admitted effect publishes the selected item.");
            Check.Require(ReferenceEquals(fixture.Attempts.Opportunity, selection),
                $"{route}: the registered item attempt binds the exact admitted opportunity.");
            Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 1,
                $"{route}: only settled work retires its exact selection.");
        }
    }

    private static async Task StaleGenerationRefusesAsync()
    {
        foreach (var (route, notice) in Shapes(includeRename: true))
        {
            using var fixture = new Fixture(realStore: true);
            var local = LocalEntry(Item, LocalId);
            fixture.Local.Seed(local, [1, 2]);
            var selection = Seed(fixture, notice);
            var lease = new Lease();
            foreach (var side in new[] { Side.Local, Side.Dropbox })
            {
                var source = fixture.Inner.ReadSource(PairId, side)!.Value;
                fixture.Inner.AdvanceSource(PairId, side, Generation, source.Epoch, source.Revision,
                    source.Provenance, source.Cursor, source.Epoch, source.Revision, source.Provenance, source.Cursor, null);
            }
            var boundary = new Boundary(request =>
            {
                fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
                AdvanceGeneration(fixture);
                fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
                return lease;
            });
            var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

            var error = await ThrowsAsync<LiveAdmissionStaleException>(
                () => runner.RunAsync(selection, default));

            Check.Require(error.Reason == LiveAdmissionStaleReason.CoverageChanged,
                $"{route}: generation change invalidates previously validated coverage; actual={error.Reason}.");
            Check.Require(boundary.Requests.Single().Single().Selection.PairGeneration == selection.PairGeneration &&
                lease.RequireCalls == 0,
                $"{route}: the boundary validates captured g against Storage g+1 before invoking the admitted callback.");
            Check.Require(fixture.Inner.ReadPairExecution(PairId)!.Value.Generation == Generation + 1,
                $"{route}: the fixture actually advanced the pair generation before the refusal.");
            Check.Require(await fixture.Dropbox.ReadOneAsync(Item, default) is null,
                $"{route}: a stale admission performs no effect.");
            Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 0,
                $"{route}: a stale admission retires no work.");
            Check.Require(fixture.Attempts.Executions == 0,
                $"{route}: a stale admission registers no item attempt.");
        }
    }

    // A2 order: required Before is persisted before the coverage capture, and the exact supplied coverage is
    // forwarded in the one execution request.
    private static async Task BeginSelectionBeforeCaptureAsync()
    {
        using var fixture = new Fixture();
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        fixture.Local.Seed(local, [1]);
        fixture.Dropbox.Seed(remote, [1]);
        var saved = SyncedEntries.From(local, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, local, false, false));
        fixture.Local.Seed(local with { Size = 2 }, [2, 2]);
        var lease = new Lease();
        var boundary = new Boundary(_ => lease);
        var captures = 0;
        SyncedEntry? persistedBefore = null;
        LiveCoverage? supplied = null;
        var runner = Runner(fixture, selection, boundary,
            (selected, _, _) =>
            {
                captures++;
                persistedBefore = fixture.Store.ReadWork(selected).Single().Before;
                supplied = Coverage(selected);
                return Task.FromResult(supplied);
            });

        var ready = await runner.RunAsync(selection, default);

        Check.Require(ready, "A changed row settles its selection.");
        Check.Require(persistedBefore == saved,
            "BeginSelection persists the identity's current preimage before effect coverage is captured.");
        Check.Require(captures == 1 && boundary.Calls == 1,
            "One coverage capture and one admitted callback serve the admission lifetime.");
        Check.Require(boundary.Requests[0][0].Coverage with { Peer = null } == supplied,
            "The execution request keeps the supplied source coverage and adds only the observed peer.");
        Check.Require(ReferenceEquals(boundary.Requests[0][0].Selection, selection),
            "The execution request carries the exact selected work.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 1,
            "A settled selection retires exactly once.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 0,
            "A successful upload retains no unfinished issue work.");
    }

    // Q17: the committed occupant-inclusive Issue and the effective row reach the selection-bound continuation once.
    private static async Task RetainedIssueAsync()
    {
        using var fixture = OccupiedFixture(out var selection, out var saved, out var occupant, out var boundary);
        var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

        var ready = await runner.RunAsync(selection, default);

        var issues = fixture.Inner.Issues(PairId);
        var clash = issues.SingleOrDefault(issue => issue.Kind == IssueKind.NameClash);
        Check.Require(ready, "A fully registered remainder still settles its exact selection.");
        Check.Require(clash is not null && clash.Path == DestinationPath,
            "The committed NameClash is raised at the actual occupied target.");
        Check.Require(clash!.Subject.Items.Any(item =>
                item.Side == Side.Dropbox && item.Path == DestinationPath && item.Id == occupant.Id),
            "The committed Issue includes the actual occupant found at the target.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
            "The exact unfinished branch is registered exactly once.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 1,
            "Registered responsibility permits retiring the parent selection.");
        var pending = PendingAt(fixture, Side.Local, SourcePath, DestinationPath).ToList();
        Check.Require(pending.Count == 1, "The registered residual remains reachable by its exact identity.");
        var residual = fixture.Inner.ReadWork(pending[0]).Single();
        Check.Require(residual.Notice.Path == DestinationPath,
            "The residual records the effective Issue path, not the initially scheduled path.");
        Check.Require(residual.Notice.Observed?.Path == SourcePath,
            "The residual records the effective observed row.");
        Check.Require(residual.Before == saved, "The residual retains the applicable preimage.");
    }

    // A provider-free Notice commits its own Issue, so the same exact continuation must register it.
    private static async Task ProviderFreeNoticeRetainsAsync()
    {
        using var fixture = new Fixture();
        fixture.Local.Seed(LocalEntry(Item, LocalId), [1]);
        fixture.Dropbox.Seed(LocalEntry(Item, DropboxId) with { IsFolder = true }, null);
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, null, false, false));
        var boundary = new Boundary(_ => new Lease());
        var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

        var ready = await runner.RunAsync(selection, default);

        var issue = fixture.Inner.Issues(PairId).Single(raised => raised.Kind == IssueKind.NameClash);
        Check.Require(ready && issue.Path == Item,
            "A notice-only decision still settles after registering its exact issue.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
            "A provider-free Notice reaches the retained-issue continuation once.");
        var pending = PendingAt(fixture, Side.Local, Item).Single();
        Check.Require(fixture.Inner.ReadWork(pending).Single().Notice.Path == Item,
            "The provider-free notice registers its effective subject.");
    }

    // The occupant read is itself fallible; its failure still commits an Issue that the continuation must register.
    private static async Task SelectedReadFailuresAsync()
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var ordinal in new[] { 1, 2 })
        {
            using var fixture = new Fixture();
            var entry = LocalEntry(Item, LocalId);
            fixture.Local.Seed(entry, [1]);
            var reads = 0;
            var cause = Text.ReadFailedOn(side);
            var faulted = new HookedSide(side == Side.Local ? fixture.Local : fixture.Dropbox,
                beforeRead: path =>
                {
                    if (path == Item && ++reads == ordinal)
                        throw new ItemFailedException(path, cause, null);
                });
            if (side == Side.Local) fixture.LocalView = faulted;
            else fixture.DropboxView = faulted;
            var selection = Seed(fixture,
                new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, entry, false, false));
            var boundary = new Boundary(_ => new Lease());
            var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));
            Check.Require(await runner.RunAsync(selection, default) && reads >= ordinal,
                "A selected branch read failure is retained without faulting the run.");
            Check.Require(fixture.Inner.Issues(PairId).Single().Cause == cause &&
                fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1 &&
                await fixture.Dropbox.ReadOneAsync(Item, default) is null,
                "Prewalk, observation and admitted reads preserve the item and attach its exact Failure.");
        }
    }

    // Bug #95: a deleted-on-both-sides item is absence for the existing planner; a genuine permanent read failure
    // is one deduped notice, including a malformed single-path reply.
    private static async Task ObservationReadOutcomesAsync()
    {
        // Absent locally and absent on Dropbox: the planner's both-absent answer, with no Failure or notice.
        using (var fixture = new Fixture())
        {
            var selection = Seed(fixture,
                new ChangeNotice(Side.Local, ChangeKind.Deleted, Item, null, null, null, false, false));
            await RunSelectionAsync(fixture, selection);
            Check.Require(fixture.Inner.Issues(PairId).Count == 0,
                "a path absent on both sides settles through the planner with no notice");
        }
        // The same both-deleted outcome with saved history is the existing ForgetOnly decision.
        using (var fixture = new Fixture())
        {
            var saved = SyncedEntries.From(LocalEntry(Item, LocalId), RemoteEntry(Item, DropboxId));
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
            var selection = Seed(fixture,
                new ChangeNotice(Side.Local, ChangeKind.Deleted, Item, null, LocalId, null, false, false));
            await RunSelectionAsync(fixture, selection);
            Check.Require(fixture.Inner.Issues(PairId).Count == 0 &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Item)) is null,
                "both-deleted saved history settles by the existing ForgetOnly decision with no notice");
        }
        // Local present, Dropbox read not_found: the ordinary planner decision, never a Failure.
        using (var fixture = new Fixture())
        {
            fixture.Local.Seed(LocalEntry(Item, LocalId), [2]);
            var selection = Seed(fixture,
                new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, null, false, false));
            await RunSelectionAsync(fixture, selection);
            Check.Require(fixture.Inner.Issues(PairId).Count == 0,
                "a not_found Dropbox read under a present local item follows the ordinary planner decision");
        }
        // A genuine permanent read failure leaves exactly one Failure notice for the path (the store's Raise
        // dedupes by path and kind, and the selection's visited guard skips the duplicate notice). The malformed
        // variant carries the boundary's cause, never the trigger side's.
        foreach (var error in new Exception[]
        {
            new ItemFailedException(Item, TextKey.ReadDropboxFailed, null),
            new ItemFailedException(Item, TextKey.ReadDropboxFailed, new JsonException())
        })
        {
            using var fixture = new Fixture();
            fixture.Local.Seed(LocalEntry(Item, LocalId), [2]);
            fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: path =>
            {
                if (PathNames.Key(path) == PathNames.Key(Item)) throw error;
            });
            var notice = new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, null, false, false);
            var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
            var watermark = fixture.Inner.AcquireJournalPage(VolumeKey, null, new JournalPosition(1, 1),
                [(PairId, Generation, source.Epoch, source.Revision, false, false)],
                [(PairId, notice), (PairId, notice)]);
            var selection = new WorkSelection(PairId, Generation, source.Epoch, Side.Local, VolumeKey, notice.Id,
                PathNames.KeyFor(Side.Local, notice.Path), watermark!.Value, watermark!.Value);
            await RunSelectionAsync(fixture, selection);
            var issues = fixture.Inner.Issues(PairId);
            Check.Require(issues.Count == 1 && issues[0].Kind == IssueKind.Failure && issues[0].Path == Item &&
                issues[0].Cause == TextKey.ReadDropboxFailed &&
                fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
                "a permanent single-path read failure creates exactly one retained Failure with this endpoint's cause: " +
                error.GetType().Name);
        }
    }

    private static async Task OccupantReadFailureStillRetainsAsync()
    {
        using var fixture = new Fixture();
        var prior = LocalEntry(SourcePath, LocalId);
        var remote = RemoteEntry(DestinationPath, DropboxId);
        var saved = SyncedEntries.From(prior, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        fixture.Local.Seed(LocalEntry(SourcePath, LocalId) with { Size = 2 }, [1, 2]);
        var occupant = RemoteEntry(DestinationPath, OccupantId);
        var reads = 0;
        fixture.DropboxView = new HookedSide(fixture.Dropbox,
            _ => fixture.Dropbox.Seed(occupant, [3]),
            path =>
            {
                if (path == DestinationPath && ++reads == OccupantReadOrdinal)
                    throw new ItemFailedException(path, TextKey.ReadDropboxFailed, null);
            });
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, SourcePath, null, LocalId, null, false, false));
        var boundary = new Boundary(_ => new Lease());
        var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

        var ready = await runner.RunAsync(selection, default);

        var issue = fixture.Inner.Issues(PairId).Single(raised => raised.Kind == IssueKind.Failure);
        Check.Require(ready && issue.Path == SourcePath && issue.Cause == TextKey.ReadDropboxFailed,
            "A failed occupant read commits the row's own Failure and still settles its selection.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
            "The occupant-read failure reaches the retained-issue continuation once.");
        var pending = PendingAt(fixture, Side.Local, SourcePath).Single();
        Check.Require(fixture.Inner.ReadWork(pending).Single().Notice.Path == SourcePath,
            "The residual records the effective Failure subject.");
    }

    private static async Task CommitFailureDoesNotRetainAsync()
    {
        using var fixture = OccupiedFixture(out var selection, out _, out _, out var boundary);
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name == nameof(IStateStore.Commit)) throw new InvalidOperationException(CommitFault);
        };
        var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

        var error = await ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(selection, default));

        Check.Require(error.Message == CommitFault, "The failed commit propagates its own failure.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 0,
            "A failed commit invokes no committed-Issue callback.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 0,
            "A failed commit retires no selection.");
        Check.Require(fixture.Inner.Issues(PairId).All(issue => issue.Kind != IssueKind.NameClash),
            "A failed commit persists no issue.");
    }

    private static async Task AttachmentFailureRetainsParentAsync()
    {
        using var fixture = OccupiedFixture(out var selection, out _, out _, out var boundary);
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name == nameof(IStateStore.AttachIssueWork)) throw new InvalidOperationException(AttachFault);
        };
        var runner = Runner(fixture, selection, boundary, (_, _, _) => Task.FromResult(Coverage(selection)));

        var error = await ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(selection, default));

        Check.Require(error.Message == AttachFault, "A failed attachment propagates rather than settling.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.AttachIssueWork)) == 1,
            "The failed attachment is attempted once.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 0,
            "A failed attachment never retires its parent.");
        Check.Require(fixture.Inner.Issues(PairId).Count(issue => issue.Kind == IssueKind.NameClash) == 1,
            "A failed attachment does not raise a second issue.");
        var parent = PendingAt(fixture, Side.Local, SourcePath).Single();
        Check.Require(fixture.Inner.ReadWork(parent).Single().Notice.Path == SourcePath,
            "A failed attachment leaves the parent selection responsible.");
    }

    private static async Task WrongSelectionRefusesAsync()
    {
        using var fixture = new Fixture();
        fixture.Local.Seed(LocalEntry(Item, LocalId), [1]);
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, null, false, false));
        var lease = new Lease();
        var boundary = new Boundary(_ => lease);
        var captures = 0;
        var runner = Runner(fixture, selection, boundary,
            (_, _, _) =>
            {
                captures++;
                return Task.FromResult(Coverage(selection));
            });

        var foreign = selection with { PairId = "other-pair" };
        var wrongPair = await ThrowsAsync<LiveAdmissionStaleException>(
            () => runner.RunAsync(foreign, default));

        Check.Require(wrongPair.Reason == LiveAdmissionStaleReason.SelectionChanged,
            "A foreign pair refuses as a changed selection.");
        Check.Require(captures == 0 && boundary.Calls == 0 && lease.RequireCalls == 0,
            "A refused admission captures no coverage and opens no boundary callback.");
        Check.Require(fixture.Hooks.Count(nameof(IStateStore.CompleteWork)) == 0,
            "A refused admission retires nothing.");
        Check.Require(await fixture.Dropbox.ReadOneAsync(Item, default) is null,
            "A refused admission performs no effect.");
    }

    // D1: a live replacement of a Local folder is refused while a descendant is known unreadable, whole, with no effect.
    private static async Task LiveFolderReplaceOverUnreadableChildAsync()
    {
        using var fixture = new Fixture();
        var folder = FolderEntry(Folder, LocalId);
        var saved = SyncedEntries.From(folder, FolderEntry(Folder, DropboxId));
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var child = LocalEntry(Folder + "/locked", OccupantId) with { Skip = nameof(TextKey.SkipUnreadable) };
        fixture.Local.Seed(folder, null);
        fixture.Local.Seed(child, [1]);
        var replacement = RemoteEntry(Folder, "dropbox-file");
        fixture.Dropbox.Seed(replacement, [1]);
        // The unreadable fact lives in Storage's skip-member projection (the store's point-of-use answer), so it is
        // recorded through the side-observation owner, exactly as Repair records it; a hand-raised Skipped issue
        // alone leaves no member for the range answer.
        fixture.Inner.ObserveSkipped(PairId, Generation, Side.Local, child.Path,
            (TextKey.SkipUnreadable, new IssueSubject([new IssueItem(Side.Local, child.Path, child.Id)], null)));
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, LocalId, folder, false, false));
        var runner = Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => Task.FromResult(Coverage(selection)));

        await runner.RunAsync(selection, default);

        Check.Require((await fixture.Local.ReadOneAsync(Folder, default))?.IsFolder == true &&
            await fixture.Local.ReadOneAsync(child.Path, default) is not null,
            "A live replacement leaves a folder with a known unreadable child in place.");
        Check.Require(fixture.Inner.Issues(PairId).Any(issue => issue.Kind == IssueKind.Failure &&
            issue.Path == Folder && issue.Cause == TextKey.ReadLocalFailed),
            "The refused live replacement raises the ordinary visible unreadable Failure.");
        Check.Require(fixture.Inner.SyncedByKey(PairId, Side.Local, saved.LocalKey) == saved,
            "The refused live replacement keeps the saved binding.");
    }

    // D2: a hold raised by a folder row covers the rest of the same selection, by the rule Repair uses.
    private static async Task RaisedHoldCoversSelectionChildrenAsync()
    {
        using var fixture = new Fixture();
        var folder = FolderEntry(Folder, LocalId);
        var child = LocalEntry(Folder + "/child", OccupantId);
        fixture.Local.Seed(folder, null);
        fixture.Local.Seed(child, [1]);
        fixture.Dropbox.Seed(RemoteEntry(Folder, DropboxId), [1]);
        var selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, Folder, null, LocalId, folder, false, false));
        var runner = Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => Task.FromResult(Coverage(selection)));

        await runner.RunAsync(selection, default);

        var issues = fixture.Inner.Issues(PairId);
        Check.Require(issues.Count == 1 && issues[0] is { Kind: IssueKind.NameClash, Cause: TextKey.ClashFileFolder },
            "The folder raises its NameClash and its child adds nothing while the clash is unresolved.");
        Check.Require(fixture.Admission.Completed == 0 &&
            await fixture.Dropbox.ReadOneAsync(child.Path, default) is null,
            "No action runs for a child of the folder that raised the hold.");
    }

    // D3: a rename under a held folder is held through the prefix rule, not only on an exact held key.
    private static async Task RenameUnderHeldFolderHoldsAsync()
    {
        using var fixture = new Fixture();
        var localFolder = FolderEntry(Folder, LocalId + "-folder");
        var remoteFolder = FolderEntry(Folder, DropboxId + "-folder");
        var before = LocalEntry(Folder + "/child", LocalId);
        var remote = RemoteEntry(Folder + "/child", DropboxId);
        var saved = SyncedEntries.From(before, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(localFolder, remoteFolder), saved],
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, Folder,
                new IssueSubject([new IssueItem(Side.Local, Folder, localFolder.Id)], null))]
        });
        fixture.Local.Seed(localFolder, null);
        fixture.Dropbox.Seed(remoteFolder, null);
        fixture.Dropbox.Seed(remote, [1]);
        var moved = before with { Path = Folder + "/child2" };
        fixture.Local.Seed(moved, [1]);
        var selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Renamed, moved.Path, before.Path,
            LocalId, moved, false, false));
        var runner = Runner(fixture, selection, new Boundary(_ => new Lease()),
            (_, _, _) => Task.FromResult(Coverage(selection)));

        await runner.RunAsync(selection, default);

        Check.Require(await fixture.Dropbox.ReadOneAsync(remote.Path, default) is not null &&
            await fixture.Dropbox.ReadOneAsync(moved.Path, default) is null,
            "A rename under a held folder moves nothing.");
        Check.Require(fixture.Inner.SyncedById(PairId, Side.Local, LocalId) == saved,
            "A rename under a held folder keeps the saved binding.");
    }

    private static Fixture OccupiedFixture(out WorkSelection selection, out SyncedEntry saved, out Entry occupant,
        out Boundary boundary)
    {
        var fixture = new Fixture();
        var prior = LocalEntry(SourcePath, LocalId);
        var remote = RemoteEntry(DestinationPath, DropboxId);
        saved = SyncedEntries.From(prior, remote);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [saved] });
        var changed = LocalEntry(SourcePath, LocalId) with { Size = 2 };
        fixture.Local.Seed(changed, [1, 2]);
        occupant = RemoteEntry(DestinationPath, OccupantId) with { Size = 3, ContentHash = DropboxContentHash.Compute(new byte[] { 3 }) };
        var occupantEntry = occupant;
        var hooked = new HookedSide(fixture.Dropbox, _ => fixture.Dropbox.Seed(occupantEntry, [3]));
        selection = Seed(fixture,
            new ChangeNotice(Side.Local, ChangeKind.Changed, SourcePath, null, LocalId, changed, false, false));
        fixture.DropboxView = hooked;
        boundary = new Boundary(_ => new Lease());
        return fixture;
    }

    private static IEnumerable<(string Route, ChangeNotice Notice)> Shapes(bool includeRename)
    {
        var local = LocalEntry(Item, LocalId);
        yield return ("row",
            new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, local, false, false));
        yield return ("path",
            new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, null, null, false, false));
        if (!includeRename) yield break;
        yield return ("rename",
            new ChangeNotice(Side.Local, ChangeKind.Renamed, Item, OldPath, LocalId, local, false, false));
    }

    private static WorkSelection Seed(Fixture fixture, ChangeNotice notice, long page = 1)
    {
        var source = fixture.Inner.ReadSource(PairId, Side.Local)!.Value;
        var watermark = fixture.Inner.AcquireJournalPage(VolumeKey,
            page == 1 ? null : new JournalPosition(1, (ulong)page - 1), new JournalPosition(1, (ulong)page),
            [(PairId, Generation, source.Epoch, source.Revision, false, false)], [(PairId, notice)]);
        Check.Require(watermark is not null, "The journal page commits the selected work.");
        return new WorkSelection(PairId, Generation, source.Epoch, Side.Local, VolumeKey, notice.Id,
            PathNames.KeyFor(Side.Local, notice.Path), watermark!.Value, watermark!.Value);
    }

    private static void AdvanceGeneration(Fixture fixture)
    {
        var current = fixture.Inner.Pair(PairId)!;
        fixture.Inner.SavePairDecision(current, Generation, current with { Name = "Pair A reconfigured" },
            null, false, null);
    }

    private static Entry FolderEntry(string path, string id) => new(path, true, 0, Stamp, id, "", "", null);

    private static Entry LocalEntry(string path, string id) =>
        new(path, false, 1, Stamp, id, "", "", null);

    private static Entry RemoteEntry(string path, string id) =>
        new(path, false, 1, Stamp, id, "rev-1", DropboxContentHash.Compute(new byte[] { 1 }), null);

    private static LiveCoverage Coverage(WorkSelection selection) =>
        new(new LocalCatchUp(PairId, selection.PairGeneration, selection.SourceEpoch,
                new NtfsUsnCheckpoint(1, 1, 1, 1), selection.ThroughWorkId),
            new DropboxCatchUp(PairId, selection.PairGeneration, selection.SourceEpoch, Cursor,
                selection.ThroughWorkId));

    private static SelectionPass Runner(Fixture fixture, WorkSelection selection, Boundary boundary,
        Func<WorkSelection, IReadOnlyList<LiveEffectScope>, CancellationToken, Task<LiveCoverage>> capture,
        IAdmission? admission = null)
    {
        var context = fixture.Context(selection, admission);
        var jobs = SyncJobs.Create(context, new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 0));
        boundary.Capture = capture;
        return new SelectionPass(context, jobs.Items, jobs.Ancestors, boundary, jobs.Actions);
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T error)
        {
            return error;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class Fixture : IDisposable
    {
        public FakeSide Local { get; } = new(Side.Local);
        public FakeSide Dropbox { get; } = new(Side.Dropbox);
        public IStateStore Inner { get; private set; }
        private readonly string? _storeFolder;
        public IStateStore Store { get; }
        public ForwardStore Hooks { get; }
        public FakeAdmission Admission { get; } = new() { Level = 1 };
        public ItemAttempts Attempts { get; } = new();
        public PairConfig Pair { get; } = new(PairId, "Pair A", "C:/PairA", "/PairA", "connection-a", [], [],
            FirstRunPolicy.NewestWins, true, null,
            new RootBinding("C:/PairA", "/PairA", "connection-a", new DirectoryIdentity(1, 2, null),
                new DropboxRootIdentity("account-a", "namespace-a", "home-a", "folder-a")));
        public ISide? DropboxView { get; set; }
        public ISide? LocalView { get; set; }

        public Fixture(bool realStore = false, string? localRoot = null, Guid? registeredAttempt = null)
        {
            if (realStore)
            {
                _storeFolder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())).FullName;
                Inner = registeredAttempt is { } attempt
                    ? Massdex.SyncShark.Engine.Storage.StateStore.Open(Path.Combine(_storeFolder, "state.db"),
                        (id, _, _, _, _) => id == attempt)
                    : Massdex.SyncShark.Engine.Storage.StateStore.Open(Path.Combine(_storeFolder, "state.db"));
            }
            else Inner = new InMemoryStateStore();
            if (localRoot is not null) Pair = Pair with { LocalRoot = localRoot };
            Inner.SavePair(Pair);
            var proxy = DispatchProxy.Create<IStateStore, ForwardStore>();
            Hooks = (ForwardStore)(object)proxy;
            Hooks.Inner = Inner;
            Store = proxy;
        }

        public void RestartStore()
        {
            Inner.Dispose();
            Inner = Massdex.SyncShark.Engine.Storage.StateStore.Open(Path.Combine(_storeFolder!, "state.db"));
            Hooks.Inner = Inner;
        }

        public EngineSettings Settings { get; } = new("{name} ({side} {time}){ext}", "yyyyMMddHHmmss", "Local", "Dropbox",
            ".case", RecycleFallback.Fail, 1, FirstRunPolicy.NewestWins, 1, "", LogBufferCharacters, ScanTestSettings.BatchRows)
        {
            // Single-row pages exercise the folder survivor across page boundaries.
            StoragePageSize = 1
        };

        public ExecutorContext Context(WorkSelection selection, IAdmission? admission = null)
        {
            return new ExecutorContext(LocalView ?? Local, DropboxView ?? Dropbox,
                new FakeRecovery { Evidence = new DriveEvidence(1, null, null), Hash = Local.ContentHashAsync }, Store, Pair,
                new PathScope(Pair, [Pair]), Settings, new ReplaceTurn(), admission ?? Admission, false, _ => { },
                (_, _, _, _) => ValueTask.FromResult<IAsyncDisposable>(NoReservation.Instance),
                new FakeTimeProvider(),
                (_, _, _) => throw new NotSupportedException(),
                operation => new RequestAttempt(operation, AttemptOwner.Transfer),
                (_, _) => Task.FromResult(false),
                ("account-a", "route-a", "source-a"), Attempts, selection, Generation);
        }

        public void Dispose()
        {
            Inner.Dispose();
            if (_storeFolder is not null) Directory.Delete(_storeFolder, true);
        }
    }

    private sealed class NoReservation : IAsyncDisposable
    {
        public static readonly NoReservation Instance = new();
        public ValueTask DisposeAsync() => default;
    }

    /// <summary>One borrowed lease that records the generation it is required to authorize and can reject after
    /// the surrounding test advances Storage.</summary>
    private sealed class Lease : IAdmittedExecution
    {
        public long? Generation { get; private set; }
        public int RequireCalls { get; private set; }
        public bool Reject { get; set; }
        public Action<Lease>? OnRequire { get; set; }
        public IReadOnlyList<string>? AllowedKeys { get; init; }
        public IReadOnlyList<PathLockScope>? AllowedScopes { get; init; }
        public int ScopeCalls { get; private set; }

        public void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys)
        {
            RequireCalls++;
            Generation = generation;
            OnRequire?.Invoke(this);
            if (AllowedKeys is { } allowed)
                Check.Require(requiredLockKeys.All(key => allowed.Contains(key, PathLocks.KeyComparer)),
                    "G18: admitted footprint supplies every required action lock");
            if (Reject) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        }

        public void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> scopes)
        {
            ScopeCalls++;
            Require(pairId, generation, scopes.Select(scope => scope.Key).ToArray());
            if (AllowedScopes is { } allowed && scopes.Any(required => !allowed.Any(scope =>
                PathLocks.KeyComparer.Equals(scope.Key, required.Key) && (!required.IncludesDescendants || scope.IncludesDescendants))))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
        }

        public ValueTask ReleaseAdmissionAsync() => AllowsRelease
            ? default : throw new NotSupportedException(nameof(ReleaseAdmissionAsync));

        public ValueTask DisposeAsync() => default;

        /// <summary>A folder removal releases its outer permit before sending; ordinary item leases never do.</summary>
        public bool AllowsRelease { get; init; }
    }

    private sealed class Boundary(Func<LiveExecutionRequest, IAdmittedExecution> leaseFor) : ILiveExecutionBoundary
    {
        public bool TryHoldGeneration(string pairId) => true;

        public List<IReadOnlyList<LiveExecutionRequest>> Requests { get; } = [];
        public int Calls => Requests.Count;
        public Func<WorkSelection, IReadOnlyList<LiveEffectScope>, CancellationToken, Task<LiveCoverage>> Capture { get; set; } =
            (_, _, _) => throw new NotSupportedException();

        public Task<T> ExecuteAsync<T>(IReadOnlyList<LiveExecutionRequest> requests,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            Requests.Add(requests);
            return execute(leaseFor(requests.Single()), ct);
        }

        public Task<LiveCoverage> CaptureCoverageAsync(WorkSelection selection,
            IReadOnlyList<LiveEffectScope> footprint, CancellationToken ct) => Capture(selection, footprint, ct);
    }

    private sealed class ItemAttempts : IItemAttempts
    {
        public object? Opportunity { get; private set; }
        public int Executions { get; private set; }

        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            Executions++;
            Opportunity = opportunity;
            return execute(Guid.NewGuid(), ct);
        }

        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path,
            IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
            PendingEffect? expectedPending, ItemRetryToken? expectedEpisode) => throw new NotSupportedException();

        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) =>
            throw new NotSupportedException();
    }

    private sealed class RecoveryBoundary : IRecoveryExecutionBoundary
    {
        public void RequireCurrent() { }

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => Run(execute, ct);

        public Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => Run(execute, ct);

        public Task<T> ExecuteAsync<T>(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => Run(execute, ct);

        public Task ExecuteAsync(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task> execute, CancellationToken ct) =>
            Run(async (lease, token) => { await execute(lease, token); return true; }, ct);

        private static async Task<T> Run<T>(Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            // A folder removal releases its outer permit before the send; recovery leases must allow that.
            await using var lease = new Lease { AllowsRelease = true };
            return await execute(lease, ct);
        }
    }

    /// <summary>Delegates every side call and runs one hook before staging, so an occupant can appear only after
    /// the decisive freshness read and before publication.</summary>
    private sealed class HookedSide(ISide inner, Action<string>? beforeStage = null,
        Action<string>? beforeRead = null, bool failDisposal = false, bool refuseRemoval = false) : ISide
    {
        public Side Side => inner.Side;

        public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) =>
            failDisposal ? new FailingDisposal(inner.ReadAllAsync(under, options, ct)) :
                inner.ReadAllAsync(under, options, ct);

        private sealed class FailingDisposal(IAsyncEnumerable<Listed> source) : IAsyncEnumerable<Listed>
        {
            public IAsyncEnumerator<Listed> GetAsyncEnumerator(CancellationToken ct = default) =>
                new Enumerator(source.GetAsyncEnumerator(ct));

            private sealed class Enumerator(IAsyncEnumerator<Listed> inner) : IAsyncEnumerator<Listed>
            {
                public Listed Current => inner.Current;
                public ValueTask<bool> MoveNextAsync() => inner.MoveNextAsync();
                public async ValueTask DisposeAsync()
                {
                    await inner.DisposeAsync();
                    throw new InventoryIncompleteException("outer disposal", null);
                }
            }
        }

        public Task<Entry?> ReadOneAsync(string path, CancellationToken ct)
        {
            beforeRead?.Invoke(path);
            return inner.ReadOneAsync(path, ct);
        }

        public Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct) =>
            inner.OpenReadAsync(entry, attempt, ct);

        public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => inner.ContentHashAsync(entry, ct);

        public Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct)
        {
            beforeStage?.Invoke(path);
            return inner.StageAsync(path, content, source, ct);
        }

        public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection,
            CancellationToken ct) => inner.PublishAsync(staged, expected, selection, ct);

        public Task<Entry> EnsureFolderAsync(string path, CancellationToken ct) => inner.EnsureFolderAsync(path, ct);

        public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) =>
            inner.DeleteAsync(expected, selection, ct);

        public Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct) =>
            inner.MoveAsync(expected, newPath, ct);

        public Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
            IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct) =>
            refuseRemoval ? Task.FromResult<FolderSubmitResult>(new FolderSubmitResult.Terminal(
                effects.Select((_, index) => (FolderMemberResult)new FolderMemberResult.KnownNoEffect(index,
                    TextKey.FailChanged)).ToArray())) : inner.SubmitFolderRemovalAsync(submission, effects, attempt, ct);

        public Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount, RequestAttempt attempt,
            CancellationToken ct) => inner.ReadFolderRemovalAsync(jobId, expectedCount, attempt, ct);
    }

    /// <summary>Forwards every store call unchanged and counts method names, with one optional fault hook.</summary>
    public class ForwardStore : DispatchProxy
    {
        private readonly List<string> _calls = [];
        private IStateStore? _inner;

        public IStateStore Inner
        {
            get => _inner ?? throw new InvalidOperationException("The store double is unbound.");
            set => _inner = value;
        }

        public Action<MethodInfo>? BeforeCall { get; set; }
        public List<WorkSelection> CommittedSelections { get; } = [];
        public int RecordedKeyVisits { get; private set; }
        public List<(Side Side, long? Through, int Count)> AncestorQueries { get; } = [];

        private sealed class CountedKeys(IReadOnlyList<string> keys, ForwardStore owner) : IReadOnlyList<string>
        {
            public int Count => keys.Count;
            public string this[int index] => keys[index];
            public IEnumerator<string> GetEnumerator()
            {
                foreach (var key in keys)
                {
                    owner.RecordedKeyVisits++;
                    yield return key;
                }
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public int Count(string method)
        {
            lock (_calls) return _calls.Count(call => call == method);
        }

        protected override object? Invoke(MethodInfo? target, object?[]? args)
        {
            var method = target ?? throw new InvalidOperationException("A store call must name its method.");
            args ??= Array.Empty<object?>();
            lock (_calls) _calls.Add(method.Name);
            if (method.Name == nameof(IStateStore.Commit) && args.Length == 4 && args[2] is WorkSelection selected)
                CommittedSelections.Add(selected);
            // AutoSync runs only after the first Repair; these fixtures start from that state.
            if (method.Name == nameof(IStateStore.FirstRepairDone)) return true;
            BeforeCall?.Invoke(method);
            try
            {
                var result = method.Invoke(Inner, args);
                if (method.Name == nameof(IStateStore.UnreadableUnder))
                    return new CountedKeys((IReadOnlyList<string>)result!, this);
                if (method.Name == nameof(IStateStore.PendingAncestors))
                {
                    var pending = ((IEnumerable<WorkSelection>)result!).ToArray();
                    AncestorQueries.Add(((Side)args[2]!, (long?)args[4], pending.Length));
                    return pending;
                }
                return result;
            }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
            {
                // The reflection boundary must not change a store/callback exception's type or message.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
