namespace Massdex.SyncShark;

// E2 symmetric pair authoring.
//
// One editable draft of both endpoints of a pair, with Local, Dropbox or Google Drive chosen
// independently on the left and on the right. The session owns only editor state: it builds the
// immutable E1 value model, delegates every acceptance and refusal decision to the frozen E1 contract,
// and performs no binding, discovery, transfer or persistence of its own. Nothing here is activated
// against saved state by itself.

/// <summary>What saving the current draft would do to the pair it was opened from.</summary>
public enum SyncPairEditIntent
{
    /// <summary>The draft is a new pair; there is no retained pair to compare against.</summary>
    NewPair = 0,

    /// <summary>Both endpoint identities are retained exactly; only policy or presentation changed.</summary>
    SelectionOnly = 1,

    /// <summary>At least one endpoint identity changed, so the pair must re-approve its first sync.</summary>
    Rebind = 2
}

/// <summary>
/// The mutable draft of one side. Every member is exactly what the E1 record persists; a draft is
/// allowed to be incomplete while the user is choosing, and incompleteness is reported by validation
/// rather than repaired by a guess.
/// </summary>
public sealed class SyncEndpointDraft
{
    internal SyncEndpointDraft(SyncEndpointKind kind) => Kind = kind;

    public SyncEndpointKind Kind { get; internal set; }
    public string? LocalPath { get; internal set; }
    public SyncLocalEndpointIdentity? LocalIdentity { get; internal set; }
    public string? ConnectionId { get; internal set; }
    public string? NamespaceId { get; internal set; }
    public string? FolderId { get; internal set; }
    public string? FolderPath { get; internal set; }

    public bool IsLocal => Kind == SyncEndpointKind.Local;
    public bool IsCloud => !IsLocal;
    public bool HasLocalFolder => !string.IsNullOrWhiteSpace(LocalPath);
    public bool HasCloudAccount => !string.IsNullOrWhiteSpace(ConnectionId);
    public bool HasCloudFolder => !string.IsNullOrWhiteSpace(FolderId) || !string.IsNullOrWhiteSpace(FolderPath);

    /// <summary>True when nothing at all has been chosen for this side yet.</summary>
    public bool IsEmpty => IsLocal
        ? !HasLocalFolder && LocalIdentity is null
        : !HasCloudAccount && !HasCloudFolder && string.IsNullOrWhiteSpace(NamespaceId);

    internal void ClearLocalIdentity()
    {
        LocalPath = null;
        LocalIdentity = null;
    }

    internal void ClearCloudIdentity(bool clearAccount)
    {
        FolderId = null;
        FolderPath = null;
        if (!clearAccount) return;
        ConnectionId = null;
        NamespaceId = null;
    }

    /// <summary>
    /// The draft's own validation, using the same rules the frozen contract applies to a draft: a
    /// local side needs a bounded local path, a cloud side needs its account and its folder.
    /// </summary>
    public SyncEndpointValidation Validate() => IsLocal
        ? SyncEndpoints.ValidateLocal(LocalPath)
        : SyncEndpoints.ValidateCloud(SyncEndpoints.ProviderLiteral(Kind), ConnectionId, FolderId, FolderPath);
}

/// <summary>
/// The symmetric pair editor's authoring session. It is deliberately independent of any window: the
/// same session drives the UI draft, and every proof of the editor's behaviour is a proof of this
/// class, not of a control's wiring.
/// </summary>
public sealed class SyncPairEditorSession
{
    private readonly SyncEndpointDraft[] _drafts;
    private readonly object _sessionIdentity = new();

    // One browse ordinal and one side-bound picker per side. The two sides never share either, which is
    // what stops a left browse from writing to the right side or the other way round.
    private readonly long[] _pickSequences = [0, 0];
    private readonly SyncEndpointSidePicker[] _pickers;

    private SyncPairEditorSession(SyncEndpointDraft left, SyncEndpointDraft right, SyncMode direction,
        string? editingPairId, long editorGeneration)
    {
        _drafts = [left, right];
        _pickers = [new SyncEndpointSidePicker(this, SyncPairSide.Left),
            new SyncEndpointSidePicker(this, SyncPairSide.Right)];
        Direction = direction;
        EditingPairId = editingPairId;
        Generation = editorGeneration;
        IsActive = true;
    }

