using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Net;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.V2;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Checks for Repair (`--engine-checks repair`).</summary>
public static class RepairChecks
{
    private static readonly EngineSettings Settings = ShippedSettings() with
    {
        ConflictLabelFormat = "{name} ({side} {time}){ext}",
        ConflictLabelTimeFormat = "yyyyMMddHHmmss", ConflictLabelLocalName = "Local",
        ConflictLabelDropboxName = "Dropbox", CaseRenameSuffix = ".case", DefaultIgnoreRules = ""
    };
    private static readonly AsyncLocal<string?> FixtureRoot = new();
    private static readonly AsyncLocal<Func<Guid, string, long, IssueSubject, ItemRetryToken?, bool>?> AttemptValidator = new();

    private static StateStore OpenStore(string path) => StateStore.Open(path,
        (id, pair, generation, subject, expected) =>
            AttemptValidator.Value?.Invoke(id, pair, generation, subject, expected) == true);



    public static void Run() => RunAsync().GetAwaiter().GetResult();

    public static void DirectRepair() => ProductionDirectRepairAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        DeletingSideAllocatesNothing();
        RenameSelectAllocatesNothing();
        Func<Task>[] cases =
        [
            () => DisabledAsync(),
            () => EmptyRunAsync(),
            () => CorruptHistoryWakesRepairAsync(),
            () => CorruptHistoryRestoreAsync(),
            () => CorruptHistoryRestoreAsync(incomplete: true),
            () => CorruptHistoryRestoreAsync(incomplete: false),
            () => CorruptHistoryRevisionRetryAsync(),
            () => CorruptHistoryDuringRowsAsync(),
            () => ListingProgressAsync(),
            () => ListingsOverlapAsync(),
            () => JournalChangedAsync(),
            () => JournalUnavailableAsync(),
            () => PendingAsync(),
            () => PendingFailureAsync(),
            () => IncompleteAsync(),
            () => SkippedAsync(),
            () => ExclusionAsync(),
            () => LogFailureAsync(),
            () => UnexpectedFailureAsync(),
            () => CancellationAsync(),
            () => HeldDownloadAsync(),
            () => NestedRootsAsync(),
            () => ReincludeDeletionHistoryAsync(),
            () => RenamesBeforeRowsAsync(),
            () => RefusedRenameAsync(),
            () => HeldFolderRenameHoldsDescendantsAsync(),
            () => FolderMovedAgainAsync(held: false),
            () => FolderMovedAgainAsync(held: true),
            () => HeldNameClashHoldsFreeRowAsync(),
            () => ReservedDropboxFolderAsync(),
            () => DeferredFolderOrderAsync(),
            () => MidRunCancellationAsync(),
            () => CancelDuringFilePassAsync(),
            () => CancelAfterListingsAsync(),
            () => RetainedDiscoveryAsync(false),
            () => RetainedDiscoveryAsync(true),
            () => AbortBothSinksAsync(),
            () => AbortFailureKeepsListingErrorAsync(),
            () => CaseRenameFallbackAsync(),
            () => MemberFaultLoggedAsync(),
            () => CompletionWriteFailureAsync(),
            () => DriveGateAsync(),
            () => DriveFinishAsync(),
            () => DriveProgressReopenAsync(),
            () => PendingStageNamesAsync(),
            () => FailureRetryAsync(),
            () => RetrySelectionAsync(),
            () => PhaseSnapshotsAsync(),
            () => ScopeRetirementAsync(),
            () => ShippedDefaultIgnoreEntryAsync(),
            () => ShippedDefaultIgnoreRetirementAsync(),
            () => ControlCharacterNamesAsync(),
            () => UnreadableRecoveryAsync(),
            () => ExclusionRetryAsync(),
            () => UncertainUploadAsync(),
            () => ConflictProofAsync(),
            () => FolderPlansJoinRowsAsync(),
            () => RestoredAncestorsAsync(),
            () => ExclusionCaptureFailureAsync(),
            () => RetainedBilateralSettlementAsync(),
            () => RetainedFolderDependencyAsync(),
            () => UpgradedMappedBindingAsync(),
            () => DiscoveryContractAsync(),
            () => RootCheckpointRefusalAsync(),
            () => AncestorRefusalCompletesAsync(),
            () => UnreliableFirstRunContentAsync(),
            () => PrerequisitePageRetryAsync(),
            () => SecondSinkFailureJoinsAsync(),
            () => RegisteredItemIdentityAsync(),
            () => ConcurrentForgottenAsync(false),
            () => ConcurrentForgottenAsync(true),
            () => ForgottenFailureAsync(),
            () => ScanReservationAuthorityAsync(),
            () => RootFailureClassificationAsync(),
            () => InitialEvidencePreservesCurrentDefinitionAsync(),
            () => ScanResetRefusesOldWriterAsync(),
            () => PartialKeepBothRepairAsync(),
            () => EnumerationHandleIdentityAsync(),
            () => ProductionDirectRepairAsync(),
            () => PrefixIgnoreAcrossRepairsAsync(),
            () => LoggingRepairChecks.RunAsync(),
        ];
        var errors = new List<Exception>();
        foreach (var test in cases)
        {
            try { await test(); }
            catch (Exception error) { errors.Add(error); }
        }
        if (errors.Count != 0) throw new AggregateException(errors);
    }

    private static async Task ListingProgressAsync()
    {
        foreach (var baseline in new[] { false, true })
        {
            await WithFolder(nameof(ListingProgressAsync) + baseline, async folder =>
            {
                await using var rig = await ComposeRigAsync(folder);
                await SeedLinkedAsync(rig, "First");
                if (baseline)
                {
                    using var seedLog = rig.Log(folder, "baseline");
                    await rig.RunAsync(seedLog, CancellationToken.None);
                }
                await SeedLinkedAsync(rig, "Second");
                Check.Require(rig.Store.SyncedCount(rig.Pair.PairId) == (baseline ? 1 : 0),
                    "baseline counts matched entries once for both sides");
                var messages = new List<string>();
                using var log = RunLog.Open(folder, rig.Pair.PairId, nameof(ListingProgressAsync), message =>
                {
                    lock (messages) messages.Add(message);
                }, Settings.LogBufferCharacters);
                await rig.RunAsync(log, CancellationToken.None);
                var events = Lines(log.Path);
                foreach (var (listingKey, saveKey) in new[]
                {
                    (TextKey.StageLocalWalk, TextKey.StageLocalSave),
                    (TextKey.StageDropboxListing, TextKey.StageDropboxSave)
                })
                {
                    var listingEnded = Array.FindIndex(events, fields => fields[2] == listingKey.ToString() && fields[5] == nameof(RunEvent.Ended));
                    var savingStarted = Array.FindIndex(events, fields => fields[2] == saveKey.ToString() && fields[5].Length == 0);
                    var savingEnded = Array.FindIndex(events, fields => fields[2] == saveKey.ToString() && fields[5] == nameof(RunEvent.Ended));
                    Check.Require(listingEnded >= 0 && savingStarted > listingEnded && savingEnded > savingStarted,
                        "each listing ends before its separately timed save drain starts and finishes");
                }
                var listings = events.Where(fields => fields[1] == nameof(ListingProgress)).ToArray();
                // D-1105-41 / RQ-178 adds a history continuation before the ordinary current-live listing.
                Check.Require(listings.Length == (baseline ? 3 : 2) &&
                    listings.Count(fields => fields[2] == nameof(Side.Local)) == 1 &&
                    listings.Count(fields => fields[2] == nameof(Side.Dropbox)) == (baseline ? 2 : 1),
                    "short listings publish one final snapshot per acquisition, including retained history");
                foreach (var side in new[] { Side.Local, Side.Dropbox })
                {
                    var sideListings = listings.Where(fields => fields[2] == side.ToString()).ToArray();
                    for (var index = 0; index < sideListings.Length; index++)
                    {
                        var listing = sideListings[index];
                        var observations = baseline && side == Side.Dropbox && index == 0 ? 0 : 2;
                        var count = baseline
                            ? Text.Get(TextKey.ListingEstimate, (TextArg.Count, observations.ToString("N0")),
                                (TextArg.SourceCount, 1.ToString("N0")),
                                (TextArg.Value, ((double)observations).ToString("P0", CultureInfo.InvariantCulture)))
                            : Text.Get(TextKey.ListingCount, (TextArg.Count, observations.ToString("N0")));
                        var key = side == Side.Local ? TextKey.StageListingLocal : TextKey.StageListingDropbox;
                        var expected = Text.Get(key, (TextArg.Count, 1.ToString("N0")),
                            (TextArg.Message, count), (TextArg.Path, listing[3]));
                        Check.Require(listing[4] == expected && messages.Contains(expected),
                            "each acquisition's final observations and baseline estimate reach both sinks");
                    }
                }
            });
        }
    }

    private static async Task PrefixIgnoreAcrossRepairsAsync()
    {
        foreach (var prefix in new[] { "~$", ".~" })
        foreach (var absent in new Side?[] { null, Side.Local, Side.Dropbox })
        {
            await WithFolder(nameof(PrefixIgnoreAcrossRepairsAsync) + prefix + absent, async folder =>
            {
                var pair = Pair() with { IgnoreRules = ["FileNamePrefix:" + prefix] };
                using var rig = NewRig(pair);
                var path = prefix + "Document";
                var localEntry = await PutAsync(rig.Local, path);
                var remoteEntry = await PutAsync(rig.Dropbox, path);
                RecordLinked(rig, localEntry, remoteEntry);
                if (absent == Side.Local) await rig.Local.DeleteAsync(localEntry, null, CancellationToken.None);
                if (absent == Side.Dropbox) await rig.Dropbox.DeleteAsync(remoteEntry, null, CancellationToken.None);
                await PutAsync(rig.Local, "Ordinary");
                var local = new ProxySide(rig.Local) { OnRead = value => Check.Require(value != path, "ignored target is never independently observed") };
                var remote = new ProxySide(rig.Dropbox) { OnRead = value => Check.Require(value != path, "ignored target is never independently observed") };
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var log = Log(rig, folder, attempt.ToString(CultureInfo.InvariantCulture));
                    Check.Require((await RunScanRepairAsync(Session(rig, log, local, remote), CancellationToken.None)).Completed,
                        "ignored saved-only and bilateral rows do not prevent ordinary Repair");
                    Check.Require(rig.Store.SyncedByKey(pair.PairId, Side.Local, PathNames.Key(path)) is not null &&
                        local.Hashes + remote.Hashes == 0, "ignored history stays intact without content work");
                }
                Check.Require((await rig.Local.ReadOneAsync(path, CancellationToken.None) is null) == (absent == Side.Local) &&
                    (await rig.Dropbox.ReadOneAsync(path, CancellationToken.None) is null) == (absent == Side.Dropbox) &&
                    await rig.Dropbox.ReadOneAsync("Ordinary", CancellationToken.None) is not null,
                    "ignored absence never deletes or recreates the counterpart while ordinary neighbors progress");
            });
        }
    }

    private static async Task ProductionDirectRepairAsync()
    {
        foreach (var failureMode in new[] { "none", "transient", "permanent" })
        {
            await WithFolder(nameof(ProductionDirectRepairAsync) + failureMode, async folder =>
            {
                var pair = Pair();
                var connection = DropboxConnectionBinding.CreateConnectionId("account-a");
                pair = pair with { ConnectionId = connection, Roots = pair.Roots! with { ConnectionId = connection } };
                using var instance = PilotInstanceLock.Acquire(Path.Combine(folder, "state", "instance.lock"));
                await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(new DropboxConnectionBinding(
                    DropboxProviderConfiguration.RegisteredClientId, "account-a", "namespace-a", "namespace-a",
                    DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
                    ConnectionId: connection));
                var captures = 0;
                var lists = 0;
                var accounts = 0;
                var metadata = 0;
                using var stop = new CancellationTokenSource();
                const int cancelOnCapture = 3;
                using var http = new HttpClient(new SourceHandler((request, token) =>
                {
                    var route = request.RequestUri!.AbsolutePath;
                    if (route == "/oauth2/token") return Task.FromResult(JsonResponse(new
                    { access_token = "synthetic-token", token_type = "bearer", expires_in = 14400, account_id = "account-a" }));
                    if (route.EndsWith("get_current_account", StringComparison.Ordinal))
                    {
                        accounts++;
                        Check.Require(!request.Headers.Contains("Dropbox-API-Path-Root"), "account acquisition is not a file route");
                        return Task.FromResult(JsonResponse(new { account_id = "account-a", root_info = new
                        { root_namespace_id = "namespace-a", home_namespace_id = "namespace-a" } }));
                    }
                    if (route.EndsWith("get_metadata", StringComparison.Ordinal))
                    {
                        metadata++;
                        return Task.FromResult(JsonResponse(new Dictionary<string, object>
                        {
                            [".tag"] = "folder", ["name"] = "RepairFixture", ["path_lower"] = pair.DropboxRoot.ToLowerInvariant(),
                            ["path_display"] = pair.DropboxRoot, ["id"] = "folder-a"
                        }));
                    }
                    if (route.EndsWith("/list_folder", StringComparison.Ordinal))
                    {
                        lists++;
                        return Task.FromResult(JsonResponse(new { entries = Array.Empty<object>(), cursor = "repair-terminal", has_more = false }));
                    }
                    if (route.EndsWith("get_latest_cursor", StringComparison.Ordinal))
                    {
                        captures++;
                        if (failureMode == "transient" && captures == cancelOnCapture)
                        {
                            stop.Cancel();
                            token.ThrowIfCancellationRequested();
                        }
                        if (failureMode != "none")
                        {
                            var response = JsonResponse(new { error_summary = failureMode == "transient"
                                ? "too_many_requests/.." : "path/not_found/.." });
                            response.StatusCode = failureMode == "transient" ? HttpStatusCode.TooManyRequests : HttpStatusCode.Conflict;
                            if (failureMode == "transient") response.Headers.RetryAfter =
                                new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                            return Task.FromResult(response);
                        }
                    }
                    return Task.FromResult(SourceHandler.DefaultResponse(request));
                }));
                await using var host = EngineHost.Compose(instance.StateAuthority.ProductRoot, _ => { },
                    instance.StateAuthority, Settings, http, TimeProvider.System,
                    id => RunLog.Open(folder, id, Guid.NewGuid().ToString("N"), _ => { }, Settings.LogBufferCharacters),
                    validator => StateStore.Open(StateStore.DatabasePath(folder), validator), EngineHost.Mode.Direct,
                    new EngineExternals(new SyntheticCredentials(), _ => new HttpClient(new SourceHandler())));
                host.Store.SavePair(pair);
                if (failureMode != "none")
                {
                    Exception? failure = null;
                    try { await host.Operations.RepairAsync(pair.PairId, stop.Token); }
                    catch (Exception error) { failure = error; }
                    if (failureMode == "transient")
                        Check.Require(failure is OperationCanceledException && stop.IsCancellationRequested &&
                            lists == 0 && captures == cancelOnCapture &&
                            host.Store.ReadPrerequisiteRetry(pair.PairId) is null && host.Store.Obligated(pair.PairId) &&
                            host.Store.LastRepair(pair.PairId) is null,
                            "persistent transient capture retries only that request until cancellation, without relisting or a second retry episode");
                    else
                        Check.Require(failure is not null && lists == 0 && captures == 1 &&
                            host.Store.ReadPrerequisiteRetry(pair.PairId)?.Attempts == 1 &&
                            host.Store.Issues(pair.PairId).Count(issue => issue.Cause == TextKey.RequiredRepairRetry) == 1,
                            "a permanent failed base capture records one prerequisite failure before any listing");
                    return;
                }
                var first = await host.Operations.RepairAsync(pair.PairId, CancellationToken.None);
                Check.Require(first.Completed && first.FirstRun && captures == 1 && lists == 1 &&
                    host.Store.ReadPrerequisiteRetry(pair.PairId) is null,
                    "direct production Repair establishes real bases before one acquisition and completes without host loops");
                var second = await host.Operations.RepairAsync(pair.PairId, CancellationToken.None);
                Check.Require(second.Completed && !second.FirstRun && captures == 1 && lists == 2,
                    "a current source base is reused while the next real Repair starts a fresh listing");
                var beforeExecution = host.Store.ReadPairExecution(pair.PairId);
                var beforeLocalSource = host.Store.ReadSource(pair.PairId, Side.Local);
                var beforeDropboxSource = host.Store.ReadSource(pair.PairId, Side.Dropbox);
                var beforeAccounts = accounts;
                var beforeMetadata = metadata;
                var rebound = await host.Operations.BindRootsAsync(pair.PairId, CancellationToken.None);
                // One root observation is one get_metadata, plus the token source's own account verification.
                Check.Require(rebound.Roots == pair.Roots && accounts == beforeAccounts + 2 &&
                    metadata == beforeMetadata + 1 && lists == 2,
                    "closed-generation bind performs one bilateral acquisition without a scan or implicit Repair " +
                    "(one auth verification plus the root account read): " +
                    "roots=" + (rebound.Roots == pair.Roots) + " accounts=" + (accounts - beforeAccounts) +
                    " metadata=" + (metadata - beforeMetadata) + " lists=" + lists);
                Check.Require(host.Store.ReadPairExecution(pair.PairId)!.Value.Generation == beforeExecution!.Value.Generation &&
                    host.Store.ReadPairExecution(pair.PairId)!.Value.Pair.Roots == beforeExecution.Value.Pair.Roots &&
                    host.Store.ReadSource(pair.PairId, Side.Local) == beforeLocalSource &&
                    host.Store.ReadSource(pair.PairId, Side.Dropbox) == beforeDropboxSource,
                    "matching rebind preserves generation, cursors, epochs and source obligations");
                host.Store.Commit(pair.PairId, StateChange.Empty with
                { Raise = [(IssueKind.RootIdentity, TextKey.RootUnavailable, string.Empty, new IssueSubject([], null))] });
                var rootNotice = host.Store.Issues(pair.PairId, [IssueKind.RootIdentity]).Single();
                var resolved = await host.Operations.ResolveAsync(pair.PairId, rootNotice.Id,
                    ResolutionChoice.BindRoots, null, null, false, null, CancellationToken.None);
                Check.Require(resolved.Resolved && host.Store.IssueById(pair.PairId, rootNotice.Id) is null &&
                    host.Store.ReadPairExecution(pair.PairId)!.Value.Generation == beforeExecution!.Value.Generation &&
                    host.Store.ReadPairExecution(pair.PairId)!.Value.Pair.Roots == beforeExecution.Value.Pair.Roots &&
                    host.Store.ReadSource(pair.PairId, Side.Local) == beforeLocalSource &&
                    host.Store.ReadSource(pair.PairId, Side.Dropbox) == beforeDropboxSource && lists == 2,
                    "the root notice action closes its exact notice without listing or invalidating sources");
                await host.DisposeAsync();
                using var stillBorrowed = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
                    "https://fixture.invalid/get_latest_cursor"));
                Check.Require(stillBorrowed.IsSuccessStatusCode, "host disposal leaves the borrowed scripted client alive");
            });
        }
    }

    private static Task EnumerationHandleIdentityAsync() => WithFolder(nameof(EnumerationHandleIdentityAsync), folder =>
    {
        using var rig = NewRig(Pair());
        var expected = rig.Pair.Roots!.Local;
        Exception? mismatch = null;
        try { _ = WindowsDirectoryListing.ListRoot(rig.Pair.LocalRoot,
            expected with { FileId = expected.FileId + 1 }).ToArray(); }
        catch (Exception error) { mismatch = error; }
        Check.Require(mismatch is not null && LocalErrors.RootReadCause(mismatch) == TextKey.RootLocalIdentityChanged,
            "the actual enumeration handle rejects a different observed identity");
        var generation = rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation;
        try
        {
            _ = PairSafety.ReadRootAsync(rig.Store, rig.Pair, generation, Side.Local,
                _ => Task.FromException<int>(mismatch!), default).GetAwaiter().GetResult();
        }
        catch (EngineRequestException) { }
        Check.Require(rig.Store.Issues(rig.Pair.PairId).Single().Cause == TextKey.RootLocalIdentityChanged,
            "native handle mismatch retains its precise shared-policy cause");
        _ = WindowsDirectoryListing.ListRoot(rig.Pair.LocalRoot, expected).ToArray();
        var moved = rig.Pair.LocalRoot + "-moved";
        Directory.Move(rig.Pair.LocalRoot, moved);
        Directory.Move(moved, rig.Pair.LocalRoot);
        return Task.CompletedTask;
    });

    private static Task PartialKeepBothRepairAsync() => WithFolder(nameof(PartialKeepBothRepairAsync), async folder =>
    {
        using var rig = NewRig(Pair());
        await CompleteBaselineAsync(rig, folder);
        const string path = "item.txt";
        var local = await PutAsync(rig.Local, path, [1, 1, 1, 1]);
        var remote = await PutAsync(rig.Dropbox, path, [2, 2, 2, 2]);
        var saved = SyncedEntries.From(local, remote) with
        { LocalModifiedTicks = local.Modified.AddDays(-1).UtcTicks, DropboxContentHash = "previous-content" };
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = [saved] });
        string? copiedLabel = null;
        string? missingLabel = null;
        using var log = Log(rig, folder, "repair");
        var input = Session(rig, log) with
        {
            BeforeRepair = async (context, token) =>
            {
                var row = new Row(PathNames.Key(path), local, remote, saved, [], null,
                    new RowContext(false, false, false, null, false, false, true, false));
                var runner = new KeepBothRunner(context, new Transfers(context));
                var labels = await runner.ChooseLabelsAsync(row, token);
                copiedLabel = labels.DropboxLabel;
                missingLabel = labels.LocalLabel;
                var occupant = await PutAsync(rig.Local, missingLabel!, [3, 3, 3, 3]);
                Exception? failure = null;
                try { await runner.RunAsync(row, labels, token); } catch (Exception error) { failure = error; }
                Check.Require(failure is PathOccupiedException &&
                    await rig.Dropbox.ReadOneAsync(copiedLabel!, token) is not null &&
                    rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, saved.LocalKey) == saved,
                    "partial keep-both retains its completed first copy and original binding");
                await rig.Local.DeleteAsync(occupant, null, token);
            }
        };
        var result = await RunScanRepairAsync(input, CancellationToken.None);
        Check.Require(result.Completed && copiedLabel is not null && missingLabel is not null &&
            rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key(path))?.DropboxPath == copiedLabel &&
            rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key(missingLabel))?.DropboxPath == path,
            "Repair reuses the completed copy and records both crossed links");
        Check.Require(result.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == 0 &&
            result.Effects.GetValueOrDefault(nameof(EffectKind.Downloaded)) == 1 &&
            rig.Store.Issues(rig.Pair.PairId).All(issue => issue.Cause == TextKey.InfoKeptBoth),
            "only the missing copy transfers and only the completed keep-both notice remains");
    });

    private static Task InitialEvidencePreservesCurrentDefinitionAsync() => WithFolder(nameof(InitialEvidencePreservesCurrentDefinitionAsync), async folder =>
    {
        var acquired = new DriveEvidence(null, null, null);
        await using var rig = await ComposeRigAsync(folder, recovery: new FakeRecovery { Evidence = acquired });
        using var log = rig.Log(folder, "merge");
        var updated = rig.Pair with { IgnoreRules = ["FileName:Unrelated"] };
        rig.Wire.BeforeListingAsync = (_, _) =>
        {
            rig.Store.SavePair(updated);
            return Task.CompletedTask;
        };
        var summary = await rig.RunAsync(log, CancellationToken.None);
        var current = rig.Store.Pair(rig.Pair.PairId)!;
        Check.Require(summary.Completed && current.IgnoreRules.SequenceEqual(updated.IgnoreRules) &&
            current.Roots == updated.Roots && current.Drive == acquired,
            "initial evidence merge reads current definition and changes only Drive, including acquired-empty evidence");
        var generation = rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation;
        rig.Store.MergeDriveEvidence(rig.Pair.PairId, generation, acquired);
        Check.Require(rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation == generation,
            "equal repeated evidence does not advance generation");
        Exception? stale = null;
        try { rig.Store.MergeDriveEvidence(rig.Pair.PairId, generation + 1, acquired); }
        catch (Exception error) { stale = error; }
        Check.Require(stale is LiveAdmissionStaleException && SamePairDefinition(rig.Store.Pair(rig.Pair.PairId), current),
            "stale evidence cannot overwrite or recreate a pair");
    });

    private static Task ScanResetRefusesOldWriterAsync() => WithFolder(nameof(ScanResetRefusesOldWriterAsync), async folder =>
    {
        using var rig = NewRig(Pair());
        var generation = rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation;
        var old = rig.Store.BeginScan(rig.Pair.PairId, generation, true);
        var writer = rig.Store.OpenScan(rig.Pair.PairId, Side.Dropbox, ScanTestSettings.BatchRows);
        await writer.AbortAsync();
        var replacement = rig.Store.BeginScan(rig.Pair.PairId, generation, false);
        Exception? refused = null;
        try { await writer.AddAsync(new Listed("Old", null), default); await writer.CompleteAsync(); }
        catch (Exception error) { refused = error; }
        finally { try { await writer.AbortAsync(); } catch (Exception) { } }
        Check.Require(refused is not null && replacement != old, "a late old writer cannot mutate its replacement scan");
        await rig.Store.OpenScan(rig.Pair.PairId, Side.Local, ScanTestSettings.BatchRows).CompleteAsync();
        await rig.Store.OpenScan(rig.Pair.PairId, Side.Dropbox, ScanTestSettings.BatchRows).CompleteAsync();
        Check.Require(!rig.Store.HasDeletedNames(rig.Pair.PairId), "nondiscovery replacement retains no old marker evidence");
    });

    private static async Task RootFailureClassificationAsync()
    {
        foreach (var (side, error, expected) in new (Side, Exception, TextKey)[]
        {
            (Side.Local, new UnauthorizedAccessException(), TextKey.RootLocalAccessDenied),
            (Side.Local, new IOException(), TextKey.RootLocalIdentityUnavailable),
            (Side.Local, new UnsupportedNetworkRootException(nameof(Side.Local)), TextKey.RootLocalNetworkUnsupported),
            (Side.Local, new UnsupportedRemovableRootFormatException(nameof(Side.Local)), TextKey.RootLocalFormatUnsupported),
            (Side.Dropbox, new ChangedUnderneathException("Root"), TextKey.RootDropboxFolderChanged),
            (Side.Dropbox, new HttpRequestException(), TextKey.RootDropboxTransportUnavailable),
            (Side.Dropbox, new JsonException(), TextKey.RootDropboxIdentityInvalid)
        })
        {
            await WithFolder(nameof(RootFailureClassificationAsync) + expected, folder =>
            {
                using var rig = NewRig(Pair());
                var generation = rig.Store.ReadPairExecution(rig.Pair.PairId)!.Value.Generation;
                Check.Require(IsRootReadFailure(side, error), "root-only boundary recognizes the classified adapter error");
                Exception mapped;
                try
                {
                    _ = PairSafety.ReadRootAsync(rig.Store, rig.Pair, generation, side,
                        _ => Task.FromException<bool>(error), default).GetAwaiter().GetResult();
                    throw new InvalidOperationException("a classified root read failure must refuse");
                }
                catch (EngineRequestException caught) { mapped = caught; }
                var issue = rig.Store.Issues(rig.Pair.PairId).Single();
                Check.Require(mapped is EngineRequestException && issue.Kind == IssueKind.RootIdentity &&
                    issue.Cause == expected && issue.Subject.Incoming == side,
                    "shared root policy persists the exact side-specific reason, not item absence");
                return Task.CompletedTask;
            });
        }
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var error in new Exception[] { new OperationCanceledException(),
            new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged), new InvalidOperationException() })
            Check.Require(!IsRootReadFailure(side, error), "cancellation, stale authority and programming errors are not root absence");
    }

    private static bool IsRootReadFailure(Side side, Exception error) => side == Side.Local
        ? LocalErrors.RootReadCause(error) is not null
        : DropboxErrors.RootReadCause(error) is not null;

    private static async Task ScanReservationAuthorityAsync()
    {
        const string pair = "Pair";
        const string other = "Other";
        var owner = new ScanOwnership();
        var foreign = new ScanOwnership();
        var held = await owner.EnterAsync([pair], () => true, CancellationToken.None);
        held.Require(owner, pair);
        foreach (var wrongOwner in new[] { false, true })
        {
            Exception? error = null;
            try { held.Require(wrongOwner ? foreign : owner, wrongOwner ? pair : other); }
            catch (Exception refusal) { error = refusal; }
            Check.Require(error is not null, "a reservation cannot authorize a different owner or pair");
        }
        using var cancel = new CancellationTokenSource();
        var waiting = owner.EnterAsync([pair], () => true, cancel.Token).AsTask();
        Check.Require(!waiting.IsCompleted, "an existing real scan excludes another scan of the pair");
        cancel.Cancel();
        Exception? canceled = null;
        try { await waiting; } catch (Exception error) { canceled = error; }
        Check.Require(canceled is OperationCanceledException, "queued cancellation joins without acquiring the held scan");
        await held.DisposeAsync();
        Exception? disposed = null;
        try { held.Require(owner, pair); } catch (Exception error) { disposed = error; }
        Check.Require(disposed is not null, "disposed reservation never regains authority");
        Exception? stale = null;
        try { await owner.EnterAsync([pair], () => false, CancellationToken.None); }
        catch (Exception error) { stale = error; }
        Check.Require(stale is LiveAdmissionStaleException, "grant refusal releases its acquired resource");
        await using var next = await owner.EnterAsync([pair], () => true, CancellationToken.None);
        next.Require(owner, pair);
    }

    public static void ForgottenFailure() => ForgottenFailureAsync().GetAwaiter().GetResult();

    private static Task ForgottenFailureAsync() => WithFolder(nameof(ForgottenFailureAsync), async folder =>
    {
        await using var rig = await ComposeRigAsync(folder);
        var saved = new[] { "Absent-A", "Absent-B" }.Select(path => new SyncedEntry(PathNames.Key(path), path,
            PathNames.DropboxKey(path), path, false, "local-" + path, 0, 0, "remote-" + path, "")).ToArray();
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = saved });
        using var log = rig.Log(folder, "failure");
        for (var run = 0; run < 3; run++)
        {
            var fail = run < 2;
            var retries = new PrefixSet();
            if (run != 0) retries.Add(saved[0].LocalKey);
            await rig.Host.DispatchAsync(rig.Pair.PairId, async (registration, pair, generation, token) =>
            {
                var context = PairSession.Open(rig.Store, pair, Settings, rig.Http, false, log,
                    rig.Instance.StateAuthority, rig.Host, registration, generation, TimeProvider.System).Context;
                context = context with { Local = new ProxySide(context.Local)
                {
                    ReadFault = path => fail && path == saved[0].LocalPath
                        ? new ItemFailedException(path, TextKey.FailLocalInUse, null) : null
                } };
                var boundary = RefusingExecutionBoundary.Instance;
                var jobs = SyncJobs.Create(context, new SyncPorts(boundary, RefusingExecutionBoundary.Instance, boundary, 0));
                foreach (var entry in run == 0 ? saved : saved[..1])
                {
                    var row = new Row(entry.LocalKey, null, null, entry, [], null,
                        new RowContext(false, false, false, null, false, false, false, false));
                    var outcome = await jobs.Items.RunRowAttemptAsync(row, token, itemRetries: retries);
                    Check.Require(outcome.Raised == (fail && entry == saved[0] ? IssueKind.Failure : null),
                        "saved-only local-access failure stays item-scoped while independent work completes");
                }
                return true;
            }, CancellationToken.None);
            if (fail)
            {
                Check.Require(rig.Store.SyncedCount(rig.Pair.PairId) == 1 &&
                    rig.Store.Issues(rig.Pair.PairId).Count(issue => issue.Kind == IssueKind.Failure) == 1,
                    "repeated failure retains only the failed saved row and one retry episode");
            }
        }
        Check.Require(rig.Store.SyncedCount(rig.Pair.PairId) == 0 &&
            rig.Store.Issues(rig.Pair.PairId).All(issue => issue.Kind != IssueKind.Failure),
            "a later successful attempt closes the saved-only failure episode");
    });

    public static void ConcurrentForgottenStore() => ConcurrentForgottenAsync(false).GetAwaiter().GetResult();
    public static void ConcurrentForgottenMemory() => ConcurrentForgottenAsync(true).GetAwaiter().GetResult();

    private static Task ConcurrentForgottenAsync(bool memory) => WithFolder(nameof(ConcurrentForgottenAsync) + memory, async folder =>
    {
        await using var rig = await ComposeRigAsync(folder, decorate: inner =>
        {
            if (!memory) return new HookStore(inner);
            inner.Dispose();
            return new HookStore(new InMemoryStateStore());
        });
        var paths = new[] { "Absent-A", "Absent-B" };
        for (var run = 0; run < 2; run++)
        {
            rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
            {
                Record = paths.Select(path => new SyncedEntry(PathNames.Key(path), path,
                    PathNames.DropboxKey(path), path, false, "local-" + path, 0, 0,
                    "remote-" + path, "")).ToArray()
            });
            var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = 0;
            rig.Transport.Respond = async (request, token) =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/get_metadata", StringComparison.Ordinal)) return null;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var path = body.RootElement.GetProperty("path").GetString()!;
                if (!paths.Any(value => path.EndsWith("/" + value, StringComparison.OrdinalIgnoreCase))) return null;
                if (Interlocked.Increment(ref entered) == paths.Length) bothEntered.TrySetResult();
                await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                return null;
            };
            rig.Wire.Calls.Clear();
            using var log = rig.Log(folder, "forgotten-" + run);
            Check.Require((await rig.RunAsync(log, CancellationToken.None)).Completed,
                "distinct absent saved files finish concurrently in one Repair");
            Check.Require(entered == paths.Length && rig.Store.SyncedCount(rig.Pair.PairId) == 0,
                "both history-only attempts overlap and settle, including a later invocation");
            Check.Require(Lines(log.Path).Count(fields => fields[1] == nameof(RunEventKind.Effect) && fields[2] == nameof(EffectKind.Forgotten)) == paths.Length,
                "each absent saved file reports Forgotten");
            Check.Require(rig.Wire.Calls.All(route => !route.EndsWith("/upload", StringComparison.Ordinal) &&
                !route.Contains("/delete", StringComparison.Ordinal) && !route.Contains("/move", StringComparison.Ordinal)),
                "forgetting history performs no provider writes");
        }
    });

    private static Task RegisteredItemIdentityAsync() => WithFolder(nameof(RegisteredItemIdentityAsync), async folder =>
    {
        await using var rig = await ComposeRigAsync(folder);
        const string path = "Owned";
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, path), "content");
        var foreign = new IssueSubject([new IssueItem(Side.Local, "Foreign", "foreign-id")], null);
        using var log = rig.Log(folder, "authority");
        await rig.Host.DispatchAsync(rig.Pair.PairId,
            async (registration, pair, generation, token) =>
            {
                var context = PairSession.Open(rig.Store, pair, Settings, rig.Http, false, log,
                    rig.Instance.StateAuthority, rig.Host, registration, generation, TimeProvider.System).Context;
                var entry = (await context.Local.ReadOneAsync(path, token))!;
                var subject = new IssueSubject([new IssueItem(Side.Local, path, entry.Id)], null);
                var epoch = rig.Store.ReadSource(rig.Pair.PairId, Side.Local)!.Value.Epoch;
                await context.ItemAttempts.ExecuteAsync(context.Opportunity, null, subject, (attempt, _) =>
                {
                    Exception? refused = null;
                    try { context.ItemAttempts.ReportFailure(attempt, epoch, "Foreign", foreign,
                        TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null); }
                    catch (Exception error) { refused = error; }
                    Check.Require(refused is not null && rig.Store.ReadItemRetryForSubject(rig.Pair.PairId,
                        context.ExpectedGeneration, "Foreign", foreign) is null,
                        "a valid same-pair attempt cannot borrow another subject");
                    var first = context.ItemAttempts.ReportFailure(attempt, epoch, path, subject,
                        TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null);
                    var duplicate = context.ItemAttempts.ReportFailure(attempt, epoch, path, subject,
                        TextKey.FailLocalInUse, null, ItemRetryDispatch.OrdinaryItem, null, null);
                    Check.Require(first == duplicate && SameItemRetry(rig.Store.ReadItemRetryForSubject(
                        rig.Pair.PairId, context.ExpectedGeneration, path, subject), first),
                        "duplicate report reuses the exact real Store lifecycle, without another obligation");
                    return Task.FromResult(true);
                }, token);
                return true;
            }, CancellationToken.None);
        await rig.RunAsync(log, CancellationToken.None);
        Check.Require(File.ReadAllText(Path.Combine(rig.Pair.LocalRoot, path)) == "content",
            "registered failure reporting never deletes the original bytes");
    });

    private static Task SecondSinkFailureJoinsAsync() => WithFolder(nameof(SecondSinkFailureJoinsAsync), async folder =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new IOException(nameof(SecondSinkFailureJoinsAsync));
        var failing = true;
        var localRows = 0;
        await using var rig = await ComposeRigAsync(folder, decorate: inner => new HookStore(inner)
        {
            BeforeOpenScan = side => { if (failing && side == Side.Dropbox) throw primary; },
            WrapSink = (side, sink) => new ObservedSink(
                new AbortSink(sink, null, () => entered.TrySetResult(), () => release.Task),
                () => { if (side == Side.Local) localRows++; })
        });
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "local-only"), "content");
        rig.Wire.Calls.Clear();
        using var log = rig.Log(folder, "failed");
        var run = rig.RunAsync(log, CancellationToken.None);
        Exception? failure = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check.Require(!run.IsCompleted && localRows == 0 &&
                rig.Wire.Calls.All(route => !route.EndsWith("/list_folder", StringComparison.Ordinal)),
                "second-sink open failure joins the first sink before releasing its scan and registration");
        }
        finally
        {
            release.TrySetResult();
            try { await run; } catch (Exception error) { failure = error; }
        }
        Check.Require(ReferenceEquals(failure, primary) && rig.Store.LastRepair(rig.Pair.PairId) is null,
            "the original sink-open error survives joined cleanup without completion");
        failing = false;
        using var next = rig.Log(folder, "next");
        Check.Require((await rig.RunAsync(next, CancellationToken.None)).Completed,
            "joined failed ownership leaves a subsequent real Repair runnable");
    });

    private static async Task PrerequisitePageRetryAsync()
    {
        foreach (var cancel in new[] { false, true })
        {
            await WithFolder(nameof(PrerequisitePageRetryAsync) + cancel, async folder =>
            {
                var localListings = 0;
                await using var rig = await ComposeRigAsync(folder, decorate: inner => new HookStore(inner)
                {
                    WrapSink = (side, sink) => { if (side == Side.Local) localListings++; return sink; }
                });
                using var stop = new CancellationTokenSource();
                const string cursor = "listing-page";
                const int failedResponses = 3;
                var roots = 0;
                var continuations = 0;
                var store = rig.Store;
                long? noticeId = null;
                using var log = rig.Log(folder, "retry");
                rig.Transport.Respond = async (request, token) =>
                    {
                        var route = request.RequestUri!.AbsolutePath;
                        if (route.EndsWith("/list_folder", StringComparison.Ordinal))
                        {
                            roots++;
                            return JsonResponse(new { entries = Array.Empty<object>(), cursor, has_more = true });
                        }
                        if (route.EndsWith("/continue", StringComparison.Ordinal))
                        {
                            using var args = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                            if (args.RootElement.GetProperty("cursor").GetString() != cursor)
                                return null;
                            continuations++;
                            Check.Require(rig.Store.ReadPrerequisiteRetry(rig.Pair.PairId) is null,
                                "a failed page never advances the durable pair retry episode");
                            if (continuations > 1)
                            {
                                var notice = rig.Store.Issues(rig.Pair.PairId).Single(issue => issue.Cause == TextKey.RequiredRepairRetry);
                                noticeId ??= notice.Id;
                                Check.Require(noticeId == notice.Id && store.DiscoveryStarts.Count == 1 && roots == 1,
                                    "persistent same-page failure retains one notice, initial scan and input cursor");
                            }
                            if (continuations <= failedResponses)
                            {
                                var response = JsonResponse(new { error_summary = "too_many_requests/.." });
                                response.StatusCode = HttpStatusCode.TooManyRequests;
                                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                                return response;
                            }
                            if (cancel) { stop.Cancel(); token.ThrowIfCancellationRequested(); }
                            return JsonResponse(new { entries = Array.Empty<object>(), cursor = "listing-complete", has_more = false });
                        }
                        return null;
                };
                RepairSummary? result = null;
                Exception? failure = null;
                try { result = await rig.RunAsync(log, stop.Token); }
                catch (Exception error) { failure = error; }
                Check.Require(roots == 1 && continuations == failedResponses + 1 && localListings == 1,
                    "only the unpublished continuation retries; Local and root acquisition are not repeated");
                Check.Require(cancel ? failure is OperationCanceledException : failure is null && result?.Completed == true,
                    "cancellation ends the existing Repair; successful sealing permits its ordinary completion");
                Check.Require(rig.Store.ReadPrerequisiteRetry(rig.Pair.PairId) is null &&
                    rig.Store.Issues(rig.Pair.PairId).All(issue => issue.Cause != TextKey.RequiredRepairRetry),
                    "page-only contribution closes on success or cancel without a pair failure");
            });
        }
    }

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static async Task UnreliableFirstRunContentAsync()
    {
        foreach (var equal in new[] { false, true })
        foreach (var saved in new[] { false, true })
        {
            await WithFolder(nameof(UnreliableFirstRunContentAsync) + equal + saved, async folder =>
            {
                using var rig = NewRig(Pair());
                const string path = "Item";
                var local = await PutAsync(rig.Local, path, [1]);
                var remote = await PutAsync(rig.Dropbox, path, equal ? [1] : [2]);
                var future = DateTimeOffset.MaxValue;
                local = local with { Modified = future };
                remote = remote with { Modified = future };
                rig.Local.Seed(local, [1]);
                rig.Dropbox.Seed(remote, equal ? [1] : [2]);
                if (saved) RecordLinked(rig, local, remote);
                var observed = new ProxySide(rig.Local);
                using var log = Log(rig, folder, "first");
                var result = await RunScanRepairAsync(Session(rig, log, local: observed), CancellationToken.None);
                Check.Require(result.Completed, "unreliable-time content comparison reaches real Repair completion");
                if (saved)
                    Check.Require(observed.Hashes == 0 && result.Effects.Count == 0,
                        "source-gap responsibility cannot turn a recorded same-stamp row into a content recheck");
                else if (equal)
                    Check.Require(observed.Hashes == 1 && rig.Store.Issues(rig.Pair.PairId).Count == 0 &&
                        result.Effects.GetValueOrDefault(nameof(EffectKind.Uploaded)) == 0 &&
                        result.Effects.GetValueOrDefault(nameof(EffectKind.Downloaded)) == 0,
                        "equal unreliable no-record copies compare once and record quietly");
                else
                {
                    Check.Require(observed.Hashes >= 1 && rig.Store.Issues(rig.Pair.PairId).Count(issue =>
                        issue.Cause == TextKey.InfoKeptBoth) == 1, "different unreliable copies retain both with one notice");
                    var versions = new HashSet<string>(StringComparer.Ordinal);
                    await foreach (var item in rig.Local.ReadAllAsync(string.Empty, new ListOptions(true, false), CancellationToken.None))
                        if (item.Entry is { IsFolder: false } entry) versions.Add(await rig.Local.ContentHashAsync(entry, CancellationToken.None));
                    Check.Require(versions.Contains(await rig.Dropbox.ContentHashAsync(remote, CancellationToken.None)) && versions.Count == 2,
                        "both unique byte versions survive instead of a single-winner overwrite");
                }
            });
        }
    }

    private static async Task DiscoveryContractAsync()
    {
        foreach (var first in new[] { false, true })
        foreach (var retained in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        {
            await WithFolder(nameof(DiscoveryContractAsync) + first + retained, async folder =>
            {
                using var rig = NewRig(Pair());
                if (!first) await CompleteBaselineAsync(rig, folder);
                const string path = "Item";
                var item = await PutAsync(rig.Local, path);
                if (retained != IssueSubject.FirstRunDeletionKind.None)
                    rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
                    {
                        Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, path,
                            new IssueSubject([new IssueItem(Side.Local, path, item.Id)], null, FirstRunDeletion: retained))]
                    });
                var local = new ProxySide(rig.Local);
                var remote = new ProxySide(rig.Dropbox);
                var store = new HookStore(rig.Store);
                using var log = Log(rig, folder, "run");
                var discovery = first || retained is IssueSubject.FirstRunDeletionKind.Item or
                    IssueSubject.FirstRunDeletionKind.UnscannedSubtree;
                var result = await ScanPassChecks.RunRepairAsync(
                    new ScanPassChecks.Rig(rig.Pair, store, local, remote, rig.Recovery, rig.Admission, rig.Scope),
                    log, Settings, includeDeleted: discovery);
                Check.Require(result.Completed && result.FirstRun == first &&
                    store.DiscoveryStarts.SequenceEqual(new[] { discovery }) &&
                    local.ListingOptions.Count == 1 && !local.ListingOptions[0].IncludeDeleted &&
                    remote.ListingOptions.Count == 1 && remote.ListingOptions[0].IncludeDeleted == discovery,
                    "one discovery decision reaches one pair scan and only its remote listing");
                var recovery = new RecoveryProbe(new FakeRecovery());
                await using var composed = await ComposeRigAsync(Path.Combine(folder, "composed"), recovery: recovery);
                if (!first)
                {
                    using var baseline = composed.Log(folder, "baseline");
                    Check.Require((await composed.RunAsync(baseline, CancellationToken.None)).Completed,
                        "the composed baseline completes before discovery selection");
                }
                await File.WriteAllTextAsync(Path.Combine(composed.Pair.LocalRoot, path), "content");
                composed.Store.DiscoveryStarts.Clear();
                var reads = recovery.Reads;
                var passes = composed.Store.ScanPasses;
                var existence = composed.Store.DiscoveryExistenceReads;
                composed.Wire.Calls.Clear();
                composed.Wire.MetadataPaths.Clear();
                using var runLog = composed.Log(folder, "composed-run");
                var composedResult = await composed.RunAsync(runLog, CancellationToken.None, async (context, token) =>
                {
                    if (retained == IssueSubject.FirstRunDeletionKind.None) return;
                    var retainedEntry = (await context.Local.ReadOneAsync(path, token))!;
                    composed.Store.Commit(composed.Pair.PairId, StateChange.Empty with
                    {
                        Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, path,
                            new IssueSubject([new IssueItem(Side.Local, path, retainedEntry.Id)], null, FirstRunDeletion: retained))]
                    });
                });
                Check.Require(composedResult.Completed && composedResult.FirstRun == first &&
                    composed.Store.DiscoveryStarts.SequenceEqual(new[] { true }) &&
                    recovery.Reads - reads == 1 && composed.Store.ScanPasses - passes == 1 &&
                    composed.Store.DiscoveryExistenceReads - existence == 1 &&
                    composed.Wire.MetadataPaths.Count(path => path == DropboxWire.RootId || path == composed.Pair.DropboxRoot) == 2 &&
                    composed.Wire.Calls.Count(route => route.EndsWith("/list_folder", StringComparison.Ordinal)) == 1,
                    "H_pre/H_post surround exactly one acquisition; no finish read or non-discovery relation query: " +
                    $"first={first} retained={retained} result={composedResult.Completed}/{composedResult.FirstRun} " +
                    $"discovery={string.Join(',', composed.Store.DiscoveryStarts)} reads={recovery.Reads - reads} " +
                    $"passes={composed.Store.ScanPasses - passes} existence={composed.Store.DiscoveryExistenceReads - existence} " +
                    "routes=" + string.Join(',', composed.Wire.Calls));
            });
        }
    }

    private static Task AncestorRefusalCompletesAsync() => WithFolder("ancestor-refusal-completes", async folder =>
    {
        IStateStore store = null!;
        var inject = false;
        var listings = 0;
        await using var rig = await ComposeRigAsync(folder, decorate: inner =>
        {
            store = inner;
            return new HookStore(inner)
            {
                WrapSink = (_, sink) => { listings++; return sink; },
                OnScanRows = () =>
                {
                    if (!inject) return;
                    inject = false;
                    var pair = store.Pairs().Single();
                    var generation = store.ReadPairExecution(pair.PairId)!.Value.Generation;
                    var source = store.ReadSource(pair.PairId, Side.Dropbox)!.Value;
                    var parent = store.SyncedByKey(pair.PairId, Side.Dropbox, PathNames.DropboxKey("Parent"))!;
                    store.AcquireDropboxPage(pair.PairId, generation, source.Epoch, source.Revision,
                        source.Provenance!, source.Cursor,
                        [new ChangeNotice(Side.Dropbox, ChangeKind.Changed, "Parent", null,
                            parent.DropboxId, null, false, false)], source.Cursor!);
                }
            };
        });
        Directory.CreateDirectory(Path.Combine(rig.Pair.LocalRoot, "Parent"));
        rig.Wire.SeedFolder("Parent");
        using (var baseline = rig.Log(folder, "baseline"))
            Check.Require((await rig.RunAsync(baseline, default)).Completed, "ancestor-refusal baseline completes");
        var pairId = rig.Pair.PairId;
        var generation = store.ReadPairExecution(pairId)!.Value.Generation;
        store.RequireRepairAfterLoss(pairId, generation);
        Check.Require(store.Obligated(pairId), "ancestor-refusal control starts with a Repair obligation");
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "Parent", "Child"), "content");
        inject = true;
        listings = 0;
        using var log = rig.Log(folder, "refused-row");
        var summary = await rig.RunAsync(log, default);
        Check.Require(!inject && summary.Completed && !store.Obligated(pairId) && listings == 2,
            "an ancestor-refused row completes Repair and clears its obligation after one pair listing");
        Check.Require(rig.Wire.Snapshot().All(item => item.Path != "Parent/Child") &&
            File.ReadAllText(Path.Combine(rig.Pair.LocalRoot, "Parent", "Child")) == "content",
            "the ancestor-refused row takes no effect and keeps its source");
        var issue = store.Issues(pairId).Single(value => value.Path == "Parent/Child");
        Check.Require(issue.Kind == IssueKind.Failure && issue.Cause == TextKey.FailChanged &&
            store.PendingAncestors(pairId, generation, Side.Dropbox, [PathNames.DropboxKey("Parent")], 0).Any(),
            "the exact failure and later ancestor notice survive successful Repair completion");
        using var ordinary = rig.Log(folder, "ordinary-successor");
        await rig.Host.DispatchSessionAsync(pairId, () => ordinary, async (session, ct) =>
        {
            await session.Syncer.SyncWorkAsync(ct);
            return true;
        }, default);
        var operations = new EngineOperations(rig.Instance.StateAuthority.ProductRoot, _ => { }, rig.Host);
        await operations.ResolveAsync(pairId, issue.Id, ResolutionChoice.Retry, null, null, false, null, default);
        Check.Require(store.IssueById(pairId, issue.Id) is null &&
            rig.Wire.Snapshot().Any(item => item.Path == "Parent/Child") &&
            !store.Obligated(pairId) && listings == 2,
            "ordinary ancestor work and exact issue Retry settle the refused child without another pair listing");
    });

    private static async Task RootCheckpointRefusalAsync()
    {
        foreach (var checkpoint in new[] { 1, 2 })
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            await WithFolder(nameof(RootCheckpointRefusalAsync) + checkpoint + side, async folder =>
            {
                var observations = 0;
                var merges = 0;
                var listings = new int[2];
                var acquired = new DriveEvidence(1, 1, 100);
                var recovery = new RecoveryProbe(new FakeRecovery { Evidence = acquired });
                await using var rig = await ComposeRigAsync(folder, recovery: recovery, decorate: inner => new HookStore(inner)
                {
                    OnMergeDrive = (_, _, value) =>
                    { Check.Require(value == acquired, "root refusal retains acquired initial Drive"); merges++; },
                    WrapSink = (provider, sink) => { listings[provider == Side.Local ? 0 : 1]++; return sink; }
                });
                await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "Unsent"), "content");
                var store = rig.Store;
                rig.Transport.Respond = (request, _) =>
                {
                    if (!request.RequestUri!.AbsolutePath.EndsWith("/get_metadata", StringComparison.Ordinal))
                        return Task.FromResult<HttpResponseMessage?>(null);
                    observations++;
                    if (observations == checkpoint)
                    {
                        if (side == Side.Dropbox)
                            return Task.FromResult<HttpResponseMessage?>(JsonResponse(new Dictionary<string, object>
                            {
                                [".tag"] = "folder", ["id"] = "id:replacement", ["name"] = "Pilot",
                                ["path_display"] = "/Pilot", ["path_lower"] = "/pilot"
                            }));
                        Directory.Move(rig.Pair.LocalRoot, rig.Pair.LocalRoot + "-original");
                        Directory.CreateDirectory(rig.Pair.LocalRoot);
                    }
                    return Task.FromResult<HttpResponseMessage?>(null);
                };
                using var log = rig.Log(folder, "refused");
                Exception? failure = null;
                try { await rig.RunAsync(log, CancellationToken.None); }
                catch (Exception error) { failure = error; }
                Check.Require(failure is EngineRequestException && observations == checkpoint && merges == 1,
                    "changed root refuses at its reached checkpoint, with one initial evidence merge");
                Check.Require(listings.All(count => count == checkpoint - 1) && recovery.Reads == 1 &&
                    store.ScanPasses == 0 && store.DiscoveryExistenceReads == 0 &&
                    rig.Wire.Snapshot().All(item => item.Path != "Unsent") &&
                    rig.Store.LastRepair(rig.Pair.PairId) is null,
                    "a refused checkpoint grants no scan-dependent action or completion authority");
                Check.Require(rig.Store.Issues(rig.Pair.PairId).Single().Kind == IssueKind.RootIdentity,
                    "the exact root owner records the refusal rather than an item failure");
                using var inspect = new SqliteConnection("Data Source=" + Path.Combine(folder, "composed.db") + ";Mode=ReadOnly");
                inspect.Open();
                using var marker = inspect.CreateCommand();
                marker.CommandText = "SELECT count(*) FROM pair WHERE deleted_history_cursor IS NOT NULL;";
                Check.Require((long)marker.ExecuteScalar()! == 0,
                    "A root refusal, including the post-list checkpoint, publishes no completed history marker");
            });
        }
    }

    private static async Task UpgradedMappedBindingAsync()
    {
        foreach (var renamed in new[] { false, true })
        {
            await WithFolder(nameof(UpgradedMappedBindingAsync) + renamed, async folder =>
            {
                const string localName = "A_B";
                const string invalidName = "A:B";
                const string validName = "AB";
                var pair = Pair();
                var local = new FakeSide(Side.Local);
                var remote = new FakeSide(Side.Dropbox);
                var originalLocal = await PutAsync(local, localName);
                var originalRemote = await PutAsync(remote, invalidName);
                var saved = SyncedEntries.From(originalLocal, originalRemote) with { DropboxKey = PathNames.Key(localName) };
                var path = StateStore.DatabasePath(folder);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = path, Pooling = false }.ToString()))
                {
                    StateStore.OpenConnection(connection);
                    void Execute(string sql, params (string Name, object? Value)[] parameters)
                    {
                        using var command = StorageCommand.Create(connection, null, sql, parameters);
                        command.ExecuteNonQuery();
                    }
                    Execute(StateSchema.Create);
                    Execute(StateSchema.PublishVersion.Replace(StateSchema.Version.ToString(CultureInfo.InvariantCulture),
                        StateSchema.UpgradeFromVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
                    Execute(StateSchema.UpsertPair, (StateSchema.ParamPair, pair.PairId),
                        (StateSchema.ParamDef, JsonSerializer.Serialize(pair)));
                    Execute(StateSchema.InsertSynced, (StateSchema.ParamPair, pair.PairId),
                        (StateSchema.ParamLocalPath, saved.LocalPath), (StateSchema.ParamLocalKey, saved.LocalKey),
                        (StateSchema.ParamDropboxKey, saved.DropboxKey), (StateSchema.ParamDropboxPath, saved.DropboxPath),
                        (StateSchema.ParamIsFolder, saved.IsFolder), (StateSchema.ParamLocalId, saved.LocalId),
                        (StateSchema.ParamLocalSize, saved.LocalSize), (StateSchema.ParamLocalMtime, saved.LocalModifiedTicks),
                        (StateSchema.ParamDropboxId, saved.DropboxId), (StateSchema.ParamDropboxHash, saved.DropboxContentHash));
                }
                if (renamed) await remote.MoveAsync(originalRemote, validName, CancellationToken.None);
                using var store = OpenStore(path);
                var upgraded = store.SyncedById(pair.PairId, Side.Dropbox, originalRemote.Id)!;
                Check.Require(upgraded.DropboxKey == PathNames.Key(invalidName) && upgraded.LocalPath == localName,
                    "the real schema upgrade converts only the raw remote key before Repair");
                var deletes = 0;
                var observed = new ProxySide(remote)
                {
                    Extra = renamed ? [] : [originalRemote with { Skip = nameof(TextKey.SkipReservedName) }]
                };
                using var log = RunLog.Open(folder, pair.PairId, "upgrade", _ => { }, Settings.LogBufferCharacters);
                var summary = await RunScanRepairAsync(new SessionInputs(store, pair, Settings, new PathScope(pair, [pair]),
                    new ProxySide(local) { OnDelete = _ => deletes++ }, observed, new FakeRecovery(),
                    new FakeAdmission { Level = 3 }, false, log), CancellationToken.None);
                Check.Require(summary.Completed && deletes == 0, "upgrade and Repair never delete the mapped local bytes");
                if (renamed)
                    Check.Require(summary.Effects.GetValueOrDefault(nameof(EffectKind.MovedLocal)) == 1 &&
                        await local.ReadOneAsync(validName, CancellationToken.None) is not null &&
                        await local.ReadOneAsync(localName, CancellationToken.None) is null &&
                        store.SyncedById(pair.PairId, Side.Dropbox, originalRemote.Id)?.DropboxKey == PathNames.Key(validName) &&
                        store.Issues(pair.PairId).All(issue => issue.Kind is not (IssueKind.Skipped or IssueKind.NameClash)),
                        "same-ID valid rename follows once, without historical-invalid-name skip or collision hold");
                else
                    Check.Require(await local.ReadOneAsync(localName, CancellationToken.None) == originalLocal &&
                        summary.Effects.Count == 0 && store.Issues(pair.PairId).Count(issue =>
                            issue.Kind == IssueKind.Skipped && issue.Cause == TextKey.SkipReservedName) == 1,
                        "the existing invalid binding preserves local bytes and produces one skip notice");
            });
        }
    }

    private static async Task RetainedBilateralSettlementAsync()
    {
        foreach (var folderItem in new[] { false, true })
        foreach (var kind in new[] { IssueSubject.FirstRunDeletionKind.Item,
            IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice })
        foreach (var saved in new[] { false, true })
        {
            // The per-case folder name feeds the scan database path, whose ".scan-"+64 hex+".db" suffix must
            // stay inside Windows' 259-character MAX_PATH; only the kind needs a short spelling.
            await WithFolder(nameof(RetainedBilateralSettlementAsync) + folderItem +
                (kind == IssueSubject.FirstRunDeletionKind.Item ? "item" : "preserve") + saved, async folder =>
            {
                using var rig = NewRig(Pair());
                const string path = "Retained";
                var local = folderItem ? await rig.Local.EnsureFolderAsync(path, CancellationToken.None)
                    : await PutAsync(rig.Local, path);
                var remote = folderItem ? await rig.Dropbox.EnsureFolderAsync(path, CancellationToken.None)
                    : await PutAsync(rig.Dropbox, path);
                if (saved) RecordLinked(rig, local, remote);
                rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, path,
                        new IssueSubject([new IssueItem(Side.Local, path, local.Id)], null, FirstRunDeletion: kind))]
                });
                var original = rig.Store.Issues(rig.Pair.PairId).Single();
                var plainInfo = 0;
                var queued = 0;
                var store = new HookStore(rig.Store)
                {
                    OnCommit = change => plainInfo += change.Raise.Count(item => item.Cause == TextKey.InfoDropboxHistoryUnchecked),
                    Record = (pairId, change) =>
                    {
                        // rev3h G05: every first-run Record enters CommitRecordAsync; its Info is delivered there.
                        plainInfo += change.Raise.Count(item => item.Cause == TextKey.InfoDropboxHistoryUnchecked);
                        queued++;
                        return rig.Store.CommitRecordAsync(pairId, change, null);
                    }
                };
                using var log = Log(rig, folder, "settle");
                var historyCalls = 0;
                var input = Session(rig, log, store: store) with
                { ReadEvidence = (_, _, _) => { historyCalls++; throw new InvalidOperationException(nameof(historyCalls)); } };
                var summary = await RunScanRepairAsync(input, CancellationToken.None);
                Check.Require(summary.Completed && rig.Store.FirstRepairDone(rig.Pair.PairId) && historyCalls == 0,
                    "retained bilateral work settles in this Repair without replaying history");
                Check.Require(!rig.Store.Issues(rig.Pair.PairId).Any(issue =>
                    issue.Kind == original.Kind && issue.Cause == original.Cause && issue.Path == original.Path &&
                    JsonSerializer.Serialize(issue.Subject) == JsonSerializer.Serialize(original.Subject)) &&
                    rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key(path)) is not null,
                    "the original exact marker and binding settle together, including originally unrecorded folders: " +
                    $"folder={folderItem} kind={kind} saved={saved} original={original} marker={rig.Store.IssueById(rig.Pair.PairId, original.Id)} " +
                    $"binding={rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key(path))} info={plainInfo} history={historyCalls} queued={queued}");
                Check.Require(plainInfo == (kind == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice ? 1 : 0),
                    "preservation owes exactly one D12 Info; Item does not invent one");
                Check.Require(store.ScanPasses == 1, "queue admission/finalization adds no second scan");
                if (!folderItem && !saved && kind == IssueSubject.FirstRunDeletionKind.Item)
                    Check.Require(queued == 1, "ack-only file Record retains the queued route");
                using var repeated = Log(rig, folder, "repeat");
                await RunScanRepairAsync(Session(rig, repeated, store: store), CancellationToken.None);
                Check.Require(plainInfo == (kind == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice ? 1 : 0),
                    "a later Repair cannot repeat completed preservation notice settlement");
            });
        }
    }

    private static Task RetainedFolderDependencyAsync() => WithFolder(nameof(RetainedFolderDependencyAsync), async folder =>
    {
        using var rig = NewRig(Pair());
        const string parent = "Parent";
        const string child = "Parent/Child";
        var local = await rig.Local.EnsureFolderAsync(parent, CancellationToken.None);
        await rig.Dropbox.EnsureFolderAsync(parent, CancellationToken.None);
        await PutAsync(rig.Local, child);
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, parent,
                new IssueSubject([new IssueItem(Side.Local, parent, local.Id)], null,
                    FirstRunDeletion: IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice))]
        });
        var marker = rig.Store.Issues(rig.Pair.PairId).Single();
        var fail = true;
        var remote = new ProxySide(rig.Dropbox)
        {
            BlockPublish = (path, _) => fail && path == child
                ? Task.FromException(new ItemFailedException(path, TextKey.PublishFailed, null)) : Task.CompletedTask
        };
        using (var blocked = Log(rig, folder, "blocked"))
            await RunScanRepairAsync(Session(rig, blocked, dropbox: remote), CancellationToken.None);
        Check.Require(rig.Store.IssueById(rig.Pair.PairId, marker.Id)?.Subject.FirstRunDeletion ==
            IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice &&
            rig.Store.Issues(rig.Pair.PairId).All(issue => issue.Cause != TextKey.InfoDropboxHistoryUnchecked),
            "recording the bilateral parent cannot acknowledge it while an ordinary child failed");
        fail = false;
        using var recovered = Log(rig, folder, "recovered");
        await RunScanRepairAsync(Session(rig, recovered, dropbox: remote), CancellationToken.None);
        // The id is a Storage handle that a later raise may reuse; the obligation's identity (2.4) is its
        // kind, cause, path and subject items, so require that exact record gone, not the numeric id.
        var remaining = rig.Store.Issues(rig.Pair.PairId);
        Check.Require(remaining.All(issue => issue.Kind != marker.Kind || issue.Cause != marker.Cause ||
                issue.Path != marker.Path || !issue.Subject.HasSameItems(marker.Subject) ||
                issue.Subject.Incoming != marker.Subject.Incoming) &&
            remaining.Count(issue => issue.Cause == TextKey.InfoDropboxHistoryUnchecked) == 1 &&
            await rig.Dropbox.ReadOneAsync(child, CancellationToken.None) is not null,
            "child recovery settles its retained parent in the same Repair, without another discovery pass");
    });

    private static async Task DriveGateAsync()
    {
        foreach (var mode in new[] { "existing", "changed", "restored" })
        {
            await WithFolder("drive-gate-" + mode, async folder =>
            {
                var native = new FakeRecovery { Evidence = mode == "changed" ? new(2, 1, 100) : new(1, 1, 99) };
                var recovery = new RecoveryProbe(native);
                await using var rig = await ComposeRigAsync(folder, recovery: recovery, drive: new(1, 1, 100));
                rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
                {
                    Record = [new SyncedEntry(PathNames.Key("saved"), "saved", PathNames.DropboxKey("saved"), "saved", false,
                        "record-local", 0, 0, "record-dropbox", "")]
                });
                var source = new Entry("pending", false, 1, DateTimeOffset.UnixEpoch, "id", "rev", "", null);
                rig.Store.ArmPending(rig.Pair.PairId, PendingFixture("pending", "stage", source));
                if (mode == "existing")
                    rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
                    {
                        Raise = [(IssueKind.DriveChange, TextKey.DriveChanged, "", new IssueSubject([], null))]
                    });
                var store = rig.Store;
                rig.Wire.Calls.Clear();
                using var log = rig.Log(folder, "blocked");
                EngineRequestException? refusal = null;
                try { await rig.RunAsync(log, CancellationToken.None); }
                catch (EngineRequestException error) { refusal = error; }
                Check.Require(refusal?.Key == TextKey.PairAwaitingDecision && store.GateReads == 1,
                    "one narrow gate refuses the blocked pair");
                Check.Require((mode == "existing" ? rig.Wire.Calls.Count == 0 :
                    rig.Wire.Calls.All(route => route.EndsWith("/get_current_account", StringComparison.Ordinal) ||
                        route.EndsWith("/get_metadata", StringComparison.Ordinal))) && native.Resolved.Count == 0 &&
                    native.Removals == 0 &&
                    recovery.Reads == (mode == "existing" ? 0 : 1),
                    "the gate precedes pending, native reads for an existing notice, listings and effects");
                Check.Require(rig.Store.Pair(rig.Pair.PairId)!.Drive == rig.Pair.Drive &&
                    rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("saved")) is not null &&
                    rig.Store.Pending(rig.Pair.PairId) is not NoPending && rig.Store.LastRepair(rig.Pair.PairId) is null,
                    "refusal neither reseeds evidence nor clears progress or pending state");
                var notice = rig.Store.Issues(rig.Pair.PairId).Single();
                Check.Require(notice.Kind == IssueKind.DriveChange &&
                    notice.Cause == (mode == "restored" ? TextKey.DriveRestored : TextKey.DriveChanged),
                    "exactly one drive notice names the detection");

                var other = rig.Pair with { PairId = "pair-b", Drive = null };
                rig.Store.SavePair(other);
                await rig.Host.RecomposeCurrentAsync(other.PairId, CancellationToken.None);
                await rig.Host.EnsureSourceBasesAsync(other.PairId, CancellationToken.None);
                using var otherLog = RunLog.Open(folder, other.PairId, "independent", _ => { }, Settings.LogBufferCharacters);
                var summary = await rig.RunAsync(otherLog, CancellationToken.None, targetPair: other.PairId);
                Check.Require(summary.Completed && rig.Store.IssueById(rig.Pair.PairId, notice.Id) is not null,
                    "another pair runs without releasing the blocked pair");
            });
        }
    }

    private static async Task DriveFinishAsync()
    {
        foreach (var exit in new[] { "success", "incomplete", "fault", "cancel" })
        foreach (var mergeFails in new[] { false, true })
        {
            await WithFolder(nameof(DriveFinishAsync) + exit + mergeFails, async folder =>
            {
                using var cancellation = new CancellationTokenSource();
                var primary = new InvalidOperationException(nameof(DriveFinishAsync));
                var mergeError = new IOException(nameof(mergeFails));
                var finished = new bool[2];
                var recovery = new RecoveryProbe(new FakeRecovery()) { Reading = _ => new(1, 1, 100) };
                var merges = 0;
                await using var rig = await ComposeRigAsync(folder, recovery: recovery, drive: new(1, 1, 50),
                    decorate: inner => new HookStore(inner)
                    {
                        WrapSink = (side, sink) => new FinishedSink(sink, () => finished[side == Side.Local ? 0 : 1] = true),
                        OnMergeDrive = (_, _, evidence) =>
                        {
                            merges++;
                            Check.Require(finished.All(value => value), "both producers join before evidence merge");
                            Check.Require(evidence == new DriveEvidence(1, 1, 100), "merge uses the initial tuple only");
                            if (mergeFails) throw mergeError;
                        }
                    });
                rig.Wire.RequestException = route =>
                {
                    if (!route.EndsWith("/list_folder", StringComparison.Ordinal)) return null;
                    if (exit == "cancel") cancellation.Cancel();
                    return exit == "fault" ? primary : exit == "incomplete"
                        ? new InventoryIncompleteException("incomplete", null) : null;
                };
                using var log = rig.Log(folder, "run");
                Exception? thrown = null;
                RepairSummary? result = null;
                try { result = await rig.RunAsync(log, cancellation.Token); }
                catch (Exception error) { thrown = error; }
                Check.Require(recovery.Reads == 1 && merges == 1, "no finish-only Drive observation on any entered exit");
                Check.Require(exit == "fault" ? ReferenceEquals(thrown, primary) : exit == "cancel"
                    ? thrown is OperationCanceledException : mergeFails ? ReferenceEquals(thrown, mergeError)
                    : thrown is null && result?.Completed == (exit == "success"),
                    "primary errors survive a failed evidence merge; lone merge failure prevents completion");
                Check.Require(rig.Store.Pair(rig.Pair.PairId)!.Drive ==
                    (mergeFails ? rig.Pair.Drive : new DriveEvidence(1, 1, 100)), "only joined initial evidence persists");
            });
        }
    }

    private static Task DriveProgressReopenAsync() => WithFolder("drive-progress-reopen", async folder =>
    {
        var fault = new InvalidOperationException("failure after committed file");
        var recovery = new RecoveryProbe(new FakeRecovery { Evidence = new(1, 1, 150) });
        var modified = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var rig = await ComposeRigAsync(folder, recovery: recovery, drive: new(1, 1, 100), level: 1,
            decorate: inner => new HookStore(inner)
            {
                OnRow = count =>
                {
                    if (count != 2) return;
                    var pair = inner.Pairs().Single();
                    // Limiter level one still overlaps rows; establish committed progress before injecting failure.
                    Check.Require(SpinWait.SpinUntil(() =>
                        inner.SyncedByKey(pair.PairId, Side.Local, PathNames.Key("a")) is not null,
                        TimeSpan.FromSeconds(10)), "the file commits before the folder failure is injected");
                }
            }))
        {
            await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "a"), "content");
            File.SetLastWriteTimeUtc(Path.Combine(rig.Pair.LocalRoot, "a"), modified.UtcDateTime);
            Directory.CreateDirectory(Path.Combine(rig.Pair.LocalRoot, "b"));
            rig.Wire.RequestException = route => route.Contains("/create_folder", StringComparison.Ordinal) ? fault : null;
            using var log = rig.Log(folder, "failed");
            Exception? thrown = null;
            try { await rig.RunAsync(log, CancellationToken.None); }
            catch (Exception error) { thrown = error; }
            Check.Require(ReferenceEquals(thrown, fault) && recovery.Reads == 1,
                "a post-progress failure preserves its diagnosis and records its initial evidence: " +
                thrown + "; reads=" + recovery.Reads + "; issues=" + JsonSerializer.Serialize(rig.Store.Issues(rig.Pair.PairId)));
        }
        await using (var rig = await ComposeRigAsync(folder, recovery: new FakeRecovery { Evidence = new(1, 1, 150) }, level: 1))
        {
            var pair = rig.Store.Pair(rig.Pair.PairId)!;
            Check.Require(pair.Drive == new DriveEvidence(1, 1, 150) &&
                rig.Store.SyncedByKey(pair.PairId, Side.Local, PathNames.Key("a")) is not null &&
                !rig.Store.FirstRepairDone(pair.PairId),
                "reopening retains committed partial progress and evidence, but no completion bit");
            rig.Wire.SeedFile("a", "content", modified);
            using var log = rig.Log(folder, "resumed");
            var summary = await rig.RunAsync(log, CancellationToken.None);
            Check.Require(summary.Completed && !summary.Effects.ContainsKey(nameof(EffectKind.Uploaded)) &&
                rig.Wire.Snapshot().Any(item => item.Path == "b" && item.Folder),
                "the next Repair completes without retransferring the committed file");
        }
    });

    private static Task PendingStageNamesAsync() => WithFolder("pending-stage-names", async folder =>
    {
        var disabled = Pair("disabled", enabled: false);
        var alias = Pair("alias", localRoot: @"C:\Alias");
        var native = new FakeRecovery { CleanupFailures = [("stale-stage", new IOException("cleanup fault"))] };
        var finished = new bool[2];
        var recovery = new RecoveryProbe(native)
        {
            OnCleanup = () => Check.Require(finished.All(value => value), "cleanup waits for successful full listings")
        };
        await using var rig = await ComposeRigAsync(folder, recovery: recovery, decorate: inner => new HookStore(inner)
        {
            WrapSink = (side, sink) => new FinishedSink(sink, () => finished[side == Side.Local ? 0 : 1] = true)
        });
        rig.Store.SavePair(disabled);
        rig.Store.SavePair(alias);
        var source = new Entry("item", false, 1, DateTimeOffset.UnixEpoch, "source", "rev", "", null);
        var stages = new[] { StageNames.For(@"C:\PARENT\item"), StageNames.For(@"C:\Alias\item") };
        var names = stages.Select(path => Path.GetFileName(path)).ToArray();
        rig.Store.ArmPending(disabled.PairId, PendingFixture("item", stages[0], source));
        rig.Store.ArmPending(alias.PairId, PendingFixture("item", stages[1], source));
        using var log = rig.Log(folder, "first");
        var summary = await rig.RunAsync(log, CancellationToken.None);
        Check.Require(summary.Completed && recovery.PendingNames is { } pending && pending.SetEquals(names) &&
            !pending.Contains(names[0].ToUpperInvariant()),
            "disabled/shared/alias pairs protect only ordinal generated filenames");
        Check.Require(rig.Store.PendingReads[disabled.PairId] == 1 && rig.Store.PendingReads[alias.PairId] == 1 &&
            rig.Store.Pending(disabled.PairId) is not NoPending && rig.Store.Pending(alias.PairId) is not NoPending,
            "foreign pair notes are read once and never recovered by this pair");
        Check.Require(Lines(log.Path).Count(fields => fields[1] == nameof(RunEventKind.Failed) &&
            fields[2] == nameof(IOException) && fields[3] == "stale-stage" &&
            fields[5] == nameof(TextKey.StageCleanupFailed)) == 1,
            "each cleanup failure produces one current-run failure event");
        native.CleanupFailures = [];
        Array.Clear(finished);
        using var next = rig.Log(folder, "next");
        await rig.RunAsync(next, CancellationToken.None);
        Check.Require(native.Removals == 2 &&
            !Lines(next.Path).Any(fields => fields[1] == nameof(RunEventKind.Failed)),
            "cleanup is attempted again on the next full Repair without a durable failure queue");
    });

    private static async Task FailureRetryAsync()
    {
        foreach (var cause in new[] { TextKey.PublishFailed, TextKey.FailDropboxNoPermission })
        {
            await WithFolder("failure-retry-" + cause, async folder =>
            {
                using var rig = NewRig(Pair());
                await PutAsync(rig.Local, "item");
                var attempts = 0;
                var reads = 0;
                var fail = true;
                var local = new ProxySide(rig.Local) { OnRead = _ => Interlocked.Increment(ref reads) };
                var dropbox = new ProxySide(rig.Dropbox)
                {
                    OnRead = _ => Interlocked.Increment(ref reads),
                    BlockPublish = (path, _) =>
                    {
                        attempts++;
                        return fail ? Task.FromException(new ItemFailedException(path, cause, null)) : Task.CompletedTask;
                    }
                };
                using (var first = Log(rig, folder, "first"))
                {
                    var summary = await RunScanRepairAsync(Session(rig, first, local, dropbox), CancellationToken.None);
                    Check.Require(summary.Completed && attempts == 1 &&
                        summary.IssuesByKind.GetValueOrDefault(nameof(IssueKind.Failure)) == 1,
                        "the first failed attempt emits one Failure");
                }
                var issue = rig.Store.Issues(rig.Pair.PairId).Single();
                var definite = cause == TextKey.FailDropboxNoPermission;
                var retry = new HashSet<long> { issue.Id };
                var readsBefore = reads;
                var hashesBefore = local.Hashes + dropbox.Hashes;
                var admissionsBefore = rig.Admission.Admitted;
                using (var second = Log(rig, folder, "ordinary"))
                {
                    var summary = await RunScanRepairAsync(Session(rig, second, local, dropbox), CancellationToken.None);
                    if (definite)
                        Check.Require(reads == readsBefore && local.Hashes + dropbox.Hashes == hashesBefore &&
                            rig.Admission.Admitted == admissionsBefore && summary.Effects.Count == 0,
                            "a held Failure adds no per-item provider reads, hashes, transfers or effects");
                    Check.Require(attempts == (definite ? 1 : 2) &&
                        summary.IssuesByKind.GetValueOrDefault(nameof(IssueKind.Failure)) == (definite ? 0 : 1),
                        "unknown failures retry next opportunity; definite refusals hold without another event" +
                        $" (cause={cause} definite={definite} attempts={attempts}" +
                        $" issues={summary.IssuesByKind.GetValueOrDefault(nameof(IssueKind.Failure))})");
                }
                using (var third = Log(rig, folder, "retry-fails"))
                {
                    var summary = await RunScanRepairAsync(Session(rig, third, dropbox: dropbox), CancellationToken.None, retry);
                    Check.Require(attempts == (definite ? 2 : 3) &&
                        summary.IssuesByKind.GetValueOrDefault(nameof(IssueKind.Failure)) == 1 &&
                        rig.Store.Issues(rig.Pair.PairId).Single().Id == issue.Id,
                        "Retry attempts once, retaining the continuing issue id on failure");
                }
                fail = false;
                using var last = Log(rig, folder, "recovered");
                await RunScanRepairAsync(Session(rig, last, dropbox: dropbox), CancellationToken.None, retry);
                Check.Require(attempts == (definite ? 3 : 4) && rig.Store.Issues(rig.Pair.PairId).Count == 0 &&
                    await rig.Dropbox.ReadOneAsync("item", CancellationToken.None) is not null,
                    "recovery closes the continuing Failure after the successful effect");
            });
        }
    }

    private static Task RetrySelectionAsync() => WithFolder("retry-selection", async folder =>
    {
        using var rig = NewRig(Pair());
        foreach (var path in new[] { "a", "b" })
        {
            var entry = await PutAsync(rig.Local, path);
            rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, path,
                    new IssueSubject([new IssueItem(Side.Local, path, entry.Id)], null))]
            });
        }
        var failures = rig.Store.Issues(rig.Pair.PairId);
        var selected = failures.Single(issue => issue.Path == "a");
        var untouched = failures.Single(issue => issue.Path == "b");
        var local = new ProxySide(rig.Local)
        {
            OnRead = path =>
            {
                if (path == "a") Check.Require(rig.Store.IssueById(rig.Pair.PairId, selected.Id) is not null,
                    "the selected Failure is not removed before its actual attempt");
                Check.Require(path != "b", "an unselected definite Failure is held before observation");
            }
        };
        var dropbox = new ProxySide(rig.Dropbox);
        var store = new HookStore(rig.Store);
        var retry = new HashSet<long> { selected.Id };
        using (var log = Log(rig, folder, "single"))
            await RunScanRepairAsync(Session(rig, log, local, dropbox, store: store), CancellationToken.None, retry);
        Check.Require(local.Listings == 1 && dropbox.Listings == 1 && local.Hashes == 0 &&
            store.HoldSnapshots.Count != 0 &&
            store.HoldSnapshots.All(set => set is not null && set.SetEquals(retry)) &&
            rig.Store.IssueById(rig.Pair.PairId, selected.Id) is null &&
            rig.Store.IssueById(rig.Pair.PairId, untouched.Id) is not null &&
            await rig.Dropbox.ReadOneAsync("a", CancellationToken.None) is not null &&
            await rig.Dropbox.ReadOneAsync("b", CancellationToken.None) is null,
            "single retry performs full listings, every hold query receives its id, and the run closes on success");
        using (var log = Log(rig, folder, "remaining"))
            await RunScanRepairAsync(Session(rig, log), CancellationToken.None, new HashSet<long> { untouched.Id });
        Check.Require(rig.Store.Issues(rig.Pair.PairId).Count == 0 &&
            await rig.Dropbox.ReadOneAsync("b", CancellationToken.None) is not null,
            "a later retry of the remaining Failure completes its work");
        var emptyLocal = new ProxySide(rig.Local);
        var emptyDropbox = new ProxySide(rig.Dropbox);
        using var empty = Log(rig, folder, "empty");
        await RunScanRepairAsync(Session(rig, empty, emptyLocal, emptyDropbox), CancellationToken.None, new HashSet<long>());
        Check.Require(emptyLocal.Listings == 1 && emptyDropbox.Listings == 1,
            "an empty selected set still executes the requested ordinary Repair");
    });

    private static Task PhaseSnapshotsAsync() => WithFolder("phase-snapshots", async folder =>
    {
        await using var rig = await ComposeRigAsync(folder);
        foreach (var path in new[] { "a", "b", "c" }) await SeedLinkedAsync(rig, path);
        using (var baseline = rig.Log(folder, "baseline"))
            Check.Require((await rig.RunAsync(baseline, CancellationToken.None)).Completed, "baseline completes");
        foreach (var path in new[] { "a", "b", "c" })
            File.Move(Path.Combine(rig.Pair.LocalRoot, path), Path.Combine(rig.Pair.LocalRoot, path + "-moved"));
        var store = rig.Store;
        var previousGates = store.GateReads;
        store.HoldSnapshots.Clear();
        store.IssueFilters.Clear();
        var retries = new HashSet<long>();
        using var log = rig.Log(folder, "run");
        await rig.RunAsync(log, CancellationToken.None, retryIssues: retries);
        Check.Require(store.GateReads - previousGates == 1 && store.HoldSnapshots.Count != 0 &&
            store.HoldSnapshots.All(snapshot => snapshot is not null && snapshot.SetEquals(retries)),
            "every per-row hold query receives the same retry set");
        Check.Require(store.IssueFilters.Count(kinds => kinds is not null &&
            kinds.SequenceEqual(IssueClosure.AfterHeldInSync)) == 1,
            "one filtered sparse issue query supplies retirement and proofs");
        foreach (var path in new[] { "a", "b", "c" })
            Check.Require(rig.Wire.Snapshot().Any(item => item.Path == path + "-moved"),
                "the count assertions accompany real rename effects");

        await using var excludedRig = await ComposeRigAsync(Path.Combine(folder, "excluded"),
            configure: pair => pair with { Exclusions = [new Exclusion(Side.Local, "excluded.txt", false)] });
        await File.WriteAllTextAsync(Path.Combine(excludedRig.Pair.LocalRoot, "excluded.txt"), "content");
        var excludedRetries = new HashSet<long>();
        var excludedStore = excludedRig.Store;
        using var excludedLog = excludedRig.Log(folder, "excluded");
        var previousRecycle = LocalBin.SendOverride;
        RepairSummary summary;
        try
        {
            // This checks Retry propagation, not whether the test volume has a Recycle Bin.
            LocalBin.SendOverride = path => { File.Delete(path); return true; };
            summary = await excludedRig.RunAsync(excludedLog, CancellationToken.None, async (context, token) =>
            {
                var excluded = (await context.Local.ReadOneAsync("excluded.txt", token))!;
                excludedRig.Store.Commit(excludedRig.Pair.PairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, excluded.Path,
                        new IssueSubject([new IssueItem(Side.Local, excluded.Path, excluded.Id)], null))]
                });
                excludedRetries.Add(excludedRig.Store.Issues(excludedRig.Pair.PairId).Single().Id);
            }, excludedRetries);
        }
        finally { LocalBin.SendOverride = previousRecycle; }
        Check.Require(excludedStore.HoldSnapshots.Count != 0 && excludedStore.HoldSnapshots.All(snapshot =>
            snapshot is not null && snapshot.SetEquals(excludedRetries)),
            "every per-row hold query receives the selected retry set");
        Check.Require(summary.Completed &&
            !File.Exists(Path.Combine(excludedRig.Pair.LocalRoot, "excluded.txt")) &&
            excludedRig.Wire.Snapshot().Any(item => item.Path == "excluded.txt"),
            "the selected retry permits the exclusion to preserve and remove its local file: " +
            $"completed={summary.Completed} local={File.Exists(Path.Combine(excludedRig.Pair.LocalRoot, "excluded.txt"))} " +
            "remote=" + JsonSerializer.Serialize(excludedRig.Wire.Snapshot().Select(item => item.Path)) +
            "; issues=" + JsonSerializer.Serialize(excludedStore.Issues(excludedRig.Pair.PairId)));
    });

    private static async Task ScopeRetirementAsync()
    {
        foreach (var mode in new[] { "ignored-file", "ignored-folder", "all-subjects", "nested", "partial-subject",
            "one-sided-folder" })
        {
            await WithFolder("scope-retirement-" + mode, async folder =>
            {
                var pair = Pair(localRoot: @"C:\Parent", dropboxRoot: "/Parent") with
                {
                    IgnoreRules = ["FileExtension:tmp", "FolderName:Ignored"]
                };
                var nested = Pair("nested", localRoot: @"C:\Parent\Inner", dropboxRoot: "/Parent/Inner");
                using var rig = NewRig(pair, nested);
                var path = mode == "ignored-folder" ? "Ignored" : "missing.tmp";
                IReadOnlyList<IssueItem> items = [new IssueItem(Side.Local, path, "missing")];
                if (mode is "all-subjects" or "partial-subject")
                {
                    path = "notice";
                    var other = mode == "partial-subject" ? await PutAsync(rig.Local, "kept") :
                        new Entry("other.tmp", false, 1, DateTimeOffset.UnixEpoch, "other", "", "", null);
                    items = [new IssueItem(Side.Local, "missing.tmp", "missing"),
                        new IssueItem(Side.Local, other.Path, other.Id)];
                }
                else if (mode == "nested")
                {
                    path = "Inner/item";
                    items = [new IssueItem(Side.Local, path, "nested-item")];
                }
                else if (mode == "one-sided-folder")
                {
                    path = "kept.tmp";
                    var local = await rig.Local.EnsureFolderAsync(path, CancellationToken.None);
                    items = [new IssueItem(Side.Local, path, local.Id)];
                }
                rig.Store.Commit(pair.PairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, path, new IssueSubject(items, null))]
                });
                var issue = rig.Store.Issues(pair.PairId).Single();
                using var log = Log(rig, folder, "run");
                var summary = await RunScanRepairAsync(Session(rig, log), CancellationToken.None);
                Check.Require((rig.Store.IssueById(pair.PairId, issue.Id) is null) ==
                    (mode is not ("partial-subject" or "one-sided-folder")),
                    "configured scope retires ignored paths, all excluded subjects and nested pairs, not an in-scope subject: " +
                    mode + "; remaining=" + JsonSerializer.Serialize(rig.Store.Issues(pair.PairId)) +
                    "; effects=" + JsonSerializer.Serialize(summary.Effects));
                Check.Require(summary.Completed && summary.Effects.Count == 0,
                    "scope retirement needs no provider effect");
            });
        }
    }

    // The fixture scope is composed from the supplied settings, so the shipped defaults reach a real Repair.
    private static Task ShippedDefaultIgnoreEntryAsync() => WithFolder("default-ignore-entry", async folder =>
    {
        var pair = Pair();
        var settings = ShippedSettings();
        using var rig = NewRig(pair, settings, OpenStore(Path.Combine(FixtureRoot.Value!, Guid.NewGuid().ToString("N") + ".db")));
        rig.Local.Seed(new Entry("desktop.ini", false, 1, DateTimeOffset.UnixEpoch, "desktop-id", "", "", null), [1]);
        using var log = Log(rig, folder, "run");
        var summary = await RunScanRepairAsync(Session(rig, log, settings: settings), CancellationToken.None);
        Check.Require(summary.Completed && summary.Effects.Count == 0 && rig.Store.Issues(pair.PairId).Count == 0,
            "a desktop.ini entry is ignored by the shipped defaults with no exclusions present");
        Check.Require(await rig.Dropbox.ReadOneAsync("desktop.ini", CancellationToken.None) is null,
            "the ignored desktop.ini entry is not transferred");
    });

    // A configured exclusion makes Repair reconstruct the scope, which must still carry the shipped defaults.
    private static Task ShippedDefaultIgnoreRetirementAsync() => WithFolder("default-ignore-retirement", async folder =>
    {
        var pair = Pair(exclusions: [new Exclusion(Side.Local, "Unrelated", true)]);
        var settings = ShippedSettings();
        using var rig = NewRig(pair);
        rig.Store.Commit(pair.PairId, StateChange.Empty with
        {
            Raise = [(IssueKind.Failure, TextKey.FailDropboxNoPermission, "desktop.ini",
                new IssueSubject([new IssueItem(Side.Local, "desktop.ini", "missing")], null))]
        });
        var issue = rig.Store.Issues(pair.PairId).Single();
        using var log = Log(rig, folder, "run");
        var summary = await RunScanRepairAsync(Session(rig, log, settings: settings), CancellationToken.None);
        Check.Require(rig.Store.IssueById(pair.PairId, issue.Id) is null,
            "the shipped default ignore name retires a saved issue while another exclusion is configured");
        Check.Require(summary.Completed && summary.Effects.Count == 0,
            "default-ignore retirement needs no provider effect");
    });

    // A non-empty rule set (defaults plus a pair rule) meets control-character names in full Repair rows.
    private static Task ControlCharacterNamesAsync() => WithFolder("control-char-names", async folder =>
    {
        var pair = Pair() with { IgnoreRules = ["FileExtension:.tmp"] };
        var settings = ShippedSettings();
        using var rig = NewRig(pair, settings, OpenStore(Path.Combine(FixtureRoot.Value!, Guid.NewGuid().ToString("N") + ".db")));
        rig.Local.Seed(new Entry("local\u0001name", false, 1, DateTimeOffset.UnixEpoch, "local-id", "", "",
            nameof(TextKey.SkipReservedName)), null);
        rig.Dropbox.Seed(new Entry("drop\u0001name", false, 1, DateTimeOffset.UnixEpoch, "drop-id", "rev", "",
            nameof(TextKey.SkipReservedName)), null);
        using var log = Log(rig, folder, "run");
        var summary = await RunScanRepairAsync(Session(rig, log, settings: settings), CancellationToken.None);
        var issues = rig.Store.Issues(pair.PairId);
        Check.Require(summary.Completed && summary.Effects.Count == 0,
            "control-character names complete Repair without effects");
        Check.Require(issues.Count == 2 && issues.All(issue => issue.Kind == IssueKind.Skipped &&
            issue.Cause == TextKey.SkipReservedName), "control-character names are reported as skipped notices as before");
    });

    private static EngineSettings ShippedSettings()
    {
        using var document = CliCatalogResource.Load(typeof(SettingsParser).Assembly, ResourceNames.Settings);
        return SettingsParser.Parse(document.RootElement.GetRawText(), null);
    }

    private static async Task UnreadableRecoveryAsync()
    {
        foreach (var crossLinked in new[] { false, true })
        foreach (var lateMarker in new[] { false, true })
        {
            await WithFolder("unreadable-" + crossLinked + "-" + lateMarker, async folder =>
            {
                // A nested root makes Root/Kept out of scope for the row pass yet still a plan survivor, so the
                // folder plan stays partial without the row pass syncing that survivor ahead of it.
                PairConfig[] nested = crossLinked
                    ? [Pair("pair-b", localRoot: @"C:\RepairFixture\Root\Kept")] : [];
                using var rig = NewRig(Pair(), OpenStore(StateStore.DatabasePath(folder)), nested);
                await CompleteBaselineAsync(rig, folder);
                var localRoot = await rig.Local.EnsureFolderAsync("Root", CancellationToken.None);
                var dropboxRoot = await rig.Dropbox.EnsureFolderAsync(crossLinked ? "Elsewhere" : "Root", CancellationToken.None);
                RecordLinked(rig, localRoot, dropboxRoot);
                var protectedFolder = crossLinked
                    ? await rig.Local.EnsureFolderAsync("Unreadable", CancellationToken.None) : localRoot;
                var child = await PutAsync(rig.Local, protectedFolder.Path + "/Child");
                Entry? kept = null;
                if (crossLinked)
                {
                    // The removed local branch maps to Elsewhere/Sub; the protected binding's Dropbox path lies
                    // inside that branch's forget range while its Local path stays outside Root, so only the
                    // selected-branch cross-linked guard reaches it.
                    var removed = await rig.Local.EnsureFolderAsync("Root/Gone", CancellationToken.None);
                    RecordLinked(rig, removed, new Entry("Elsewhere/Sub", true, 0, DateTimeOffset.UnixEpoch,
                        "removed-dropbox", "", "", null));
                    kept = await rig.Local.EnsureFolderAsync("Root/Kept", CancellationToken.None);
                    RecordLinked(rig, child, new Entry("Elsewhere/Sub/Child", false, 0, DateTimeOffset.UnixEpoch,
                        "protected-dropbox", "", "", null));
                }
                else
                {
                    var remoteChild = await PutAsync(rig.Dropbox, dropboxRoot.Path + "/Child");
                    RecordLinked(rig, child, remoteChild);
                }
                await rig.Dropbox.DeleteAsync(dropboxRoot, null, CancellationToken.None);
                var independentLocal = await rig.Local.EnsureFolderAsync("Independent", CancellationToken.None);
                var independentDropbox = await rig.Dropbox.EnsureFolderAsync("Independent", CancellationToken.None);
                RecordLinked(rig, independentLocal, independentDropbox);
                await rig.Dropbox.DeleteAsync(independentDropbox, null, CancellationToken.None);
                var listing = new ProxySide(rig.Local)
                {
                    Extra = [protectedFolder with { Skip = nameof(TextKey.SkipUnreadable) }],
                    LateMarkers = lateMarker
                };
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var log = Log(rig, folder, "blocked-" + attempt);
                    var summary = await RunScanRepairAsync(Session(rig, log, local: listing), CancellationToken.None);
                    Check.Require(summary.Completed &&
                        rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key(child.Path)) is not null &&
                        await rig.Local.ReadOneAsync(localRoot.Path, CancellationToken.None) is not null,
                        "repeated unreadability preserves both provider contents and the complete saved range");
                    Check.Require(rig.Store.Issues(rig.Pair.PairId).Any(issue => issue.Kind == IssueKind.Skipped &&
                        issue.Cause == TextKey.SkipUnreadable), "the unreadable folder remains visible as skipped");
                    if (crossLinked)
                        Check.Require(rig.Store.Issues(rig.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure),
                            "the cross-linked forget guard creates a visible temporary Failure");
                }
                Check.Require(await rig.Local.ReadOneAsync("Independent", CancellationToken.None) is null &&
                    rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("Independent")) is null,
                    "an independent deferred folder completes while the protected operation waits");
                if (crossLinked)
                {
                    // Both must be gone before the readable run, or a survivor would keep its plan partial.
                    await rig.Local.DeleteAsync(child, null, CancellationToken.None);
                    await rig.Local.DeleteAsync(kept!, null, CancellationToken.None);
                }
                using var readable = Log(rig, folder, "readable");
                await RunScanRepairAsync(Session(rig, readable), CancellationToken.None);
                Check.Require(await rig.Local.ReadOneAsync(localRoot.Path, CancellationToken.None) is null &&
                    rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key(child.Path)) is null &&
                    rig.Store.Issues(rig.Pair.PairId).All(issue => issue.Kind is not (IssueKind.Skipped or IssueKind.Failure)),
                    "a readable Repair removes the protected subtree, forgets its bindings and closes notices");
            });
        }
    }

    private static async Task ExclusionRetryAsync()
    {
        foreach (var failedPath in new[] { "Root/a", "Root/c" })
        {
            await WithFolder("exclusion-retry-" + Path.GetFileName(failedPath), async folder =>
            {
                using var rig = NewRig(Pair(exclusions: [new Exclusion(Side.Local, "Root", false)]),
                    OpenStore(StateStore.DatabasePath(folder)));
                await rig.Local.EnsureFolderAsync("Root", CancellationToken.None);
                foreach (var name in new[] { "a", "b", "c" }) await PutAsync(rig.Local, "Root/" + name);
                var copied = new List<string>();
                var fail = true;
                var dropbox = new ProxySide(rig.Dropbox)
                {
                    BlockPublish = (path, _) =>
                    {
                        if (fail && path == failedPath)
                            throw new ItemFailedException(path, TextKey.PublishFailed, null);
                        copied.Add(path);
                        return Task.CompletedTask;
                    }
                };
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var log = Log(rig, folder, "pending-" + attempt);
                    await RunScanRepairAsync(Session(rig, log, dropbox: dropbox), CancellationToken.None);
                    Check.Require(await rig.Local.ReadOneAsync("Root", CancellationToken.None) is not null &&
                        rig.Store.Issues(rig.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure),
                        "failed preservation keeps the exclusion pending and its Failure visible");
                }
                Check.Require(copied.Count == 2 && copied.Distinct().Count() == 2 &&
                    await rig.Dropbox.ReadOneAsync("Root/b", CancellationToken.None) is not null,
                    "independent siblings continue and completed copies are not repeated next opportunity");
                await PutAsync(rig.Local, "Root/d");
                fail = false;
                using var recovered = Log(rig, folder, "recovered");
                await RunScanRepairAsync(Session(rig, recovered, dropbox: dropbox), CancellationToken.None);
                Check.Require(copied.Count == 4 && copied.Distinct().Count() == 4 &&
                    await rig.Dropbox.ReadOneAsync("Root/d", CancellationToken.None) is not null &&
                    await rig.Local.ReadOneAsync("Root", CancellationToken.None) is null &&
                    rig.Store.Issues(rig.Pair.PairId).All(issue => issue.Kind != IssueKind.Failure),
                    "recovery discovers new children, copies only remaining work, then removes and closes");
            });
        }
    }

    private static Task UncertainUploadAsync() => WithFolder("uncertain-upload", async folder =>
    {
        using var rig = NewRig(Pair());
        await PutAsync(rig.Local, "item");
        var effects = 0;
        var dropbox = new ProxySide(rig.Dropbox)
        {
            AfterPublish = entry =>
            {
                effects++;
                throw new ItemFailedException(entry.Path, TextKey.PublishFailed, new IOException("reply lost"));
            }
        };
        using (var log = Log(rig, folder, "uncertain"))
            await RunScanRepairAsync(Session(rig, log, dropbox: dropbox), CancellationToken.None);
        Check.Require(effects == 1 && rig.Store.Issues(rig.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure) &&
            await rig.Dropbox.ReadOneAsync("item", CancellationToken.None) is not null,
            "a lost publication reply leaves an ordinary failure despite the committed remote effect");
        using var next = Log(rig, folder, "reconciled");
        await RunScanRepairAsync(Session(rig, next, dropbox: dropbox), CancellationToken.None);
        Check.Require(effects == 1 && rig.Store.Issues(rig.Pair.PairId).Count == 0 &&
            rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("item")) is not null,
            "fresh facts reconcile an uncertain upload without replaying the effect");
    });

    private static Task ConflictProofAsync() => WithFolder("conflict-proof", async folder =>
    {
        using var rig = NewRig(Pair());
        await CompleteBaselineAsync(rig, folder);
        await PutAsync(rig.Local, "item", [1]);
        await PutAsync(rig.Dropbox, "item", [2]);
        var local = new ProxySide(rig.Local);
        long issueId;
        using (var first = Log(rig, folder, "first"))
        {
            await RunScanRepairAsync(Session(rig, first, local: local), CancellationToken.None);
            var issue = rig.Store.Issues(rig.Pair.PairId).Single();
            issueId = issue.Id;
            Check.Require(issue.Kind == IssueKind.Tie && issue.Subject.LocalHash is not null && local.Hashes == 1,
                "the first conflict stores the Local hash proof from its one comparison");
        }
        using var second = Log(rig, folder, "second");
        await RunScanRepairAsync(Session(rig, second, local: local), CancellationToken.None);
        Check.Require(local.Hashes == 1 && rig.Store.Issues(rig.Pair.PairId).Single().Id == issueId,
            "an unchanged held conflict reuses its proof without another hash or issue episode");
    });

    private static Task FolderPlansJoinRowsAsync() => WithFolder("folder-plans-join", async folder =>
    {
        using var rig = NewRig(Pair(), OpenStore(StateStore.DatabasePath(folder)));
        await CompleteBaselineAsync(rig, folder);
        var localFolder = await rig.Local.EnsureFolderAsync("a", CancellationToken.None);
        var remoteFolder = await rig.Dropbox.EnsureFolderAsync("a", CancellationToken.None);
        RecordLinked(rig, localFolder, remoteFolder);
        await rig.Dropbox.DeleteAsync(remoteFolder, null, CancellationToken.None);
        await PutAsync(rig.Local, "z");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = false;
        var deleted = false;
        var local = new ProxySide(rig.Local)
        {
            OnDelete = _ =>
            {
                Check.Require(published, "every row task is joined before a deferred folder effect");
                deleted = true;
            }
        };
        var dropbox = new ProxySide(rig.Dropbox)
        {
            BlockPublish = async (_, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            },
            AfterPublish = _ => published = true
        };
        using var log = Log(rig, folder, "run");
        var run = RunScanRepairAsync(Session(rig, log, local, dropbox), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check.Require(!deleted && !run.IsCompleted, "a deferred plan waits for outstanding file work");
        }
        finally
        {
            release.TrySetResult();
            await run;
        }
        Check.Require(deleted && rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("a")) is null,
            "the joined plan executes and forgets before successful scan cleanup");
        using var next = Log(rig, folder, "next");
        Check.Require((await RunScanRepairAsync(Session(rig, next), CancellationToken.None)).Effects.Count == 0,
            "disposed plans leave the next scan able to run without repeating completed work");
    });

    private static async Task RestoredAncestorsAsync()
    {
        foreach (var mapped in new[] { false, true })
        {
            await WithFolder("restored-ancestors-" + mapped, async folder =>
            {
                using var rig = NewRig(Pair(), OpenStore(StateStore.DatabasePath(folder)));
                rig.Recovery.Evidence = new(1, 1, 100);
                await CompleteBaselineAsync(rig, folder);
                var remoteRoot = mapped ? "Remote" : "FoLdEr";
                foreach (var (localPath, remotePath) in new[]
                {
                    ("FoLdEr", remoteRoot), ("FoLdEr/Child", remoteRoot + "/Child"),
                    ("FoLdEr-Other", "FoLdEr-Other"), ("Other", "Other")
                })
                {
                    var local = await rig.Local.EnsureFolderAsync(localPath, CancellationToken.None);
                    var remote = await rig.Dropbox.EnsureFolderAsync(remotePath, CancellationToken.None);
                    RecordLinked(rig, local, remote);
                }
                foreach (var name in new[] { "changed", "removed" })
                {
                    var local = await PutAsync(rig.Local, "FoLdEr/Child/" + name);
                    var remote = await PutAsync(rig.Dropbox, remoteRoot + "/Child/" + name);
                    RecordLinked(rig, local, remote);
                }
                await LinkAsync(rig, "FoLdEr-Other/kept");
                var changed = (await rig.Local.ReadOneAsync("FoLdEr/Child/changed", CancellationToken.None))!;
                rig.Local.Seed(changed with { Size = 2 }, [2, 3]);
                foreach (var path in new[] { remoteRoot, "FoLdEr-Other", "Other" })
                    await rig.Dropbox.DeleteAsync((await rig.Dropbox.ReadOneAsync(path, CancellationToken.None))!, null,
                        CancellationToken.None);
                var dropbox = new ProxySide(rig.Dropbox)
                {
                    BlockPublish = async (_, ct) =>
                    {
                        // This unrelated folder reappears after listing, without a restored descendant.
                        await rig.Dropbox.EnsureFolderAsync("FoLdEr-Other", ct);
                    }
                };
                using var log = Log(rig, folder, "run");
                var summary = await RunScanRepairAsync(Session(rig, log, dropbox: dropbox), CancellationToken.None);
                Check.Require(summary.Completed && rig.Store.Issues(rig.Pair.PairId).All(issue =>
                    issue.Kind != IssueKind.Failure) &&
                    await rig.Local.ReadOneAsync("FoLdEr/Child/removed", CancellationToken.None) is null &&
                    await rig.Local.ReadOneAsync("FoLdEr/Child/changed", CancellationToken.None) is not null &&
                    await rig.Dropbox.ReadOneAsync(remoteRoot + "/Child/changed", CancellationToken.None) is not null,
                    "Repair builds strict Local join-key ancestors of a restored deep file for nested deferred plans");
                Check.Require(await rig.Local.ReadOneAsync("FoLdEr-Other/kept", CancellationToken.None) is not null &&
                    await rig.Dropbox.ReadOneAsync("FoLdEr-Other", CancellationToken.None) is not null &&
                    await rig.Local.ReadOneAsync("Other", CancellationToken.None) is null,
                    "a restored ancestor never matches an adjacent prefix or the empty root");
            });
        }
    }

    private static Task ExclusionCaptureFailureAsync() => WithFolder("exclusion-capture-failure", async folder =>
    {
        using var rig = NewRig(Pair(exclusions: [new Exclusion(Side.Local, "Root", false)]),
            OpenStore(StateStore.DatabasePath(folder)));
        await rig.Local.EnsureFolderAsync("Root", CancellationToken.None);
        await PutAsync(rig.Local, "Root/a");
        var local = new ProxySide(rig.Local)
        {
            ListingFault = under => under == "Root" ? new InventoryIncompleteException("relist failed", null) : null
        };
        using (var failed = Log(rig, folder, "failed"))
        {
            var summary = await RunScanRepairAsync(Session(rig, failed, local: local), CancellationToken.None);
            Check.Require(summary.Completed && summary.Effects.Count == 0 &&
                await rig.Local.ReadOneAsync("Root/a", CancellationToken.None) is not null &&
                await rig.Dropbox.ReadOneAsync("Root", CancellationToken.None) is null &&
                rig.Store.Issues(rig.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure),
                "a subtree relist failing after a yielded child performs no copy or removal");
        }
        using var recovered = Log(rig, folder, "recovered");
        await RunScanRepairAsync(Session(rig, recovered), CancellationToken.None);
        Check.Require(await rig.Local.ReadOneAsync("Root", CancellationToken.None) is null &&
            await rig.Dropbox.ReadOneAsync("Root/a", CancellationToken.None) is not null &&
            rig.Store.Issues(rig.Pair.PairId).All(issue => issue.Kind != IssueKind.Failure),
            "the next complete capture retries the pending exclusion and closes its failure");
    });

    private static Task RenamesBeforeRowsAsync() => WithFolder("renames-before-rows", async folder =>
    {
        using var rig = NewRig(Pair());
        await CompleteBaselineAsync(rig, folder);
        var local = await PutAsync(rig.Local, "Old");
        var dropbox = await PutAsync(rig.Dropbox, "Old");
        RecordLinked(rig, local, dropbox);
        await rig.Local.MoveAsync(local, "New", CancellationToken.None);
        using var log = Log(rig, folder, "run-1");
        var summary = await RunScanRepairAsync(Session(rig, log), CancellationToken.None);

        Check.Require(await rig.Dropbox.ReadOneAsync("New", CancellationToken.None) is not null &&
            await rig.Dropbox.ReadOneAsync("Old", CancellationToken.None) is null,
            "the rename runs before the rows: the counterpart follows the local move");
        Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Dropbox, PathNames.DropboxKey("New")) is not null,
            "the saved link follows the rename");
        Check.Require(summary.Effects.Count == 1 &&
            summary.Effects.TryGetValue(nameof(EffectKind.MovedDropbox), out var moved) && moved == 1,
            "only the counterpart move is counted, so no row uploaded or downloaded");
        Check.Require(rig.Store.Issues(rig.Pair.PairId).Count == 0, "a followed rename raises no issue");
    });

    private static Task RefusedRenameAsync() => WithFolder("refused-rename", async folder =>
    {
        using var rig = NewRig(Pair());
        await CompleteBaselineAsync(rig, folder);
        var local = await PutAsync(rig.Local, "Old");
        var dropbox = await PutAsync(rig.Dropbox, "Old");
        RecordLinked(rig, local, dropbox);
        var moved = await rig.Local.MoveAsync(local, "New", CancellationToken.None);
        var listing = new ProxySide(rig.Local)
        {
            Extra = [moved with { Skip = nameof(TextKey.SkipOnlineOnly) }]
        };
        using var log = Log(rig, folder, "run-1");
        var summary = await RunScanRepairAsync(Session(rig, log, listing), CancellationToken.None);

        Check.Require(await rig.Dropbox.ReadOneAsync("Old", CancellationToken.None) is not null,
            "a refused rename never deletes the Dropbox counterpart");
        Check.Require(await rig.Local.ReadOneAsync("New", CancellationToken.None) is not null &&
            await rig.Local.ReadOneAsync("Old", CancellationToken.None) is null,
            "the refused rename leaves the moved local file in place");
        Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("Old")) is not null,
            "a held rename keeps the saved link");
        Check.Require(summary.Effects.Count == 0, "a held rename commits no effect");
    });

    private static Task HeldFolderRenameHoldsDescendantsAsync() => WithFolder("held-folder-rename", async folder =>
    {
        using var rig = NewRig(Pair());
        await CompleteBaselineAsync(rig, folder);
        var localFolder = await rig.Local.EnsureFolderAsync("Old", CancellationToken.None);
        var localChild = await PutAsync(rig.Local, "Old/a.txt");
        var dropboxFolder = await rig.Dropbox.EnsureFolderAsync("Old", CancellationToken.None);
        var dropboxChild = await PutAsync(rig.Dropbox, "Old/a.txt");
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(localFolder, dropboxFolder), SyncedEntries.From(localChild, dropboxChild)]
        });
        await rig.Local.MoveAsync(localFolder, "New", CancellationToken.None);
        var listing = new ProxySide(rig.Local)
        {
            Extra = [localFolder with { Path = "New", Skip = nameof(TextKey.SkipOnlineOnly) }]
        };
        using var log = Log(rig, folder, "run-1");
        var summary = await RunScanRepairAsync(Session(rig, log, local: listing), CancellationToken.None);

        Check.Require(await rig.Dropbox.ReadOneAsync("Old/a.txt", CancellationToken.None) is not null &&
            await rig.Dropbox.ReadOneAsync("New/a.txt", CancellationToken.None) is null,
            "a held folder rename holds its descendants: no child rename follows");
        Check.Require(summary.Effects.Count == 0, "no descendant rename runs under a held folder");
    });

    private static Task FolderMovedAgainAsync(bool held) => WithFolder(
        held ? "held-folder-moved-again" : "retired-folder-conflict", async folder =>
    {
        using var rig = NewRig(Pair());
        await CompleteBaselineAsync(rig, folder);
        var localFolder = await rig.Local.EnsureFolderAsync("Old", CancellationToken.None);
        var localChild = await PutAsync(rig.Local, "Old/a.txt");
        var dropboxFolder = await rig.Dropbox.EnsureFolderAsync("Old", CancellationToken.None);
        var dropboxChild = await PutAsync(rig.Dropbox, "Old/a.txt");
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Record = [SyncedEntries.From(localFolder, dropboxFolder), SyncedEntries.From(localChild, dropboxChild)],
            Raise = held
                ? [(IssueKind.Failure, TextKey.FailDropboxNoPermission, "Old", new IssueSubject([], null))]
                : [(IssueKind.Tie, TextKey.TieEqualTimes, "Old", new IssueSubject(
                    [new IssueItem(Side.Local, "Old", localFolder.Id),
                        new IssueItem(Side.Dropbox, "Old", dropboxFolder.Id)], null))]
        });
        var issueId = rig.Store.Issues(rig.Pair.PairId).Single().Id;
        var moved = localFolder;
        foreach (var target in new[] { "New", "Newest" })
        {
            moved = await rig.Local.MoveAsync(moved, target, CancellationToken.None);
            using var log = Log(rig, folder, target);
            var summary = await RunScanRepairAsync(Session(rig, log), CancellationToken.None);
            if (held)
            {
                Check.Require(summary.Effects.Count == 0 &&
                    rig.Store.IssueById(rig.Pair.PairId, issueId)?.Kind == IssueKind.Failure &&
                    await rig.Dropbox.ReadOneAsync("Old/a.txt", CancellationToken.None) is not null &&
                    await rig.Dropbox.ReadOneAsync(target, CancellationToken.None) is null,
                    "a still-held folder carries its new path forward and holds descendant renames on both moves");
            }
            else
            {
                Check.Require(summary.Effects.GetValueOrDefault(nameof(EffectKind.MovedDropbox)) == 1 &&
                    await rig.Dropbox.ReadOneAsync(target, CancellationToken.None) is not null &&
                    await rig.Dropbox.ReadOneAsync(target + "/a.txt", CancellationToken.None) is not null &&
                    rig.Store.Issues(rig.Pair.PairId).All(issue => issue.Kind != IssueKind.Tie),
                    "a moved participant retires the conflict before scheduling and never holds a later move");
            }
            Check.Require(await rig.Local.ReadOneAsync(target + "/a.txt", CancellationToken.None) is not null,
                "the folder and child stay where they were moved locally");
        }
    });

    private static Task HeldNameClashHoldsFreeRowAsync() => WithFolder("held-name-clash", async folder =>
    {
        using var rig = NewRig(Pair());
        await CompleteBaselineAsync(rig, folder);
        var local = await PutAsync(rig.Local, "Old", [1]);
        var dropbox = await PutAsync(rig.Dropbox, "Old", [1]);
        RecordLinked(rig, local, dropbox);
        // A free file occupies the rename's target; time and size make the free row a Tie if it is evaluated.
        await PutAsync(rig.Dropbox, "New", [2, 3]);
        await rig.Local.MoveAsync(local, "New", CancellationToken.None);
        using var log = Log(rig, folder, "run-1");
        await RunScanRepairAsync(Session(rig, log), CancellationToken.None);

        var issues = rig.Store.Issues(rig.Pair.PairId);
        Check.Require(issues.Any(issue => issue.Kind == IssueKind.NameClash && issue.Path == "New"),
            "a rename onto an occupied path raises the NameClash notice");
        Check.Require(issues.All(issue => issue.Kind != IssueKind.Tie),
            "the free row under the rename's held key keeps its holding kind, so no Tie notice is raised");
    });

    private static Task ReservedDropboxFolderAsync() => WithFolder("reserved-folder", async folder =>
    {
        using var rig = NewRig(Pair());
        var con = await rig.Dropbox.EnsureFolderAsync("CON", CancellationToken.None);
        await PutAsync(rig.Dropbox, "CON/a.txt");
        var dropbox = new ProxySide(rig.Dropbox)
        {
            Extra = [con with { Skip = nameof(TextKey.SkipReservedName) }]
        };
        using var log = Log(rig, folder, "run-1");
        var summary = await RunScanRepairAsync(Session(rig, log, dropbox: dropbox), CancellationToken.None);

        var issues = rig.Store.Issues(rig.Pair.PairId);
        Check.Require(issues.Count == 1 && issues[0].Kind == IssueKind.Skipped &&
            issues[0].Cause == TextKey.SkipReservedName && issues[0].Path == "CON",
            "a reserved Dropbox folder is one Skipped notice, its children add none");
        Check.Require(await rig.Local.ReadOneAsync("CON", CancellationToken.None) is null &&
            await rig.Local.ReadOneAsync("CON/a.txt", CancellationToken.None) is null,
            "nothing is created locally for a reserved folder or its children");
        Check.Require(summary.Completed && issues.All(issue => issue.Kind != IssueKind.Failure),
            "a reserved folder fails nothing");
    });

    private static Task DeferredFolderOrderAsync() => WithFolder("deferred-folder-order", async folder =>
    {
        using var rig = NewRig(Pair(), OpenStore(StateStore.DatabasePath(folder)));
        var a = await rig.Dropbox.EnsureFolderAsync("a", CancellationToken.None);
        var x = await rig.Dropbox.EnsureFolderAsync("x", CancellationToken.None);
        var xy = await rig.Dropbox.EnsureFolderAsync("x/y", CancellationToken.None);
        var d = await rig.Dropbox.EnsureFolderAsync("d", CancellationToken.None);
        var localX = await rig.Local.EnsureFolderAsync("x", CancellationToken.None);
        // x stays on both sides (unchanged); its child, a and d are absent locally, so each is deferred.
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Record =
            [
                new SyncedEntry(PathNames.Key("a"), "a", PathNames.DropboxKey("a"), "a", true, "local-a", 0, 0, a.Id, ""),
                new SyncedEntry(PathNames.Key("x"), "x", PathNames.DropboxKey("x"), "x", true, localX.Id, 0, 0, x.Id, ""),
                new SyncedEntry(PathNames.Key("x/y"), "x/y", PathNames.DropboxKey("x/y"), "x/y", true, "local-xy", 0, 0, xy.Id, ""),
                new SyncedEntry(PathNames.Key("d"), "d", PathNames.DropboxKey("d"), "d", true, "local-d", 0, 0, d.Id, "")
            ]
        });
        var deleted = new List<string>();
        var dropbox = new ProxySide(rig.Dropbox)
        {
            OnDelete = deleted.Add,
            OnSubmit = effects => deleted.AddRange(effects.Select(effect => effect.Expected!.Path))
        };
        using var log = Log(rig, folder, "run-1");
        var summary = await RunScanRepairAsync(Session(rig, log, dropbox: dropbox), CancellationToken.None);

        Check.Require(deleted.SequenceEqual(new[] { "x/y", "a", "d" }),
            "deferred folders run deepest first, then by canonical policy key");
        Check.Require(await rig.Dropbox.ReadOneAsync("x", CancellationToken.None) is not null &&
            await rig.Dropbox.ReadOneAsync("a", CancellationToken.None) is null &&
            await rig.Dropbox.ReadOneAsync("d", CancellationToken.None) is null &&
            await rig.Dropbox.ReadOneAsync("x/y", CancellationToken.None) is null,
            "the kept folder stays and the deferred ones are gone");
        Check.Require(summary.Effects.TryGetValue(EffectKind.DeletedDropbox.ToString(), out var deletedEffects) &&
            deletedEffects == 3, "the deferred deletions are counted");
    });

    private static Task MidRunCancellationAsync() => WithFolder("mid-run-cancellation", async folder =>
    {
        using var rig = NewRig(Pair());
        await PutAsync(rig.Local, "item");
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource();
        var dropbox = new ProxySide(rig.Dropbox)
        {
            BlockPublish = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        };
        using var log = Log(rig, folder, "run-1");
        var run = RunScanRepairAsync(Session(rig, log, dropbox: dropbox), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        var stopped = false;
        try { await run; }
        catch (OperationCanceledException) { stopped = true; }
        Check.Require(stopped, "cancelling in the middle of the run leaves through the cancellation");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId) is null, "a cancelled run saves no summary");
        Check.Require(!Lines(log.Path).Any(fields => fields[1] == RunLog.CompleteKind),
            "a cancelled run writes no completion line");
    });

    private static Task CancelDuringFilePassAsync() => WithFolder("cancel-file-pass", async folder =>
    {
        using var rig = NewRig(Pair());
        await PutAsync(rig.Local, "0a.txt");
        await rig.Local.EnsureFolderAsync("a", CancellationToken.None);
        await PutAsync(rig.Local, "b.txt");
        await PutAsync(rig.Local, "c.txt");
        using var cancellation = new CancellationTokenSource();
        var publishEntered = new TaskCompletionSource();
        var publishCancelled = new TaskCompletionSource();
        var ensureEntered = new TaskCompletionSource();
        var releaseEnsure = new TaskCompletionSource();
        var dropbox = new ProxySide(rig.Dropbox)
        {
            BlockPublish = async (path, ct) =>
            {
                if (path != "0a.txt") return;
                publishEntered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { publishCancelled.TrySetResult(); throw; }
            },
            BlockEnsure = path =>
            {
                if (path != "a") return null;
                ensureEntered.TrySetResult();
                return releaseEnsure.Task;
            }
        };
        var store = new HookStore(rig.Store);
        using var log = Log(rig, folder, "run-1");
        var run = RunScanRepairAsync(new SessionInputs(store, rig.Pair, Settings, rig.Scope, rig.Local, dropbox,
            rig.Recovery, rig.Admission, false, log), cancellation.Token);
        // A first-run folder with no deleted-name match is ordinary sync (RQ-178): the row pass ensures it in order,
        // with the earlier file's publication still in flight.
        await Task.WhenAll(publishEntered.Task, ensureEntered.Task).WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        releaseEnsure.SetResult();

        var stopped = false;
        try { await run; }
        catch (OperationCanceledException) { stopped = true; }
        Check.Require(stopped, "cancelling in the middle of the file pass leaves through the cancellation");
        Check.Require(store.Rows < 4, "the cancelled file pass schedules no row after the cancellation: " + store.Rows);
        Check.Require(publishCancelled.Task.IsCompleted, "the outstanding file task is joined before the run returns");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId) is null, "a cancelled file pass saves no summary");
    });

    private static Task CancelAfterListingsAsync() => WithFolder("cancel-after-listings", async folder =>
    {
        using var cancellation = new CancellationTokenSource();
        await using var rig = await ComposeRigAsync(folder,
            decorate: inner => new HookStore(inner) { OnScanRows = cancellation.Cancel });
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "a.txt"), "content");
        using var log = rig.Log(folder, "run-1");
        var stopped = false;
        try
        {
            await rig.RunAsync(log, cancellation.Token);
        }
        catch (OperationCanceledException) { stopped = true; }

        Check.Require(stopped, "a cancellation with nothing outstanding still stops the run");
        Check.Require(rig.Store.Rows == 1, "the row pass stops at its first row");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId) is null && !rig.Store.FirstRepairDone(rig.Pair.PairId),
            "a cancelled run saves no summary and marks no repair done");
        Check.Require(!Lines(log.Path).Any(fields => fields[1] == RunLog.CompleteKind),
            "a cancelled run writes no completion line");
        Check.Require(rig.Wire.Snapshot().All(item => item.Path != "a.txt"),
            "no row work starts after the cancellation");
    });

    private static Task RetainedDiscoveryAsync(bool reset) => WithFolder("retained-discovery-" + reset, async folder =>
    {
        using var cancellation = new CancellationTokenSource();
        var stop = true;
        await using var rig = await ComposeRigAsync(folder,
            decorate: inner => new HookStore(inner) { OnScanRows = () => { if (stop) cancellation.Cancel(); } });
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "a.txt"), "content");
        using (var log = rig.Log(folder, "interrupted"))
        {
            try { await rig.RunAsync(log, cancellation.Token); }
            catch (OperationCanceledException) { }
        }
        Check.Require(!rig.Store.FirstRepairDone(rig.Pair.PairId), "Cancelled first Repair stays unfinished.");
        stop = false;
        var listingFlags = new List<bool>();
        var continued = new List<string>();
        var resetReturned = false;
        rig.Transport.Respond = async (request, ct) =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (!route.EndsWith(DropboxProtocol.ListFolder, StringComparison.Ordinal) &&
                !route.EndsWith(DropboxProtocol.ListFolderContinue, StringComparison.Ordinal)) return null;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (route.EndsWith(DropboxProtocol.ListFolder, StringComparison.Ordinal))
                listingFlags.Add(body.RootElement.GetProperty("include_deleted").GetBoolean());
            if (request.RequestUri!.AbsolutePath.EndsWith(DropboxProtocol.ListFolderContinue, StringComparison.Ordinal))
            {
                var cursor = body.RootElement.GetProperty("cursor").GetString()!;
                continued.Add(cursor);
                if (reset && !resetReturned && cursor == "listing-done")
                {
                    resetReturned = true;
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent("{\"error_summary\":\"reset/..\",\"error\":{\".tag\":\"reset\"}}", Encoding.UTF8, "application/json")
                    };
                }
            }
            return null;
        };
        using (var log = rig.Log(folder, "resumed"))
            Check.Require((await rig.RunAsync(log, CancellationToken.None)).Completed, "Retained discovery Repair completes.");
        Check.Require(listingFlags.SequenceEqual(new[] { reset }) && continued.Contains("listing-done") && resetReturned == reset,
            "After abort, continue discovery then list live only; invalid cursors fall back to full discovery.");
        Check.Require(rig.Wire.Snapshot().Any(item => item.Path == "a.txt"), "Fresh row work is not skipped by retained history.");
        listingFlags.Clear();
        continued.Clear();
        using (var log = rig.Log(folder, "ordinary"))
            Check.Require((await rig.RunAsync(log, CancellationToken.None)).Completed, "Ordinary retained-history Repair completes.");
        // D-1105-41 / RQ-178 retains the resumed acquisition's advanced cursor, not its original cursor.
        var retainedCursor = reset ? "listing-done" : "listing-done-end";
        Check.Require(listingFlags.SequenceEqual(new[] { false }) &&
            continued.Where(cursor => cursor.StartsWith("listing-done", StringComparison.Ordinal))
                .SequenceEqual(new[] { retainedCursor }),
            "An unchanged ordinary Repair continues the last published history cursor exactly once and lists current live items once: " +
            string.Join(",", continued));
    });

    private static Task AbortBothSinksAsync() => WithFolder("abort-both-sinks", async folder =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await ComposeRigAsync(folder, decorate: inner => new HookStore(inner)
        {
            WrapSink = (side, sink) => side == Side.Local
                ? new AbortSink(sink, new InvalidOperationException("first abort failed"), null)
                : new AbortSink(sink, new InvalidOperationException("second abort failed"),
                    () => entered.TrySetResult(), () => release.Task)
        });
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "item"), "content");
        rig.Wire.RequestException = route => route.EndsWith("/list_folder", StringComparison.Ordinal)
            ? new InventoryIncompleteException("incomplete", null) : null;
        using var log = rig.Log(folder, "run-1");
        var run = rig.RunAsync(log, CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.Require(!run.IsCompleted, "the run waits for the second abort to finish");
        release.TrySetResult();
        var summary = await run;

        Check.Require(!summary.Completed && summary.IncompleteReason == "incomplete",
            "the listing's own incomplete diagnosis survives abort failures");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId)!.Completed == false,
            "the incomplete summary is still saved");
        Check.Require(Lines(log.Path).Any(fields => fields[1] == nameof(RunEventKind.Failed) &&
            fields[2] == nameof(AggregateException) &&
            fields[4].Split(nameof(InvalidOperationException)).Length == 3),
            "both abort failures retain their typed evidence as secondary diagnoses");
        RequireFailed(log.Path, nameof(InventoryIncompleteException));
    });

    private static Task AbortFailureKeepsListingErrorAsync() => WithFolder("abort-listing-error", async folder =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await ComposeRigAsync(folder, decorate: inner => new HookStore(inner)
        {
            WrapSink = (side, sink) => side == Side.Local
                ? new AbortSink(sink, new InvalidOperationException("abort failed"), null)
                : new AbortSink(sink, null, () => entered.TrySetResult(), () => release.Task)
        });
        rig.Wire.RequestException = route => route.EndsWith("/list_folder", StringComparison.Ordinal)
            ? new InvalidOperationException("listing failed") : null;
        using var log = rig.Log(folder, "run-1");
        var run = rig.RunAsync(log, CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.Require(!run.IsCompleted, "the run waits for the second abort to finish");
        release.TrySetResult();
        Exception? thrown = null;
        try { await run; }
        catch (Exception error) { thrown = error; }

        Check.Require(thrown is InvalidOperationException { Message: "listing failed" },
            "a non-inventory listing failure is rethrown, not replaced by the abort failure");
        Check.Require(Lines(log.Path).Any(fields => fields[1] == nameof(RunEventKind.Failed) &&
            fields[2] == nameof(AggregateException) && fields[4].Contains(nameof(InvalidOperationException))),
            "the abort failure is logged as secondary");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId) is null, "a crashed run saves no summary");
    });

    private static async Task CaseRenameFallbackAsync()
    {
        foreach (var matching in new[] { true, false })
        {
            await WithFolder(matching ? "case-rename-fallback" : "case-rename-fallback-mismatch", async folder =>
            {
                using var rig = NewRig(Pair());
                await CompleteBaselineAsync(rig, folder);
                var local = await PutAsync(rig.Local, "Old");
                var dropbox = await PutAsync(rig.Dropbox, "OLD");
                RecordLinked(rig, local, dropbox);
                var moved = await rig.Dropbox.MoveAsync(dropbox, "OLD" + Settings.CaseRenameSuffix,
                    CancellationToken.None);
                if (!matching)
                    rig.Local.Seed(local with { Id = "replaced" }, [1]);
                using var log = Log(rig, folder, "run-1");
                var summary = await RunScanRepairAsync(Session(rig, log), CancellationToken.None);

                var expected = matching ? "Old" : "OLD";
                Check.Require(await rig.Dropbox.ReadOneAsync(expected, CancellationToken.None) is not null &&
                    await rig.Dropbox.ReadOneAsync(moved.Path, CancellationToken.None) is null,
                    matching
                        ? "the scan entry at the saved local key supplies the local spelling"
                        : "a mismatched id at the saved local key leaves the saved Dropbox spelling");
                Check.Require(summary.Completed && rig.Store.Issues(rig.Pair.PairId).Count == 0,
                    "finishing an interrupted case rename raises no issue");
            });
        }
    }

    private static Task MemberFaultLoggedAsync() => WithFolder("member-fault-logged", async folder =>
    {
        await using var rig = await ComposeRigAsync(folder);
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "a.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "b.txt"), "content");
        foreach (var path in new[] { "a.txt", "b.txt" })
            File.SetLastWriteTimeUtc(Path.Combine(rig.Pair.LocalRoot, path), new DateTime(2020, 1, 1));
        var fault = new InvalidOperationException("provider exploded");
        var injected = 0;
        rig.Wire.RequestException = route =>
            (route.EndsWith(DropboxProtocol.UploadSessionStart, StringComparison.Ordinal) ||
                route.EndsWith(DropboxProtocol.Upload, StringComparison.Ordinal)) &&
            Interlocked.CompareExchange(ref injected, 1, 0) == 0 ? fault : null;
        using var log = rig.Log(folder, "run-1");
        Exception? surfaced = null;
        try { await rig.RunAsync(log, CancellationToken.None); }
        catch (Exception error) { surfaced = error; }

        Check.Require(surfaced is InvalidOperationException && surfaced.Message == fault.Message,
            "a member fault surfaces as the original error, not as cancellation: " + surfaced?.GetType().Name +
            " " + surfaced?.Message + "; routes=" + string.Join(",", rig.Wire.Calls) +
            "; issues=" + JsonSerializer.Serialize(rig.Store.Issues(rig.Pair.PairId)));
        Check.Require(Lines(log.Path).Any(fields => fields[1] == nameof(RunEventKind.Failed) &&
            fields[2] == nameof(InvalidOperationException)), "the member fault is logged as the original error");
        Check.Require(rig.Wire.Snapshot().All(item => item.Path != "b.txt"),
            "no further row work starts after the member fault");
    });

    private static Task CompletionWriteFailureAsync() => WithFolder("completion-write-failure", async folder =>
    {
        await using var rig = await ComposeRigAsync(folder);
        using var log = rig.Log(folder, "run-1");
        FailOnLogEvent(log, RunLog.CompleteKind);
        var summary = await rig.RunAsync(log, CancellationToken.None);

        Check.Require(summary.Completed && summary.LogFailed, "a failed completion line does not fail the run");
        var saved = rig.Store.LastRepair(rig.Pair.PairId)!;
        Check.Require(saved.Completed == summary.Completed && saved.LogFailed == summary.LogFailed &&
            rig.Store.FirstRepairDone(rig.Pair.PairId),
            "late writer failure preserves authoritative completion and agrees with reloaded logging metadata");
    });

    // The deleting-side selector adds no allocation over the inline null test.
    private static void DeletingSideAllocatesNothing()
    {
        var local = MakeRow("local", local: true);
        var dropbox = MakeRow("dropbox", local: false);
        Consume(local.Local is null ? Side.Dropbox : Side.Local);
        Consume(FolderDeletes.DeletingSide(local));
        var baseline = AllocProbe.Measure(i =>
            Consume((i % 2 == 0 ? local : dropbox).Local is null ? Side.Dropbox : Side.Local), 1_000, 0);
        var owner = AllocProbe.Measure(i =>
            Consume(FolderDeletes.DeletingSide(i % 2 == 0 ? local : dropbox)), 1_000, 0);
        Check.Require(owner == baseline && owner == 0,
            "DeletingSide adds no allocation over the inline null test (owner=" + owner + " B, baseline=" + baseline +
            " B per 1,000 calls)");
    }

    // FirstOn over a real GroupBy-produced grouping costs no more than the original predicate. The
    // original predicate captured nothing, so no closure saving is claimed; both enumerate the grouping's
    // boxed struct enumerator.
    private static void RenameSelectAllocatesNothing()
    {
        var moved = new Entry("New", false, 1, DateTimeOffset.UnixEpoch, "id", "", "", null);
        var localGroup = Group(
            new RenameCandidate(Side.Local, moved, SyncedEntryAt("A")),
            new RenameCandidate(Side.Dropbox, moved, SyncedEntryAt("A")));
        var dropboxOnlyGroup = Group(new RenameCandidate(Side.Dropbox, moved, SyncedEntryAt("B")));
        Consume(localGroup.FirstOrDefault(candidate => candidate.Side == Side.Local));
        Consume(Renames.FirstOn(localGroup, Side.Local));
        Consume(dropboxOnlyGroup.FirstOrDefault(candidate => candidate.Side == Side.Local));
        Consume(Renames.FirstOn(dropboxOnlyGroup, Side.Local));
        // The explicit calls above warm both group variants; no extra warm-up here so the measured loop
        // still captures the same first-call cost as the inline baseline.
        var baseline = AllocProbe.Measure(i =>
        {
            var group = i % 2 == 0 ? localGroup : dropboxOnlyGroup;
            Consume(group.FirstOrDefault(candidate => candidate.Side == Side.Local));
        }, 1_000, 0);
        var owner = AllocProbe.Measure(i =>
        {
            var group = i % 2 == 0 ? localGroup : dropboxOnlyGroup;
            Consume(Renames.FirstOn(group, Side.Local));
        }, 1_000, 0);
        Check.Require(owner <= baseline,
            "FirstOn over a GroupBy grouping costs no more than the original predicate (owner=" + owner +
            " B, baseline=" + baseline + " B per 1,000 calls; local-present and local-absent groups)");
    }

    private static IGrouping<SyncedEntry, RenameCandidate> Group(params RenameCandidate[] candidates) =>
        candidates.GroupBy(candidate => candidate.Synced).First();

    private static SyncedEntry SyncedEntryAt(string path) =>
        new(PathNames.Key(path), path, PathNames.DropboxKey(path), path, false, "", 0, 0, "", "");

    private static Row MakeRow(string key, bool local) =>
        new(key, local ? new Entry(key, false, 1, DateTimeOffset.UnixEpoch, key, "", "", null) : null,
            local ? null : new Entry(key, false, 1, DateTimeOffset.UnixEpoch, key, "", "", null), null, [], null,
            new RowContext(false, false, false, null, false, false, false, false));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(Side side) => _ = side;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(RenameCandidate? candidate) => _ = candidate;

    // Selects the exact boundary so a synthetic disk failure cannot hide an earlier logging defect.
    internal static void FailOnLogEvent(RunLog log, string kind, string? what = null)
    {
        var field = typeof(RunLog).GetField("_writer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(log, new EventFailingWriter((TextWriter)field.GetValue(log)!, kind, what));
    }

    private static Task DisabledAsync() => WithFolder("disabled", async folder =>
    {
        await using var rig = await ComposeRigAsync(folder, enabled: false);
        using var log = rig.Log(folder, "run-1");
        var refused = false;
        try { await rig.RunAsync(log, CancellationToken.None); }
        catch (EngineRequestException error) { refused = error.Key == TextKey.PairDisabled; }
        Check.Require(refused, "a switched-off pair is refused");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId) is null && !rig.Store.FirstRepairDone(rig.Pair.PairId),
            "a refused pair changes nothing");
    });

    private static Task EmptyRunAsync() => WithFolder("empty", async folder =>
    {
        var recovery = new FakeRecovery { Evidence = new(1, 1, 100) };
        await using var rig = await ComposeRigAsync(folder, recovery: recovery);
        using var first = rig.Log(folder, "run-1");
        IAdmission? admission = null;
        var summary = await rig.RunAsync(first, CancellationToken.None, (context, _) =>
        {
            admission = context.Admission;
            return Task.CompletedTask;
        });

        Check.Require(summary.Completed && summary.FirstRun && !summary.RecoveredPending &&
            summary.IncompleteReason is null, "an empty first run completes as a first run");
        Check.Require(summary.RunId == "run-1" && summary.LogPath == first.Path && !summary.LogFailed,
            "the summary's run id and log path come from the opened log");
        Check.Require(summary.Effects.Count == 0 && summary.IssuesByKind.Count == 0,
            "an empty run counts nothing");
        Check.Require(summary.Started <= summary.Finished, "the summary timestamps are ordered");
        Check.Require(rig.Store.FirstRepairDone(rig.Pair.PairId), "a completed run marks the first repair done");
        var saved = rig.Store.LastRepair(rig.Pair.PairId)!;
        Check.Require(saved.Completed && saved.RunId == "run-1" && saved.LogFailed == summary.LogFailed,
            "the returned summary is the saved one");
        Check.Require(rig.Store.Pair(rig.Pair.PairId)!.Drive == recovery.Evidence,
            "the available journal id is saved");
        Check.Require(admission is not null && rig.Store.LimiterLevel(rig.Pair.ConnectionId) == admission.Level,
            "the current limiter level, including completed listing reads, is saved");
        RequireStages(first.Path, nameof(TextKey.StageListing), nameof(TextKey.StageAfterListing),
            nameof(TextKey.StageRenames), nameof(TextKey.StageRows), nameof(TextKey.StageFolderPlans),
            nameof(TextKey.StageFinish));

        using var second = rig.Log(folder, "run-2");
        var later = await rig.RunAsync(second, CancellationToken.None);
        Check.Require(!later.FirstRun && later.RunId == "run-2", "a later run is not a first run");
    });

    private static Task CorruptHistoryRestoreAsync(bool? incomplete = null) => WithFolder("corrupt-history-restore-" + incomplete, async folder =>
    {
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(TimeProvider.System);
        StateStore storage = null!;
        var finishes = 0;
        var recovering = false;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await ComposeRigAsync(folder, monitor: monitor,
            openLog: id => RunLog.Open(folder, id, "automatic-restore", _ => { }, Settings.LogBufferCharacters),
            decorate: inner =>
        {
            storage = (StateStore)inner;
            return new HookStore(inner)
            {
                BeforeFinishRepair = () =>
                {
                    finishes++;
                    if (recovering) finished.TrySetResult();
                }
            };
        });
        rig.Wire.SeedDeletedEntry("Unused");
        using (var baseline = rig.Log(folder, "baseline"))
            Check.Require((await rig.RunAsync(baseline, default)).Completed, "restore fixture baseline completes");
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "A"), "retained");
        using (var setup = rig.Log(folder, "retained-issue"))
            await rig.Host.DispatchSessionAsync(rig.Pair.PairId, () => setup, async (session, ct) =>
            {
                var entry = (await session.Local.ReadOneAsync("A", ct))!;
                rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, "A",
                        new IssueSubject([new IssueItem(Side.Local, "A", entry.Id)], null,
                            FirstRunDeletion: IssueSubject.FirstRunDeletionKind.Item))]
                });
                return true;
            }, default);
        Massdex.SyncShark.Engine.Tests.Storage.DeletedHistoryChecks.CorruptSavedHistory(storage);
        var revision = storage.ReadSource(rig.Pair.PairId, Side.Local)!.Value.Revision;
        if (incomplete is { } classified)
        {
            var fault = classified ? (Exception)new InventoryIncompleteException("incomplete", null)
                : new InvalidOperationException("listing failed");
            rig.Wire.RequestException = route => route.EndsWith("/list_folder", StringComparison.Ordinal) ? fault : null;
            RepairSummary? failed = null;
            var operations = new EngineOperations(rig.Instance.StateAuthority.ProductRoot, _ => { }, rig.Host);
            try { failed = await operations.RepairAsync(rig.Pair.PairId, default); }
            catch (InvalidOperationException error) when (ReferenceEquals(error, fault)) { }
            Check.Require((classified ? failed is { Completed: false } : failed is null) &&
                storage.DeletedHistoryOwed(rig.Pair.PairId) && storage.Obligated(rig.Pair.PairId) &&
                storage.ReadSource(rig.Pair.PairId, Side.Local)!.Value.Revision == revision + 1 &&
                storage.SyncedByKey(rig.Pair.PairId, Side.Dropbox, "A") is null &&
                File.Exists(Path.Combine(rig.Pair.LocalRoot, "A")) &&
                storage.Issues(rig.Pair.PairId).Any(issue => issue.Path == "A" && issue.Kind == IssueKind.Failure),
                "both incomplete and exceptional Restore exits retain rows and install acquisition responsibility");
            rig.Wire.RequestException = null;
            recovering = true;
            monitor.Start();
            try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { await monitor.StopAsync(); }
            Check.Require(finishes == 2 && storage.LastRepair(rig.Pair.PairId) is { Completed: true, RunId: "automatic-restore" } &&
                !storage.DeletedHistoryOwed(rig.Pair.PairId) && !storage.Obligated(rig.Pair.PairId) &&
                !storage.Issues(rig.Pair.PairId).Any(issue => issue.Cause == TextKey.RequiredRepairRetry ||
                    issue.Path == "A" && issue.Kind == IssueKind.Failure) &&
                storage.SyncedByKey(rig.Pair.PairId, Side.Dropbox, "A") is not null,
                "Monitor automatically reacquires history and settles the retained row after a failed Restore attempt");
            return;
        }
        using var log = rig.Log(folder, "restore-repair");
        var summary = await rig.RunAsync(log, default);
        Check.Require(summary.Completed && !log.HasFailed && finishes == 2 &&
            storage.ReadSource(rig.Pair.PairId, Side.Local)!.Value.Revision == revision &&
            !storage.DeletedHistoryOwed(rig.Pair.PairId) && !storage.Obligated(rig.Pair.PairId) &&
            storage.ReadPrerequisiteRetry(rig.Pair.PairId) is null &&
            storage.Issues(rig.Pair.PairId).Any(issue => issue.Cause == TextKey.DeletedHistoryCorrupt) &&
            !storage.Issues(rig.Pair.PairId).Any(issue => issue.Cause == TextKey.RequiredRepairRetry ||
                issue.Path == "A" && issue.Kind == IssueKind.Failure) &&
            storage.SyncedByKey(rig.Pair.PairId, Side.Dropbox, "A") is not null,
            "Restore corruption is recovered by exactly one completed Repair without retry debt or failure notice");
    });

    private static Task CorruptHistoryRevisionRetryAsync() => WithFolder("corrupt-revision-retry", async folder =>
    {
        var clock = new FakeTimeProvider();
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StateStore storage = null!;
        var overlap = false;
        var finishes = 0;
        await using var rig = await ComposeRigAsync(folder, time: clock, monitor: monitor,
            openLog: id => RunLog.Open(folder, id, "prerequisite-" + Guid.NewGuid().ToString("N"),
                _ => { }, Settings.LogBufferCharacters),
            decorate: inner =>
            {
                storage = (StateStore)inner;
                return new HookStore(inner)
                {
                    BeforeFinishRepair = () =>
                    {
                        if (!overlap) return;
                        if (++finishes == 1)
                        {
                            entered.TrySetResult();
                            // FinishRepair is synchronous; the test holds its worker, not the async driver.
                            Check.Require(release.Wait(TimeSpan.FromSeconds(30)), "overlap driver releases the finish gate");
                        }
                        else finished.TrySetResult();
                    }
                };
            });
        var pairId = rig.Pair.PairId;
        Directory.CreateDirectory(Path.Combine(rig.Pair.LocalRoot, "Parent"));
        rig.Wire.SeedFolder("Parent");
        rig.Wire.SeedDeletedEntry("Unused");
        using (var baseline = rig.Log(folder, "baseline"))
            Check.Require((await rig.RunAsync(baseline, default)).Completed, "overlap fixture baseline completes");
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "A"), "settled");
        var generation = storage.ReadPairExecution(pairId)!.Value.Generation;
        storage.RequireRepairAfterLoss(pairId, generation);
        var revision = storage.ReadSource(pairId, Side.Local)!.Value.Revision;
        overlap = true;
        var first = Task.Run(() => rig.Host.RunPrerequisiteAsync(pairId, default));
        var refused = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Check.Require(storage.SyncedByKey(pairId, Side.Local, "A") is not null,
                "the whole Repair commits a row before the overlapping Retry");
            await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "Parent", "B"), "retained");
            using (var setup = rig.Log(folder, "retained-issues"))
                await rig.Host.DispatchSessionAsync(pairId, () => setup, async (session, ct) =>
                {
                    var entry = (await session.Local.ReadOneAsync("Parent/B", ct))!;
                    var parent = (await session.Dropbox.ReadOneAsync("Parent", ct))!;
                    storage.Commit(pairId, StateChange.Empty with
                    {
                        Raise = [
                            (IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, "Parent/B",
                                new IssueSubject([new IssueItem(Side.Local, "Parent/B", entry.Id)], null,
                                    FirstRunDeletion: IssueSubject.FirstRunDeletionKind.Item)),
                            (IssueKind.Failure, TextKey.FailChanged, "Parent",
                                new IssueSubject([new IssueItem(Side.Dropbox, "Parent", parent.Id)], null))]
                    });
                    return true;
                }, default);
            Massdex.SyncShark.Engine.Tests.Storage.DeletedHistoryChecks.CorruptSavedHistory(storage);
            var issue = storage.Issues(pairId).Single(value => value.Path == "Parent");
            var operations = new EngineOperations(rig.Instance.StateAuthority.ProductRoot, _ => { }, rig.Host);
            await operations.ResolveAsync(pairId, issue.Id, ResolutionChoice.Retry, null, null, false, null, default);
            Check.Require(storage.DeletedHistoryOwed(pairId) && File.Exists(Path.Combine(rig.Pair.LocalRoot, "Parent", "B")) &&
                storage.SyncedByKey(pairId, Side.Dropbox, PathNames.Key("Parent/B")) is null &&
                storage.Issues(pairId).Any(value => value.Path == "Parent/B" && value.Kind == IssueKind.Failure),
                "overlapping Retry discovers corrupt history and retains its affected row without upload or deletion");
        }
        finally
        {
            release.Set();
            try { await first; }
            catch (InvalidOperationException) { refused = true; }
        }
        var committed = storage.SyncedByKey(pairId, Side.Local, "A");
        var retry = storage.ReadPrerequisiteRetry(pairId);
        Check.Require(refused && committed is not null && storage.Obligated(pairId) &&
            storage.ReadSource(pairId, Side.Local)!.Value.Revision == revision + 1 && retry is { Attempts: 1 } &&
            storage.Issues(pairId).Any(issue => issue.Cause == TextKey.RequiredRepairRetry) &&
            storage.LastRepair(pairId) is { RunId: "baseline" },
            "refused finish preserves committed rows and newer debt and records the failed prerequisite");
        clock.Advance(retry!.Value.RetryAfter - clock.GetUtcNow());
        monitor.Start();
        try { await finished.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { await monitor.StopAsync(); }
        Check.Require(finishes == 2 && storage.LastRepair(pairId) is { Completed: true } &&
            !storage.DeletedHistoryOwed(pairId) && !storage.Obligated(pairId) &&
            storage.ReadPrerequisiteRetry(pairId) is null &&
            !storage.Issues(pairId).Any(issue => issue.Cause == TextKey.RequiredRepairRetry ||
                issue.Path == "Parent/B" && issue.Kind == IssueKind.Failure) &&
            storage.SyncedByKey(pairId, Side.Local, "A") == committed &&
            storage.SyncedByKey(pairId, Side.Dropbox, PathNames.Key("Parent/B")) is not null,
            "the same host automatically retries, retains settled rows, settles held rows and clears the retry notice");
    });

    private static Task CorruptHistoryDuringRowsAsync() => WithFolder("corrupt-during-rows", async folder =>
    {
        StateStore storage = null!;
        var inject = false;
        var detected = false;
        void Corrupt()
        {
            if (!inject) return;
            Check.Require(!storage!.DeletedHistoryOwed("pair-a"), "corruption is injected after history publication");
            Massdex.SyncShark.Engine.Tests.Storage.DeletedHistoryChecks.CorruptSavedHistory(storage);
        }
        await using var rig = await ComposeRigAsync(folder, decorate: inner =>
        {
            storage = (StateStore)inner;
            return new HookStore(inner)
            {
                OnScanRows = Corrupt
            };
        });
        using (var baseline = rig.Log(folder, "baseline"))
            Check.Require((await rig.RunAsync(baseline, default)).Completed, "corruption fixture baseline completes");
        var pairId = rig.Pair.PairId;
        var local = rig.Store.ReadSource(pairId, Side.Local)!.Value;
        Check.Require(!rig.Store.Obligated(pairId), "corruption starts without prior Repair debt");
        rig.Wire.SeedDeletedEntry("Unused");
        {
            await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "A"), "retained");
            using var setup = rig.Log(folder, "retained-issue");
            await rig.Host.DispatchSessionAsync(pairId, () => setup, async (session, ct) =>
            {
                var entry = (await session.Local.ReadOneAsync("A", ct))!;
                rig.Store.Commit(pairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, "A",
                        new IssueSubject([new IssueItem(Side.Local, "A", entry.Id)], null,
                            FirstRunDeletion: IssueSubject.FirstRunDeletionKind.Item))]
                });
                return true;
            }, default);
        }
        // A null saved cursor forces acquisition of the scripted tombstone, not an unrelated empty delta page.
        var generation = rig.Store.ReadPairExecution(pairId)!.Value.Generation;
        var token = storage!.BeginScan(pairId, generation, true);
        storage!.ResetDeletedHistory(pairId, token, discardNames: true);
        await storage.EndScanAsync(pairId, token, Settings.StoragePageSize);
        inject = true;
        using var log = rig.Log(folder, "corrupt-run");
        RepairSummary? summary = null;
        try { summary = await rig.RunAsync(log, default); }
        catch (InvalidDataException) { detected = true; }
        var after = rig.Store.ReadSource(pairId, Side.Local)!.Value;
        Check.Require(detected && storage.DeletedHistoryOwed(pairId) && rig.Store.Obligated(pairId) &&
            after.Revision == local.Revision + 1 && after.Obligation == SourceObligation.NormalRepair,
            "corruption advances the obligation revision and an already-running Repair cannot erase it");
        Check.Require(summary is null, "Repair row-pass corruption returns no completed summary");
        Check.Require(File.Exists(Path.Combine(rig.Pair.LocalRoot, "A")),
            "Repair row-pass corruption preserves the local file");
        Check.Require(rig.Wire.LastUploadCommitPaths.Count == 0,
            "Repair row-pass corruption performs no upload");
        Check.Require(rig.Store.Issues(pairId).Any(issue => issue.Path == "A" && issue.Kind == IssueKind.Failure),
            "Repair row-pass corruption preserves the retained issue");
        RequireFailed(log.Path, nameof(InvalidDataException));
    });

    private static Task CorruptHistoryWakesRepairAsync() => WithFolder("corrupt-history-wake", async folder =>
    {
        var monitor = new Massdex.SyncShark.Engine.Run.Monitor(TimeProvider.System);
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairing = false;
        StateStore storage = null!;
        await using var rig = await ComposeRigAsync(folder, monitor: monitor,
            openLog: pair => RunLog.Open(folder, pair, "automatic-repair", _ => { }, Settings.LogBufferCharacters),
            decorate: inner =>
            {
                storage = (StateStore)inner;
                return new HookStore(inner) { BeforeFinishRepair = () => { if (repairing) finishing.TrySetResult(); } };
            });
        Directory.CreateDirectory(Path.Combine(rig.Pair.LocalRoot, "Parent"));
        rig.Wire.SeedFolder("Parent");
        using (var baseline = rig.Log(folder, "baseline"))
            Check.Require((await rig.RunAsync(baseline, default)).Completed, "wake fixture baseline completes");
        var pairId = rig.Pair.PairId;
        Check.Require(!rig.Store.Obligated(pairId) &&
            rig.Store.ReadSource(pairId, Side.Local)!.Value.Obligation is null &&
            rig.Store.ReadSource(pairId, Side.Dropbox)!.Value.Obligation is null,
            "both sources are healthy before history corruption");
        var paths = new[] { "Parent/A", "Parent/B" };
        foreach (var path in paths) await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, path), "retained");
        using (var setup = rig.Log(folder, "retained-issues"))
            await rig.Host.DispatchSessionAsync(pairId, () => setup, async (session, ct) =>
            {
                foreach (var path in paths)
                {
                    var entry = (await session.Local.ReadOneAsync(path, ct))!;
                    rig.Store.Commit(pairId, StateChange.Empty with
                    {
                        Raise = [(IssueKind.Failure, TextKey.FailFirstSyncCheckWaiting, path,
                            new IssueSubject([new IssueItem(Side.Local, path, entry.Id)], null,
                                FirstRunDeletion: IssueSubject.FirstRunDeletionKind.Item))]
                    });
                }
                var parent = (await session.Dropbox.ReadOneAsync("Parent", ct))!;
                rig.Store.Commit(pairId, StateChange.Empty with
                {
                    Raise = [(IssueKind.Failure, TextKey.FailChanged, "Parent",
                        new IssueSubject([new IssueItem(Side.Dropbox, "Parent", parent.Id)], null))]
                });
                return true;
            }, default);
        var generation = rig.Store.ReadPairExecution(pairId)!.Value.Generation;
        var token = storage!.BeginScan(pairId, generation, true);
        var sink = storage!.OpenScan(pairId, Side.Dropbox, ScanTestSettings.BatchRows);
        await sink.AddAsync(new Listed("Unused", null), default);
        await sink.CompleteAsync();
        storage.SaveDeletedHistory(pairId, token, "retained-history");
        await storage.EndScanAsync(pairId, token, Settings.StoragePageSize);
        Massdex.SyncShark.Engine.Tests.Storage.DeletedHistoryChecks.CorruptSavedHistory(storage);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Wire.BeforeListingAsync = async (_, _) =>
        {
            if (!storage.DeletedHistoryOwed(pairId)) return;
            repairing = true;
            entered.TrySetResult();
            await release.Task;
        };
        monitor.Start();
        try
        {
            var issue = rig.Store.Issues(pairId).Single(value => value.Path == "Parent");
            var operations = new EngineOperations(rig.Instance.StateAuthority.ProductRoot, _ => { }, rig.Host);
            await operations.ResolveAsync(pairId, issue.Id, ResolutionChoice.Retry, null, null, false, null, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Check.Require(storage.DeletedHistoryOwed(pairId) && rig.Store.Obligated(pairId) &&
                rig.Store.ReadSource(pairId, Side.Local)!.Value.Obligation == SourceObligation.NormalRepair &&
                paths.All(path => File.Exists(Path.Combine(rig.Pair.LocalRoot, path)) &&
                    rig.Store.Issues(pairId).Any(value => value.Path == path && value.Kind == IssueKind.Failure)),
                "operation completion automatically schedules owed-history Repair while retained rows stay held");
            release.TrySetResult();
            await finishing.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            release.TrySetResult();
            await monitor.StopAsync();
        }
        Check.Require(!storage.DeletedHistoryOwed(pairId) && !rig.Store.Obligated(pairId) &&
            rig.Store.LastRepair(pairId) is { Completed: true, RunId: "automatic-repair" } &&
            paths.All(path => File.Exists(Path.Combine(rig.Pair.LocalRoot, path)) &&
                !rig.Store.Issues(pairId).Any(value => value.Path == path && value.Kind == IssueKind.Failure) &&
                rig.Store.SyncedByKey(pairId, Side.Dropbox, PathNames.Key(path)) is not null),
            "automatic Repair reacquires history and settles every retained row without another user action");
    });

    private static Task ListingsOverlapAsync() => WithFolder("overlap", async folder =>
    {
        var dropboxListing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new bool[2];
        await using var rig = await ComposeRigAsync(folder, decorate: inner => new HookStore(inner)
        {
            WrapSink = (side, sink) => new FinishedSink(side == Side.Local
                ? new CompletionGateSink(sink, () => dropboxListing.Task.WaitAsync(TimeSpan.FromSeconds(5))) : sink,
                () => finished[side == Side.Local ? 0 : 1] = true)
        });
        rig.Wire.BeforeListingAsync = (_, _) => { dropboxListing.TrySetResult(); return Task.CompletedTask; };
        using var log = rig.Log(folder, "run-1");
        var summary = await rig.RunAsync(log, CancellationToken.None);

        Check.Require(summary.Completed && finished.All(value => value),
            "both overlapping listings are joined before the run completes");
    });

    private static Task JournalChangedAsync() => WithFolder("journal-changed", async folder =>
    {
        var recovery = new FakeRecovery { Evidence = new(1, 1, 100) };
        await using var rig = await ComposeRigAsync(folder, recovery: recovery, drive: recovery.Evidence);
        await SeedLinkedAsync(rig, "kept");
        await SeedLinkedAsync(rig, "gone");
        using (var baseline = rig.Log(folder, "baseline"))
            Check.Require((await rig.RunAsync(baseline, CancellationToken.None)).Completed, "baseline completes");
        recovery.Evidence = new(1, 2, 10);
        File.Delete(Path.Combine(rig.Pair.LocalRoot, "gone"));
        using var log = rig.Log(folder, "run-1");
        var summary = await rig.RunAsync(log, CancellationToken.None);

        Check.Require(!summary.FirstRun, "a changed journal id stays a normal run");
        Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("kept")) is not null,
            "a changed journal id keeps the saved records");
        Check.Require(rig.Wire.Snapshot().All(item => item.Path != "gone") &&
            !File.Exists(Path.Combine(rig.Pair.LocalRoot, "gone")),
            "a deletion with a record propagates instead of restoring the file");
        Check.Require(summary.Effects.TryGetValue(EffectKind.DeletedDropbox.ToString(), out var deleted) &&
            deleted == 1, "the propagated deletion is counted");
        Check.Require(rig.Store.Pair(rig.Pair.PairId)!.Drive == recovery.Evidence, "the new journal id is saved");
        Check.Require(recovery.Removals == 2, "baseline and current Repair each clean only after completed listings");
        RequireStages(log.Path, nameof(TextKey.StageListing), nameof(TextKey.StageAfterListing),
            nameof(TextKey.StageRenames), nameof(TextKey.StageRows), nameof(TextKey.StageFolderPlans),
            nameof(TextKey.StageFinish));
    });

    private static Task JournalUnavailableAsync() => WithFolder("journal-unavailable", async folder =>
    {
        var recovery = new FakeRecovery { Evidence = new(1, 1, 100) };
        await using var rig = await ComposeRigAsync(folder, recovery: recovery, drive: recovery.Evidence);
        await SeedLinkedAsync(rig, "item");
        using (var baseline = rig.Log(folder, "baseline"))
            Check.Require((await rig.RunAsync(baseline, CancellationToken.None)).Completed, "baseline completes");
        recovery.Evidence = new(null, null, null);
        using var log = rig.Log(folder, "run-1");
        var summary = await rig.RunAsync(log, CancellationToken.None);

        Check.Require(!summary.FirstRun, "an unavailable journal id is not evidence of a restore");
        Check.Require(rig.Store.Pair(rig.Pair.PairId)!.Drive == rig.Pair.Drive,
            "an unavailable id never clears the saved one");
        Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("item")) is not null,
            "an unavailable id sets aside no record");
        RequireStages(log.Path, nameof(TextKey.StageListing), nameof(TextKey.StageAfterListing),
            nameof(TextKey.StageRenames), nameof(TextKey.StageRows), nameof(TextKey.StageFolderPlans),
            nameof(TextKey.StageFinish));
    });

    private static Task PendingAsync() => WithFolder("pending", async folder =>
    {
        var source = new Entry("item", false, 1, DateTimeOffset.UnixEpoch, "source-id", "rev-1", "", null);
        var pending = PendingFixture("item", "stage", source);
        var native = new FakeRecovery
        {
            Result = new RemovalResult(RemovalOutcome.NoEffect, pending.OperationId, null, null)
        };
        var sequence = new List<string>();
        IStateStore? observed = null;
        var recovery = new RecoveryProbe(native)
        {
            Reading = read =>
            {
                if (read == 1) sequence.Add("drive");
                return new(1, 1, 100);
            },
            OnPending = () => sequence.Add("pending"),
            OnCleanup = () => Check.Require(observed!.Pending("pair-a") is NoPending,
                "own pending recovery finishes before stale-stage cleanup")
        };
        await using var rig = await ComposeRigAsync(folder, recovery: recovery, decorate: inner => new HookStore(inner)
        {
            BeforeOpenScan = _ => Check.Require(sequence.SequenceEqual(new[] { "drive", "pending" }),
                "start evidence precedes pending recovery, which precedes either listing")
        });
        observed = rig.Store;
        rig.Store.ArmPending(rig.Pair.PairId, pending);
        rig.Wire.SeedFile("item", "content");
        using var log = rig.Log(folder, "run-1");
        var summary = await rig.RunAsync(log, CancellationToken.None);

        Check.Require(native.Resolved.Count == 1 && native.Resolved[0].Pending is CurrentPending { Value.Path: "item" },
            "the pending replace is resolved once before the listing");
        Check.Require(!summary.RecoveredPending && rig.Store.Pending(rig.Pair.PairId) is NoPending,
            "known no-effect settlement clears its exact note without claiming a recovered publication");
        RequireStages(log.Path, nameof(TextKey.StagePending), nameof(TextKey.StageListing),
            nameof(TextKey.StageAfterListing), nameof(TextKey.StageRenames), nameof(TextKey.StageRows),
            nameof(TextKey.StageFolderPlans), nameof(TextKey.StageFinish));
    });

    private static Task PendingFailureAsync() => WithFolder("pending-failure", async folder =>
    {
        await using var rig = await ComposeRigAsync(folder, recovery: new FailingRecovery(new FakeRecovery()));
        // The pending note protects the Failure even when its source no longer exists.
        var source = new Entry("item", false, 1, DateTimeOffset.UnixEpoch, "source-id", "rev-1", "", null);
        var pending = PendingFixture("item", "stage", source);
        rig.Store.ArmPending(rig.Pair.PairId, pending);
        using var log = rig.Log(folder, "run-1");
        Exception? failure = null;
        try { await rig.RunAsync(log, CancellationToken.None); }
        catch (Exception error) { failure = error; }
        Check.Require(failure is ItemFailedException && rig.Store.Pending(rig.Pair.PairId) is CurrentPending current &&
            current.Value == pending, "recovery failure preserves exact original custody and its diagnosis");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId) is null &&
            !Lines(log.Path).Any(fields => fields[1] == RunLog.CompleteKind),
            "failed recovery is not a completed acquisition or recovered-publication claim");
    });

    private static Task IncompleteAsync() => WithFolder("incomplete", async folder =>
    {
        var recovery = new FakeRecovery { Evidence = new(1, 2, 10) };
        var finished = new bool[2];
        await using var rig = await ComposeRigAsync(folder, drive: new(1, 1, 100), recovery: recovery,
            configure: pair => pair with { Exclusions = [new Exclusion(Side.Local, "excluded.txt", false)] },
            decorate: inner => new HookStore(inner)
            {
                WrapSink = (side, sink) => new FinishedSink(sink, () => finished[side == Side.Local ? 0 : 1] = true)
            });
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Record = [new SyncedEntry(PathNames.Key("item"), "item", PathNames.DropboxKey("item"), "item", false,
                "record-local", 0, 0, "record-dropbox", "")]
        });
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "excluded.txt"), "content");
        rig.Wire.RequestException = route => route.EndsWith("/list_folder", StringComparison.Ordinal)
            ? new InventoryIncompleteException("incomplete", null) : null;
        using var log = rig.Log(folder, "run-1");
        var summary = await rig.RunAsync(log, CancellationToken.None);

        Check.Require(!summary.Completed && summary.IncompleteReason == "incomplete",
            "an incomplete listing stops the run");
        Check.Require(finished.All(value => value), "both listings are joined before the run returns");
        Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("item")) is not null,
            "an incomplete listing sets aside no record");
        Check.Require(rig.Store.Pair(rig.Pair.PairId)!.Drive == recovery.Evidence,
            "an incomplete listing still saves finishing drive evidence");
        Check.Require(!rig.Store.FirstRepairDone(rig.Pair.PairId), "an incomplete run is not a completed repair");
        Check.Require(rig.Store.Issues(rig.Pair.PairId).Count == 0, "an incomplete listing replaces no skipped issue");
        Check.Require(recovery.Removals == 0, "an incomplete listing removes no leftover stage");
        Check.Require(File.Exists(Path.Combine(rig.Pair.LocalRoot, "excluded.txt")) &&
            rig.Wire.Snapshot().All(item => item.Path != "excluded.txt"),
            "an incomplete listing applies no exclusion");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId)!.Completed == false,
            "the incomplete summary is saved");
        RequireFailed(log.Path, nameof(InventoryIncompleteException));
        RequireComplete(log.Path, "false");
    });

    private static async Task SkippedAsync()
    {
        await WithFolder("skipped", async folder =>
        {
            using var rig = NewRig(Pair());
            var online = await PutAsync(rig.Local, "online.txt");
            var local = new ProxySide(rig.Local) { Extra = [online with { Skip = nameof(TextKey.SkipOnlineOnly) }] };
            using var log = Log(rig, folder, "run-1");
            var summary = await RunScanRepairAsync(Session(rig, log, local), CancellationToken.None);

            var issues = rig.Store.Issues(rig.Pair.PairId);
            Check.Require(issues.Count == 1 && issues[0].Kind == IssueKind.Skipped &&
                issues[0].Cause == TextKey.SkipOnlineOnly && issues[0].Path == "online.txt" &&
                issues[0].Subject.Items.Count == 1 && issues[0].Subject.Items[0].Side == Side.Local,
                "a skipped entry becomes one Skipped issue for its side");
            Check.Require(await rig.Dropbox.ReadOneAsync("online.txt", CancellationToken.None) is null &&
                summary.Effects.Count == 0, "a skipped file is never uploaded");

            // A recovered entry must not keep a stale skipped notice.
            using var second = Log(rig, folder, "run-2");
            await RunScanRepairAsync(Session(rig, second), CancellationToken.None);
            Check.Require(rig.Store.Issues(rig.Pair.PairId).Count == 0,
                "the next listing replaces the skipped set");
        });

        await WithFolder("skipped-folder", async folder =>
        {
            using var rig = NewRig(Pair());
            var link = await rig.Local.EnsureFolderAsync("link", CancellationToken.None);
            await PutAsync(rig.Local, "link/child.txt");
            var local = new ProxySide(rig.Local) { Extra = [link with { Skip = nameof(TextKey.SkipLink) }] };
            using var log = Log(rig, folder, "run-1");
            await RunScanRepairAsync(Session(rig, log, local), CancellationToken.None);

            Check.Require(await rig.Dropbox.ReadOneAsync("link", CancellationToken.None) is null &&
                await rig.Dropbox.ReadOneAsync("link/child.txt", CancellationToken.None) is null,
                "a skipped folder keeps its descendants untouched");
        });
    }

    private static async Task ExclusionAsync()
    {
        await WithFolder("exclusion", async folder =>
        {
            using var rig = NewRig(Pair(exclusions: [new Exclusion(Side.Local, "gone.txt", false)]),
                OpenStore(StateStore.DatabasePath(folder)));
            rig.Recovery.Evidence = new(1, 1, 100);
            await PutAsync(rig.Local, "gone.txt");
            using var log = Log(rig, folder, "run-1");
            var summary = await RunScanRepairAsync(Session(rig, log), CancellationToken.None);

            Check.Require(await rig.Dropbox.ReadOneAsync("gone.txt", CancellationToken.None) is not null,
                "an excluded local-only file is copied before removal");
            Check.Require(await rig.Local.ReadOneAsync("gone.txt", CancellationToken.None) is null,
                "then the excluded local copy is removed");
            Check.Require(summary.Effects.TryGetValue(EffectKind.Uploaded.ToString(), out var uploaded) && uploaded == 1 &&
                summary.Effects.TryGetValue(EffectKind.DeletedLocal.ToString(), out var deleted) && deleted == 1,
                "the exclusion's copy and removal are counted");
            Check.Require(await rig.Local.ReadOneAsync("gone.txt", CancellationToken.None) is null,
                "the removed local copy is not downloaded back");
        });

        await WithFolder("exclusion-folder", async folder =>
        {
            using var rig = NewRig(Pair(exclusions: [new Exclusion(Side.Local, "folder", false)]),
                OpenStore(StateStore.DatabasePath(folder)));
            rig.Recovery.Evidence = new(1, 1, 100);
            await rig.Local.EnsureFolderAsync("folder", CancellationToken.None);
            await PutAsync(rig.Local, "folder/file.txt");
            using var log = Log(rig, folder, "run-1");
            await RunScanRepairAsync(Session(rig, log), CancellationToken.None);

            Check.Require(await rig.Dropbox.ReadOneAsync("folder/file.txt", CancellationToken.None) is not null,
                "an excluded local-only folder is copied with its contents first");
            Check.Require(await rig.Local.ReadOneAsync("folder", CancellationToken.None) is null,
                "then the excluded local folder is removed");
        });

        await WithFolder("exclusion-guarded", async folder =>
        {
            using var rig = NewRig(Pair(exclusions: [new Exclusion(Side.Local, "guarded", false)]),
                OpenStore(StateStore.DatabasePath(folder)));
            await rig.Local.EnsureFolderAsync("guarded", CancellationToken.None);
            var online = await PutAsync(rig.Local, "guarded/online.txt");
            var local = new ProxySide(rig.Local) { Extra = [online with { Skip = nameof(TextKey.SkipOnlineOnly) }] };
            using var log = Log(rig, folder, "run-1");
            var summary = await RunScanRepairAsync(Session(rig, log, local), CancellationToken.None);

            Check.Require(await rig.Local.ReadOneAsync("guarded", CancellationToken.None) is not null,
                "a folder holding skipped content is not removed");
            Check.Require(await rig.Dropbox.ReadOneAsync("guarded", CancellationToken.None) is null,
                "nothing is copied out of a protected exclusion");
            Check.Require(rig.Store.Issues(rig.Pair.PairId).Any(issue => issue.Kind == IssueKind.Failure &&
                issue.Cause == TextKey.FailExcludeHoldsProtected && issue.Path == "guarded"),
                "the blocked exclusion is a Failure notice");
            Check.Require(summary.Completed, "a blocked exclusion does not stop the run");
        });
    }

    private static Task LogFailureAsync() => WithFolder("log-failure", async folder =>
    {
        await using var rig = await ComposeRigAsync(folder);
        // A directory where the log file belongs: opening the log fails, its folder stays real for pruning.
        Directory.CreateDirectory(Path.Combine(folder,
            rig.Pair.PairId + RunLog.NameSeparator + "run-1" + ResourceNames.LogExtension));
        using var log = rig.Log(folder, "run-1");
        Check.Require(log.HasFailed, "the fixture's log cannot be opened");
        var summary = await rig.RunAsync(log, CancellationToken.None);

        Check.Require(summary.Completed && summary.LogFailed, "a failed log does not fail the run");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId)!.LogFailed == summary.LogFailed,
            "the saved LogFailed equals the returned one");
        Check.Require(rig.Store.FirstRepairDone(rig.Pair.PairId), "the run is still completed");
    });

    private static Task UnexpectedFailureAsync() => WithFolder("failure", async folder =>
    {
        var recovery = new FakeRecovery();
        await using var rig = await ComposeRigAsync(folder, recovery: recovery);
        var fault = new InvalidOperationException("boom");
        rig.Wire.RequestException = route => route.EndsWith("/list_folder", StringComparison.Ordinal) ? fault : null;
        using var log = rig.Log(folder, "run-1");
        var stopped = false;
        try { await rig.RunAsync(log, CancellationToken.None); }
        catch (InvalidOperationException) { stopped = true; }

        Check.Require(stopped, "an unexpected exception stops the run");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId) is null && !rig.Store.FirstRepairDone(rig.Pair.PairId),
            "a crashed run saves no summary");
        Check.Require(recovery.Removals == 0, "a crashed run changes nothing of its own");
        RequireFailed(log.Path, nameof(InvalidOperationException));
    });

    private static async Task BlockAsync(CancellationToken ct, Action entered)
    {
        entered();
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private static Task CancellationAsync() => WithFolder("cancellation", async folder =>
    {
        await using var rig = await ComposeRigAsync(folder);
        await File.WriteAllTextAsync(Path.Combine(rig.Pair.LocalRoot, "item"), "content");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var log = rig.Log(folder, "run-1");
        var stopped = false;
        try { await rig.RunAsync(log, cancellation.Token); }
        catch (OperationCanceledException) { stopped = true; }

        Check.Require(stopped, "cancellation propagates out of the run");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId) is null, "a cancelled run saves no summary");
    });

    private static async Task HeldDownloadAsync()
    {
        foreach (var cancel in new[] { true, false })
        {
            await WithFolder("held-download-" + cancel, async folder =>
            {
                IAdmission? admission = null;
                var capacity = 0;
                var totalItems = 0;
                var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var released = false;
                await using var rig = await ComposeRigAsync(folder, level: 1, onRow: count =>
                {
                    capacity = Math.Min(admission!.Level, admission.WorkCapacity) + LocalSide.HashSlots;
                    Check.Require(released || count <= capacity,
                        "held downloads decode no rows beyond current ordinary admission plus hash slots");
                    if (count == capacity) full.TrySetResult();
                });
                rig.Transport.Hold = true;
                using var cancellation = new CancellationTokenSource();
                using var log = rig.Log(folder, "run");
                var run = rig.RunAsync(log, cancellation.Token, (context, _) =>
                {
                    admission = context.Admission;
                    // Leave unread work even if successful metadata reads grow admission to its work bound.
                    totalItems = admission.WorkCapacity + LocalSide.HashSlots + 1;
                    for (var index = 0; index < totalItems; index++)
                        rig.Wire.SeedFile("item-" + index, "content");
                    return Task.CompletedTask;
                });
                try
                {
                    await Task.WhenAll(full.Task, rig.Transport.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10));
                    Check.Require(rig.Store.Rows == capacity && !run.IsCompleted,
                        "a real held download fills exactly the ordinary share plus hash slots");
                    if (cancel)
                    {
                        cancellation.Cancel();
                        var stopped = false;
                        try { await run; }
                        catch (OperationCanceledException) { stopped = true; }
                        Check.Require(stopped && rig.Transport.Active == 0 && log.Effects.Count == 0 &&
                            rig.Store.LastRepair(rig.Pair.PairId) is null && !rig.Store.FirstRepairDone(rig.Pair.PairId) &&
                            !Lines(log.Path).Any(fields => fields[1] == RunLog.CompleteKind) &&
                            !Directory.EnumerateFiles(rig.Pair.LocalRoot, "item-*").Any(),
                            "cancellation joins the held download without completion, effects or published files");
                    }
                    else
                    {
                        released = true;
                        rig.Transport.Release.TrySetResult();
                        var summary = await run;
                        Check.Require(summary.Completed && rig.Store.FirstRepairDone(rig.Pair.PairId) &&
                            rig.Store.Rows == totalItems &&
                            Directory.EnumerateFiles(rig.Pair.LocalRoot, "item-*").Count() == totalItems,
                            "releasing the download drains every row and durably marks the first Repair done");
                    }
                }
                finally
                {
                    released = true;
                    cancellation.Cancel();
                    rig.Transport.Release.TrySetResult();
                    try { await run; }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                }
            });
        }
    }

    private static async Task ReincludeDeletionHistoryAsync()
    {
        foreach (var real in new[] { true, false })
        foreach (var side in Enum.GetValues<Side>())
        foreach (var keep in new[] { true, false })
        await WithFolder("reincluded-history", async folder =>
        {
            var pair = Pair(exclusions: [new Exclusion(side, "Root", keep)]);
            using var rig = NewRig(pair, real ? OpenStore(Path.Combine(folder, "state.db")) : new InMemoryStateStore());
            rig.Recovery.Evidence = new(1, 1, 100);
            await rig.Local.EnsureFolderAsync("Root", default);
            await rig.Dropbox.EnsureFolderAsync("Root", default);
            var local = await PutAsync(rig.Local, "Root/Child");
            var remote = await PutAsync(rig.Dropbox, "Root/Child");
            RecordLinked(rig, local, remote);
            await (side == Side.Local ? rig.Local : rig.Dropbox).DeleteAsync(side == Side.Local ? local : remote,
                null, default);
            rig.Store.SavePairDecision(pair, rig.Store.ReadPairExecution(pair.PairId)!.Value.Generation,
                pair with { Exclusions = [] }, null, false, PathNames.Key("root"));
            var included = pair with { Exclusions = [] };
            using var log = Log(rig, folder, "reincluded");
            await RunScanRepairAsync(Session(rig, log) with
            {
                Pair = included, Scope = new PathScope(included, [included], Settings.DefaultIgnoreRules)
            }, CancellationToken.None);
            Check.Require(await rig.Local.ReadOneAsync(local.Path, default) is null &&
                await rig.Dropbox.ReadOneAsync(remote.Path, default) is null,
                $"Re-inclusion propagates the remembered deletion, rather than restoring it ({real}, {side}, {keep}).");
        });
    }

    private static async Task NestedRootsAsync()
    {
        foreach (var real in new[] { true, false })
        foreach (var nesting in new[] { "both", "local", "dropbox", "foreign" })
        await WithFolder("nested", async folder =>
        {
            var outer = Pair("outer-pair", localRoot: @"C:\Outer", dropboxRoot: "/Outer");
            var inner = Pair("inner-pair", localRoot: nesting == "dropbox" ? @"C:\Other" : @"C:\Outer\Inner",
                dropboxRoot: nesting == "local" ? "/Other" : "/Outer/Inner");
            if (nesting == "foreign") inner = inner with { ConnectionId = "other-account" };
            using var rig = NewRig(outer, real ? OpenStore(Path.Combine(folder, "state.db")) : new InMemoryStateStore(), inner);
            rig.Recovery.Evidence = new(1, 1, 100);
            foreach (var adapter in new ISide[] { rig.Local, rig.Dropbox })
            {
                await adapter.EnsureFolderAsync("Inner", default);
                await adapter.EnsureFolderAsync("Outside", default);
            }
            var nestedLocal = await PutAsync(rig.Local, "Inner/x");
            var nestedDropbox = await PutAsync(rig.Dropbox, "Inner/x");
            var keepLocal = await PutAsync(rig.Local, "y");
            var keepDropbox = await PutAsync(rig.Dropbox, "y");
            RecordLinked(rig, nestedLocal, nestedDropbox);
            RecordLinked(rig, keepLocal, keepDropbox);
            var crossLocal = await PutAsync(rig.Local, "Inner/CrossLocal");
            var crossRemote = await PutAsync(rig.Dropbox, "Outside/CrossLocal");
            RecordLinked(rig, crossLocal, crossRemote);
            var reverseLocal = await PutAsync(rig.Local, "Outside/CrossRemote");
            var reverseRemote = await PutAsync(rig.Dropbox, "Inner/CrossRemote");
            RecordLinked(rig, reverseLocal, reverseRemote);
            var saved = new[] { SyncedEntries.From(nestedLocal, nestedDropbox),
                SyncedEntries.From(crossLocal, crossRemote), SyncedEntries.From(reverseLocal, reverseRemote) };
            await rig.Local.DeleteAsync(nestedLocal, null, default);
            await rig.Local.DeleteAsync(crossLocal, null, default);
            await rig.Dropbox.DeleteAsync(reverseRemote, null, default);
            var local = new ProxySide(rig.Local)
            {
                Extra = [nestedLocal with { Skip = nameof(TextKey.SkipLink) }, keepLocal with { Skip = nameof(TextKey.SkipLink) }]
            };
            var dropbox = new ProxySide(rig.Dropbox)
            {
                Extra =
                [
                    nestedDropbox with { Skip = nameof(TextKey.SkipNotDownloadable) },
                    keepDropbox with { Skip = nameof(TextKey.SkipNotDownloadable) }
                ]
            };
            using var log = Log(rig, folder, "run-1");
            await RunScanRepairAsync(Session(rig, log, local, dropbox), CancellationToken.None);

            foreach (var before in saved.Where(before => rig.Scope.Excludes(before.LocalPath, before.DropboxPath, before.IsFolder)))
                Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, before.LocalKey) == before &&
                    rig.Store.SyncedByKey(rig.Pair.PairId, Side.Dropbox, before.DropboxKey) == before,
                    $"Nested protection retains same-area and cross-boundary deletion history ({real}, {nesting}).");
            Check.Require((!rig.Scope.Excludes(nestedLocal.Path, nestedDropbox.Path, false) ||
                    await rig.Dropbox.ReadOneAsync(nestedDropbox.Path, default) == nestedDropbox) &&
                (!rig.Scope.Excludes(crossLocal.Path, crossRemote.Path, false) ||
                    await rig.Dropbox.ReadOneAsync(crossRemote.Path, default) == crossRemote) &&
                (!rig.Scope.Excludes(reverseLocal.Path, reverseRemote.Path, false) ||
                    await rig.Local.ReadOneAsync(reverseLocal.Path, default) == reverseLocal),
                $"Nested protection performs no child deletion while either endpoint is protected ({real}, {nesting}).");
            rig.Store.SavePair(inner with { Enabled = false });
            using var resumedLog = Log(rig, folder, "handover");
            await RunScanRepairAsync(Session(rig, resumedLog) with
            {
                Scope = new PathScope(outer, [outer, inner with { Enabled = false }], Settings.DefaultIgnoreRules)
            }, CancellationToken.None);
            Check.Require(await rig.Local.ReadOneAsync(nestedLocal.Path, default) is null &&
                await rig.Dropbox.ReadOneAsync(nestedDropbox.Path, default) is null &&
                await rig.Dropbox.ReadOneAsync(crossRemote.Path, default) is null &&
                await rig.Local.ReadOneAsync(reverseLocal.Path, default) is null,
                $"Handover propagates remembered deletions instead of recreating missing children ({real}, {nesting}).");
            Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("y")) is not null,
                "records outside the nested root stay");
        });

        await WithFolder("shared-root", async folder =>
        {
            var first = Pair("shared-a", localRoot: @"C:\Shared", dropboxRoot: "/SharedA");
            var second = Pair("shared-b", localRoot: @"C:\Shared", dropboxRoot: "/SharedB");
            using var rig = NewRig(first, second);
            var local = await PutAsync(rig.Local, "y");
            var dropbox = await PutAsync(rig.Dropbox, "y");
            RecordLinked(rig, local, dropbox);
            var listing = new ProxySide(rig.Local) { Extra = [local with { Skip = nameof(TextKey.SkipLink) }] };
            var otherListing = new ProxySide(rig.Dropbox)
            {
                Extra = [dropbox with { Skip = nameof(TextKey.SkipNotDownloadable) }]
            };
            using var log = Log(rig, folder, "run-1");
            await RunScanRepairAsync(Session(rig, log, listing, otherListing), CancellationToken.None);

            Check.Require(rig.Scope.NestedRoots(Side.Local).Count == 0 && rig.Scope.NestedRoots(Side.Dropbox).Count == 0,
                "D12: an equal root is shared, not nested");
            Check.Require(rig.Store.SyncedByKey(rig.Pair.PairId, Side.Local, PathNames.Key("y")) is not null,
                "a shared root keeps its own records");
        });

        await WithFolder("shared-root-sync", async folder =>
        {
            var first = Pair("shared-a", localRoot: @"C:\Shared", dropboxRoot: "/SharedA");
            var second = Pair("shared-b", localRoot: @"C:\Shared", dropboxRoot: "/SharedB");
            using var rig = NewRig(first, second);
            await PutAsync(rig.Local, "y");
            var dropboxA = new FakeSide(Side.Dropbox);
            var dropboxB = new FakeSide(Side.Dropbox);
            using (var firstLog = Log(rig, folder, "run-a"))
                await RunScanRepairAsync(new SessionInputs(rig.Store, first, Settings,
                    new PathScope(first, [first, second]), rig.Local, dropboxA, rig.Recovery, rig.Admission, false,
                    firstLog), CancellationToken.None);
            using (var secondLog = Log(rig, folder, "run-b"))
                await RunScanRepairAsync(new SessionInputs(rig.Store, second, Settings,
                    new PathScope(second, [first, second]), rig.Local, dropboxB, rig.Recovery, rig.Admission, false,
                    secondLog), CancellationToken.None);

            Check.Require(await dropboxA.ReadOneAsync("y", CancellationToken.None) is not null &&
                await dropboxB.ReadOneAsync("y", CancellationToken.None) is not null,
                "two pairs sharing one local root each upload the file to their own Dropbox root");
            Check.Require(rig.Store.SyncedByKey(first.PairId, Side.Local, PathNames.Key("y")) is not null &&
                rig.Store.SyncedByKey(second.PairId, Side.Local, PathNames.Key("y")) is not null,
                "each shared-root pair records its own link");
        });

        await WithFolder("one-sided-nested", async folder =>
        {
            var outer = Pair("one-sided-outer", localRoot: @"C:\Outer", dropboxRoot: "/Outer");
            var inner = Pair("one-sided-inner", localRoot: @"C:\Elsewhere\Inner", dropboxRoot: "/Outer/Inner");
            using var rig = NewRig(outer, inner);
            await PutAsync(rig.Local, "Inner/x");
            using var log = Log(rig, folder, "run-1");
            await RunScanRepairAsync(Session(rig, log), CancellationToken.None);

            Check.Require(rig.Scope.NestedRoots(Side.Local).Count == 0,
                "the inner pair is nested on the Dropbox side only");
            Check.Require(await rig.Dropbox.ReadOneAsync("Inner/x", CancellationToken.None) is null,
                "a one-sided nested root keeps the local row out of sync");
        });
    }

    // Records with list members compare their lists by reference, so persisted values need a value comparison.
    private static bool SameItemRetry(ItemRetryCandidate? left, ItemRetryCandidate? right) =>
        left is not null && right is not null && SameToken(left.Token, right.Token) &&
        left.PairId == right.PairId && left.Dispatch == right.Dispatch &&
        left.PendingOperationId == right.PendingOperationId && left.PendingGeneration == right.PendingGeneration;

    private static bool SameToken(ItemRetryToken? left, ItemRetryToken? right) =>
        left is null ? right is null : right is not null && left.IssueId == right.IssueId &&
        left.EpisodeId == right.EpisodeId && left.Revision == right.Revision && left.WorkId == right.WorkId &&
        left.PairGeneration == right.PairGeneration && left.SourceEpoch == right.SourceEpoch &&
        left.Subject.HasSameItems(right.Subject);

    private static bool SamePairDefinition(PairConfig? left, PairConfig? right) =>
        left is not null && right is not null && left.PairId == right.PairId && left.Name == right.Name &&
        left.LocalRoot == right.LocalRoot && left.DropboxRoot == right.DropboxRoot &&
        left.ConnectionId == right.ConnectionId && left.IgnoreRules.SequenceEqual(right.IgnoreRules) &&
        left.Exclusions.SequenceEqual(right.Exclusions) && left.FirstRun == right.FirstRun &&
        left.Enabled == right.Enabled && left.Drive == right.Drive && left.Roots == right.Roots &&
        (left.IgnoredFolderKeepRules is null ? right.IgnoredFolderKeepRules is null
            : right.IgnoredFolderKeepRules is not null &&
                left.IgnoredFolderKeepRules.SequenceEqual(right.IgnoredFolderKeepRules));

    private static PairConfig Pair(string id = "pair-a", DriveEvidence? drive = null,
        IReadOnlyList<Exclusion>? exclusions = null, bool enabled = true,
        string localRoot = @"C:\RepairFixture", string dropboxRoot = "/RepairFixture")
    {
        var root = Path.Combine(FixtureRoot.Value ?? throw new InvalidOperationException(),
            "roots", localRoot.Replace(':', '_').TrimStart('\\', '/'));
        Directory.CreateDirectory(root);
        return new PairConfig(id, id, root, dropboxRoot, "connection-a", [], exclusions ?? [],
            FirstRunPolicy.NewestWins, enabled, drive,
            new RootBinding(root, dropboxRoot, "connection-a", WindowsDirectoryListing.ReadIdentity(root),
                new DropboxRootIdentity("account-a", "namespace-a", "namespace-a", "folder-a")));
    }

    private static Rig NewRig(PairConfig pair, params PairConfig[] others) =>
        NewRig(pair, Settings, OpenStore(Path.Combine(FixtureRoot.Value!, Guid.NewGuid().ToString("N") + ".db")), others);

    private static Rig NewRig(PairConfig pair, IStateStore store, params PairConfig[] others) =>
        NewRig(pair, Settings, store, others);

    private static Rig NewRig(PairConfig pair, EngineSettings settings, IStateStore store, params PairConfig[] others)
    {
        store.SavePair(pair);
        foreach (var other in others) store.SavePair(other);
        var all = new List<PairConfig> { pair };
        all.AddRange(others);
        return new Rig(pair, store, new FakeSide(Side.Local), new FakeSide(Side.Dropbox), new FakeRecovery(),
            new FakeAdmission { Level = 3 }, new PathScope(pair, all, settings.DefaultIgnoreRules));
    }

    private static SessionInputs Session(Rig rig, RunLog log, ISide? local = null, ISide? dropbox = null,
        ILocalRecovery? recovery = null, IStateStore? store = null, EngineSettings? settings = null) =>
        new(store ?? rig.Store, rig.Store.Pair(rig.Pair.PairId)!, settings ?? Settings, rig.Scope,
            local ?? rig.Local, dropbox ?? rig.Dropbox, recovery ?? rig.Recovery, rig.Admission, false, log);

    private sealed record SessionInputs(IStateStore Store, PairConfig Pair, EngineSettings Settings,
        PathScope Scope, ISide Local, ISide Dropbox, ILocalRecovery Recovery,
        IAdmission Admission, bool Priority, RunLog Log)
    {
        public Func<string, bool, CancellationToken, Task<DeletionEvidence>>? ReadEvidence { get; init; }
        public Func<ExecutorContext, CancellationToken, Task>? BeforeRepair { get; init; }
    }

    /// <summary>One contract-tier scan pass at the module's ports, completing like one Repair.</summary>
    private static Task<RepairSummary> RunScanRepairAsync(SessionInputs input, CancellationToken ct,
        IReadOnlySet<long>? retryIssues = null, IItemAttempts? itemAttempts = null)
    {
        var rig = new ScanPassChecks.Rig(input.Pair, input.Store, input.Local, input.Dropbox, input.Recovery,
            input.Admission, input.Scope);
        return ScanPassChecks.RunRepairAsync(rig, input.Log, input.Settings, store: input.Store, ct: ct,
            retryIssues: retryIssues, readEvidence: input.ReadEvidence, itemAttempts: itemAttempts,
            before: input.BeforeRepair);
    }

    private static async Task SeedLinkedAsync(ComposedRig rig, string path)
    {
        var modified = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var local = Path.Combine(rig.Pair.LocalRoot, path);
        await File.WriteAllTextAsync(local, "content");
        File.SetLastWriteTimeUtc(local, modified.UtcDateTime);
        rig.Wire.SeedFile(path, "content", modified);
    }

    internal static async Task<ComposedRig> ComposeRigAsync(string folder, bool enabled = true,
        Action<int>? onRow = null, ILocalRecovery? recovery = null, DriveEvidence? drive = null,
        Func<IStateStore, HookStore>? decorate = null, int level = 3,
        Func<PairConfig, PairConfig>? configure = null, TimeProvider? time = null, EngineSettings? settings = null,
        Action<string>? progress = null, Massdex.SyncShark.Engine.Run.Monitor? monitor = null,
        Func<string, RunLog?>? openLog = null)
    {
        var root = Path.Combine(folder, "local");
        Directory.CreateDirectory(root);
        var connection = DropboxConnectionBinding.CreateConnectionId("account-id");
        var pair = new PairConfig("pair-a", "Pair A", root, "/Pilot", connection, [], [],
            FirstRunPolicy.NewestWins, enabled, drive, new RootBinding(root, "/Pilot", connection,
                WindowsDirectoryListing.ReadIdentity(root),
                new DropboxRootIdentity("account-id", "namespace-a", "namespace-a", DropboxWire.RootId)));
        pair = configure?.Invoke(pair) ?? pair;
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "namespace-a", "namespace-a", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes, ConnectionId: connection);
        var instance = PilotInstanceLock.AcquireForMaintenance(Path.Combine(folder, "authority", "state", "lock"));
        var wire = new DropboxWire { AccountBinding = binding };
        var transport = new DownloadGate(wire);
        var http = new HttpClient(transport);
        EngineHost? host = null;
        try
        {
            await new DropboxConnectionStore(instance.StateAuthority).SaveAsync(binding);
            host = EngineHost.Compose(instance.StateAuthority.ProductRoot, progress ?? (_ => { }), instance.StateAuthority,
                settings ?? Settings, http, time ?? TimeProvider.System, openLog ?? (_ => null),
                validator =>
                {
                    var store = StateStore.Open(Path.Combine(folder, "composed.db"), validator);
                    return decorate?.Invoke(store) ?? new HookStore(store) { OnRow = onRow };
                }, EngineHost.Mode.Direct,
                new EngineExternals(new SyntheticCredentials(),
                    _ => new HttpClient(transport, disposeHandler: false), Recovery: recovery), startupMonitor: monitor);
            pair = host.Store.Pair(pair.PairId) ?? pair;
            host.Store.SavePair(pair);
            host.Store.SaveLimiterLevel(connection, level);
            // The host keys the account limiter by account id; the case checks the connection-keyed save.
            host.Store.SaveLimiterLevel("account-id", level);
            await host.RecomposeCurrentAsync(pair.PairId, CancellationToken.None);
            if (enabled) await host.EnsureSourceBasesAsync(pair.PairId, CancellationToken.None);
            return new ComposedRig(pair, (HookStore)host.Store, wire, transport, host, instance, http);
        }
        catch
        {
            if (host is not null) await host.DisposeAsync();
            http.Dispose();
            instance.Dispose();
            throw;
        }
    }

    internal sealed class DownloadGate(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Hold { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? Respond { get; set; }
        public int Active;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Respond is not null && await Respond(request, ct) is { } response) return response;
            if (!Hold || !request.RequestUri!.AbsolutePath.EndsWith(DropboxProtocol.Download, StringComparison.Ordinal))
                return await base.SendAsync(request, ct);
            Interlocked.Increment(ref Active);
            try
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
                return await base.SendAsync(request, ct);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    internal sealed record ComposedRig(PairConfig Pair, HookStore Store, DropboxWire Wire, DownloadGate Transport,
        EngineHost Host, PilotInstanceLock Instance, HttpClient Http) : IAsyncDisposable
    {
        public RunLog Log(string folder, string runId) => RunLog.Open(folder, Pair.PairId, runId,
            _ => { }, Settings.LogBufferCharacters);

        public Task<RepairSummary> RunAsync(RunLog log, CancellationToken ct,
            Func<ExecutorContext, CancellationToken, Task>? before = null, IReadOnlySet<long>? retryIssues = null,
            string? targetPair = null) =>
            Host.DispatchAsync(targetPair ?? Pair.PairId,
                async (registration, pair, generation, token) =>
                {
                    var session = PairSession.Open(Store, pair, Host.Settings, Http, false, log,
                        Instance.StateAuthority, Host, registration, generation, TimeProvider.System);
                    if (before is not null) await before(session.Context, token);
                    return await Repair.RunAsync(session, token, retryIssues);
                }, ct);

        public async ValueTask DisposeAsync()
        {
            try { await Host.DisposeAsync(); }
            finally
            {
                Http.Dispose();
                Instance.Dispose();
            }
        }
    }

    private sealed class SourceHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return respond?.Invoke(request, ct) ?? Task.FromResult(DefaultResponse(request));
        }

        public static HttpResponseMessage DefaultResponse(HttpRequestMessage request)
        {
            var route = request.RequestUri!.AbsolutePath;
            var body = route.EndsWith("get_latest_cursor", StringComparison.Ordinal)
                ? "{\"cursor\":\"fixture-cursor\"}"
                : route.EndsWith("/continue", StringComparison.Ordinal)
                    ? "{\"entries\":[],\"cursor\":\"fixture-cursor\",\"has_more\":false}"
                    : throw new InvalidOperationException(route);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static RunLog Log(Rig rig, string folder, string runId) =>
        RunLog.Open(folder, rig.Pair.PairId, runId, _ => { }, Settings.LogBufferCharacters);

    private static async Task CompleteBaselineAsync(Rig rig, string folder)
    {
        using var log = Log(rig, folder, nameof(CompleteBaselineAsync));
        var summary = await RunScanRepairAsync(Session(rig, log), CancellationToken.None);
        Check.Require(summary.Completed && rig.Store.FirstRepairDone(rig.Pair.PairId),
            "the baseline uses actual source acknowledgments and authoritative Repair completion");
    }

    private static PendingEffect PendingFixture(string path, string stage, Entry source)
    {
        const string identity = "1";
        var hash = new string('0', 64);
        var losing = source with { Path = path, Id = identity };
        return new PendingEffect(1, Guid.NewGuid().ToString("N"), 0, PendingKind.LocalReplace,
            PendingPhase.Armed, path, new RemovalProof(losing, identity, hash), null, source,
            new StageProof(stage, identity, identity, source.Size, hash), null, null, false, null);
    }

    private static void RecordSynced(Rig rig, string path) =>
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with
        {
            Record =
            [
                new SyncedEntry(PathNames.Key(path), path, PathNames.DropboxKey(path), path, false,
                    "record-local", 0, 0, "record-dropbox", "")
            ]
        });

    private static void RecordLinked(Rig rig, Entry local, Entry dropbox) =>
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, dropbox)] });

    private static async Task<SyncedEntry> LinkAsync(Rig rig, string path)
    {
        var local = await PutAsync(rig.Local, path);
        var dropbox = await PutAsync(rig.Dropbox, path);
        var saved = SyncedEntries.From(local, dropbox);
        rig.Store.Commit(rig.Pair.PairId, StateChange.Empty with { Record = [saved] });
        return saved;
    }

    private static async Task<Entry> PutAsync(ISide side, string path, byte[]? bytes = null)
    {
        var source = new Entry(path, false, bytes?.Length ?? 1,
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), path, "", "", null);
        using var content = new MemoryStream(bytes ?? [1]);
        await using var stage = await side.StageAsync(path, content, source, CancellationToken.None);
        var result = await side.PublishAsync(stage, null, null, CancellationToken.None);
        Check.Require(result.Outcome == RemovalOutcome.Published && result.Final is not null, nameof(PutAsync));
        return result.Final!;
    }

    private static void RequireStages(string path, params string[] expected)
    {
        var boundaries = Lines(path).Where(fields => fields[1] == nameof(RunEventKind.Stage)).ToArray();
        var active = new HashSet<string>();
        foreach (var boundary in boundaries)
            Check.Require(boundary[5] == nameof(RunEvent.Ended)
                ? active.Remove(boundary[2]) : active.Add(boundary[2]),
                "each stage starts once and ends after its start: " + boundary[2]);
        Check.Require(active.Count == 0, "successful repair closes every stage");
        var stages = boundaries.Where(fields => fields[5] != nameof(RunEvent.Ended))
            .Select(fields => fields[2]).Where(expected.Contains).ToArray();
        Check.Require(stages.SequenceEqual(expected),
            "the run log records the stages in order: " + string.Join(",", stages));
        RequireComplete(path, "true");
    }

    private static void RequireComplete(string path, string completed)
    {
        var last = Lines(path)[^1];
        Check.Require(last[1] == RunLog.CompleteKind && last[5] == completed,
            "the run log ends with its completion line");
    }

    private static void RequireFailed(string path, string exceptionName) =>
        Check.Require(Lines(path).Any(fields => fields[1] == nameof(RunEventKind.Failed) && fields[2] == exceptionName),
            "the run log records the stop as Failed");

    // The run log stays open for writing until disposed, so read it the way a log viewer would.
    private static string[][] Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(RunLog.FieldSeparator)).ToArray();
    }

    private static async Task WithFolder(string name, Func<string, Task> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-repair-" + name + "-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var previousRoot = FixtureRoot.Value;
        FixtureRoot.Value = folder;
        // Production keeps pair roots outside the product root; the local removal proof reads that separation.
        using var productRoot = (IDisposable)typeof(SyncSharkRuntimePaths)
            .GetMethod("OverrideProductRootForTest", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [Path.Combine(folder, "product")])!;
        try { await body(folder); }
        finally
        {
            FixtureRoot.Value = previousRoot;
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record Rig(PairConfig Pair, IStateStore Store, FakeSide Local, FakeSide Dropbox,
        FakeRecovery Recovery, FakeAdmission Admission, PathScope Scope) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    /// <summary>Scripts drive readings and observes recovery boundaries without touching a real volume.</summary>
    private sealed class RecoveryProbe(ILocalRecovery inner) : ILocalRecovery
    {
        public bool CanArmReplace() => inner.CanArmReplace();
        public Task<RemovalProof> ProveRemovalAsync(Entry expected, string? hash, CancellationToken ct) =>
            inner.ProveRemovalAsync(expected, hash, ct);
        public StageAllocation PlanRecoveryStage(string operationId, long ordinal) => inner.PlanRecoveryStage(operationId, ordinal);
        public Task<IRecoveryStage> StageRecoveryAsync(StageAllocation allocation, Entry selected, Stream revisionContent, CancellationToken ct) =>
            inner.StageRecoveryAsync(allocation, selected, revisionContent, ct);
        public Task<ILocalRecovery.AllocationOccupancy> InspectRecoveryAllocationAsync(StageAllocation allocation, CancellationToken ct) =>
            inner.InspectRecoveryAllocationAsync(allocation, ct);
        public Task<Stream> OpenRecoveryStageAsync(StageProof proof, CancellationToken ct) => inner.OpenRecoveryStageAsync(proof, ct);
        public Task DiscardRecoveryStageAsync(StageProof proof, CancellationToken ct) => inner.DiscardRecoveryStageAsync(proof, ct);

        public Func<int, DriveEvidence>? Reading { get; init; }
        public Action? OnPending { get; init; }
        public Action? OnCleanup { get; init; }
        public IReadOnlySet<string>? PendingNames { get; private set; }
        public int Reads { get; private set; }

        public DriveEvidence Drive()
        {
            Reads++;
            return Reading?.Invoke(Reads) ?? inner.Drive();
        }

        public Task<RemovalResult> ResolvePendingAsync(PendingRead pending, RecoveryChoice choice, CancellationToken ct)
        {
            OnPending?.Invoke();
            return inner.ResolvePendingAsync(pending, choice, ct);
        }

        public IReadOnlyList<(string Path, Exception Error)> RemoveStaleStages(IReadOnlySet<string> pendingStageNames)
        {
            PendingNames = pendingStageNames;
            OnCleanup?.Invoke();
            return inner.RemoveStaleStages(pendingStageNames);
        }
    }

    /// <summary>A recovery whose pending resolution fails like a locked stage or final file would.</summary>
    private sealed class FailingRecovery(ILocalRecovery inner) : ILocalRecovery
    {
        public bool CanArmReplace() => inner.CanArmReplace();
        public Task<RemovalProof> ProveRemovalAsync(Entry expected, string? hash, CancellationToken ct) =>
            inner.ProveRemovalAsync(expected, hash, ct);
        public StageAllocation PlanRecoveryStage(string operationId, long ordinal) => inner.PlanRecoveryStage(operationId, ordinal);
        public Task<IRecoveryStage> StageRecoveryAsync(StageAllocation allocation, Entry selected, Stream revisionContent, CancellationToken ct) =>
            inner.StageRecoveryAsync(allocation, selected, revisionContent, ct);
        public Task<ILocalRecovery.AllocationOccupancy> InspectRecoveryAllocationAsync(StageAllocation allocation, CancellationToken ct) =>
            inner.InspectRecoveryAllocationAsync(allocation, ct);
        public Task<Stream> OpenRecoveryStageAsync(StageProof proof, CancellationToken ct) => inner.OpenRecoveryStageAsync(proof, ct);
        public Task DiscardRecoveryStageAsync(StageProof proof, CancellationToken ct) => inner.DiscardRecoveryStageAsync(proof, ct);

        public Task<RemovalResult> ResolvePendingAsync(PendingRead pending, RecoveryChoice choice, CancellationToken ct) =>
            Task.FromException<RemovalResult>(new ItemFailedException(nameof(FailingRecovery), TextKey.PublishFailed, null));

        public IReadOnlyList<(string Path, Exception Error)> RemoveStaleStages(IReadOnlySet<string> pendingStageNames) =>
            inner.RemoveStaleStages(pendingStageNames);
        public DriveEvidence Drive() => inner.Drive();
    }

    /// <summary>Passes log lines through except the selected event, which fails like a full disk would.</summary>
    private sealed class EventFailingWriter(TextWriter inner, string kind, string? what) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            var fields = value?.Split(RunLog.FieldSeparator) ?? [];
            if (fields.Length > 2 && fields[1] == kind && (what is null || fields[2] == what))
                throw new IOException();
            inner.WriteLine(value);
        }

        public override void Flush() => inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>The real in-use lifecycle over the store, so contract checks exercise the same obligation.</summary>
    private sealed class StoreItemAttempts(IStateStore store, string pairId, long generation) : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) => execute(Guid.NewGuid(), ct);
        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode) => store.RecordInUseFailure(pairId, generation, sourceEpoch, path,
                subject, cause, selection, dispatch, expectedPending, expectedEpisode, attemptId);
        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) => store.CompleteItemAttempt(expected, attemptId, change, completedPending, selection, committedIssue);
    }

    private sealed class CompletionGateSink(IScanSink inner, Func<Task> gate) : IScanSink
    {
        public Task AddAsync(Listed item, CancellationToken ct) => inner.AddAsync(item, ct);
        public async Task CompleteAsync() { await gate(); await inner.CompleteAsync(); }
        public Task AbortAsync() => inner.AbortAsync();
    }

    private sealed class ObservedSink(IScanSink inner, Action added) : IScanSink
    {
        public async Task AddAsync(Listed item, CancellationToken ct) { added(); await inner.AddAsync(item, ct); }
        public Task CompleteAsync() => inner.CompleteAsync();
        public Task AbortAsync() => inner.AbortAsync();
    }

    private sealed class FinishedSink(IScanSink inner, Action finished) : IScanSink
    {
        public Task AddAsync(Listed item, CancellationToken ct) => inner.AddAsync(item, ct);
        public async Task CompleteAsync()
        {
            try { await inner.CompleteAsync(); }
            finally { finished(); }
        }
        public async Task AbortAsync()
        {
            try { await inner.AbortAsync(); }
            finally { finished(); }
        }
    }

    /// <summary>Delegates one scan sink, running its abort hook, then an optional gate, then optionally failing.</summary>
    private sealed class AbortSink(IScanSink inner, Exception? throwOnAbort, Action? onAbort, Func<Task>? gate = null)
        : IScanSink
    {
        public Task AddAsync(Listed item, CancellationToken ct) => inner.AddAsync(item, ct);
        public Task CompleteAsync() => inner.CompleteAsync();

        public async Task AbortAsync()
        {
            onAbort?.Invoke();
            if (gate is not null) await gate();
            if (throwOnAbort is not null) throw throwOnAbort;
            await inner.AbortAsync();
        }
    }
}
