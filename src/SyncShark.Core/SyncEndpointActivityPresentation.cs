namespace Massdex.SyncShark;

// EPS3A correction: the ONE shared Core action-presentation resolver.
//
// Orientation comes ONLY from the recorded SyncEndpointContext and its CompatibilityPort. Logical left is NOT
// assumed to be the local port: a local-containing pair with its local folder on the right records the local
// port on the right, so any hardcoded orientation is wrong. When no context is available the row is neutral.
// The typed SyncAuthorityOperationKind selects the verb. No display or translated string is parsed, and no
// provider PAIR is switched on.

/// <summary>One retained activity phase, preserved as an exact code rather than a translated sentence.</summary>
public enum SyncActivityPhase
{
    Unknown,
    Planned,
    Running,
    Completed,
    Blocked,
    Conflict,
    Cancelled,
    EffectUnknown,
    Internal,
    Superseded
}

/// <summary>The one neutral category of an activity row. Configuration stays out of Files.</summary>
public enum SyncActivityCategory
{
    UserFile,
    UserFolder,
    Internal,
    Configuration
}

/// <summary>Which recorded side an action affects.</summary>
public enum SyncActivitySide
{
    Neutral,
    Left,
    Right
}

/// <summary>
/// The Core semantic icon kind. It is deliberately independent of any WPF resource: a file action carries
/// File, a folder or directory action carries Folder, and an internal or non-file event carries Internal.
/// The UI consumer maps these through its own existing image sources; no resource key is invented here and
/// StatusIconStyle is a preference string, never an icon or template.
/// </summary>
public enum SyncActivityIconKind
{
    File,
    Folder,
    Internal
}

/// <summary>
/// One display-only activity row. Affected, Source and Target name the recorded sides so two same-provider
/// accounts remain distinguishable, and neither is ever resolved from a currently selected account.
/// </summary>
public sealed record SyncEndpointActivityRow(
    string Title,
    string Action,
    string Status,
    SyncActivityCategory Category,
    SyncActivityIconKind IconKind,
    string? IconTemplateKey,
    SyncActivityPhase Phase,
    SyncActivitySide Affected,
    SyncActivitySide Source,
    SyncActivitySide Target,
    bool IsNeutral);

public static class SyncEndpointActivityPresentation
{
    private const string UnknownAction = "Unknown";

    /// <summary>
    /// Resolves one activity row from the nullable typed kind, the recorded endpoint context and the retained
    /// phase. An unknown enum, kind or context stays neutral and never throws or fabricates an orientation.
    /// </summary>
    public static SyncEndpointActivityRow Resolve(SyncAuthorityOperationKind? kind, SyncEndpointContext? context,
        SyncActivityPhase phase, string? relativePath = null, bool isDirectory = false)
    {
        var category = CategoryFor(kind, isDirectory);
        var (affected, source, target) = Sides(kind, context);
        var neutral = affected == SyncActivitySide.Neutral;
        return new SyncEndpointActivityRow(
            TitleFor(kind, category, relativePath),
            ActionFor(kind, context),
            StatusFor(phase),
            category,
            IconKindFor(category),
            null,
            phase,
            affected,
            source,
            target,
            neutral);
    }

    /// <summary>
    /// The exact recorded sides of one operation, from the recorded CompatibilityPort only. Create and update
    /// name a source and a target; delete, move and the rest name only the affected endpoint and leave the
    /// source neutral rather than inventing one.
    /// </summary>
    public static (SyncActivitySide Affected, SyncActivitySide Source, SyncActivitySide Target) Sides(
        SyncAuthorityOperationKind? kind, SyncEndpointContext? context)
    {
        if (kind is null || context is null || SyncEndpointContext.Validate(context, context.PairId).Count != 0)
            return (SyncActivitySide.Neutral, SyncActivitySide.Neutral,
            SyncActivitySide.Neutral);
        // The target is the recorded side carrying the local port for a Local-kind operation, and the recorded
        // side carrying the dropbox port for a Remote-kind operation. The opposite recorded side is the source.
        var targetPort = TargetPort(kind.Value);
        if (targetPort is null) return (SyncActivitySide.Neutral, SyncActivitySide.Neutral, SyncActivitySide.Neutral);
        var affected = SideCarrying(context, targetPort);
        if (affected == SyncActivitySide.Neutral) return (SyncActivitySide.Neutral, SyncActivitySide.Neutral,
            SyncActivitySide.Neutral);
        var directional = kind is SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote
            or SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote;
        return directional
            ? (affected, Opposite(affected), affected)
            : (affected, SyncActivitySide.Neutral, SyncActivitySide.Neutral);
    }