    /// <summary>
    /// The identity of this editor session. A browse result carries the generation it started under, so a
    /// result that arrives after this editor was cancelled, closed or replaced can be recognised and left
    /// unapplied instead of overwriting the newer editor's choice.
    /// </summary>
    public long Generation { get; }

    /// <summary>
    /// A new draft. The left side starts on this computer and the right side starts on Dropbox because
    /// that is the ordered shape the existing binding pipeline retains; both sides are freely
    /// selectable and neither choice constrains the other.
    /// </summary>
    public static SyncPairEditorSession ForNewPair() => ForNewPair(0);

    /// <summary>A new draft bound to the editor session identity that owns it.</summary>
    public static SyncPairEditorSession ForNewPair(long editorGeneration) =>
        new(new SyncEndpointDraft(SyncEndpointKind.Local), new SyncEndpointDraft(SyncEndpointKind.Dropbox),
            SyncMode.TwoWay, null, editorGeneration);

    /// <summary>
    /// Opens the draft for a retained pair by reading it through the frozen compatibility mapping. The
    /// saved pair object is never written: a legacy pair renders from its own retained fields, and an
    /// invalid or legacy-ambiguous pair opens as an empty draft for the affected side so the user sees
    /// the actionable refusal instead of an invented identity.
    /// </summary>
    public static SyncPairEditorSession ForExistingPair(SyncPair pair, out SyncEndpointValidation validation)
        => ForExistingPair(pair, 0, out validation);

    /// <summary>
    /// Opens the draft for a retained pair, bound to the editor session identity that owns it. Each
    /// side's own kind, account and folder come from the retained pair. For legacy records only, an
    /// exact verified account binding can supply the namespace that the older format did not store.
    /// </summary>
    public static SyncPairEditorSession ForExistingPair(SyncPair pair, long editorGeneration,
        out SyncEndpointValidation validation, IEnumerable<SyncVerifiedAccountBinding>? verifiedAccounts = null)
    {
        ArgumentNullException.ThrowIfNull(pair);
        var mapped = SafeMap(pair);
        validation = mapped.Validation;
        var left = new SyncEndpointDraft(SyncEndpointKind.Local);
        var right = new SyncEndpointDraft(SyncEndpointKind.Dropbox);
        if (mapped.Endpoints is { } endpoints)
        {
            Apply(left, endpoints.Left);
            Apply(right, endpoints.Right);
            if (validation.IsValid && pair.EndpointDocument is null && verifiedAccounts is not null)
            {
                var accounts = verifiedAccounts.ToArray();
                CompleteLegacyNamespace(left, accounts);
                CompleteLegacyNamespace(right, accounts);
            }
        }
        else
        {
            // A pair that cannot be mapped still opens on the side the legacy shape itself names, so the
            // refusal is about the missing identity and not about a silently substituted provider.
            left.Kind = SyncEndpointKind.Local;
            if (SyncEndpoints.TryMapProvider(pair.Provider, out var kind))
            {
                right.Kind = kind;
                right.ConnectionId = string.IsNullOrWhiteSpace(pair.ConnectionId) ? null : pair.ConnectionId;
                right.FolderId = string.IsNullOrWhiteSpace(pair.DropboxFolderId) ? null : pair.DropboxFolderId;
                right.FolderPath = string.IsNullOrWhiteSpace(pair.DropboxRoot) ? null : pair.DropboxRoot;
            }
            if (!string.IsNullOrWhiteSpace(pair.LocalRoot)) left.LocalPath = pair.LocalRoot;
        }
        return new SyncPairEditorSession(left, right, pair.Direction, pair.Id, editorGeneration)
        {
            EditingPolicyRevision = pair.PolicyRevision
        };
    }

