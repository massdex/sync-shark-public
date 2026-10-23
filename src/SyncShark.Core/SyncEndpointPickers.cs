namespace Massdex.SyncShark;

// E2 per-side endpoint pickers.
//
// Each side of the pair editor owns its own folder selection. This module holds the production
// decisions for that job: which accounts one side may offer, which browse result is still current for
// the side that asked for it, and what applying a result does to the draft. The window only routes a
// control to these methods, so the behaviour proved by the focused checks is the behaviour the editor
// runs; nothing here is a parallel decision path.
//
// The folder listing is the existing product transport (IDropboxFolderBrowserService) bound to the
// side's own account. No second transport, provider contract or shared left/right selection field is
// introduced.

/// <summary>What applying one browse result did to the draft.</summary>
public enum SyncEndpointPickerState
{
    /// <summary>The result belongs to the side's current kind and account and was applied.</summary>
    Applied = 0,

    /// <summary>
    /// The side changed after this browse started, so nothing was written and the newer choice is kept.
    /// A stale result is not a user-facing validation error; it is a result the editor must not apply.
    /// </summary>
    Stale = 1,

    /// <summary>The browse was still current but produced nothing usable; the side keeps what it had.</summary>
    Refused = 2
}

/// <summary>
/// One folder-browse attempt on one side. The session that owned the side issues the ticket when the
/// browse starts, so a result arriving after the side's kind, account or session changed can be
/// recognised as belonging to an older choice instead of overwriting the newer one.
///
/// A caller cannot construct a ticket, so a result can never claim a currency it was not issued with.
/// </summary>
public sealed record SyncEndpointPickerTicket
{
    internal SyncEndpointPickerTicket(object sessionIdentity, long editorGeneration, SyncPairSide side, SyncEndpointKind kind,
        string? connectionId, long sequence)
    {
        SessionIdentity = sessionIdentity;
        EditorGeneration = editorGeneration;
        Side = side;
        Kind = kind;
        ConnectionId = connectionId;
        Sequence = sequence;
    }

    internal object SessionIdentity { get; }

    /// <summary>The editor session the browse was started in.</summary>
    public long EditorGeneration { get; }

    /// <summary>The side the browse was started for.</summary>
    public SyncPairSide Side { get; }

    /// <summary>The kind that side held when the browse started.</summary>
    public SyncEndpointKind Kind { get; }

    /// <summary>The account that side held when the browse started, for a cloud side.</summary>
    public string? ConnectionId { get; }

    /// <summary>
    /// The browse ordinal on this side. A later browse on the same side supersedes an earlier one, so
    /// two overlapping browses cannot both write.
    /// </summary>
    public long Sequence { get; }
}

/// <summary>The outcome of applying one browse result to one side.</summary>
public sealed record SyncEndpointPickerOutcome(SyncEndpointPickerState State,
    SyncEndpointValidation Validation, string Reason)
{
    /// <summary>True when the side's draft identity is now exactly the chosen folder.</summary>
    public bool Applied => State == SyncEndpointPickerState.Applied;

    /// <summary>A result that belongs to a superseded choice; the draft is untouched.</summary>
    public static SyncEndpointPickerOutcome Stale(string reason) =>
        new(SyncEndpointPickerState.Stale, SyncEndpointValidation.Accepted, reason);

    /// <summary>A current result that is not a usable folder; the draft is untouched.</summary>
    public static SyncEndpointPickerOutcome Refused(SyncEndpointError error, string reason) =>
        new(SyncEndpointPickerState.Refused,
            new SyncEndpointValidation(new[] { new SyncEndpointRefusal(error, reason) }), reason);

    /// <summary>The validation outcome of a current choice that has already been applied.</summary>
    internal static SyncEndpointPickerOutcome From(SyncEndpointValidation validation, string applied) =>
        validation.IsValid
            ? new(SyncEndpointPickerState.Applied, validation, applied)
            : new(SyncEndpointPickerState.Refused, validation, validation.Describe());
}

