namespace Massdex.SyncShark;

// E2 reusable endpoint presentation.
//
// One place decides how a Local, Dropbox or Google Drive endpoint is named, described and marked in
// every pair surface, so a provider label is never hardcoded at a call site and the two sides of the
// compact pair editor are presented symmetrically. This module is pure presentation: it decides no
// sync semantics, performs no validation (the frozen E1 contract owns that) and touches no saved
// state. Icons are named by the existing resource template keys only; no brand artwork is created.

/// <summary>
/// Which side of a pair an endpoint occupies. The side is a position in the ordered pair, not a
/// direction: stored directions refer to these logical positions regardless of endpoint kind.
/// </summary>
public enum SyncPairSide
{
    Left = 0,
    Right = 1
}

/// <summary>One selectable endpoint kind exactly as the pair editor presents it.</summary>
public sealed record SyncEndpointKindChoice(SyncEndpointKind Kind, string ShortLabel, string SectionTitle,
    string IconTemplateKey, string Description);

/// <summary>One selectable direction exactly as the pair editor presents it.</summary>
public sealed record SyncDirectionChoice(SyncMode Mode, string Label, string Description);

/// <summary>
/// The single reusable presentation of endpoint kinds, sides, accounts, directions and endpoint
/// values. Provider-specific wording lives here once; every consumer asks this module instead of
/// writing "Dropbox" into a surface that may be paired with Google Drive or a local folder.
/// </summary>
public static class SyncEndpointPresentation
{
    /// <summary>The local endpoint kind, presented by the existing folder mark.</summary>
    public const string LocalIconTemplateKey = "FolderIconTemplate";

    /// <summary>The Dropbox endpoint mark already used by the connection and pair rows.</summary>
    public const string DropboxIconTemplateKey = "DropboxIconTemplate";

    /// <summary>The Google Drive endpoint mark already used by the connection and pair rows.</summary>
    public const string GoogleDriveIconTemplateKey = "GoogleDriveIconTemplate";

    private static readonly SyncEndpointKindChoice[] KindChoiceList =
    [
        new(SyncEndpointKind.Local, "Local", "This computer", LocalIconTemplateKey,
            "A folder on this computer, identified by its own local volume and root folder identity."),
        new(SyncEndpointKind.Dropbox, SyncEndpointProviders.Get(SyncEndpointKind.Dropbox).DisplayName,
            SyncEndpointProviders.Get(SyncEndpointKind.Dropbox).DisplayName, DropboxIconTemplateKey,
            "A Dropbox folder on one chosen Dropbox account."),
        new(SyncEndpointKind.GoogleDrive, SyncEndpointProviders.Get(SyncEndpointKind.GoogleDrive).DisplayName,
            SyncEndpointProviders.Get(SyncEndpointKind.GoogleDrive).DisplayName, GoogleDriveIconTemplateKey,
            "A Google Drive folder on one chosen Google account.")
    ];

    private static readonly SyncDirectionChoice[] DirectionChoiceList =
    [
        new(SyncMode.TwoWay, "Two-way",
            "Changes on either side sync to the other. Conflicting edits are preserved for review."),
        new(SyncMode.UploadOnly, "Left to right",
            "Changes on the left endpoint copy to the right endpoint. Right-side changes are not copied back."),
        new(SyncMode.DownloadOnly, "Right to left",
            "Changes on the right endpoint copy to the left endpoint. Left-side changes are not copied back.")
    ];

    /// <summary>The three endpoint kinds in presentation order.</summary>
    public static IReadOnlyList<SyncEndpointKindChoice> KindChoices => KindChoiceList;

    /// <summary>The three directions in presentation order, which is the persisted SyncMode order.</summary>
    public static IReadOnlyList<SyncDirectionChoice> DirectionChoices => DirectionChoiceList;

    /// <summary>The presentation of one kind.</summary>
    public static SyncEndpointKindChoice Choice(SyncEndpointKind kind) =>
        KindChoiceList.SingleOrDefault(choice => choice.Kind == kind)
        ?? throw new ArgumentOutOfRangeException(nameof(kind), "This endpoint has no registered presentation.");