    private static void CompleteLegacyNamespace(SyncEndpointDraft draft,
        IReadOnlyList<SyncVerifiedAccountBinding> accounts)
    {
        if (draft.IsLocal || !string.IsNullOrWhiteSpace(draft.NamespaceId) ||
            string.IsNullOrWhiteSpace(draft.ConnectionId)) return;
        var matches = accounts.Where(account =>
            SyncEndpoints.TryMapProvider(account.Provider, out var kind) && kind == draft.Kind &&
            string.Equals(account.ConnectionId, draft.ConnectionId, StringComparison.Ordinal)).ToArray();
        // Never select a default account or guess between duplicate bindings. This only completes the
        // editor draft; Save still revalidates the current account, namespace and both folder identities.
        if (matches.Length == 1 && !string.IsNullOrWhiteSpace(matches[0].NamespaceId))
            draft.NamespaceId = matches[0].NamespaceId;
    }

    private static void Apply(SyncEndpointDraft draft, SyncEndpoint endpoint)
    {
        draft.Kind = endpoint.Kind;
        if (endpoint.IsLocal)
        {
            draft.LocalPath = endpoint.LocalPath;
            draft.LocalIdentity = endpoint.LocalIdentity;
            return;
        }
        draft.ConnectionId = endpoint.ConnectionId;
        draft.NamespaceId = endpoint.NamespaceId;
        draft.FolderId = endpoint.FolderId;
        draft.FolderPath = endpoint.FolderPath;
    }

    /// <summary>
    /// Reads a retained pair without ever throwing on its own malformed state. A saved local path that
    /// cannot form an absolute folder identity is reported as a refusal for the editor to show, instead
    /// of letting an exception escape from opening a pair.
    /// </summary>
    private static SyncLegacyEndpoints SafeMap(SyncPair pair)
    {
        try
        {
            var resolved = SyncEndpointPairRuntime.Resolve(pair, pair.EndpointDocument);
            return new SyncLegacyEndpoints(pair, resolved.Endpoints, resolved.Validation);
        }
        catch (ArgumentException)
        {
            return new SyncLegacyEndpoints(pair, null, new SyncEndpointValidation(
            [
                new SyncEndpointRefusal(SyncEndpointError.InvalidLocalPath,
                    "The saved local folder path cannot be resolved to an absolute folder, so this pair's endpoints cannot be read.")
            ]));
        }
        // The runtime refuses a retained local endpoint that carries no stable identity by throwing its own
        // typed refusal rather than an argument error. Retained malformed state must still never escape from
        // opening a pair, so that exact refusal becomes the editor's validation result - no identities are
        // invented and no other exception is absorbed.
        catch (SyncEndpointRuntimeRefusedException error)
        {
            return new SyncLegacyEndpoints(pair, null, error.Validation);
        }
    }

    /// <summary>The retained pair this draft edits, or null for a new pair.</summary>
    public string? EditingPairId { get; }
    public long? EditingPolicyRevision { get; private init; }

    /// <summary>False once the draft has been cancelled; a cancelled draft builds no pair.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The direction held separately from endpoint identity.</summary>
    public SyncMode Direction { get; private set; }

    public SyncEndpointDraft Draft(SyncPairSide side) => _drafts[(int)side];

    public SyncEndpointKind KindOf(SyncPairSide side) => Draft(side).Kind;

    /// <summary>
    /// Chooses the endpoint kind for one side. Changing the kind clears that side's incompatible draft
    /// identity and nothing else: the other side, the other side's account, the direction, the pair
    /// name and every policy setting stay exactly as they were. A kind that is already chosen is a
    /// no-op, so re-selecting it can never discard a completed selection.
    /// </summary>
    public void SetKind(SyncPairSide side, SyncEndpointKind kind)
    {
        var draft = Draft(side);
        if (draft.Kind == kind) return;
        ++_pickSequences[(int)side];
        if (kind == SyncEndpointKind.Local)
        {
            // Leaving a cloud kind: the account and folder of that provider cannot describe a local
            // folder. Any local folder already chosen on this side is retained.
            draft.ClearCloudIdentity(clearAccount: true);
        }
        else
        {
            // Entering or switching between cloud kinds: a local path is not a cloud folder, and an
            // account plus folder belong to one provider. The folder is always re-chosen, and the
            // account is always re-chosen too, because the only ways into this branch are from a local
            // side (which has no account) or from the other cloud provider (whose account does not
            // apply here).
            draft.ClearLocalIdentity();
            draft.ClearCloudIdentity(clearAccount: true);
        }
        draft.Kind = kind;
    }