/// <summary>The outcome of listing one cloud side's folders through the existing transport.</summary>
public sealed record SyncEndpointBrowseResult(SyncEndpointPickerState State,
    IReadOnlyList<DropboxFolderEntry> Folders, string Reason)
{
    /// <summary>True when the listing belongs to the side's current choice and may be shown.</summary>
    public bool Applied => State == SyncEndpointPickerState.Applied;
}

/// <summary>
/// The accounts one cloud side may offer. A side is offered only accounts of the provider it names: a
/// Dropbox account is never a candidate for a Google Drive side, and the two sides of a pair are
/// independent, so the same provider on both sides still means two separate account choices.
/// </summary>
public static class SyncEndpointAccountChoices
{
    /// <summary>
    /// The accounts offered for one endpoint kind, in the installation's own account order and with each
    /// verified connection appearing once. A local side is offered no account at all, and an account
    /// whose connection id is empty is never offered because it cannot identify an account.
    /// </summary>
    public static IReadOnlyList<SyncVerifiedAccountBinding> ForKind(SyncEndpointKind kind,
        IEnumerable<SyncVerifiedAccountBinding>? accounts)
    {
        if (kind == SyncEndpointKind.Local || accounts is null)
            return Array.Empty<SyncVerifiedAccountBinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offered = new List<SyncVerifiedAccountBinding>();
        foreach (var binding in accounts)
        {
            if (binding is null) continue;
            var connectionId = (binding.ConnectionId ?? string.Empty).Trim();
            if (connectionId.Length == 0) continue;
            if (!ActionKind(binding, kind)) continue;
            if (!seen.Add(connectionId)) continue;
            offered.Add(string.Equals(binding.ConnectionId, connectionId, StringComparison.Ordinal)
                ? binding
                : binding with { ConnectionId = connectionId });
        }
        return offered;
    }

    /// <summary>
    /// The account a side keeps without the user choosing one, or null when nothing unambiguous exists.
    /// Only the exact account the side already retains is returned: "the first available account" is
    /// never selected automatically, and two accounts that share a display name are still two accounts.
    /// </summary>
    public static SyncVerifiedAccountBinding? Retained(SyncEndpointKind kind, string? retainedConnectionId,
        IEnumerable<SyncVerifiedAccountBinding>? accounts)
    {
        if (string.IsNullOrWhiteSpace(retainedConnectionId)) return null;
        var wanted = retainedConnectionId.Trim();
        foreach (var binding in ForKind(kind, accounts))
            if (string.Equals(binding.ConnectionId, wanted, StringComparison.Ordinal)) return binding;
        return null;
    }

    private static bool ActionKind(SyncVerifiedAccountBinding binding, SyncEndpointKind kind) =>
        SyncEndpoints.TryMapProvider(binding.Provider, out var bindingKind) && bindingKind == kind;
}

/// <summary>
/// One validated draft extraction: the complete ordered endpoints and direction the E3/E4 integration
/// owner consumes, together with exactly why saving is or is not possible right now.
/// </summary>
public sealed record SyncPairDraft(
    SyncPairEndpoints? Endpoints,
    SyncMode Direction,
    SyncPairEditIntent Intent,
    SyncEndpointValidation Validation,
    string? UnsupportedBindingReason,
    string? RuntimeHook)
{
    // The editor's opening snapshot, not the latest runtime pair looked up at save.
    public string? EditingPairId { get; init; }
    public long? ExpectedPolicyRevision { get; init; }

    /// <summary>
    /// True only when both endpoints exist, validation accepts them and this build can bind the ordered
    /// combination. An unsupported combination stays authorable and reviewable but is never saved, and
    /// is never presented as a working pair.
    /// </summary>
    public bool CanSave => Endpoints is not null && Validation.IsValid && UnsupportedBindingReason is null;

    /// <summary>The ordered pair's left endpoint, or null when the draft is not extractable yet.</summary>
    public SyncEndpoint? Left => Endpoints?.Left;

    /// <summary>The ordered pair's right endpoint, or null when the draft is not extractable yet.</summary>
    public SyncEndpoint? Right => Endpoints?.Right;
}