    /// <summary>The compact kind name used in a control, for example "Google Drive".</summary>
    public static string ShortLabel(SyncEndpointKind kind) => Choice(kind).ShortLabel;

    /// <summary>The existing resource template key for a kind; never a newly drawn icon.</summary>
    public static string IconTemplateKey(SyncEndpointKind kind) => Choice(kind).IconTemplateKey;

    /// <summary>The side name, which is stable and independent of the provider chosen for it.</summary>
    public static string SideLabel(SyncPairSide side) => side == SyncPairSide.Right ? "Right" : "Left";

    /// <summary>The section heading for one side, for example "Left - Google Drive".</summary>
    public static string SectionTitle(SyncPairSide side, SyncEndpointKind kind) =>
        SideLabel(side) + " \u2014 " + Choice(kind).SectionTitle;

    /// <summary>Public wording for an engine port, resolved from this pair's ordered endpoints.</summary>
    public static string PortTitle(SyncEndpointContext context, string port)
    {
        var side = context.Left.CompatibilityPort == port ? context.Left
            : context.Right.CompatibilityPort == port ? context.Right
            : throw new ArgumentException("Unknown endpoint port.", nameof(port));
        return SectionTitle(side.Side, side.Kind);
    }

    /// <summary>The compact caption under one side's heading.</summary>
    public static string SectionCaption(SyncPairSide side, SyncEndpointKind kind) => kind switch
    {
        SyncEndpointKind.Local => "Paired folder on this computer",
        _ => "Paired folder in " + Choice(kind).ShortLabel
    };

    /// <summary>What the side's folder control does, which depends on the chosen kind.</summary>
    public static string FolderControlHint(SyncEndpointKind kind) => kind switch
    {
        SyncEndpointKind.Local => "Browse this computer and tick the local folder to pair.",
        _ => "Choose the " + Choice(kind).ShortLabel +
             " account, then choose the exact folder to pair in that account."
    };

    /// <summary>
    /// The established compact account naming: the provider email first with the holder's display
    /// name in brackets. Kept identical to the Connections and Folder Pairs forms so two accounts that
    /// share a display name stay distinguishable.
    /// </summary>
    public static string AccountLabel(string? providerName, string? displayName, string? email)
    {
        var mail = (email ?? string.Empty).Trim();
        var holder = (displayName ?? string.Empty).Trim();
        if (mail.Length > 0)
            return holder.Length > 0 && !string.Equals(mail, holder, StringComparison.OrdinalIgnoreCase)
                ? mail + " (" + holder + ")" : mail;
        if (holder.Length > 0) return holder;
        var provider = (providerName ?? string.Empty).Trim();
        return provider.Length > 0 ? provider + " account" : "Account";
    }

    /// <summary>The user-facing label of one direction.</summary>
    public static string DirectionLabel(SyncMode direction) => direction switch
    {
        SyncMode.UploadOnly => DirectionChoiceList[1].Label,
        SyncMode.DownloadOnly => DirectionChoiceList[2].Label,
        _ => DirectionChoiceList[0].Label
    };

    /// <summary>The direction's explanation, written in terms of the two sides.</summary>
    public static string DirectionDescription(SyncMode direction) => direction switch
    {
        SyncMode.UploadOnly => DirectionChoiceList[1].Description,
        SyncMode.DownloadOnly => DirectionChoiceList[2].Description,
        _ => DirectionChoiceList[0].Description
    };

    /// <summary>The kind a persisted record names, or the local kind when the record is absent.</summary>
    public static SyncEndpointKind KindOf(SyncEndpointRecord? record) => record?.Kind ?? SyncEndpointKind.Local;

    /// <summary>
    /// W2-C: the ordered heading of one endpoint-hierarchy side, for example "Left - Google Drive" or
    /// "Right - This computer". Consumers render a hierarchy panel from this rather than assuming that the
    /// first ordered endpoint is the physical local folder.
    /// </summary>
    public static string HierarchyTitle(SyncEndpointContextSide side)
    {
        ArgumentNullException.ThrowIfNull(side);
        return SectionTitle(side.Side, side.Kind);
    }

