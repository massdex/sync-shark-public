using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

// CC2C: the one generic two-cloud observation component.
//
// Both logical sides run through the same per-port loop and the same selection, evidence and path-index
// logic; there is no duplicated left/right implementation and no provider switch. The component lists each
// cloud port exactly through its typed adapter, verifies each root before and after its listing, applies the
// pair's existing selection and ignore policies and records each side's native identities into the existing
// scan, observation, membership and version tables. Both listings must finish successfully before any new
// scan generation is begun, so a partial or throwing listing, or a root that drifted during the listing, can
// never become absence or delete authority. Cloud-native, unsupported and excluded paths keep explicit
// per-side path evidence instead of disappearing.
//
// No local filesystem API is reachable from here: the pair carries no selected root, the ports carry cloud
// identities and the adapters are the only store access.
public sealed class SyncAuthorityCloudObservation(
    SyncPair pair,
    SyncAuthorityCloudEndpointSet endpoints,
    SyncAuthorityDatabase authority,
    Func<SyncComparisonProgress, Task>? onInventory = null)
{
    public async Task<SyncAuthorityObservationResult> ObserveAsync(string triggerKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(authority);
        if (!string.Equals(endpoints.PairId, pair.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The typed cloud endpoint set belongs to another pair.");
        var started = DateTimeOffset.UtcNow;
        using var feedbackGate = new SemaphoreSlim(1, 1);
        async Task ReportAsync(string phase, string path, int found = 0)
        {
            if (onInventory is null) return;
            await feedbackGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Serialize callbacks, not endpoint work. Every distinct target remains in the log.
                await onInventory(new SyncComparisonProgress(phase, path, found, 0, started,
                    DateTimeOffset.UtcNow)).ConfigureAwait(false);
            }
            finally { feedbackGate.Release(); }
        }
        // Both listings are completed first. Nothing is begun or retained until both sides have listed
        // successfully and both roots have verified unchanged after their own listing.
        var (left, right) = await SyncEndpointInventoryRead.BothAsync(
            token => ListPortAsync(endpoints.Left, ReportAsync, token),
            token => ListPortAsync(endpoints.Right, ReportAsync, token), cancellationToken).ConfigureAwait(false);
        // The two completed inventories are compared on their actual provider-qualified native object keys
        // and route-issued mount aliases BEFORE selection or ignore filtering and before either scan
        // generation, so a backing object both sides can reach - at the same or a different relative path,
        // including a path the selection excludes - refuses the observation and is never normalised into two
        // independent copy targets. Genuinely distinct backing objects keep the existing per-side mapping.
        SyncAuthorityCloudObjectOverlapGuard.RequireDistinctInventories(endpoints, left, right);
        var selection = SyncSelectionPolicy.Compile(pair);
        var leftIncluded = left.Where(entry => selection.IsIncluded(entry.RelativePath,
            entry.Kind == PilotEntryKind.Directory)).ToArray();
        var rightIncluded = right.Where(entry => selection.IsIncluded(entry.RelativePath,
            entry.Kind == PilotEntryKind.Directory)).ToArray();
        var retainedIgnored = await authority.LoadIgnoredPathsAsync(pair.Id, pair.IgnoreRules, cancellationToken);
        var excludedPaths = left.Where(entry => !selection.IsIncluded(entry.RelativePath,
                entry.Kind == PilotEntryKind.Directory)).Select(entry => entry.RelativePath)
            .Concat(right.Where(entry => !selection.IsIncluded(entry.RelativePath,
                entry.Kind == PilotEntryKind.Directory)).Select(entry => entry.RelativePath))
            .Concat(retainedIgnored)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // A cloud-native or unsupported item stays present-but-protected: it is never handed to the byte
        // planner and it keeps explicit path evidence on its own side below.
        var leftObservations = leftIncluded.Where(entry => entry.TransferBlockedReason is null)
            .Select(entry => BuildObservation(endpoints.Left, entry)).ToArray();
        var rightObservations = rightIncluded.Where(entry => entry.TransferBlockedReason is null)
            .Select(entry => BuildObservation(endpoints.Right, entry)).ToArray();
        await ReportAsync("Creating inventory snapshots in Sync Shark's database", pair.Name, leftIncluded.Length + rightIncluded.Length);
        var leftScan = await authority.BeginScanGenerationAsync(pair.Id, endpoints.Left.RootBindingId,
            endpoints.Left.CompatibilitySide, triggerKind, "{\"scope\":\"complete\"}", cancellationToken);
        SyncAuthorityScanGeneration? rightScan = null;
        try
        {
            rightScan = await authority.BeginScanGenerationAsync(pair.Id, endpoints.Right.RootBindingId,
                endpoints.Right.CompatibilitySide, triggerKind, "{\"scope\":\"complete\"}", cancellationToken);
            var leftEvidence = BuildEvidence(leftScan, leftIncluded, excludedPaths);
            var rightEvidence = BuildEvidence(rightScan, rightIncluded, excludedPaths);
            await ReportAsync("Writing left endpoint metadata to the inventory database", endpoints.Left.FolderPath ?? "/", leftObservations.Length);
            await authority.PrepareCompactScanAsync(leftScan, leftObservations, leftEvidence, null, null,
                cancellationToken, onInventory,
                SyncEndpointPresentation.PortTitle(SyncEndpointContext.Capture(pair), endpoints.Left.CompatibilitySide));
            await ReportAsync("Writing right endpoint metadata to the inventory database", endpoints.Right.FolderPath ?? "/", rightObservations.Length);
            await authority.PrepareCompactScanAsync(rightScan, rightObservations, rightEvidence, null, null,
                cancellationToken, onInventory,
                SyncEndpointPresentation.PortTitle(SyncEndpointContext.Capture(pair), endpoints.Right.CompatibilitySide));
            await ReportAsync("Committing the completed inventory snapshots", pair.Name, leftIncluded.Length + rightIncluded.Length);
            await authority.CompleteScanGenerationAsync(leftScan, cancellationToken);
            await authority.CompleteScanGenerationAsync(rightScan, cancellationToken);
            await ReportAsync("Updating the saved folder path index", pair.Name, left.Count + right.Count);
            await authority.ReplacePathIndexAsync(pair.Id, leftScan.ScanGenerationId, rightScan.ScanGenerationId,
                BuildPathIndex(left, right, selection), selection, cancellationToken, onInventory);
            return new SyncAuthorityObservationResult(leftScan, rightScan, leftIncluded.Length, rightIncluded.Length,
                0, 0, 0, TimeSpan.Zero);
        }
        catch (Exception failure)
        {
            try
            {
                await authority.CloseFailedScanGenerationsAsync(
                    new[] { leftScan, rightScan }.OfType<SyncAuthorityScanGeneration>().ToArray());
            }
            catch (Exception closureFailure)
            {
                throw new AggregateException("Inventory failed and its incomplete state could not be closed.",
                    failure, closureFailure);
            }
            throw;
        }
    }

    // One per-port loop: verify the exact retained root, list it, then verify it again. A root that drifts
    // during its listing, or an adapter that throws, propagates and nothing is retained for either side.
    private static async Task<IReadOnlyList<PilotRemoteEntry>> ListPortAsync(
        SyncAuthorityCloudEndpointPort port, Func<string, string, int, Task> report,
        CancellationToken cancellationToken)
    {
        await report("Checking " + SyncProviders.DisplayName(port.Provider) + " Root", port.FolderPath ?? "/", 0);
        await port.Store.VerifyRootIdentityAsync(cancellationToken);
        await report("Reading " + SyncProviders.DisplayName(port.Provider) + " Inventory",
            port.FolderPath ?? "/", 0);
        var entries = await port.Store.ListEntriesAsync(item => report(item.Detail,
            string.IsNullOrWhiteSpace(item.Target) ? port.FolderPath ?? "/" : item.Target, item.ObservedCount), cancellationToken)
            ?? throw new InvalidDataException(
                "The provider returned no inventory for this root; a missing inventory is never emptiness.");
        await report("Verifying " + SyncProviders.DisplayName(port.Provider) + " Inventory",
            port.FolderPath ?? "/", entries.Count);
        await port.Store.VerifyRootIdentityAsync(cancellationToken);
        return entries;
    }

    private static SyncAuthorityObservationInput BuildObservation(SyncAuthorityCloudEndpointPort port,
        PilotRemoteEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
            throw new InvalidDataException("A cloud observation has no stable provider file or folder identity.");
        var identity = port.ObservationIdentity(entry.Id!, entry.Revision);
        var identityJson = identity.Serialize();
        var evidence = JsonSerializer.Serialize(new
        {
            path = PilotPath.NormalizeRelative(entry.RelativePath),
            kind = entry.Kind.ToString().ToLowerInvariant(),
            identity = identityJson,
            entry.ContentHash,
            entry.Size,
            contentAlgorithm = identity.ContentAlgorithm,
            modifiedUtc = entry.ModifiedUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
        return new SyncAuthorityObservationInput(PilotPath.NormalizeRelative(entry.RelativePath),
            entry.Kind == PilotEntryKind.File ? "file" : "directory", identityJson, entry.ContentHash,
            entry.Size, evidence);
    }

    // Excluded paths protect both sides and a cloud-native or unsupported item is retained as explicit
    // present-but-protected evidence on its own side. Neither is ever inference of absence.
    private static (string ObservationId, string EvidenceJson)[] BuildEvidence(SyncAuthorityScanGeneration scan,
        IReadOnlyList<PilotRemoteEntry> included, IReadOnlyList<string> excludedPaths)
    {
        var excluded = excludedPaths.Select(path =>
        {
            var evidence = JsonSerializer.Serialize(new
            {
                path,
                state = "selective_sync_excluded",
                reason = "The saved folder selection or ignore rules exclude this path. Existing contents on both cloud sides are retained."
            });
            return (DeterministicId("observation", scan.ScanGenerationId, path, "selection"), evidence);
        });
        var protectedEntries = included.Where(entry => entry.TransferBlockedReason is not null).Select(entry =>
        {
            var evidence = JsonSerializer.Serialize(new
            {
                path = PilotPath.NormalizeInventoryPath(entry.RelativePath),
                state = "cloud_native",
                reason = entry.TransferBlockedReason,
                fileId = entry.Id,
                revision = entry.Revision
            });
            return (DeterministicId("observation", scan.ScanGenerationId, entry.RelativePath, "cloud_native"),
                evidence);
        });
        return excluded.Concat(protectedEntries).ToArray();
    }

    // The path index records logical side presence only. It never claims device residency: online_only stays
    // false, and the left logical side's stable-identity column stays null so a cloud file identity is never
    // read as an NTFS identity. Each side's native identity is retained authoritatively in object_version.
    private IReadOnlyList<SyncPathIndexEntry> BuildPathIndex(IReadOnlyList<PilotRemoteEntry> left,
        IReadOnlyList<PilotRemoteEntry> right, SyncSelectionIndex selection)
    {
        var leftEntries = left.Where(entry => PilotPath.IsWindowsTransferPath(entry.RelativePath))
            .ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        var rightEntries = right.Where(entry => PilotPath.IsWindowsTransferPath(entry.RelativePath))
            .ToDictionary(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);
        var paths = leftEntries.Keys.Concat(rightEntries.Keys)
            .Concat(SyncSelectionPolicy.Normalize(pair.SelectionRules).Select(rule => rule.FolderPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        var result = new List<SyncPathIndexEntry>();
        foreach (var path in paths)
        {
            leftEntries.TryGetValue(path, out var leftEntry);
            rightEntries.TryGetValue(path, out var rightEntry);
            var kind = leftEntry?.Kind ?? rightEntry?.Kind ?? PilotEntryKind.Directory;
            if (leftEntry is not null && rightEntry is not null && leftEntry.Kind != rightEntry.Kind)
                kind = PilotEntryKind.Directory;
            var directory = kind == PilotEntryKind.Directory;
            result.Add(new SyncPathIndexEntry(path, kind, LocalObserved: true,
                LocalPresent: leftEntry is not null, DropboxPresent: rightEntry is not null, OnlineOnly: false,
                LocalStableIdentity: null, DropboxStableIdentity: rightEntry?.Id,
                Included: selection.IsIncluded(path, directory)));
        }
        return result;
    }

    private static string DeterministicId(string role, params string[] parts)
    {
        var material = role + "\n" + string.Join("\n", parts);
        return role + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant()[..32];
    }
}
