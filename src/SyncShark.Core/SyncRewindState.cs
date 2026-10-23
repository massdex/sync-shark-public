using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

// The bounded replacement for the growing Rewind collection in the product-state aggregate. After
// publication the aggregate retains only this reference, bound to the actual authority installation
// that owns the normalized rows, and RewindSessions is empty. It is a publication marker, never a
// copy of the state: every reader of Rewind state reads the authority database.
public sealed record SyncRewindStorageReference(string InstallationId, int StorageVersion)
{
    public const int CurrentStorageVersion = 1;

    internal static SyncRewindStorageReference Create(string installationId) =>
        new(SyncControlNamespace.NormalizeInstallationId(installationId), CurrentStorageVersion);

    // A retained reference is accepted only when it is fully formed, on the supported storage version
    // and bound to the installation identity this aggregate already carries. Anything else fails
    // closed instead of becoming an empty, resumable Rewind state.
    internal static SyncRewindStorageReference Normalize(SyncRewindStorageReference? value,
        string? installationId)
    {
        if (value is null) throw new InvalidDataException("A retained Rewind storage reference is absent.");
        var normalized = SyncControlNamespace.NormalizeInstallationId(value.InstallationId);
        if (value.StorageVersion != CurrentStorageVersion)
            throw new InvalidDataException("The retained Rewind storage version is not supported by this build.");
        if (installationId is not null && !string.Equals(normalized, installationId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The retained Rewind storage reference belongs to another installation identity.");
        return new SyncRewindStorageReference(normalized, value.StorageVersion);
    }
}

// One shared normalizer for every Rewind session, applied on creation and on import. The authority
// reader validates typed rows through the same rules, so a malformed row can never become an empty
// active session or permission to resume.
public static class SyncRewindState
{
    public static string PathKey(string relativePath) => PilotPath.CanonicalComparisonKey(relativePath);

    internal static string StatusLiteral(SyncRewindStatus status) => status switch
    {
        SyncRewindStatus.AwaitingDropbox => "awaiting_dropbox",
        SyncRewindStatus.PreviewReady => "preview_ready",
        SyncRewindStatus.Applying => "applying",
        SyncRewindStatus.AppliedPaused => "applied_paused",
        SyncRewindStatus.Resumed => "resumed",
        SyncRewindStatus.Blocked => "blocked",
        SyncRewindStatus.PartiallyApplied => "partially_applied",
        SyncRewindStatus.ApplyStopped => "apply_stopped",
        SyncRewindStatus.EffectUnknown => "effect_unknown",
        SyncRewindStatus.ApplyBlocked => "apply_blocked",
        _ => throw new InvalidDataException("An unsupported Rewind session status was supplied.")
    };

    internal static SyncRewindStatus ParseStatus(string value) => value switch
    {
        "awaiting_dropbox" => SyncRewindStatus.AwaitingDropbox,
        "preview_ready" => SyncRewindStatus.PreviewReady,
        "applying" => SyncRewindStatus.Applying,
        "applied_paused" => SyncRewindStatus.AppliedPaused,
        "resumed" => SyncRewindStatus.Resumed,
        "blocked" => SyncRewindStatus.Blocked,
        "partially_applied" => SyncRewindStatus.PartiallyApplied,
        "apply_stopped" => SyncRewindStatus.ApplyStopped,
        "effect_unknown" => SyncRewindStatus.EffectUnknown,
        "apply_blocked" => SyncRewindStatus.ApplyBlocked,
        _ => throw new InvalidDataException("A retained Rewind session status is not recognized.")
    };

    internal static string PathStatusLiteral(SyncRewindPathStatus status) => status switch
    {
        SyncRewindPathStatus.Pending => "pending",
        SyncRewindPathStatus.Planned => "planned",
        SyncRewindPathStatus.Completed => "completed",
        SyncRewindPathStatus.Conflict => "conflict",
        SyncRewindPathStatus.Blocked => "blocked",
        SyncRewindPathStatus.Stopped => "stopped",
        SyncRewindPathStatus.EffectUnknown => "effect_unknown",
        _ => throw new InvalidDataException("An unsupported Rewind path status was supplied.")
    };

    internal static SyncRewindPathStatus ParsePathStatus(string value) => value switch
    {
        "pending" => SyncRewindPathStatus.Pending,
        "planned" => SyncRewindPathStatus.Planned,
        "completed" => SyncRewindPathStatus.Completed,
        "conflict" => SyncRewindPathStatus.Conflict,
        "blocked" => SyncRewindPathStatus.Blocked,
        "stopped" => SyncRewindPathStatus.Stopped,
        "effect_unknown" => SyncRewindPathStatus.EffectUnknown,
        _ => throw new InvalidDataException("A retained Rewind path status is not recognized.")
    };

    internal static bool IsTerminal(SyncRewindPathStatus status) =>
        status is SyncRewindPathStatus.Completed or SyncRewindPathStatus.Conflict;

    // The exact retained validation that previously lived in SyncProductStateStore.Normalize. It is
    // shared so one rule set governs import, creation and the authority reader.
    public static SyncRewindSession Normalize(SyncRewindSession session, IReadOnlySet<string> retainedPairIds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(retainedPairIds);
        if (session.SessionId.Length != 32 || session.SessionId.Any(character => !char.IsAsciiHexDigit(character)) ||
            string.IsNullOrWhiteSpace(session.ConnectionId) || !retainedPairIds.Contains(session.PairId) ||
            string.IsNullOrWhiteSpace(session.LocalCheckpointScanId) ||
            string.IsNullOrWhiteSpace(session.DropboxCheckpointScanId) ||
            string.IsNullOrWhiteSpace(session.DropboxCheckpointCursor) || !Enum.IsDefined(session.Status))
            throw new InvalidDataException("A retained Rewind checkpoint is incomplete.");
        var preview = (session.Preview ?? []).Select(item => item with
        {
            RelativePath = PilotPath.NormalizeRelative(item.RelativePath)
        }).OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (preview.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != preview.Length)
            throw new InvalidDataException("A Rewind preview contains duplicate paths.");
        var previewPaths = preview.Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var progress = (session.PathProgress ?? []).Select(item => item with
        {
            RelativePath = PilotPath.NormalizeRelative(item.RelativePath),
            Detail = item.Detail?.Trim() ?? string.Empty,
            OperationId = string.IsNullOrWhiteSpace(item.OperationId) ? null : item.OperationId.Trim()
        }).OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        if (progress.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            progress.Length || progress.Any(item => !previewPaths.Contains(item.RelativePath) ||
                                                    !Enum.IsDefined(item.Status)))
            throw new InvalidDataException("A Rewind apply checkpoint is invalid or outside its exact preview.");
        var previewAbsent = string.IsNullOrWhiteSpace(session.LocalPreviewScanId);
        var dropboxPreviewAbsent = string.IsNullOrWhiteSpace(session.DropboxPreviewScanId);
        if (previewAbsent != dropboxPreviewAbsent)
            throw new InvalidDataException("A Rewind preview generation is only half identified.");
        return session with
        {
            ConnectionId = session.ConnectionId.Trim(),
            PairName = session.PairName.Trim(),
            DropboxRoot = PilotPath.NormalizeDropboxRoot(session.DropboxRoot),
            Preview = preview,
            OperationGroupId = string.IsNullOrWhiteSpace(session.OperationGroupId)
                ? null
                : session.OperationGroupId.Trim(),
            PathProgress = progress
        };
    }

    public static IReadOnlyList<SyncRewindSession> NormalizeAll(IEnumerable<SyncRewindSession> sessions,
        IReadOnlySet<string> retainedPairIds)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var normalized = sessions.Select(session => Normalize(session, retainedPairIds))
            .OrderByDescending(item => item.CheckpointUtc).ToArray();
        if (normalized.Select(item => item.SessionId).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new InvalidDataException("A Rewind session identity is retained more than once.");
        if (normalized.GroupBy(item => item.PairId, StringComparer.Ordinal)
            .Any(group => group.Count(item => item.Status is not SyncRewindStatus.Resumed) > 1))
            throw new InvalidDataException("A pair retains more than one active Rewind checkpoint.");
        return normalized;
    }

    // The full normalized canonical identity of a legacy collection. Import completion is decided on
    // exact semantic equality of this digest, never on a path or timestamp coincidence.
    public static string CanonicalDigest(IEnumerable<SyncRewindSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var canonical = sessions.OrderBy(item => item.SessionId, StringComparer.Ordinal).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    // A session's reviewed path set is immutable once its effect group is admitted.
    internal static bool PreviewIsReplaceable(SyncRewindStatus status) =>
        status is SyncRewindStatus.AwaitingDropbox or SyncRewindStatus.PreviewReady or SyncRewindStatus.Blocked;
}
