using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

// EPS1: the one shared immutable snapshot of the ordered endpoints behind an activity or action record.
//
// Retained history needs the endpoint context as it was AT THE TIME OF THE EVENT. A single legacy
// ConnectionId cannot describe a local/local pair or two cloud accounts, and a currently selected or
// primary account can never substitute for the account a historical event actually used. This type is
// the exact description of the pair's two ordered endpoints: registered kind and provider, the known
// connection, verified namespace and native folder identity, the recorded root path, and the
// compatibility port the existing endpoint layout assigns to that side.
//
// It is presentation evidence ONLY. It authorizes no effect, performs no account-store, credential,
// callback or provider access, consults no currently selected account, copies no arbitrary extension
// JSON, and is never re-derived from the pair's current mutable definition. Unknown stays absent rather
// than guessed. The nine ordered provider combinations are configuration inputs to one helper, never
// nine execution or presentation engines.

/// <summary>One ordered side of a captured endpoint context.</summary>
public sealed record SyncEndpointContextSide(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] SyncPairSide Side,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] SyncEndpointKind Kind,
    string CompatibilityPort,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Provider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConnectionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NamespaceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FolderId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FolderPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LocalPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? LocalVolumeSerialNumber = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ulong? LocalRootFileId = null)
{
    /// <summary>True when this side is a local folder rather than a cloud endpoint.</summary>
    [JsonIgnore] public bool IsLocal => Kind == SyncEndpointKind.Local;

    /// <summary>True when this cloud side records the account it was reached through.</summary>
    [JsonIgnore] public bool HasRecordedAccount => ConnectionId is not null;
}