    /// <summary>
    /// The exact verb of one typed kind. Only create and update choose Upload, Download or Copy from the actual
    /// source and target; delete archives and move moves within the affected endpoint, and a missing or unknown
    /// kind is Unknown and never a standing label such as Synced.
    /// </summary>
    public static string ActionFor(SyncAuthorityOperationKind? kind, SyncEndpointContext? context) => kind switch
    {
        SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote or
            SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote => TransferVerb(kind.Value, context),
        SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote => "Archive",
        SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote => "Move",
        SyncAuthorityOperationKind.CrossPairMove => "Move",
        SyncAuthorityOperationKind.ResolveConflict => "Resolve conflict",
        SyncAuthorityOperationKind.AllocateControl => "Prepare sync storage",
        SyncAuthorityOperationKind.Cleanup => "Clean up",
        SyncAuthorityOperationKind.Reconcile => "Check sync state",
        _ => UnknownAction
    };

    private static string TransferVerb(SyncAuthorityOperationKind kind, SyncEndpointContext? context)
    {
        var (_, source, target) = Sides(kind, context);
        if (context is null || source == SyncActivitySide.Neutral || target == SyncActivitySide.Neutral)
            // The typed operation still proves a transfer. Older history may lack its ordered
            // endpoint snapshot, so do not invent an upload/download direction or hide the known action.
            return "Transfer";
        var from = source == SyncActivitySide.Left ? context.Left : context.Right;
        var to = target == SyncActivitySide.Left ? context.Left : context.Right;
        if (from.IsLocal == to.IsLocal) return "Copy";
        return from.IsLocal ? "Upload" : "Download";
    }

    /// <summary>The recorded compatibility port the target of one kind lives on, or null when unproved.</summary>
    public static string? TargetPort(SyncAuthorityOperationKind kind) => kind switch
    {
        SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.UpdateLocal or
            SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.MoveLocal =>
            SyncAuthorityCloudSides.LogicalLeft,
        SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateRemote or
            SyncAuthorityOperationKind.DeleteRemote or SyncAuthorityOperationKind.MoveRemote =>
            SyncAuthorityCloudSides.LogicalRight,
        _ => null
    };

    private static SyncActivitySide SideCarrying(SyncEndpointContext context, string port)
    {
        if (string.Equals(context.Left.CompatibilityPort, port, StringComparison.Ordinal))
            return SyncActivitySide.Left;
        if (string.Equals(context.Right.CompatibilityPort, port, StringComparison.Ordinal))
            return SyncActivitySide.Right;
        return SyncActivitySide.Neutral;
    }

    private static SyncActivitySide Opposite(SyncActivitySide side) => side switch
    {
        SyncActivitySide.Left => SyncActivitySide.Right,
        SyncActivitySide.Right => SyncActivitySide.Left,
        _ => SyncActivitySide.Neutral
    };

    private static SyncActivityCategory CategoryFor(SyncAuthorityOperationKind? kind, bool isDirectory) => kind switch
    {
        SyncAuthorityOperationKind.AllocateControl => SyncActivityCategory.Configuration,
        SyncAuthorityOperationKind.Cleanup or
        SyncAuthorityOperationKind.Reconcile or SyncAuthorityOperationKind.ResolveConflict or
            SyncAuthorityOperationKind.CrossPairMove or null => SyncActivityCategory.Internal,
        SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote or
            SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote or
            SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote or
            SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote =>
            isDirectory ? SyncActivityCategory.UserFolder : SyncActivityCategory.UserFile,
        _ => SyncActivityCategory.Internal
    };

    /// <summary>File and folder actions keep their semantic icon; internal events keep the branded status icon.</summary>
    private static SyncActivityIconKind IconKindFor(SyncActivityCategory category) => category switch
    {
        SyncActivityCategory.UserFolder => SyncActivityIconKind.Folder,
        SyncActivityCategory.UserFile => SyncActivityIconKind.File,
        _ => SyncActivityIconKind.Internal
    };

    private static string TitleFor(SyncAuthorityOperationKind? kind, SyncActivityCategory category,
        string? relativePath) => !string.IsNullOrWhiteSpace(relativePath)
        ? relativePath!
        : kind switch
            {
                SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote => "New file",
                SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote => "Changed file",
                SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote => "Removed file",
                SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote => "Moved file",
                SyncAuthorityOperationKind.ResolveConflict => "Conflict resolution",
                SyncAuthorityOperationKind.CrossPairMove => "Moved between pairs",
                SyncAuthorityOperationKind.AllocateControl => "Sync storage setup",
                SyncAuthorityOperationKind.Cleanup => "Storage cleanup",
                SyncAuthorityOperationKind.Reconcile => "Sync state check",
                _ => "Sync activity"
            };