    /// <summary>
    /// W2-C: one ordered endpoint's own location for a hierarchy surface: the local path of a local
    /// endpoint, or the recorded cloud folder path or stable identity of a cloud endpoint. A local endpoint
    /// never reports a cloud path and a cloud endpoint never reports a local path.
    /// </summary>
    public static string HierarchyLocation(SyncEndpointContextSide side)
    {
        ArgumentNullException.ThrowIfNull(side);
        if (side.IsLocal)
            return string.IsNullOrWhiteSpace(side.LocalPath) ? "path not recorded" : side.LocalPath!;
        if (!string.IsNullOrWhiteSpace(side.FolderPath)) return side.FolderPath!;
        return string.IsNullOrWhiteSpace(side.FolderId) ? "folder identity not recorded" : side.FolderId!;
    }

    /// <summary>
    /// One endpoint as a reviewer reads it: the provider-aware kind first, then the local folder path
    /// or the cloud folder path or id. Never returns a provider name for a local endpoint and never
    /// returns a local path for a cloud endpoint.
    /// </summary>
    public static string EndpointDisplay(SyncEndpointRecord? record)
    {
        if (record is null) return "Not chosen";
        var name = ShortLabel(record.Kind);
        if (record.Kind == SyncEndpointKind.Local)
            return string.IsNullOrWhiteSpace(record.LocalPath) ? name : name + ": " + record.LocalPath;
        var folder = !string.IsNullOrWhiteSpace(record.FolderPath) ? record.FolderPath
            : !string.IsNullOrWhiteSpace(record.FolderId) ? record.FolderId
            : "folder not chosen";
        return name + ": " + folder;
    }

    /// <summary>One endpoint as a reviewer reads it.</summary>
    public static string EndpointDisplay(SyncEndpoint? endpoint) =>
        endpoint is null ? "Not chosen" : EndpointDisplay(SyncEndpointRecord.From(endpoint));

    /// <summary>Both sides of a pair on one line, left first, with the direction label.</summary>
    public static string PairSummary(SyncPairEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return EndpointDisplay(endpoints.Left) + "  \u21c4  " + EndpointDisplay(endpoints.Right) +
               "  (" + DirectionLabel(endpoints.Direction) + ")";
    }

    /// <summary>
    /// The reason this build cannot yet bind one ordered kind combination, or null when it can. Today
    /// the binding pipeline accepts exactly the existing ordered shape - a local left endpoint and a
    /// cloud right endpoint - so every other combination stays visible and selectable in the editor but
    /// is refused with this reason instead of being pretended to work. The runtime lanes extend the
    /// supported set; this list is presentation honesty, not a sync rule.
    /// </summary>
    public static string? BindingSupportReason(SyncEndpointKind left, SyncEndpointKind right) =>
        SyncEndpointPairRuntime.BindingSupportReason(left, right);

    /// <summary>True when this build can bind the ordered combination.</summary>
    public static bool BindingSupported(SyncEndpointKind left, SyncEndpointKind right) =>
        BindingSupportReason(left, right) is null;

    /// <summary>
    /// The exact remaining runtime hook for one ordered combination, or null when nothing is missing.
    /// This names the missing lane instead of leaving the integration owner to interpret the refusal:
    /// two local endpoints need the local-to-local run executor, and each cloud combination needs its
    /// own cloud endpoint binding, split by which side the cloud endpoint occupies.
    /// </summary>
    public static string? BindingRuntimeHook(SyncEndpointKind left, SyncEndpointKind right)
    {
        if (BindingSupportReason(left, right) is null) return null;
        if (left == SyncEndpointKind.Local) return "E3 local-to-local run executor";
        return right == SyncEndpointKind.Local
            ? "E4 reversed local/cloud endpoint binding"
            : "E4 cloud-to-cloud endpoint binding";
    }
}
