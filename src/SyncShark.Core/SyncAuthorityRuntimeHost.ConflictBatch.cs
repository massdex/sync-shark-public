using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record SyncConflictResolutionRequest(SyncConflictSelection Conflict,
    SyncAuthorityConflictResolutionKind Resolution, string? NoticeChoice = null, string? NoticeDestinationPath = null, NoticeActionInput? NoticeInput = null);

/// <summary>Optional bounded input for a notice action, shared by app and CLI.</summary>
public sealed record NoticeActionInput(string? Name = null, int Offset = 0, string? PageToken = null);

public sealed partial class SyncAuthorityRuntimeHost
{
    // One operator snapshot is one effect-bearing command. The existing coordinator still
    // reserves, fences, verifies and retains the losing bytes independently for each target.
    public async Task<SyncAuthorityBatchResult> ResolveConflictSelectionCommandAsync(
        IReadOnlyList<SyncConflictResolutionRequest> requests, string? connectionId = null,
        CancellationToken cancellationToken = default,
        Func<SyncAuthorityBatchProgress, Task>? onProgress = null, Action<string>? admissionProgress = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var selected = requests.ToArray();
        if (selected.Length == 0 || selected.Any(item => item.NoticeChoice is not null || item.Conflict is null ||
                string.IsNullOrWhiteSpace(item.Conflict.ConflictId) || item.Conflict.Generation <= 0 ||
                string.IsNullOrWhiteSpace(item.Conflict.PairId) ||
                !Enum.IsDefined(item.Resolution)) ||
            selected.Select(item => item.Conflict.ConflictId).Distinct(StringComparer.Ordinal).Count() != selected.Length ||
            selected.Select(item => item.Conflict.PairId + "\0" +
                PilotPath.NormalizeRelative(item.Conflict.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new ArgumentException("Select distinct exact conflict IDs, paths, generations and choices.", nameof(requests));
        foreach (var item in selected)
            if (item.Conflict.RelativePath != PilotPath.NormalizeRelative(item.Conflict.RelativePath))
                throw new ArgumentException("Conflict paths must match the captured normalized snapshot.", nameof(requests));

        async Task ValidateSelectionAsync(CancellationToken token)
        {
            var current = (await CaptureOpenConflictSelectionAsync(null, null, token, connectionId)).ToHashSet();
            if (selected.Any(item => !current.Contains(item.Conflict)))
                throw new InvalidOperationException("A selected conflict changed, disappeared or belongs to another account. No selected file was changed.");
        }
        await ValidateSelectionAsync(cancellationToken);
        var pairIds = selected.Select(item => item.Conflict.PairId).Distinct(StringComparer.Ordinal).ToArray();
        var retainedPairs = (await LoadPairsAsync(cancellationToken)).Pairs
            .Where(pair => pairIds.Contains(pair.Id, StringComparer.Ordinal)).ToDictionary(pair => pair.Id, StringComparer.Ordinal);
        if (retainedPairs.Count != pairIds.Length)
            throw new InvalidOperationException("A selected conflict belongs to a retired pair.");
        var pairDefinitions = retainedPairs.ToDictionary(item => item.Key,
            item => JsonSerializer.Serialize(item.Value), StringComparer.Ordinal);
        var payload = JsonSerializer.Serialize(new { pairIds, connectionId, conflictSelection = selected });
        var commandId = await CreateAcceptedCommandAsync("resolve_conflict",
            pairIds.Length == 1 ? pairIds[0] : null, null, payload, cancellationToken,
            admissionProgress: admissionProgress);
        var commandAdmission = await RequireRetainedCommandAdmissionAsync(commandId, cancellationToken);
        PilotRunControl? runControl = null;
        var decisions = pairIds.ToDictionary(id => id, _ => new List<SyncDecision>(), StringComparer.Ordinal);
        var targetOutcomes = new List<object>();
        var started = new int[selected.Length];
        var returned = new bool[selected.Length];
        var resolved = 0;
        var stopped = false;
        string? failure = null;
        string? failureType = null;
        SyncAuthorityBatchItemResult[] Items() => pairIds.Select(id => new SyncAuthorityBatchItemResult(
            retainedPairs[id], new SyncRunResult(id, decisions[id]), failureType, failure)).ToArray();
        string ResultJson() => JsonSerializer.Serialize(new
        {
            conflictSelection = selected, resolved, attempted = started.Count(value => value != 0), stopped, failureType,
            failureMessage = SyncActivityText.Safe(failure ?? ""),
            targets = targetOutcomes, detailsSource = "retained_history_and_authority",
            pairs = RetainedRunSummaries(Items())
        });
        try
        {
            runControl = await BeginRunAsync(cancellationToken, commandId, "resolve_conflict",
                pairIds.Length == 1 ? pairIds[0] : null);
            await using var connections = _authority.BeginConnectionReuseScope();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runControl.StopToken);
            await _authority.TransitionCommandRequestAsync(commandId, "executing", cancellationToken: linked.Token);
            // Admission may take time. Revalidate the original selection, never recapture consent.
            await ValidateSelectionAsync(linked.Token);
            foreach (var pairId in pairIds)
            {
                await runControl.WaitBeforeNextEffectAsync(linked.Token);
                var currentPair = (await LoadPairsAsync(linked.Token)).Pairs.SingleOrDefault(pair => pair.Id == pairId);
                if (currentPair is null || JsonSerializer.Serialize(currentPair) != pairDefinitions[pairId])
                    throw new InvalidOperationException("The selected pair settings or endpoint identity changed; no later target was attempted.");
                // Prepare the exact pair once for this command. Executors still reopen/verify native
                // roots and exact provider versions, and reservation/lease fences require current policy.
                var prepared = await PreparePairAsync(currentPair, linked.Token);
                var runner = CreatePairRunner(currentPair, prepared, runControl, commandAdmission, prepared.CloudFiles, onProgress: onProgress);
                var work = new List<(int Index, SyncAuthorityPairRunner.PreparedConflictResolution Work)>();
                var verifiedResults = new HashSet<SyncRunResult>(ReferenceEqualityComparer.Instance);
                try
                {
                    for (var index = 0; index < selected.Length; index++)
                    {
                        var request = selected[index];
                        if (request.Conflict.PairId != pairId) continue;
                        linked.Token.ThrowIfCancellationRequested();
                        admissionProgress?.Invoke($"Planning conflict {index + 1} of {selected.Length}: {request.Conflict.RelativePath}; exact generation {request.Conflict.Generation}.");
                        work.Add((index, await runner.PrepareConflictResolutionAsync(request.Conflict.RelativePath,
                            request.Resolution, request.Conflict.ConflictId, request.Conflict.Generation, linked.Token)));
                    }
                    var drained = await SyncAuthorityOperationScheduler.RunAsync(work,
                        item => item.Work.SchedulingOperation,
                        async item =>
                        {
                            var request = selected[item.Index];
                            var target = request.Conflict;
                            try
                            {
                                var livePair = (await LoadPairsAsync(linked.Token)).Pairs.SingleOrDefault(pair => pair.Id == pairId);
                                if (livePair is null || JsonSerializer.Serialize(livePair) != pairDefinitions[pairId])
                                    return new SyncRunResult(pairId, [new SyncDecision(target.RelativePath,
                                        SyncDecisionKind.Blocked, "Pair settings or endpoint identity changed before dispatch; no file effect was issued.") { NotAttempted = true }]);
                                Interlocked.Exchange(ref started[item.Index], 1);
                                admissionProgress?.Invoke($"Conflict {item.Index + 1} of {selected.Length}: {target.RelativePath}; {request.Resolution}.");
                                return await item.Work.ExecuteAsync(linked.Token);
                            }
                            catch (Exception error)
                            {
                                return new SyncRunResult(pairId, [new SyncDecision(target.RelativePath,
                                    SyncDecisionKind.ResponseUnknown,
                                    "Resolution interrupted; verify retained operation receipts before retrying. " + SyncActivityText.Safe(error.Message))]);
                            }
                        },
                        async (item, result) =>
                        {
                            // The existing pump reduces results serially and drains admitted workers.
                            // Retain each real result before any cancellable read or subsequent admission.
                            var request = selected[item.Index];
                            var target = request.Conflict;
                            returned[item.Index] = true;
                            decisions[pairId].AddRange(result.Decisions);
                            targetOutcomes.Add(new { target, request.Resolution, result, verifiedResolved = false });
                            await _authority.CheckpointExecutingCommandResultAsync(commandId, ResultJson(), CancellationToken.None);
                            var retained = await _authority.LoadConflictAsync(target.ConflictId, CancellationToken.None);
                            var verified = result.Recovered == 1 && result.Blocked == 0 && result.Conflicts == 0 &&
                                !result.Stopped && retained.ConflictGeneration == target.Generation && retained.Status == "resolved";
                            targetOutcomes[^1] = new { target, request.Resolution, result, verifiedResolved = verified };
                            if (verified)
                            {
                                resolved++;
                                verifiedResults.Add(result);
                            }
                            else
                            {
                                failure ??= "Some exact conflicts did not reach a verified resolved state. Their per-file outcomes remain retained.";
                                if (result.Recovered > 0)
                                {
                                    decisions[pairId].RemoveAll(value => value.RelativePath == target.RelativePath);
                                    decisions[pairId].Add(new SyncDecision(target.RelativePath, SyncDecisionKind.CleanupPending,
                                        "The file effect returned complete, but exact conflict finalization needs verification before retrying."));
                                }
                            }
                            await _authority.CheckpointExecutingCommandResultAsync(commandId, ResultJson(), CancellationToken.None);
                            // A successful effect is not a verified resolution until the exact retained
                            // generation/status agrees. Do not admit or reward further work on that gap.
                            return (result.Recovered == 0 || verified) && !result.Stopped && result.NeedsVerification == 0 &&
                                !result.Decisions.Any(value => value.NotAttempted && value.Reason.Contains("settings or endpoint identity changed", StringComparison.Ordinal));
                        }, linked.Token, adaptive: runner.TransferScheduling,
                        successful: result => verifiedResults.Contains(result));
                    if (!drained) break;
                }
                finally { foreach (var item in work) item.Work.Dispose(); }
            }
        }
        catch (Exception error)
        {
            stopped = error is OperationCanceledException || runControl?.IsStopRequested == true;
            failureType = error.GetType().Name;
            failure = SyncActivityText.Safe(error.Message);
            for (var index = 0; index < selected.Length; index++)
            {
                if (Volatile.Read(ref started[index]) == 0 || returned[index]) continue;
                var active = selected[index].Conflict;
                if (!decisions[active.PairId].Any(item => item.RelativePath == active.RelativePath))
                    decisions[active.PairId].Add(new SyncDecision(active.RelativePath,
                        SyncDecisionKind.ResponseUnknown,
                        "Resolution interrupted before a terminal result; verify its retained operation receipts before retrying. " + failure));
            }
        }
        finally
        {
            stopped |= cancellationToken.IsCancellationRequested || runControl?.IsStopRequested == true;
            for (var index = 0; index < selected.Length; index++)
            {
                var target = selected[index].Conflict;
                if (Volatile.Read(ref started[index]) != 0 || returned[index]) continue;
                decisions[target.PairId].Add(new SyncDecision(target.RelativePath, SyncDecisionKind.Blocked,
                    "Not attempted: the earlier target, selection validation or batch deadline stopped this command.") { NotAttempted = true });
            }
            if (runControl is not null)
            {
                try
                {
                    await _authority.TransitionCommandRequestAsync(commandId,
                        resolved == selected.Length ? "completed" : "blocked", ResultJson(), CancellationToken.None);
                }
                finally
                {
                    try { await FlushActivityProgressAsync(); }
                    finally { EndRun(runControl); }
                }
            }
            else await TryBlockCommandAsync(commandId, "conflict_batch_admission_failed");
        }
        return new SyncAuthorityBatchResult(Items(), stopped);
    }
}