    /// <summary>One honest status sentence. It never claims an outcome the phase does not establish.</summary>
    public static string StatusFor(SyncActivityPhase phase) => phase switch
    {
        SyncActivityPhase.Planned => "Ready to run",
        SyncActivityPhase.Running => "In progress",
        SyncActivityPhase.Completed => "Completed",
        SyncActivityPhase.Blocked => "Blocked; the retained state is preserved",
        SyncActivityPhase.Conflict => "Needs a decision",
        SyncActivityPhase.Cancelled => "Cancelled; the retained state is preserved",
        SyncActivityPhase.EffectUnknown => "Outcome not yet known; it will be reconciled",
        SyncActivityPhase.Internal => "Recorded",
        SyncActivityPhase.Superseded => "Superseded by a newer plan",
        _ => "Recorded"
    };

    /// <summary>
    /// The one central phase mapping for a typed status. It maps the TYPE, never a display string; every
    /// unrecognised status stays Unknown rather than being inferred or presented as complete.
    /// </summary>
    public static SyncActivityPhase PhaseFor(SyncAuthorityOperationStatus status) => status switch
    {
        SyncAuthorityOperationStatus.Planned or SyncAuthorityOperationStatus.Ready =>
            SyncActivityPhase.Planned,
        SyncAuthorityOperationStatus.Leased or SyncAuthorityOperationStatus.Validating =>
            SyncActivityPhase.Running,
        SyncAuthorityOperationStatus.AllocationAuthorized => SyncActivityPhase.Planned,
        SyncAuthorityOperationStatus.Staged => SyncActivityPhase.Running,
        SyncAuthorityOperationStatus.EffectIssuing => SyncActivityPhase.Running,
        SyncAuthorityOperationStatus.EffectObserved => SyncActivityPhase.Running,
        SyncAuthorityOperationStatus.Reconciling => SyncActivityPhase.Running,
        SyncAuthorityOperationStatus.Verifying => SyncActivityPhase.Running,
        SyncAuthorityOperationStatus.Committed => SyncActivityPhase.Running,
        SyncAuthorityOperationStatus.CleanupPending => SyncActivityPhase.Running,
        SyncAuthorityOperationStatus.Completed => SyncActivityPhase.Completed,
        SyncAuthorityOperationStatus.Superseded => SyncActivityPhase.Superseded,
        SyncAuthorityOperationStatus.Blocked => SyncActivityPhase.Blocked,
        SyncAuthorityOperationStatus.EffectUnknown => SyncActivityPhase.EffectUnknown,
        SyncAuthorityOperationStatus.Cancelled => SyncActivityPhase.Cancelled,
        _ => SyncActivityPhase.Unknown
    };

    // Database codes and JSON enum names are different representations. Never parse display text,
    // and never reinterpret a stored activity phase as an operation status.
    public static SyncAuthorityOperationKind? KindForRetainedCode(string? code)
    {
        if (code is null) return null;
        try { return SyncAuthorityNames.OperationKind(code); }
        catch (InvalidDataException) { return null; }
    }

    public static SyncActivityPhase? PhaseForRetainedCode(string? code)
    {
        if (code is null) return null;
        try { return PhaseFor(SyncAuthorityNames.OperationStatus(code)); }
        catch (InvalidDataException) { return null; }
    }

    internal static SyncAuthorityOperationKind? KindForJsonName(string? name) =>
        DefinedJsonName<SyncAuthorityOperationKind>(name);

    internal static SyncActivityPhase? PhaseForJsonName(string? name) =>
        DefinedJsonName<SyncActivityPhase>(name);

    private static T? DefinedJsonName<T>(string? name) where T : struct, Enum =>
        name is not null && Enum.TryParse<T>(name, out var value) &&
        Enum.IsDefined(value) && string.Equals(Enum.GetName(value), name, StringComparison.Ordinal)
            ? value : null;

    internal static string? OperationCodeForJsonName(object? value) =>
        value is string name && KindForJsonName(name) is { } kind
            ? SyncAuthorityNames.OperationKind(kind) : null;

    // A lifecycle row describes its own immutable event, never the operation's mutable current status.
    internal static SyncActivityPhase? PhaseForHistoryEvent(string? eventKind) => eventKind switch
    {
        "operation_completed" => PhaseFor(SyncAuthorityOperationStatus.Completed),
        "unissued_lease_recovered" => SyncActivityPhase.Superseded,
        "cleanup_started" or "effect_committed" or "auxiliary_effect_committed" or
            "deletion_committed" or "effect_reconciled_and_committed" or
            "intermediate_effect_verified" or "intermediate_effect_reconciled" =>
            SyncActivityPhase.Running,
        _ => null
    };

    internal static SyncActivityPhase? PhaseForDecision(string? code) => code switch
    {
        "conflict" => SyncActivityPhase.Conflict,
        "blocked" => SyncActivityPhase.Blocked,
        _ => null
    };
}
