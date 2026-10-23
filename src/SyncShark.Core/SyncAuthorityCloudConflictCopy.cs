using System.Globalization;
using System.Text.Json;
using static Massdex.SyncShark.SyncAuthorityCloudExecution;

namespace Massdex.SyncShark;

// CC3F-2: the ONE generic cloud preserve-conflict-copy executor/reconciler.
//
// The legacy NTFS/Dropbox visible-conflict-copy engine stays the compatibility path for a pair that still
// carries a local selected root. This engine is the same semantics on typed cloud ports: the logical RIGHT
// endpoint's own adapter performs one create-only copy of the exact retained source object to the retained
// deterministic destination, the copy is proven by the adapter's own known receipt plus an independent exact
// destination readback, and it is committed as an AUXILIARY attempt - never as a cross-side convergence
// synchronization point. An unknown response is never replayed; a retained known-success receipt may resume
// only the readback and the durable commit. It is a copy inside the logical-right endpoint and is never a
// cross-provider move, and it never runs before its own retained prerequisite.

/// <summary>One safe refusal of the shared cloud conflict-copy slice. It is never a partial effect.</summary>
public sealed class SyncAuthorityCloudConflictCopyException(string message)
    : SyncAuthorityCloudExecutionException(message);

/// <summary>
/// The typed cloud conflict-execution context: the one CC3C cloud execution context (the CC2C typed two-port
/// set, the private control namespace and its actually held control-root lease) plus the two logical sides'
/// retained account and recovery authorities. It is produced only for an explicit cloud/cloud pair and carries
/// no local pair path, so a cloud conflict effect is never bound to an NTFS selected root.
/// </summary>
internal sealed record SyncAuthorityCloudConflictContext(
    SyncAuthorityCloudExecutionContext Cloud,
    IReadOnlyList<SyncAuthorityCloudSideAuthority> SideBindings)
{
    internal string PairId => Cloud.PairId;

    internal SyncAuthorityCloudEndpointPort Left => Cloud.Port(SyncAuthorityCloudSides.LogicalLeft);

    internal SyncAuthorityCloudEndpointPort Right => Cloud.Port(SyncAuthorityCloudSides.LogicalRight);

    /// <summary>The typed port of one retained logical side; only the two exact compatibility tags exist.</summary>
    internal SyncAuthorityCloudEndpointPort Port(string side) => Cloud.Port(side);

    /// <summary>
    /// Requires both retained side authorities to describe exactly these two typed ports. A retained authority
    /// that names another provider account, namespace, folder or pair root than its typed port is refused
    /// before any effect rather than reinterpreted.
    /// </summary>
    internal void RequireSideAuthorities()
    {
        if (SideBindings.Count != 2)
            throw new SyncAuthorityCloudConflictCopyException(
                "The typed cloud conflict context requires exactly two retained side authorities.");
        foreach (var port in new[] { Left, Right })
        {
            var authority = SideBindings.SingleOrDefault(item =>
                string.Equals(item.CompatibilitySide, port.CompatibilitySide, StringComparison.Ordinal));
            if (authority is null)
                throw new SyncAuthorityCloudConflictCopyException(
                    "The typed cloud conflict context is missing the retained authority of a logical side.");
            if (!string.Equals(authority.Provider, port.Provider, StringComparison.Ordinal) ||
                !string.Equals(authority.ProviderAccountId, port.ProviderAccountId, StringComparison.Ordinal) ||
                !string.Equals(authority.NamespaceId, port.NamespaceId, StringComparison.Ordinal) ||
                !string.Equals(authority.FolderId, port.FolderId, StringComparison.Ordinal) ||
                !string.Equals(authority.PairRootBindingId, port.RootBindingId, StringComparison.Ordinal))
                throw new SyncAuthorityCloudConflictCopyException(
                    "A retained cloud conflict side authority names another provider account, namespace, folder or root than its typed port; the conflict plan is refused before any effect.");
        }
    }
}