/// <summary>
/// The exact immutable endpoint context of one pair at the moment a record was created. Descriptive
/// only: no consumer may treat it as authorization, and no consumer may substitute a currently selected
/// account for the account recorded here (or absent here).
/// </summary>
public sealed record SyncEndpointContext(
    string PairId,
    string CapturedName,
    SyncEndpointContextSide Left,
    SyncEndpointContextSide Right)
{
    /// <summary>The existing authority identifier bound: 160 characters with no control characters.</summary>
    public const int MaximumIdentifierLength = 160;

    /// <summary>The existing retained activity path bound.</summary>
    public const int MaximumPathLength = 32768;

    /// <summary>The existing presentation text bound the captured pair name is reduced to.</summary>
    public const int MaximumNameLength = SyncCliSanitize.MaximumMessageLength;

    // The two retained compatibility side tags, as the existing physical endpoint layout names them: the
    // local endpoint of a local-containing pair occupies the engine's local port and its cloud endpoint the
    // remote port; two cloud endpoints keep the retained logical left (local) / right (dropbox) order.
    private const string LocalPort = SyncAuthorityCloudSides.LogicalLeft;
    private const string RemotePort = SyncAuthorityCloudSides.LogicalRight;

    /// <summary>
    /// Captures the ordered endpoint descriptions of one pair. The pair's own typed endpoint document is
    /// the source for an explicit pair; the saved legacy fields are read only when their meaning is proven
    /// (a pair with no document means left local and right cloud). Nothing here reads an account store, a
    /// credential, a provider payload, the currently selected account or any arbitrary extension member.
    /// A pair whose declared endpoints cannot be described safely is refused rather than guessed, and the
    /// caller's existing append failure/gap behaviour follows.
    /// </summary>
    public static SyncEndpointContext Capture(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var (left, right) = OrderedRecords(pair);
        var ports = Ports(left.Kind, right.Kind);
        var context = new SyncEndpointContext(pair.Id, SyncActivityText.Safe(pair.Name),
            Describe(SyncPairSide.Left, left, ports.Left), Describe(SyncPairSide.Right, right, ports.Right));
        var reasons = Validate(context, pair.Id);
        if (reasons.Count != 0)
            throw new InvalidDataException(
                "The pair's declared endpoints cannot be captured as presentation context: " +
                string.Join("; ", reasons));
        return context;
    }

    /// <summary>
    /// The safe reasons a supplied context cannot be retained with its record, or an empty list when it
    /// can. This is deliberately a bounded safe reason rather than the frozen SyncEndpointError taxonomy:
    /// that taxonomy describes the endpoint/pair contract, while this is presentation capture.
    /// </summary>
    public static IReadOnlyList<string> Validate(SyncEndpointContext? context, string? pairId)
    {
        var reasons = new List<string>();
        if (context is null) return reasons;
        if (!string.Equals(context.PairId, pairId, StringComparison.Ordinal))
            reasons.Add("The endpoint context names a different folder pair than the record it belongs to.");
        RequireIdentifier(reasons, context.PairId, "pair id");
        RequireBounded(reasons, context.CapturedName, MaximumNameLength, "captured pair name");
        if (context.Left is null || context.Right is null)
        {
            reasons.Add("An endpoint context requires both ordered sides.");
            return reasons;
        }
        if (context.Left.Side != SyncPairSide.Left || context.Right.Side != SyncPairSide.Right)
            reasons.Add("An endpoint context carries its ordered sides as left then right.");
        RequireSide(reasons, context.Left, "left");
        RequireSide(reasons, context.Right, "right");
        var ports = Ports(context.Left.Kind, context.Right.Kind);
        if (!string.Equals(context.Left.CompatibilityPort, ports.Left, StringComparison.Ordinal) ||
            !string.Equals(context.Right.CompatibilityPort, ports.Right, StringComparison.Ordinal))
            reasons.Add("The compatibility ports are not the ports the shared endpoint layout assigns to this ordered combination.");
        if (string.Equals(context.Left.CompatibilityPort, context.Right.CompatibilityPort, StringComparison.Ordinal))
            reasons.Add("The two ordered sides must carry two distinct compatibility ports.");
        return reasons;
    }

    /// <summary>
    /// The append-side rule: an activity record validates its own context before anything is retained, so
    /// foreign, malformed or locally assigned cloud metadata can never reach the append-only history.
    /// </summary>
    public static void RequireRetainable(SyncEndpointContext? context, string? pairId)
    {
        var reasons = Validate(context, pairId);
        if (reasons.Count != 0)
            throw new ArgumentException(
                "The endpoint context cannot be retained with this activity record: " +
                string.Join("; ", reasons), nameof(context));
    }

    /// <summary>
    /// The compatibility port the existing endpoint layout assigns to one ordered side. A local-containing
    /// pair uses the existing physical layout, so a right-side local folder reverses the two sides; two
    /// same-class endpoints keep the retained order. The port is a compatibility tag only: it licenses no
    /// NTFS call and no provider-specific behaviour.
    /// </summary>
    public static string CompatibilityPort(SyncEndpointKind left, SyncEndpointKind right, SyncPairSide side) =>
        side == SyncPairSide.Left ? Ports(left, right).Left : Ports(left, right).Right;

    private static (string Left, string Right) Ports(SyncEndpointKind left, SyncEndpointKind right) =>
        right == SyncEndpointKind.Local && left != SyncEndpointKind.Local
            ? (RemotePort, LocalPort)
            : (LocalPort, RemotePort);

    private static (SyncEndpointRecord Left, SyncEndpointRecord Right) OrderedRecords(SyncPair pair)
    {
        if (pair.EndpointDocument is { } document)
        {
            // An explicit document is the only source of truth for an explicit pair, and an incomplete or
            // unsupported one fails closed as an explicit pair rather than falling back to legacy fields.
            if (document.Version != SyncEndpointSchema.SymmetricEndpoints || document.Left is null ||
                document.Right is null)
                throw new InvalidDataException(
                    "The saved endpoint document is not a complete symmetric left/right document, so it is never read as a legacy pair.");
            var validation = SyncEndpointValidation.Combine(SyncEndpointContracts.ValidateRecord(document.Left),
                SyncEndpointContracts.ValidateRecord(document.Right));
            if (!validation.IsValid)
                throw new InvalidDataException(
                    "The saved endpoint document is not a usable pair: " + validation.Describe());
            return (document.Left, document.Right);
        }

        // A pair with no endpoint document is a legacy pair: its saved fields mean left local, right cloud.
        if (!SyncEndpoints.TryMapProvider(pair.Provider, out var kind) ||
            Definition(kind) is not { ProviderId: not null } definition)
            throw new InvalidDataException(
                "The saved pair names an endpoint provider this build does not register, so its endpoints are never guessed.");
        var identity = SyncLocalEndpointIdentity.From(pair.LocalVolumeSerialNumber, pair.LocalRootFileId);
        var local = new SyncEndpointRecord(SyncEndpointKind.Local, pair.LocalRoot,
            identity?.VolumeSerialNumber, identity?.RootFileId);
        var cloud = new SyncEndpointRecord(kind, Provider: definition.ProviderId,
            ConnectionId: pair.ConnectionId, FolderId: pair.DropboxFolderId, FolderPath: pair.DropboxRoot);
        return (local, cloud);
    }

    private static SyncEndpointContextSide Describe(SyncPairSide side, SyncEndpointRecord record, string port)
    {
        if (record.IsLocal)
        {
            var identity = SyncLocalEndpointIdentity.From(record.LocalVolumeSerialNumber ?? 0,
                record.LocalRootFileId ?? 0);
            return new SyncEndpointContextSide(side, SyncEndpointKind.Local, port,
                LocalPath: Blank(record.LocalPath) ? null : record.LocalPath,
                LocalVolumeSerialNumber: identity?.VolumeSerialNumber, LocalRootFileId: identity?.RootFileId);
        }
        if (Definition(record.Kind) is not { ProviderId: not null } definition)
            throw new InvalidDataException("A captured endpoint names an endpoint kind this build does not register.");
        return new SyncEndpointContextSide(side, record.Kind, port,
            Provider: Blank(record.Provider) ? definition.ProviderId : record.Provider,
            ConnectionId: Blank(record.ConnectionId) ? null : record.ConnectionId,
            NamespaceId: Blank(record.NamespaceId) ? null : record.NamespaceId,
            FolderId: Blank(record.FolderId) ? null : record.FolderId,
            FolderPath: Blank(record.FolderPath) ? null : record.FolderPath);
    }

    private static void RequireSide(List<string> reasons, SyncEndpointContextSide side, string label)
    {
        if (Definition(side.Kind) is not { } definition)
        {
            reasons.Add("The " + label + " endpoint kind is not a registered endpoint kind.");
            return;
        }
        if (side.Kind == SyncEndpointKind.Local)
        {
            if (side.Provider is not null || side.ConnectionId is not null || side.NamespaceId is not null ||
                side.FolderId is not null || side.FolderPath is not null)
                reasons.Add("A local " + label +
                    " endpoint cannot carry a provider, cloud account, namespace or cloud folder identity.");
            if (side.LocalPath is null)
                reasons.Add("A local " + label + " endpoint requires its recorded local folder path.");
            else
            {
                // The existing local-path rule runs only once the value is inside the retained bounds, so an
                // unbounded value is reported as the bound violation it is rather than as a path failure.
                if (RequireBounded(reasons, side.LocalPath, MaximumPathLength, "local " + label + " folder path"))
                    foreach (var refusal in SyncEndpoints.ValidateLocal(side.LocalPath).Refusals)
                        reasons.Add("The local " + label + " endpoint is not a usable local folder: " + refusal.Reason);
            }
            if (side.LocalVolumeSerialNumber is not null || side.LocalRootFileId is not null)
            {
                if (side.LocalVolumeSerialNumber is null || side.LocalRootFileId is null ||
                    side.LocalVolumeSerialNumber == 0 || side.LocalRootFileId == 0)
                    reasons.Add("A local " + label +
                        " endpoint records both parts of its local folder identity or neither; an incomplete identity is never invented.");
            }
            return;
        }

        if (side.LocalPath is not null || side.LocalVolumeSerialNumber is not null || side.LocalRootFileId is not null)
            reasons.Add("A cloud " + label + " endpoint cannot carry a local folder path or local folder identity.");
        RequireIdentifier(reasons, side.Provider, label + " provider");
        if (definition.ProviderId is null || string.IsNullOrWhiteSpace(side.Provider))
            reasons.Add("A cloud " + label + " endpoint requires the registered provider literal of its endpoint kind.");
        else if (!string.Equals(side.Provider, definition.ProviderId, StringComparison.OrdinalIgnoreCase))
            reasons.Add("The " + label + " endpoint provider is not the registered provider of its endpoint kind.");

        // A cloud account may be absent: a grandfathered legacy pair can save no account id, and that stays
        // unknown. It is never replaced by a currently selected, primary or first available account.
        RequireIdentifier(reasons, side.ConnectionId, label + " cloud account");
        RequireIdentifier(reasons, side.NamespaceId, label + " cloud namespace");
        RequireIdentifier(reasons, side.FolderId, label + " cloud folder identity");
        RequireBounded(reasons, side.FolderPath, MaximumPathLength, label + " recorded cloud root path");
        if (side.FolderId is null && side.FolderPath is null)
            reasons.Add("A cloud " + label +
                " endpoint requires the provider folder identity or the recorded root path of the folder it syncs.");
    }

    private static void RequireIdentifier(List<string> reasons, string? value, string label)
    {
        if (value is null) return;
        if (string.IsNullOrWhiteSpace(value))
        {
            reasons.Add("The " + label + " is blank; an unknown identity is absent rather than blank.");
            return;
        }
        RequireBounded(reasons, value, MaximumIdentifierLength, label);
    }

    /// <summary>True when the value is inside the declared retained bound and carries no control character.</summary>
    private static bool RequireBounded(List<string> reasons, string? value, int maximum, string label)
    {
        if (value is null) return true;
        if (value.Length > maximum)
        {
            reasons.Add("The " + label + " exceeds the retained presentation bound of " + maximum + " characters.");
            return false;
        }
        else if (value.Any(char.IsControl))
        {
            reasons.Add("The " + label + " contains a control character, which presentation metadata never carries.");
            return false;
        }
        return true;
    }

    private static SyncEndpointProviderDefinition? Definition(SyncEndpointKind kind) =>
        SyncEndpointProviders.All.FirstOrDefault(provider => provider.Kind == kind);

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