/// <summary>
/// One side of the pair editor, bound to the session that owns that side. Every browse starts and ends
/// here, so a result can only ever be applied to the side, kind and account it was started for, and the
/// two sides never share a selection field.
/// </summary>
public sealed class SyncEndpointSidePicker
{
    private readonly SyncPairEditorSession _session;
    private readonly SyncPairSide _side;

    internal SyncEndpointSidePicker(SyncPairEditorSession session, SyncPairSide side)
    {
        _session = session;
        _side = side;
    }

    /// <summary>Which side of the pair this picker owns.</summary>
    public SyncPairSide Side => _side;

    /// <summary>The kind this side currently names.</summary>
    public SyncEndpointKind Kind => _session.KindOf(_side);

    /// <summary>The account this side currently names, or null.</summary>
    public string? ConnectionId => _session.Draft(_side).ConnectionId;

    /// <summary>True when this side is a cloud folder rather than a folder on this computer.</summary>
    public bool IsCloud => !_session.Draft(_side).IsLocal;

    /// <summary>
    /// True when this side can open its account-bound remote folder browser. A cloud side with no account
    /// chosen cannot browse yet, because a folder identity belongs to the account that issued it.
    /// </summary>
    public bool CanBrowseCloud => IsCloud && !string.IsNullOrWhiteSpace(ConnectionId);

    /// <summary>The exact reason this side cannot browse its cloud folders yet, or null when it can.</summary>
    public string? CloudBrowseRefusal => IsCloud
        ? "Choose the " + SyncEndpointPresentation.ShortLabel(Kind) +
          " account for this side before choosing a folder inside it."
        : "This side is a folder on this computer, so it is browsed on this computer.";

    /// <summary>
    /// True when this side's selected path can be opened. An empty path is not clickable: there is no
    /// folder to open yet, so the control does nothing instead of opening an invented location.
    /// </summary>
    public bool CanOpenSelectedFolder => _session.CanOpenSelectedFolder(_side);

    /// <summary>The folder this side currently names, as one reviewer-readable line.</summary>
    public string Display => SyncEndpointPresentation.EndpointDisplay(_session.RecordOf(_side));

    /// <summary>Starts one browse for this side and returns the ticket its result must carry.</summary>
    public SyncEndpointPickerTicket BeginPick() => _session.BeginFolderPick(_side);

    /// <summary>
    /// Lists this side's remote folders through the existing account-bound transport. A listing that
    /// finished after the side changed is reported stale and must not be shown as the side's folders.
    /// </summary>
    public async Task<SyncEndpointBrowseResult> ListFoldersAsync(SyncEndpointPickerTicket ticket,
        IDropboxFolderBrowserService transport, string? parentFolderId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (ticket.Side != _side || !_session.IsCurrentPick(ticket))
            return Stale("This side changed after the folder list was requested, so the older list is not used.");
        if (!CanBrowseCloud)
            return new SyncEndpointBrowseResult(SyncEndpointPickerState.Refused,
                Array.Empty<DropboxFolderEntry>(), CloudBrowseRefusal ?? "Choose a cloud account before browsing.");
        try
        {
            var page = await transport.ListFoldersAsync(parentFolderId, cancellationToken);
            if (!_session.IsCurrentPick(ticket))
                return Stale("This side changed while its folders were loading, so the older list is not used.");
            return new SyncEndpointBrowseResult(SyncEndpointPickerState.Applied, page.Folders,
                "Showing " + SyncEndpointPresentation.ShortLabel(Kind) + " folders.");
        }
        catch (OperationCanceledException)
        {
            return new SyncEndpointBrowseResult(SyncEndpointPickerState.Refused,
                Array.Empty<DropboxFolderEntry>(),
                "The folder browse was cancelled; this side keeps the folder it already names.");
        }
        catch (Exception error)
        {
            return new SyncEndpointBrowseResult(SyncEndpointPickerState.Refused,
                Array.Empty<DropboxFolderEntry>(),
                "The " + SyncEndpointPresentation.ShortLabel(Kind) + " folders could not be read: " +
                error.Message);
        }
    }