    /// <summary>Chooses the local folder for a local side, with the stable identity when it is known.</summary>
    public SyncEndpointValidation SetLocalFolder(SyncPairSide side, string? localPath,
        SyncLocalEndpointIdentity? identity = null)
    {
        var draft = Draft(side);
        if (!draft.IsLocal)
            return Refuse(SyncEndpointError.EndpointKindMismatch,
                "This side is not a local folder, so a local folder cannot be chosen for it.");
        draft.ClearCloudIdentity(clearAccount: true);
        draft.LocalPath = string.IsNullOrWhiteSpace(localPath) ? null : localPath.Trim();
        draft.LocalIdentity = identity;
        ++_pickSequences[(int)side];
        return draft.Validate();
    }

    /// <summary>Chooses the local folder together with the stable identity the product verified for it.</summary>
    public SyncEndpointValidation SetLocalFolder(SyncPairSide side, string localPath, uint volumeSerialNumber,
        ulong rootFileId) => SetLocalFolder(side, localPath,
        SyncLocalEndpointIdentity.From(volumeSerialNumber, rootFileId));

    /// <summary>
    /// Chooses the verified account for a cloud side. The account's provider must be the kind the side
    /// already names; a Dropbox account can never be chosen for a Google Drive side or the other way
    /// round. Changing the account clears that side's folder, because a folder identity belongs to the
    /// account that issued it; choosing the same account again keeps the folder exactly as it was.
    /// </summary>
    public SyncEndpointValidation SetCloudAccount(SyncPairSide side, SyncVerifiedAccountBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var draft = Draft(side);
        if (draft.IsLocal)
            return Refuse(SyncEndpointError.EndpointKindMismatch,
                "This side is a local folder, so a cloud account cannot be chosen for it.");
        if (!SyncEndpoints.TryMapProvider(binding.Provider, out var kind) || kind != draft.Kind)
            return Refuse(SyncEndpointError.UnknownProvider,
                "The chosen account is not a " + SyncEndpointPresentation.ShortLabel(draft.Kind) +
                " account, so it cannot be used for this side.");
        var changed = !string.Equals(draft.ConnectionId, binding.ConnectionId, StringComparison.Ordinal) ||
                      !string.Equals(draft.NamespaceId, binding.NamespaceId, StringComparison.Ordinal);
        draft.ConnectionId = string.IsNullOrWhiteSpace(binding.ConnectionId) ? null : binding.ConnectionId;
        draft.NamespaceId = string.IsNullOrWhiteSpace(binding.NamespaceId) ? null : binding.NamespaceId;
        if (changed)
        {
            ++_pickSequences[(int)side];
            draft.ClearCloudIdentity(clearAccount: false);
        }
        // Choosing an account is valid before choosing its folder. Whole-draft validation still
        // prevents saving until that independent next step is complete.
        return draft.HasCloudAccount ? SyncEndpointValidation.Accepted :
            Refuse(SyncEndpointError.MissingCloudAccount, "Choose a verified account for this side.");
    }

    /// <summary>
    /// Chooses the folder inside the already chosen account. The folder and its path are retained
    /// exactly as the provider returned them; no display name is ever promoted into an identity.
    /// </summary>
    public SyncEndpointValidation SetCloudFolder(SyncPairSide side, string? folderId, string? folderPath)
    {
        var draft = Draft(side);
        if (draft.IsLocal)
            return Refuse(SyncEndpointError.EndpointKindMismatch,
                "This side is a local folder, so a cloud folder cannot be chosen for it.");
        draft.FolderId = string.IsNullOrWhiteSpace(folderId) ? null : folderId.Trim();
        draft.FolderPath = string.IsNullOrWhiteSpace(folderPath) ? null : folderPath.Trim();
        ++_pickSequences[(int)side];
        return draft.Validate();
    }