/// <summary>
/// The one generic cloud visible-conflict-copy engine. It serves the <c>ResolveConflict</c> preserve-both
/// prerequisite of any explicit cloud/cloud ordered provider combination, direct or queued, through the same
/// retained journal, lease, allocation and auxiliary-commit APIs the legacy engine uses.
/// </summary>
internal sealed class SyncAuthorityCloudConflictCopyExecutor(
    SyncAuthorityDatabase authority,
    SyncAuthorityCloudConflictContext conflict)
{
    /// <summary>
    /// The bounded crash/cancellation seam used by the focused proofs. It is an ordinary in-process hook with
    /// no production default; it performs no provider or journal effect itself.
    /// </summary>
    internal static Action<string>? PhaseForTest { get; set; }

    internal async Task<SyncAuthorityExecutionResult> ExecuteAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        conflict.Cloud.RequireHeld();
        conflict.RequireSideAuthorities();
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.ResolveConflict ||
            !string.Equals(operation.PairId, conflict.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudConflictCopyException(
                "The requested operation is not a visible conflict-copy preservation of this typed cloud pair.");
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud visible conflict copy is already complete; the repeated call is a no-op and no request was issued.");
        if (operation.Status is not (SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased))
            throw new SyncAuthorityCloudConflictCopyException(
                "The cloud visible conflict copy is not at a runnable pre-effect boundary; its current state is " +
                SyncAuthorityNames.OperationStatus(operation.Status) + ".");
        if (operation.Attempts.Any(item => item.Status is SyncAuthorityAttemptStatus.RequestSent or
                SyncAuthorityAttemptStatus.ResponseReceived or SyncAuthorityAttemptStatus.EffectUnknown))
            throw new SyncAuthorityCloudConflictCopyException(
                "The cloud visible conflict copy already carries an unresolved request; it must be reconciled rather than re-executed.");

        var plan = RequireCopyPlan(operation);
        var sourceVersion = await authority.LoadObjectVersionAsync(plan.SourceVersionId, cancellationToken);
        var sourceIdentity = RequireRetainedVersionIdentity(sourceVersion, plan.SourceMember, conflict.Right,
            "conflict-copy source");
        if (sourceVersion.Size is null || string.IsNullOrWhiteSpace(sourceVersion.ContentHash))
            throw new SyncAuthorityCloudConflictCopyException(
                "The retained conflict-copy source version has no exact size and native content hash.");

        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        var dispatched = false;
        try
        {
            // The exact current source is re-read from the logical-right adapter and must still carry the
            // retained stable identity, revision, size and native digest before anything is copied.
            var source = await ReadExactSourceAsync(plan.SourcePath, sourceIdentity.FileId, sourceIdentity.Revision, sourceVersion,
                cancellationToken);
            PhaseForTest?.Invoke("after-source-verified");
            var destination = await ReadExactDestinationAsync(plan.DestinationPath, cancellationToken);
            if (destination is not null)
                return await BlockWithoutCopyAsync(operationId, lease,
                    "The deterministic visible cloud conflict-copy destination already exists; no copy was issued and the conflict is preserved.",
                    cancellationToken);

            var fingerprint = Hash("cloud-preserve-conflict-copy", conflict.PairId, source.Id!, source.Revision!,
                plan.SourcePath, plan.DestinationPath, plan.SourceVersionId, sourceVersion.ContentHash!,
                sourceVersion.Size.Value.ToString(CultureInfo.InvariantCulture));
            var attempt = await authority.StartDispatchedAttemptAsync(operationId, lease, fingerprint,
                conflict.Right.Provider + "-visible-conflict-copy-add", cancellationToken);
            dispatched = true;
            PhaseForTest?.Invoke("request-sent-before-copy");
            PilotUploadResult copied;
            try
            {
                copied = await conflict.Right.Store.CopyCreateOnlyAsync(source, plan.DestinationPath,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error) when (ConclusiveNonApplication(error))
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.KnownNotApplied, RemoteJson(source), "{}",
                    JsonSerializer.Serialize(new { classification = "copy_unsupported", adapter = error.GetType().Name }),
                    Hash("cloud-preserve-not-applied", fingerprint), cancellationToken);
                await authority.ReconcileAttemptAsync(attempt.AttemptId, lease,
                    SyncAuthorityAttemptStatus.Blocked,
                    "{\"result\":\"the logical-right adapter cannot copy this object at all\"}", cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, true,
                    "The logical-right adapter declares the create-only conflict copy unsupported; nothing was copied, the conflict is preserved and no winner was chosen.");
            }
            catch (Exception error)
            {
                await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                    SyncAuthorityReceiptCertainty.Unknown, RemoteJson(source), "{}",
                    JsonSerializer.Serialize(new { classification = "copy_response_unknown", adapter = error.GetType().Name }),
                    Hash("cloud-preserve-unknown", fingerprint), cancellationToken);
                return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.EffectUnknown,
                    true,
                    "The visible cloud conflict-copy response is unknown; the retained conflict and its evidence are preserved and the copy is never replayed from absence or matching bytes.");
            }

            PhaseForTest?.Invoke("after-copy-before-receipt");
            if (string.IsNullOrWhiteSpace(copied.FileId) || string.IsNullOrWhiteSpace(copied.Revision) ||
                copied.Size != sourceVersion.Size ||
                !string.Equals(copied.ContentHash, sourceVersion.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    "The visible cloud conflict copy does not carry the exact retained predecessor size and native hash.");
            var destinationJson = JsonSerializer.Serialize(new
            {
                path = plan.DestinationPath,
                copied.FileId,
                copied.Revision,
                copied.Size,
                copied.ContentHash
            });
            await authority.RecordReceiptAsync(attempt.AttemptId, lease,
                SyncAuthorityReceiptCertainty.KnownSuccess, RemoteJson(source), destinationJson, destinationJson,
                Hash("cloud-preserve-success", fingerprint, destinationJson), cancellationToken);
            PhaseForTest?.Invoke("after-receipt-before-readback");
            // The adapter's own receipt is not the proof: the destination is read back independently.
            var readback = await ReadExactDestinationAsync(plan.DestinationPath, cancellationToken);
            if (readback is null || readback.Kind != PilotEntryKind.File ||
                !string.Equals(readback.Id, copied.FileId, StringComparison.Ordinal) ||
                !string.Equals(readback.Revision, copied.Revision, StringComparison.Ordinal) ||
                readback.Size != sourceVersion.Size ||
                readback.ContentHash is null ||
                !string.Equals(readback.ContentHash, sourceVersion.ContentHash, StringComparison.OrdinalIgnoreCase))
                return new SyncAuthorityExecutionResult(operationId,
                    SyncAuthorityOperationStatus.EffectUnknown, true,
                    "The visible cloud conflict copy was reported applied but its exact destination readback does not agree; the retained receipt and evidence are preserved and nothing was committed or replayed.");
            PhaseForTest?.Invoke("after-readback-before-commit");
            await CommitCopiedObjectAsync(operation, plan.DestinationMember, attempt.AttemptId, lease,
                plan.DestinationPath, copied.FileId, copied.Revision, copied.Size, copied.ContentHash,
                cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"visible cloud conflict copy preserved without claiming cross-side convergence\"}",
                cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, true,
                "The exact logical-right conflict version was preserved under its deterministic visible copy name on the right provider.");
        }
        finally
        {
            try
            {
                await leaseRenewal.ReleaseAsync();
            }
            catch when (dispatched)
            {
                // The durable operation state stays authoritative; a refused lease release never masks the
                // exact returned disposition.
            }
        }
    }

    /// <summary>
    /// Reconciles the same cloud conflict copy without ever reissuing it. A completed operation is a no-op; an
    /// unresolved copy with no known receipt is left explicitly unresolved with zero requests; an operation
    /// whose retained known-success receipt and exact destination readback both agree resumes only the durable
    /// commit.
    /// </summary>
    internal async Task<SyncAuthorityExecutionResult> ReconcileAsync(string operationId, string workerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        conflict.Cloud.RequireHeld();
        conflict.RequireSideAuthorities();
        var operation = await authority.LoadOperationAsync(operationId, cancellationToken);
        if (operation.Kind != SyncAuthorityOperationKind.ResolveConflict ||
            !string.Equals(operation.PairId, conflict.PairId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudConflictCopyException(
                "The requested operation is not a visible conflict-copy preservation of this typed cloud pair.");
        if (operation.Status == SyncAuthorityOperationStatus.Completed)
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The cloud visible conflict copy is already complete; reconciliation is a no-op.");
        var plan = RequireCopyPlan(operation);
        if (operation.Status is SyncAuthorityOperationStatus.Committed or SyncAuthorityOperationStatus.CleanupPending)
            return await FinalizeAsync(authority, operationId, workerId, conflict.Cloud, "cloud conflict copy",
                cancellationToken);
        if (operation.Status is not (SyncAuthorityOperationStatus.EffectIssuing or
            SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved or
            SyncAuthorityOperationStatus.Reconciling or SyncAuthorityOperationStatus.Verifying))
            throw new SyncAuthorityCloudConflictCopyException(
                "The cloud conflict copy has no unresolved dispatched effect to reconcile.");
        var attempt = operation.Attempts.SingleOrDefault(item =>
            item.Status is SyncAuthorityAttemptStatus.RequestSent or SyncAuthorityAttemptStatus.ResponseReceived or
                SyncAuthorityAttemptStatus.EffectUnknown);
        if (attempt is null)
            return new SyncAuthorityExecutionResult(operationId, operation.Status, false,
                "The unresolved cloud conflict copy carries no unresolved request in its retained journal; no copy was reissued and nothing was committed from absence.");
        var sourceVersion = await authority.LoadObjectVersionAsync(plan.SourceVersionId, cancellationToken);
        var sourceIdentity = RequireRetainedVersionIdentity(sourceVersion, plan.SourceMember, conflict.Right,
            "conflict-copy source");
        await using var leaseRenewal = await authority.BeginFencedOperationLeaseRenewalAsync(operationId, workerId, SyncAuthorityLeasePolicy.LeaseDuration,
            cancellationToken);
        var lease = leaseRenewal.Lease;
        cancellationToken = leaseRenewal.WorkToken;
        try
        {
            var receipt = await authority.LoadLatestEffectReceiptAsync(operationId, attempt.AttemptId,
                cancellationToken);
            if (receipt is null || !string.Equals(receipt.Certainty,
                    SyncAuthorityNames.ReceiptCertainty(SyncAuthorityReceiptCertainty.KnownSuccess),
                    StringComparison.Ordinal) ||
                !TryReadBoundReceipt(receipt, plan, sourceIdentity, sourceVersion, out var copied))
                return new SyncAuthorityExecutionResult(operationId, operation.Status, false,
                    "The unresolved cloud conflict copy has no retained known-success receipt, so no copy is reissued and nothing is committed merely because a path or its bytes match.");
            var readback = await ReadExactDestinationAsync(plan.DestinationPath, cancellationToken);
            if (readback is null || readback.Kind != PilotEntryKind.File ||
                !string.Equals(readback.Id, copied!.FileId, StringComparison.Ordinal) ||
                !string.Equals(readback.Revision, copied.Revision, StringComparison.Ordinal) ||
                sourceVersion.Size is null || readback.Size != copied.Size ||
                readback.ContentHash is null ||
                !string.Equals(readback.ContentHash, copied.ContentHash, StringComparison.OrdinalIgnoreCase))
                return new SyncAuthorityExecutionResult(operationId, operation.Status, false,
                    "The retained known-success cloud conflict-copy receipt is not confirmed by an exact destination readback; nothing is committed and no copy is reissued.");
            await CommitCopiedObjectAsync(operation, plan.DestinationMember, attempt.AttemptId, lease,
                plan.DestinationPath, readback.Id!, readback.Revision!, readback.Size, readback.ContentHash!,
                cancellationToken);
            await authority.CompleteOperationAsync(lease,
                "{\"result\":\"visible cloud conflict copy resumed from its retained receipt\"}", cancellationToken);
            return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Completed, false,
                "The retained known-success cloud conflict copy was confirmed by its exact destination readback and committed without a second copy request.");
        }
        finally
        {
            await leaseRenewal.ReleaseAsync();
        }
    }

    private async Task<SyncAuthorityExecutionResult> BlockWithoutCopyAsync(string operationId,
        SyncAuthorityLease lease, string reason, CancellationToken cancellationToken)
    {
        var fingerprint = Hash("cloud-preserve-precondition", operationId, reason);
        var attempt = await authority.StartDispatchedAttemptAsync(operationId, lease, fingerprint,
            conflict.Right.Provider + "-visible-conflict-copy-precondition", cancellationToken);
        await authority.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.KnownNotApplied,
            "{}", "{}", JsonSerializer.Serialize(new { classification = "destination_collision", reason }),
            Hash("cloud-preserve-blocked", fingerprint), cancellationToken);
        await authority.ReconcileAttemptAsync(attempt.AttemptId, lease, SyncAuthorityAttemptStatus.Blocked,
            JsonSerializer.Serialize(new { reason }), cancellationToken);
        return new SyncAuthorityExecutionResult(operationId, SyncAuthorityOperationStatus.Blocked, false, reason);
    }

    internal async Task CommitCopiedObjectAsync(SyncAuthorityOperationSnapshot operation,
        SyncAuthorityMember destinationMember, string attemptId, SyncAuthorityLease lease, string destinationPath,
        string fileId, string revision, long size, string contentHash, CancellationToken cancellationToken)
    {
        var objectId = operation.ObjectId
            ?? throw new SyncAuthorityCloudConflictCopyException(
                "The conflict-copy operation has no destination object identity.");
        if (!string.Equals(destinationMember.ObjectId, objectId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudConflictCopyException(
                "The conflict-copy destination member belongs to another object.");
        var identity = new SyncAuthorityCloudObservationIdentity(conflict.Right.Provider,
            conflict.Right.ProviderAccountId, conflict.Right.NamespaceId, fileId, revision,
            conflict.Right.Definition.ContentHashAlgorithm).Serialize();
        var versionId = Id("version-conflict-copy", objectId, identity, contentHash,
            size.ToString(CultureInfo.InvariantCulture));
        var membershipId = Id("membership-conflict-copy", operation.OperationId, destinationPath, versionId);
        var generation = await authority.LoadMembershipGenerationAsync(membershipId, cancellationToken)
            ?? await authority.NextMembershipGenerationAsync(destinationMember.RootBindingId,
                destinationPath, cancellationToken);
        await authority.RegisterObjectAsync(objectId, "file", cancellationToken);
        await authority.RegisterObjectVersionAsync(versionId, objectId, conflict.Right.CompatibilitySide, identity,
            contentHash, size, cancellationToken);
        await authority.RegisterMembershipAsync(membershipId, objectId, destinationMember.RootBindingId,
            destinationPath, generation, cancellationToken: cancellationToken);
        PhaseForTest?.Invoke("after-membership-before-auxiliary-commit");
        await authority.CommitSuccessfulAuxiliaryAttemptAsync(attemptId, lease,
            "{\"result\":\"visible cloud conflict copy registered without claiming cross-side convergence\"}",
            cancellationToken);
        PhaseForTest?.Invoke("after-auxiliary-commit-before-completion");
    }

    private async Task<PilotRemoteEntry> ReadExactSourceAsync(string sourcePath, string fileId, string? revision,
        SyncAuthorityObjectVersion sourceVersion, CancellationToken cancellationToken)
    {
        await conflict.Right.Store.VerifyRootIdentityAsync(cancellationToken);
        var entries = await conflict.Right.Store.ListEntriesAsync(cancellationToken);
        await conflict.Right.Store.VerifyRootIdentityAsync(cancellationToken);
        var matches = entries.Where(entry => MatchesPath(entry, sourcePath)).ToArray();
        if (matches.Length != 1)
            throw new SyncAuthorityCloudConflictCopyException(
                "The exact retained conflict-copy source is not present exactly once at its retained path; nothing was copied.");
        var entry = matches[0];
        if (entry.Kind != PilotEntryKind.File || string.IsNullOrWhiteSpace(revision) ||
            !string.Equals(entry.Id, fileId, StringComparison.Ordinal) ||
            !string.Equals(entry.Revision, revision, StringComparison.Ordinal) ||
            entry.Size != sourceVersion.Size ||
            entry.ContentHash is null ||
            !string.Equals(entry.ContentHash, sourceVersion.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudConflictCopyException(
                "The current logical-right source no longer carries the exact retained identity, revision, size and native hash; nothing was copied.");
        return entry;
    }

    private async Task<PilotRemoteEntry?> ReadExactDestinationAsync(string destinationPath,
        CancellationToken cancellationToken)
    {
        await conflict.Right.Store.VerifyRootIdentityAsync(cancellationToken);
        var entries = await conflict.Right.Store.ListEntriesAsync(cancellationToken);
        await conflict.Right.Store.VerifyRootIdentityAsync(cancellationToken);
        return entries.SingleOrDefault(entry => MatchesPath(entry, destinationPath));
    }

    private CopyPlan RequireCopyPlan(SyncAuthorityOperationSnapshot operation)
    {
        var sourceMember = operation.Members.SingleOrDefault(item => item.Role == "source")
            ?? throw new SyncAuthorityCloudConflictCopyException(
                "The visible conflict copy has no exact retained source member.");
        var destinationMember = operation.Members.SingleOrDefault(item => item.Role == "destination")
            ?? throw new SyncAuthorityCloudConflictCopyException(
                "The visible conflict copy has no exact retained destination member.");
        var sourcePath = RequireExactRetainedPath(sourceMember);
        var destinationPath = RequireExactRetainedPath(destinationMember);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            throw new SyncAuthorityCloudConflictCopyException(
                "The conflict-copy destination is not distinct from its exact retained source.");
        RequirePreconditions(operation, sourcePath);
        if (operation.Members.Count != 2 ||
            !string.Equals(destinationMember.ObjectId, operation.ObjectId, StringComparison.Ordinal))
            throw new SyncAuthorityCloudConflictCopyException(
                "The visible conflict copy does not retain exactly one source and its destination object.");
        foreach (var member in new[] { sourceMember, destinationMember })
            if (!string.Equals(member.PairId, conflict.PairId, StringComparison.Ordinal) ||
                !string.Equals(member.RootBindingId, conflict.Right.RootBindingId, StringComparison.Ordinal))
                throw new SyncAuthorityCloudConflictCopyException(
                    "The visible conflict copy may only be created inside the logical-right endpoint's own retained root; a crossed root or account binding is refused before any effect.");
        var sourceVersionId = sourceMember.ObjectVersionId
            ?? throw new SyncAuthorityCloudConflictCopyException(
                "The visible conflict copy has no exact retained source version authority.");
        using var conditions = JsonDocument.Parse(operation.PreconditionsJson);
        var root = conditions.RootElement;
        if (!root.TryGetProperty("sourcePath", out var sourcePathValue) ||
            sourcePathValue.ValueKind != JsonValueKind.String || sourcePathValue.GetString() != sourcePath ||
            !root.TryGetProperty("destinationPath", out var destinationPathValue) ||
            destinationPathValue.ValueKind != JsonValueKind.String || destinationPathValue.GetString() != destinationPath ||
            !root.TryGetProperty("sourceVersion", out var sourceVersionValue) ||
            sourceVersionValue.ValueKind != JsonValueKind.String || sourceVersionValue.GetString() != sourceVersionId ||
            !root.TryGetProperty("destinationAbsent", out var absent) || absent.ValueKind != JsonValueKind.True ||
            destinationMember.ObjectVersionId is not null)
            throw new SyncAuthorityCloudConflictCopyException(
                "The retained conflict-copy preconditions do not bind the exact source version and absent destination.");
        return new CopyPlan(sourceMember, destinationMember, sourcePath, destinationPath, sourceVersionId);
    }

    private sealed record CopyPlan(SyncAuthorityMember SourceMember, SyncAuthorityMember DestinationMember,
        string SourcePath, string DestinationPath, string SourceVersionId);

    private static bool TryReadBoundReceipt(SyncAuthorityEffectReceipt receipt, CopyPlan plan,
        SyncAuthorityCloudObservationIdentity source, SyncAuthorityObjectVersion version,
        out PilotUploadResult? copied)
    {
        copied = null;
        try
        {
            using var sourceDocument = JsonDocument.Parse(receipt.SourceIdentityJson);
            using var destinationDocument = JsonDocument.Parse(receipt.DestinationIdentityJson);
            var s = sourceDocument.RootElement;
            var d = destinationDocument.RootElement;
            if (s.ValueKind != JsonValueKind.Object || d.ValueKind != JsonValueKind.Object ||
                version.Size is null || string.IsNullOrWhiteSpace(version.ContentHash) ||
                Text(s, "RelativePath") != plan.SourcePath || Text(s, "Id") != source.FileId ||
                Text(s, "Revision") != source.Revision || Number(s, "Size") != version.Size ||
                !string.Equals(Text(s, "ContentHash"), version.ContentHash, StringComparison.OrdinalIgnoreCase) ||
                Text(d, "path") != plan.DestinationPath || string.IsNullOrWhiteSpace(Text(d, "FileId")) ||
                string.IsNullOrWhiteSpace(Text(d, "Revision")) || Number(d, "Size") != version.Size ||
                !string.Equals(Text(d, "ContentHash"), version.ContentHash, StringComparison.OrdinalIgnoreCase))
                return false;
            copied = new PilotUploadResult(Text(d, "FileId")!, Text(d, "Revision")!, version.Size.Value,
                Text(d, "ContentHash")!);
            return true;
        }
        catch (JsonException) { return false; }

        static string? Text(JsonElement e, string key) =>
            e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        static long? Number(JsonElement e, string key) =>
            e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number) ? number : null;
    }

    private static string RemoteJson(PilotRemoteEntry entry) => JsonSerializer.Serialize(new
    {
        entry.RelativePath,
        entry.Id,
        entry.Revision,
        entry.Size,
        entry.ContentHash
    });
}
