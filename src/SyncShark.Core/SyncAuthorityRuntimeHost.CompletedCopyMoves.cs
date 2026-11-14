using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record SyncCompletedCopyMoveRepairItem(string OldPath, string NewPath, string? OperationId,
    string Status, string Detail);
public sealed record SyncCompletedCopyMoveRepairResult(string PairId, string RunId,
    IReadOnlyList<SyncCompletedCopyMoveRepairItem> Items);

public sealed partial class SyncAuthorityRuntimeHost
{
    // Explicit recovery action, shared by CLI and GUI. Inventory preparation
    // remains effect-free; it must never silently remove a user's file.
    public async Task<SyncCompletedCopyMoveRepairResult> RepairCompletedCopyMovesAsync(string pairId,
        Action<string>? progress = null, CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null)
    {
        var pair = (await LoadPairsAsync(cancellationToken)).Pairs.Single(p => p.Id == pairId);
        if (pair.Provider != SyncProviders.Dropbox || IsLocalPair(pair) || SyncCloudPairConfiguration.IsCloudOnly(pair))
            throw new InvalidOperationException("Completed-copy recovery is only supported for a local/Dropbox pair.");
        if (NewSyncAdmissionReason(pair) is { } reason) throw new InvalidOperationException(reason);
        var commandId = await CreateAcceptedCommandAsync("run_pair", pair.Id, null,
            JsonSerializer.Serialize(new { trigger = "Finish previously copied cloud moves", pairIds = new[] { pair.Id },
                pairNames = new[] { pair.Name }, connectionId = pair.ConnectionId }), cancellationToken,
            admissionProgress: progress);
        var commandAdmission = await RequireRetainedCommandAdmissionAsync(commandId, cancellationToken);
        PilotRunControl? control = null;
        var publicationCommitted = false;
        try
        {
            control = await BeginRunAsync(cancellationToken, commandId, "run_pair", pair.Id);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, control.StopToken);
            var token = linked.Token;
            await using var connections = _authority.BeginConnectionReuseScope();
            await _authority.TransitionCommandRequestAsync(commandId, "executing", cancellationToken: token);
            var current = (await LoadPairsAsync(token)).Pairs.Single(p => p.Id == pair.Id);
            if (current.PolicyRevision != pair.PolicyRevision || current.LocalRootFileId != pair.LocalRootFileId ||
                current.LocalVolumeSerialNumber != pair.LocalVolumeSerialNumber || current.DropboxFolderId != pair.DropboxFolderId)
                throw new InvalidOperationException("The pair changed before recovery acquired its run lock.");
            pair = current;
            var prepared = await PreparePairAsync(pair, token, onPreparationProgress: entry =>
            {
                progress?.Invoke(entry.Detail);
                return NotifyProgressAsync(onProgress, new(pair, entry.Phase, null, null, null,
                    Comparison: entry));
            });
            pair = (await LoadPairsAsync(token)).Pairs.Single(p => p.Id == pair.Id);
            var remote = prepared.Remote;
            var options = prepared.Options;
            progress?.Invoke("Starting: Reading retained completed-copy move lineage; no file transfer or retirement is running.");
            var candidateStarted = System.Diagnostics.Stopwatch.StartNew();
            var candidates = (await _authority.LoadCompletedCopyMovesAsync(pair.Id, token))
                .GroupBy(p => p.OldPath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            progress?.Invoke($"Completed: Read {candidates.Length} retained completed-copy move candidates in {candidateStarted.Elapsed.TotalSeconds:F3}s; each still requires fresh physical/provider verification.");
            var items = new List<SyncCompletedCopyMoveRepairItem>();
            var identityRecovery = new List<SyncCompletedCopyMove>();
            foreach (var proof in candidates)
            {
                token.ThrowIfCancellationRequested();
                string? operationId = null;
                try
                {
                    var priorCloud = await _authority.LoadObjectVersionAsync(proof.OldRemoteVersionId, token);
                    var currentCloud = await _authority.LoadObjectVersionAsync(proof.NewRemoteVersionId, token);
                    using var priorIdentity = JsonDocument.Parse(priorCloud.IdentityJson);
                    using var currentIdentity = JsonDocument.Parse(currentCloud.IdentityJson);
                    if (priorIdentity.RootElement.GetProperty("fileId").GetString() !=
                        currentIdentity.RootElement.GetProperty("fileId").GetString())
                    {
                        // Distinct provider objects are not a move. Never retire
                        // either file. Repair only their conflated observation map
                        // after a complete fresh inventory verifies both sides.
                        identityRecovery.Add(proof);
                        await Report(proof, "Distinct cloud files: verifying explicit metadata identity recovery; both physical files will be retained.");
                        continue;
                    }
                    await Report(proof, "Verifying the old physical file, the completed copy and fresh Dropbox identity before recoverable retirement.");
                    var oldPath = PilotPath.ResolveContainedPath(pair.LocalRoot, proof.OldPath);
                    operationId = await _authority.FindRetainedActionIdAsync(SyncAuthorityMoveLocalExecutor.Id("idempotency",
                        SyncAuthorityCompletedCopyMove.OperationId(pair.Id, proof)), token);
                    var previouslyRetained = operationId is not null;
                    if (!previouslyRetained && !File.Exists(oldPath) && !Directory.Exists(oldPath))
                    {
                        items.Add(new(proof.OldPath, proof.NewPath, null, "already_absent",
                            "No old local file is present. No effect was replayed."));
                        continue;
                    }
                    if (!previouslyRetained)
                        operationId = await SyncAuthorityCompletedCopyMove.PlanAsync(pair, remote, _authority, options, proof, commandAdmission, token);
                    if (operationId is null) throw new InvalidDataException("The completed-copy repair has no retained or freshly registered action.");
                    var operation = await _authority.LoadOperationAsync(operationId, token);
                    if (previouslyRetained && operation.Status == SyncAuthorityOperationStatus.Planned)
                    {
                        // A prior attempt may have retained the complete one-file
                        // plan but stopped before sealing. Revalidate its exact
                        // proof through the normal planner; never just activate it.
                        await SyncAuthorityCompletedCopyMove.PlanAsync(pair, remote, _authority, options, proof, commandAdmission, token);
                        operation = await _authority.LoadOperationAsync(operationId, token);
                    }
                    SyncAuthorityExecutionResult result;
                    if (operation.Status == SyncAuthorityOperationStatus.Completed)
                        result = File.Exists(oldPath) || Directory.Exists(oldPath)
                            ? new(operationId, SyncAuthorityOperationStatus.Blocked, false,
                                "Previously completed retirement, but the old path is occupied again. No effect was replayed; observe the new occupant before planning.")
                            : new(operationId, operation.Status, false, "Previously completed retirement; no effect replayed.");
                    else if (operation.Status is SyncAuthorityOperationStatus.EffectIssuing or SyncAuthorityOperationStatus.EffectUnknown or
                        SyncAuthorityOperationStatus.EffectObserved or SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying)
                        result = await new SyncAuthorityDeleteLocalReconciler(pair, _authority).ReconcileAsync(operationId,
                            commandId, token);
                    else
                        result = await new SyncAuthorityDeleteLocalExecutor(pair, _authority, remote).ExecuteAsync(operationId,
                            commandId, token);
                    // Read-only reconciliation may prove an interrupted rename
                    // was never applied. Retry only that same retained operation,
                    // through its fresh physical/provider fences, once here.
                    if (result.Status == SyncAuthorityOperationStatus.Ready)
                        result = await new SyncAuthorityDeleteLocalExecutor(pair, _authority, remote).ExecuteAsync(operationId,
                            commandId, token);
                    if (result.Status == SyncAuthorityOperationStatus.Completed)
                        await _authority.CompleteOperationGroupAsync(SyncAuthorityCompletedCopyMove.GroupId(pair.Id, proof), token);
                    items.Add(new(proof.OldPath, proof.NewPath, operationId,
                        result.Status == SyncAuthorityOperationStatus.Completed ? "Completed" : "preserved", result.Reason));
                    await Report(proof, result.Reason);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
                {
                    items.Add(new(proof.OldPath, proof.NewPath, operationId, "preserved", error.Message));
                    await Report(proof, "Preserved: " + error.Message);
                }
            }
            if (identityRecovery.Count > 0)
            {
                var mappedItems = identityRecovery.Select(proof => new SyncCompletedCopyMoveRepairItem(
                    proof.OldPath, proof.NewPath, null, "Completed",
                    "Verified distinct files now have separate current identities. Both files, accepted points and completed receipts were preserved; no transfer was replayed.")).ToArray();
                var publicationResult = JsonSerializer.Serialize(new
                {
                    PairId = pair.Id, RunId = commandId, Items = items.Concat(mappedItems).ToArray(),
                    firstSyncScanComplete = true, identityRecovery = identityRecovery.ToArray(),
                    configurationRevision = pair.PolicyRevision
                });
                try
                {
                    await new SyncAuthorityObservationCoordinator(pair, remote, _authority,
                        await _authority.LoadActiveRootBindingIdAsync(pair.Id, "local", token),
                        await _authority.LoadActiveRootBindingIdAsync(pair.Id, "dropbox", token),
                        entry =>
                        {
                            progress?.Invoke(SyncProgressDescription.Describe(entry));
                            return NotifyProgressAsync(onProgress, new(pair, "Repairing File Identities", null, null, null,
                                Comparison: entry));
                        }) { CompletedCopyIdentityRecovery = identityRecovery,
                            IdentityRecoveryCommandId = commandId,
                            IdentityRecoveryResultJson = publicationResult,
                            IdentityRecoveryCompletionStatus = items.Any(i => i.Status == "preserved") ? "blocked" : "completed",
                            IdentityRecoveryPublished = () => publicationCommitted = true,
                            ConcurrentProgressRetention = true, InventoryResources = _transferLimiter.Resources }
                        .ObserveAsync("manual", token);
                    items.AddRange(mappedItems);
                }
                catch (Exception error) when (!publicationCommitted && error is (IOException or InvalidDataException or InvalidOperationException))
                {
                    foreach (var proof in identityRecovery)
                        items.Add(new(proof.OldPath, proof.NewPath, null, "preserved", error.Message));
                    progress?.Invoke("Identity recovery preserved all files: " + error.Message);
                }
            }
            var resultData = new SyncCompletedCopyMoveRepairResult(pair.Id, commandId, items);
            var inventoryRecovered = identityRecovery.Count > 0 && !items.Any(i => i.Status == "preserved");
            if (!publicationCommitted) await _authority.TransitionCommandRequestAsync(commandId,
                items.Any(i => i.Status == "preserved") ? "blocked" : "completed",
                inventoryRecovered ? JsonSerializer.Serialize(new
                {
                    resultData.PairId, resultData.RunId, resultData.Items, firstSyncScanComplete = true
                }) : JsonSerializer.Serialize(resultData),
                cancellationToken: CancellationToken.None);
            if (inventoryRecovered)
                await AppendFirstSyncProblemEventAsync(pair, commandId, "Success",
                    "The completed copied-file mappings were verified against both fresh inventories. Both files and their history were preserved; review current choices before syncing.",
                    "Copied file mappings repaired");
            return resultData;

            async Task Report(SyncCompletedCopyMove proof, string detail)
            {
                progress?.Invoke(proof.OldPath + " -> " + proof.NewPath + ": " + detail);
                var now = DateTimeOffset.UtcNow;
                await NotifyProgressAsync(onProgress, new(pair, "Finishing copied cloud move", null, null, null,
                    Comparison: new("Finishing copied cloud move", proof.OldPath, items.Count, candidates.Length,
                        now, now, detail)));
            }
        }
        catch (Exception error)
        {
            if (!publicationCommitted) await _authority.TransitionCommandRequestAsync(commandId, "blocked",
                JsonSerializer.Serialize(new { pairId, failure = error.Message }), cancellationToken: CancellationToken.None);
            throw;
        }
        finally
        {
            if (control is not null)
            {
                try { await FlushActivityProgressAsync(); }
                finally { EndRun(control); }
            }
        }
    }
}