    /// <summary>
    /// Sets the direction. Direction is stored separately from endpoint identity, so this can never
    /// change which two endpoints the pair names, and changing it can never be a rebind.
    /// </summary>
    public void SetDirection(SyncMode direction) => Direction = direction;

    /// <summary>Refuses when no verified namespace separates two cloud sides that name the same folder.</summary>
    public SyncEndpointValidation Validate() => ValidateAgainstExisting(null);

    /// <summary>
    /// The draft's complete validation: each side's own identity, the pairwise rules (distinct
    /// endpoints, non-overlapping local roots, no cloud overlap) and, when supplied, the cross-pair
    /// rules against the pairs already retained. Every refusal is safe to show to the user.
    /// </summary>
    public SyncEndpointValidation ValidateAgainstExisting(IEnumerable<SyncPairEndpoints>? existingPairs)
    {
        var refusals = new List<SyncEndpointRefusal>();
        foreach (var side in new[] { SyncPairSide.Left, SyncPairSide.Right })
            refusals.AddRange(Draft(side).Validate().Refusals);
        if (!TryBuildEndpoints(out var left, out var right)) return new SyncEndpointValidation(refusals);
        var cloudOnly = !left!.IsLocal && !right!.IsLocal;
        refusals.AddRange(SyncCloudPairConfiguration.ValidateEndpointPair(left, right!).Refusals);
        if (existingPairs is not null)
            refusals.AddRange(cloudOnly
                ? SyncCloudPairConfiguration.ValidateAgainstRetainedPairs(
                    new SyncPairEndpoints(left, right!, Direction), existingPairs).Refusals
                : SyncEndpointContracts.ValidatePairAgainstExisting(left, right!, existingPairs).Refusals);
        return refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);
    }

    /// <summary>
    /// The persistence readiness of the draft: the draft validation plus the stable local identity a
    /// persisted local endpoint must carry. A draft may be valid for the editor while still not being
    /// persistable, which is exactly the case the user must resolve before saving.
    /// </summary>
    public SyncEndpointValidation ValidateForPersistence()
    {
        var refusals = new List<SyncEndpointRefusal>(Validate().Refusals);
        foreach (var side in new[] { SyncPairSide.Left, SyncPairSide.Right })
        {
            if (RecordOf(side) is { } record) refusals.AddRange(SyncEndpointContracts.ValidateRecord(record).Refusals);
        }
        return refusals.Count == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(refusals);
    }

    /// <summary>The completed ordered pair, or a refusal. A cancelled draft never builds a pair.</summary>
    public bool TryBuild(out SyncPairEndpoints? pair, out SyncEndpointValidation validation)
    {
        pair = null;
        if (!IsActive)
        {
            validation = Refuse(SyncEndpointError.MissingEndpointDocument,
                "This draft was cancelled, so it produces no pair.");
            return false;
        }
        validation = Validate();
        if (!validation.IsValid) return false;
        if (!TryBuildEndpoints(out var left, out var right)) return false;
        pair = new SyncPairEndpoints(left!, right!, Direction);
        return true;
    }

    /// <summary>The persisted record for one side, or null while that side is still incomplete.</summary>
    public SyncEndpointRecord? RecordOf(SyncPairSide side)
    {
        var draft = Draft(side);
        if (draft.IsLocal)
            return draft.HasLocalFolder
                ? new SyncEndpointRecord(SyncEndpointKind.Local, draft.LocalPath,
                    draft.LocalIdentity?.VolumeSerialNumber, draft.LocalIdentity?.RootFileId)
                : null;
        if (!draft.HasCloudAccount || !draft.HasCloudFolder) return null;
        return new SyncEndpointRecord(draft.Kind, Provider: SyncEndpoints.ProviderLiteral(draft.Kind),
            ConnectionId: draft.ConnectionId, NamespaceId: draft.NamespaceId,
            FolderId: draft.FolderId, FolderPath: draft.FolderPath);
    }

    /// <summary>
    /// Abandons the draft. Nothing was ever written, so cancelling simply produces no pair: the
    /// session keeps no retained identity and TryBuild refuses afterwards.
    /// </summary>
    public void Cancel()
    {
        IsActive = false;
        foreach (var draft in _drafts)
        {
            draft.ClearCloudIdentity(clearAccount: true);
            draft.ClearLocalIdentity();
        }
    }


    /// <summary>
    /// The side-bound picker for one side. Every folder choice goes through it, so a browse can only
    /// write to the side, kind and account it was started for.
    /// </summary>
    public SyncEndpointSidePicker PickerFor(SyncPairSide side) => _pickers[(int)side];

    /// <summary>
    /// Starts one browse on one side and returns the ticket any result of that browse must carry. The
    /// ticket records the side's kind and account at this moment, which is what makes a later result
    /// recognisable as belonging to an older choice.
    /// </summary>
    public SyncEndpointPickerTicket BeginFolderPick(SyncPairSide side)
    {
        var draft = Draft(side);
        return new SyncEndpointPickerTicket(_sessionIdentity, Generation, side, draft.Kind, draft.ConnectionId,
            ++_pickSequences[(int)side]);
    }

    /// <summary>
    /// True while a ticket still describes this side's current choice: the same editor session, the same
    /// side kind, the same account and the newest browse on that side. Anything else is a superseded
    /// result and is never applied.
    /// </summary>
    public bool IsCurrentPick(SyncEndpointPickerTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!IsActive || !ReferenceEquals(ticket.SessionIdentity, _sessionIdentity) || ticket.EditorGeneration != Generation)
            return false;
        var draft = Draft(ticket.Side);
        if (draft.Kind != ticket.Kind) return false;
        if (!string.Equals(draft.ConnectionId, ticket.ConnectionId, StringComparison.Ordinal)) return false;
        return ticket.Sequence == _pickSequences[(int)ticket.Side];
    }

    /// <summary>
    /// True when one side's selected path can be opened. An empty path is never clickable, because there
    /// is no folder to open yet and an invented location must not be offered.
    /// </summary>
    public bool CanOpenSelectedFolder(SyncPairSide side)
    {
        var draft = Draft(side);
        return draft.IsLocal && draft.HasLocalFolder;
    }

    /// <summary>
    /// The one validated draft extraction the E3/E4 integration owner consumes: both ordered endpoints,
    /// the direction, what saving would mean for a retained pair, and exactly why saving is or is not
    /// possible right now.
    ///
    /// retainedPair is the pair this editor was opened from. When the draft names a retained pair but
    /// that pair was not supplied, the intent fails closed to Rebind rather than being reported as a
    /// selection-only change.
    /// </summary>
    public SyncPairDraft ExtractDraft(SyncPair? retainedPair = null,
        IEnumerable<SyncPairEndpoints>? existingPairs = null)
        => ExtractDraftCore(retainedPair, ExtractValidation(existingPairs));

    /// <summary>
    /// The complete-policy draft extraction. It reports every refusal the strict extraction reports and
    /// replaces ONLY the retained-endpoint overlap decision with the one shared nested-pair partition
    /// policy over complete retained pair definitions, so a proven excluded nested child can be admitted
    /// by the same draft the save path consumes. The candidate is supplied by the caller because only the
    /// caller owns the current selection/ignore/control draft; a candidate that does not describe THIS
    /// draft's exact endpoints, or that cannot be derived, keeps the strict endpoint-only refusal.
    /// </summary>
    public SyncPairDraft ExtractDraftWithCompletePolicy(SyncPair? retainedPair,
        IEnumerable<SyncPairEndpoints>? existingPairs, IEnumerable<SyncPair>? retainedPairs,
        Func<SyncPairEndpoints, SyncPair?> completeCandidate, bool deferOpaqueHierarchyVerification = false)
        => ExtractDraftCore(retainedPair,
            ExtractCompleteValidation(existingPairs, retainedPairs, completeCandidate, deferOpaqueHierarchyVerification));

    private SyncPairDraft ExtractDraftCore(SyncPair? retainedPair, SyncEndpointValidation validation)
    {
        var leftKind = KindOf(SyncPairSide.Left);
        var rightKind = KindOf(SyncPairSide.Right);
        SyncPairEndpoints? endpoints = null;
        if (TryBuildEndpoints(out var left, out var right))
            endpoints = new SyncPairEndpoints(left!, right!, Direction);
        var intent = EditingPairId is null
            ? SyncPairEditIntent.NewPair
            : retainedPair is null ? SyncPairEditIntent.Rebind : ClassifyAgainst(retainedPair);
        return new SyncPairDraft(endpoints, Direction, intent, validation,
            SyncEndpointPresentation.BindingSupportReason(leftKind, rightKind),
            SyncEndpointPresentation.BindingRuntimeHook(leftKind, rightKind))
        {
            EditingPairId = EditingPairId,
            ExpectedPolicyRevision = EditingPolicyRevision
        };
    }

    /// <summary>
    /// The complete refusal set a save must satisfy: each side's own identity and the pairwise rules,
    /// followed by the stable local identity a persisted local endpoint must carry. A refusal reached
    /// from both paths is reported once.
    /// </summary>
    private SyncEndpointValidation ExtractValidation(IEnumerable<SyncPairEndpoints>? existingPairs)
    {
        var refusals = new List<SyncEndpointRefusal>(ValidateAgainstExisting(existingPairs).Refusals);
        refusals.AddRange(ValidateForPersistence().Refusals);
        var distinct = refusals.Distinct().ToArray();
        return distinct.Length == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(distinct);
    }

    /// <summary>
    /// The complete-policy refusal set: each side's own identity, the intra-pair rules and the stable
    /// persisted identity the strict extraction already enforces, with the cross-pair decision taken by
    /// the shared partition policy over complete retained pair definitions. A missing or mismatched
    /// complete candidate never waives a retained overlap; it falls back to the strict endpoint-only
    /// cross-pair refusal.
    /// </summary>
    private SyncEndpointValidation ExtractCompleteValidation(IEnumerable<SyncPairEndpoints>? existingPairs,
        IEnumerable<SyncPair>? retainedPairs, Func<SyncPairEndpoints, SyncPair?>? completeCandidate,
        bool deferOpaqueHierarchyVerification)
    {
        var refusals = new List<SyncEndpointRefusal>();
        foreach (var side in new[] { SyncPairSide.Left, SyncPairSide.Right })
            refusals.AddRange(Draft(side).Validate().Refusals);
        if (!TryBuildEndpoints(out var left, out var right))
            return Distinct(refusals.ToArray());
        var cloudOnly = !left!.IsLocal && !right!.IsLocal;
        refusals.AddRange(SyncCloudPairConfiguration.ValidateEndpointPair(left, right!).Refusals);
        var endpoints = new SyncPairEndpoints(left, right!, Direction);
        var candidate = completeCandidate?.Invoke(endpoints);
        if (candidate is null || !DescribesSameEndpoints(candidate, endpoints))
        {
            // Without a complete definition of exactly these endpoints there is no exclusion policy that
            // could prove a retained nested partition, so the raw endpoint-only refusal still owns it.
            if (candidate is not null)
                refusals.Add(new SyncEndpointRefusal(SyncEndpointError.MissingEndpointDocument,
                    "The complete candidate does not describe this draft's exact endpoints, so its exclusion policy cannot waive a retained overlap."));
            if (existingPairs is not null)
                refusals.AddRange(cloudOnly
                    ? SyncCloudPairConfiguration.ValidateAgainstRetainedPairs(endpoints, existingPairs).Refusals
                    : SyncEndpointContracts.ValidatePairAgainstExisting(left, right!, existingPairs).Refusals);
        }
        else
        {
            // A complete editor draft may defer opaque cross-pair ancestry to the host's fresh async
            // provider verification on Save. This is availability, not authority: raw callers keep
            // the strict default, and a missing or mismatched complete draft never gets this deferral.
            foreach (var retained in retainedPairs ?? Array.Empty<SyncPair>())
                refusals.AddRange(SyncPairPartitionPolicy.ValidateAgainstRetainedPairs(candidate, [retained],
                    refuseUnverified: !cloudOnly && !(deferOpaqueHierarchyVerification &&
                        SyncPairPartitionPolicy.HasComparableOpaqueEndpoints(candidate, retained))).Refusals);
        }
        refusals.AddRange(ValidateForPersistence().Refusals);
        return Distinct(refusals.ToArray());
    }

    /// <summary>True when the complete candidate resolves to exactly this draft's two endpoints.</summary>
    private static bool DescribesSameEndpoints(SyncPair candidate, SyncPairEndpoints endpoints)
    {
        if (SyncCloudPairConfiguration.SavedEndpoints(candidate) is not { } saved) return false;
        return SameEndpoint(saved.Left, endpoints.Left) && SameEndpoint(saved.Right, endpoints.Right);
    }

    private static SyncEndpointValidation Distinct(SyncEndpointRefusal[] refusals)
    {
        var distinct = refusals.Distinct().ToArray();
        return distinct.Length == 0 ? SyncEndpointValidation.Accepted : new SyncEndpointValidation(distinct);
    }

    /// <summary>
    /// What saving this draft would mean for a retained pair. A change to either endpoint's identity - 
    /// its kind, its local folder identity or its provider folder identity - is a rebind. A rename, a
    /// direction change or a policy change keeps both identities and is only a selection change.
    /// </summary>
    public SyncPairEditIntent ClassifyAgainst(SyncPair existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (RetainedEndpoints(existing) is not { } retained) return SyncPairEditIntent.Rebind;
        if (!TryBuildEndpoints(out var left, out var right)) return SyncPairEditIntent.Rebind;
        return SameEndpoint(left!, retained.Left) && SameEndpoint(right!, retained.Right)
            ? SyncPairEditIntent.SelectionOnly
            : SyncPairEditIntent.Rebind;
    }

    /// <summary>
    /// The ordered endpoints a retained pair already names, or null when its own saved identity cannot
    /// be read. Callers use this instead of mapping the pair themselves, so a malformed retained pair is
    /// reported once and never throws out of an editor surface.
    /// </summary>
    public static SyncPairEndpoints? RetainedEndpoints(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return SafeMap(pair).Endpoints;
    }

    /// <summary>
    /// True when saving this draft must re-enter the existing first-sync safeguard instead of keeping
    /// the retained baseline and approval. A cosmetic rename never requires it.
    /// </summary>
    public bool RequiresFirstSyncReview(SyncPair existing) => ClassifyAgainst(existing) == SyncPairEditIntent.Rebind;

    /// <summary>
    /// True when both endpoints name exactly the same store. A local comparison uses the stable folder
    /// identity first and the path second; a cloud comparison uses the provider, the account and the
    /// folder identity, so equal folder names on two accounts are never the same endpoint.
    /// </summary>
    public static bool SameEndpoint(SyncEndpoint left, SyncEndpoint right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.IsLocal != right.IsLocal || left.Kind != right.Kind) return false;
        return left.IsLocal
            ? SyncEndpointContracts.SameLocalStore(left, right)
            : SyncEndpoints.ReferencesSameStore(left, right);
    }

    private bool TryBuildEndpoints(out SyncEndpoint? left, out SyncEndpoint? right)
    {
        left = BuildEndpoint(SyncPairSide.Left);
        right = BuildEndpoint(SyncPairSide.Right);
        return left is not null && right is not null;
    }

    private SyncEndpoint? BuildEndpoint(SyncPairSide side)
    {
        var draft = Draft(side);
        try
        {
            if (draft.IsLocal)
                return draft.HasLocalFolder ? SyncEndpoint.Local(draft.LocalPath!, draft.LocalIdentity) : null;
            if (!draft.HasCloudAccount || !draft.HasCloudFolder) return null;
            return SyncEndpoint.Cloud(draft.Kind, draft.ConnectionId!, draft.NamespaceId, draft.FolderId,
                draft.FolderPath);
        }
        catch (ArgumentException)
        {
            // An incomplete or malformed draft identity is already reported by the side's own
            // validation; building an endpoint is not the place to invent a substitute.
            return null;
        }
    }

    private static SyncEndpointValidation Refuse(SyncEndpointError error, string reason) =>
        new(new[] { new SyncEndpointRefusal(error, reason) });
}
