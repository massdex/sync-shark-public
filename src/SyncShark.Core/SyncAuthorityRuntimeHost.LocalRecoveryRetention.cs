namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityRuntimeHost
{
    private readonly SemaphoreSlim _localRecoveryRetentionGate = new(1, 1);

    // Retention is maintenance after a run, never an ordinary startup scan. The byte
    // limit is deliberately soft when all remaining copies are in use or unavailable.
    private async Task PruneLocalRecoveryAsync()
    {
        await _localRecoveryRetentionGate.WaitAsync();
        try
        {
            var maximum = LoadTransferSettings().RecoveryMaxBytes;
            var artifacts = (await _authority.LoadRecoveryArtifactsAsync())
                .Where(item => item.Side == "local" && item.Status != "released" &&
                               !SyncAuthorityCloudRecoveryDescriptor.TryParse(item.IdentityJson, out _))
                .OrderBy(item => item.RetainedUtc).ThenBy(item => item.RecoveryArtifactId, StringComparer.Ordinal)
                .ToArray();
            var total = artifacts.Aggregate(0L, (sum, item) => checked(sum + item.Size));
            var now = DateTimeOffset.UtcNow;
            foreach (var artifact in artifacts)
            {
                var pair = await _authority.LoadPairDefinitionAsync(artifact.PairId);
                if (pair is null) continue;
                var expired = pair.RecoveryRetentionDays is { } days &&
                              artifact.RetainedUtc.AddDays(days) <= now;
                var capacityPressure = total > maximum;
                if (artifact.Status != "release_authorized" && !expired && !capacityPressure) continue;
                try
                {
                    // Reuse exact identity/hash verification and the transactional
                    // pending-restore/unfinished-attempt guard before deleting anything.
                    await RemoveRecoveryCopyCoreAsync(ToRecoveryCatalogItem(pair, "local", artifact), capacityPressure);
                    total -= artifact.Size;
                    await RecordRecoveryRetentionWarningAsync(
                        $"Local recovery pruned {artifact.RecoveryArtifactId} ({artifact.OriginalRelativePath}, {artifact.Size} bytes): " +
                        (expired ? "retention expired." : "recovery size cap."), pair.Id);
                }
                catch (Exception error) when (error is InvalidOperationException or IOException)
                {
                    // A pending restore or changed/unavailable object wins over retention.
                    // Leave its durable release state intact for the next maintenance pass.
                    System.Diagnostics.Trace.TraceWarning("Local recovery retention skipped {0}: {1}",
                        artifact.RecoveryArtifactId, error.Message);
                }
            }
            if (total > maximum)
                await RecordRecoveryRetentionWarningAsync(
                    $"Local recovery retains {total} bytes above its {maximum}-byte cap because remaining copies are protected or unavailable.");
        }
        finally { _localRecoveryRetentionGate.Release(); }
    }

    private async Task RecordRecoveryRetentionWarningAsync(string message, string? pairId = null)
    {
        System.Diagnostics.Trace.TraceWarning(message);
        try
        {
            await _authority.AppendDiagnosticActivityAsync(new SyncActivityEvent("", DateTimeOffset.UtcNow,
                [SyncActivityCategories.Sync, SyncActivityCategories.Diagnostics], "Warning", "Recovery",
                "Local recovery retention", message, PairId: pairId));
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning("Recovery retention warning could not be retained: {0}", error.Message);
        }
    }
}