    /// <summary>
    /// Applies the exact cloud folder the user picked. A folder is applied only while the ticket is still
    /// the side's current choice, so a result for an account the side no longer names is discarded.
    /// </summary>
    public SyncEndpointPickerOutcome ApplyCloudFolder(SyncEndpointPickerTicket ticket,
        DropboxFolderEntry? folder) => ApplyCloudFolder(ticket, folder?.FolderId, folder?.Path);

    /// <summary>Applies a cloud folder by its stable id and displayed path.</summary>
    public SyncEndpointPickerOutcome ApplyCloudFolder(SyncEndpointPickerTicket ticket, string? folderId,
        string? folderPath)
    {
        if (ticket.Side != _side || !_session.IsCurrentPick(ticket))
            return SyncEndpointPickerOutcome.Stale(
                "This side changed after the folder was chosen, so the older choice is not applied.");
        if (!IsCloud)
            return SyncEndpointPickerOutcome.Refused(SyncEndpointError.EndpointKindMismatch,
                "This side is a folder on this computer, so a cloud folder cannot be chosen for it.");
        if (string.IsNullOrWhiteSpace(folderId) && string.IsNullOrWhiteSpace(folderPath))
            return SyncEndpointPickerOutcome.Refused(SyncEndpointError.MissingCloudFolder,
                "No folder was chosen, so this side keeps the folder it already names.");
        if (string.IsNullOrWhiteSpace(ConnectionId))
            return SyncEndpointPickerOutcome.Refused(SyncEndpointError.MissingCloudAccount,
                "Choose the account for this side before choosing a folder inside it.");
        var validation = _session.SetCloudFolder(_side, folderId, folderPath);
        return SyncEndpointPickerOutcome.From(validation,
            "Chosen " + SyncEndpointPresentation.EndpointDisplay(_session.RecordOf(_side)) + ".");
    }

    /// <summary>
    /// Applies the folder the user chose on this computer. The folder's own volume and root file identity
    /// is read through the product's existing directory lease, so a missing, reparse or otherwise
    /// unverifiable folder is refused instead of being recorded as a usable endpoint.
    /// </summary>
    public SyncEndpointPickerOutcome ApplyLocalFolder(SyncEndpointPickerTicket ticket, string? localPath)
    {
        if (ticket.Side != _side || !_session.IsCurrentPick(ticket))
            return SyncEndpointPickerOutcome.Stale(
                "This side changed after the folder was chosen, so the older choice is not applied.");
        if (IsCloud)
            return SyncEndpointPickerOutcome.Refused(SyncEndpointError.EndpointKindMismatch,
                "This side is a cloud folder, so a folder on this computer cannot be chosen for it.");
        if (string.IsNullOrWhiteSpace(localPath))
            return SyncEndpointPickerOutcome.Refused(SyncEndpointError.MissingLocalPath,
                "No folder on this computer was chosen, so this side keeps the folder it already names.");
        SyncLocalEndpointIdentity? identity;
        try
        {
            using var lease = PilotDirectoryLease.Open(localPath);
            identity = SyncLocalEndpointIdentity.From(lease.Identity.VolumeSerialNumber, lease.Identity.FileId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return SyncEndpointPickerOutcome.Refused(SyncEndpointError.InvalidLocalPath,
                "The chosen folder on this computer could not be verified: " + error.Message);
        }
        if (identity is null || !identity.IsComplete)
            return SyncEndpointPickerOutcome.Refused(SyncEndpointError.InvalidLocalIdentity,
                "The chosen folder on this computer has no complete volume and root file identity, so it " +
                "cannot be recorded as an endpoint.");
        var validation = _session.SetLocalFolder(_side, localPath, identity);
        return SyncEndpointPickerOutcome.From(validation,
            "Chosen " + SyncEndpointPresentation.EndpointDisplay(_session.RecordOf(_side)) + ".");
    }

    private SyncEndpointBrowseResult Stale(string reason) =>
        new(SyncEndpointPickerState.Stale, Array.Empty<DropboxFolderEntry>(), reason);
}
