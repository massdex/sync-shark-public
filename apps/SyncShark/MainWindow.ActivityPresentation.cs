namespace Massdex.SyncShark;

// P37: one centralised provider derivation for the whole activity surface. A row's provider is
// resolved only from the recorded endpoint context or connection of the event, never its current pair
// or display text, so a Google account keeps its real name, a
// Dropbox account keeps its real name, and a row with no recorded provider evidence keeps a
// neutral Cloud label instead of a fabricated Dropbox one.
internal static class ActivityCloud
{
    // The neutral label for a row whose provider was not recorded. It is deliberately not a
    // provider name, so an unrecorded historical row can never be presented as Dropbox.
    internal const string UnrecordedLabel = "Cloud";

    // Connection ids are storage and protocol identities, so classifying them is metadata rather
    // than display-text parsing. Each namespace is followed by exactly 24 lowercase hex digits,
    // which is the same provider-aware identity rule core already applies to account scopes.
    internal static string? ProviderForConnectionId(string? connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || !PauseScopeRules.IsExactConnectionId(connectionId)) return null;
        return connectionId.StartsWith("gdrive-connection-", StringComparison.Ordinal)
            ? SyncProviders.GoogleDrive : SyncProviders.Dropbox;
    }

    internal static string Label(string? provider) =>
        SyncProviders.IsKnown(provider) ? SyncProviders.DisplayName(provider) : UnrecordedLabel;

    internal static string LabelForConnectionId(string? connectionId) => Label(ProviderForConnectionId(connectionId));

    // The recorded location fields name the provider, so the field label and its value can never
    // disagree about which cloud the row belongs to.
    internal static string LocationFieldLabel(string? provider) => "Recorded " + Label(provider) + " Location:";
}

public partial class MainWindow
{
    // One retained-row factory serves Activity, Home and the tray. Current pair/account selection
    // is not historical evidence and must not rewrite an event after a pair is rebound or removed.
    internal static ActivityRow RetainedActivityRow(SyncAuthorityActivityProjection entry)
    {
        var context = entry.EndpointContext;
        if (context is not null && SyncEndpointContext.Validate(context, entry.PairId).Count != 0)
            context = null;
        var endpoints = context is null ? Array.Empty<SyncEndpointContextSide>() : new[] { context.Left, context.Right };
        var local = endpoints.Where(side => side.IsLocal).ToArray();
        var cloud = endpoints.Where(side => !side.IsLocal).ToArray();
        var label = context is null ? string.IsNullOrWhiteSpace(entry.ConnectionId)
            ? "Not recorded" : ActivityCloud.LabelForConnectionId(entry.ConnectionId)
            : string.Join(" ↔ ", endpoints.Select(ActivityRow.EndpointLabel));
        return new ActivityRow(entry.OccurredUtc.ToLocalTime().ToString("O"), entry.RelativePath,
            entry.Outcome, entry.Reason, label, entry.PairId, context?.CapturedName ?? "Folder pair",
            entry.OperationId, entry.ItemKind, entry.FileType, entry.SizeBytes,
            context is null ? entry.LocalRoot : local.Length == 1 ? local[0].LocalPath : null,
            context is null ? entry.DropboxRoot : cloud.Length == 1 ? cloud[0].FolderPath : null,
            entry.EventId, entry.Sequence, entry.Categories, entry.Severity, entry.Entity, entry.RunId,
            entry.ConnectionId, context, entry.OperationKind, entry.Phase, entry.DecisionCode);
    }

    // The exact current legacy connection id, or null. This is not historical event evidence.
    internal string? ExactPairConnectionId(string? pairId)
    {
        if (string.IsNullOrWhiteSpace(pairId)) return null;
        var pair = _pairSet.Pairs.FirstOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
        if (pair is null) return null;
        return string.IsNullOrWhiteSpace(pair.ConnectionId) ? null : pair.ConnectionId;
    }

    // Missing historical account evidence remains missing. Live producers capture their own pair.
    internal string? ExactActivityConnectionId(string? recordedConnectionId, string? pairId) =>
        string.IsNullOrWhiteSpace(recordedConnectionId) ? null : recordedConnectionId;

    internal string? ActivityProviderFor(ActivityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.RecordedCloudProvider;
    }

    // The one provider label every activity detail, copied text, context action, tooltip and
    // accessibility name consumes.
    internal string ActivityCloudName(ActivityRow row) => ActivityCloud.Label(ActivityProviderFor(row));

    // The row derives its action sentence from its own connection id. Describing it through the
    // exact resolved binding therefore reuses that single wording rule instead of repeating it.
    internal ActivityRow WithExactProviderBinding(ActivityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var connectionId = ExactActivityConnectionId(row.ConnectionId, row.PairId);
        return string.Equals(connectionId, row.ConnectionId, StringComparison.Ordinal)
            ? row : row with { ConnectionId = connectionId };
    }

}
