using System.IO;
using System.Text.Json;
using Massdex.SyncShark;

// Focused proof for the E2 symmetric pair editor (EP-03). Every assertion here is a production
// decision taken by SyncPairEditorSession and SyncEndpointPresentation, not by a control's wiring: the
// same session drives the window, and the fakes below only supply account and folder inputs that a
// provider would supply in the shipped product.
internal static class PairEditorChecks
{
    private static readonly List<Dictionary<string, object?>> Results = [];
    private static int _assertions;

    internal static Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        Results.Clear();
        _assertions = 0;

        var dropboxOne = new SyncVerifiedAccountBinding(SyncProviders.Dropbox, "conn-dropbox-1", "ns:root-1", true);
        var dropboxTwo = new SyncVerifiedAccountBinding(SyncProviders.Dropbox, "conn-dropbox-2", "ns:root-2", false);
        var googleOne = new SyncVerifiedAccountBinding(SyncProviders.GoogleDrive, "conn-google-1", "gd-ns-1", false);
        var googleTwo = new SyncVerifiedAccountBinding(SyncProviders.GoogleDrive, "conn-google-2", "gd-ns-2", false);

        var localLeft = "C:/SyncShark/E2/LeftFolder";
        var localRight = "C:/SyncShark/E2/RightFolder";

        Claim("E21-nine-combinations",
            "A draft can be created and completed for each of the nine ordered endpoint-kind combinations");
        var combinations = 0;
        foreach (var leftKind in new[] { SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
        foreach (var rightKind in new[] { SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
        {
            var session = SyncPairEditorSession.ForNewPair();
            Complete(session, SyncPairSide.Left, leftKind, localLeft, 1234567890u, 42ul, dropboxOne, googleOne);
            Complete(session, SyncPairSide.Right, rightKind, localRight, 1234567890u, 43ul, dropboxTwo, googleTwo);
            Require(session.TryBuild(out var built, out var validation) && validation.IsValid && built is not null,
                "E21 " + leftKind + "/" + rightKind + " builds a valid ordered pair");
            Require(built!.Left.Kind == leftKind && built.Right.Kind == rightKind,
                "E21 " + leftKind + "/" + rightKind + " keeps the chosen kind on each side in order");
            combinations++;
        }
        Require(combinations == 9, "E21 every one of the nine ordered combinations was exercised");
        var sameLocalIdentity = SyncPairEditorSession.ForNewPair();
        Complete(sameLocalIdentity, SyncPairSide.Left, SyncEndpointKind.Local, localLeft,
            1234567890u, 42ul, dropboxOne, googleOne);
        Complete(sameLocalIdentity, SyncPairSide.Right, SyncEndpointKind.Local, localRight,
            1234567890u, 42ul, dropboxTwo, googleTwo);
        Require(!sameLocalIdentity.TryBuild(out _, out var sameIdentityValidation) &&
                sameIdentityValidation.Errors.Contains(SyncEndpointError.SameEndpoint),
            "E21 differing local paths with the same physical folder identity are refused as SameEndpoint");

        Claim("E22-side-independence",
            "Switching one side's kind clears only that side's incompatible draft identity");
        var independence = SyncPairEditorSession.ForNewPair();
        Complete(independence, SyncPairSide.Left, SyncEndpointKind.Dropbox, localLeft, dropboxOne, googleOne);
        Complete(independence, SyncPairSide.Right, SyncEndpointKind.GoogleDrive, localRight, dropboxTwo, googleTwo);
        independence.SetKind(SyncPairSide.Left, SyncEndpointKind.Local);
        Require(independence.Draft(SyncPairSide.Left).ConnectionId is null &&
                independence.Draft(SyncPairSide.Left).FolderId is null &&
                independence.Draft(SyncPairSide.Left).FolderPath is null &&
                independence.Draft(SyncPairSide.Left).NamespaceId is null,
            "E22 leaving a cloud kind clears that side's account, namespace, folder id and folder path");
        Require(independence.Draft(SyncPairSide.Right).Kind == SyncEndpointKind.GoogleDrive &&
                independence.Draft(SyncPairSide.Right).ConnectionId == "conn-google-2" &&
                independence.Draft(SyncPairSide.Right).FolderId == "gd-folder-right",
            "E22 the other side's kind, account and folder are untouched");
        independence.SetKind(SyncPairSide.Left, SyncEndpointKind.Dropbox);
        Require(independence.Draft(SyncPairSide.Left).LocalPath is null &&
                independence.Draft(SyncPairSide.Left).ConnectionId is null &&
                independence.Draft(SyncPairSide.Left).FolderId is null,
            "E22 entering a cloud kind clears the local folder and needs a fresh account and folder");
        Complete(independence, SyncPairSide.Left, SyncEndpointKind.Dropbox, localLeft, dropboxOne, googleOne);
        independence.SetKind(SyncPairSide.Left, SyncEndpointKind.Dropbox);
        Require(independence.Draft(SyncPairSide.Left).ConnectionId == "conn-dropbox-1" &&
                independence.Draft(SyncPairSide.Left).FolderId == "id:folder-left",
            "E22 re-selecting the kind already chosen keeps the completed selection");
        independence.SetKind(SyncPairSide.Left, SyncEndpointKind.GoogleDrive);
        Require(independence.Draft(SyncPairSide.Left).ConnectionId is null &&
                independence.Draft(SyncPairSide.Left).FolderId is null &&
                independence.Draft(SyncPairSide.Right).ConnectionId == "conn-google-2" &&
                independence.Draft(SyncPairSide.Right).FolderId == "gd-folder-right",
            "E22 switching between the two cloud providers clears only that side and leaves the other side alone");
        Require(independence.Direction == SyncMode.TwoWay,
            "E22 a kind change never changes the direction");

        Claim("E23-account-provider-agreement",
            "A cloud side accepts only an account of the provider it names");
        var agreement = SyncPairEditorSession.ForNewPair();
        agreement.SetKind(SyncPairSide.Right, SyncEndpointKind.GoogleDrive);
        var mismatch = agreement.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        Require(!mismatch.IsValid && mismatch.Errors.Contains(SyncEndpointError.UnknownProvider),
            "E23 a Dropbox account is refused for a Google Drive side");
        Require(agreement.Draft(SyncPairSide.Right).ConnectionId is null,
            "E23 the refused account is not retained on the side");
        Require(agreement.SetCloudAccount(SyncPairSide.Right, googleOne).IsValid,
            "E23 a Google Drive account is accepted for a Google Drive side");
        var localAccount = SyncPairEditorSession.ForNewPair();
        Require(localAccount.SetCloudAccount(SyncPairSide.Left, dropboxOne).Errors
                .Contains(SyncEndpointError.EndpointKindMismatch),
            "E23 a cloud account is refused for a local side with the kind-mismatch reason");
        Require(localAccount.Draft(SyncPairSide.Left).ConnectionId is null,
            "E23 the local side keeps no account after the refusal");

        Claim("E24-account-change-clears-folder",
            "Changing the account clears that side's folder; re-choosing the same account keeps it");
        var accountChange = SyncPairEditorSession.ForNewPair();
        accountChange.SetKind(SyncPairSide.Right, SyncEndpointKind.Dropbox);
        accountChange.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        accountChange.SetCloudFolder(SyncPairSide.Right, "id:folder-right", "/Team/Right");
        accountChange.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        Require(accountChange.Draft(SyncPairSide.Right).FolderId == "id:folder-right",
            "E24 choosing the same account again keeps the chosen folder on that side");
        accountChange.SetCloudAccount(SyncPairSide.Right, dropboxTwo);
        Require(accountChange.Draft(SyncPairSide.Right).ConnectionId == "conn-dropbox-2" &&
                accountChange.Draft(SyncPairSide.Right).FolderId is null &&
                accountChange.Draft(SyncPairSide.Right).FolderPath is null,
            "E24 a different account clears the folder identity issued by the previous account");
        Require(accountChange.Draft(SyncPairSide.Right).NamespaceId == "ns:root-2",
            "E24 the verified namespace follows the account it belongs to");

        Claim("E25-cancel-produces-no-pair",
            "Cancelling a draft produces no pair and retains no endpoint identity");
        var cancelled = SyncPairEditorSession.ForNewPair();
        Complete(cancelled, SyncPairSide.Left, SyncEndpointKind.Local, localLeft, dropboxOne, googleOne);
        Complete(cancelled, SyncPairSide.Right, SyncEndpointKind.Dropbox, localRight, dropboxOne, googleOne);
        Require(cancelled.TryBuild(out _, out _), "E25 the completed draft would otherwise build a pair");
        cancelled.Cancel();
        Require(!cancelled.IsActive, "E25 the cancelled draft is inactive");
        Require(!cancelled.TryBuild(out var cancelledPair, out var cancelledValidation) && cancelledPair is null,
            "E25 a cancelled draft produces no pair");
        Require(!cancelledValidation.IsValid, "E25 the cancellation is reported as a refusal, not silently accepted");
        Require(cancelled.Draft(SyncPairSide.Left).LocalPath is null &&
                cancelled.Draft(SyncPairSide.Right).ConnectionId is null,
            "E25 the cancelled draft retains no endpoint identity");

        Claim("E26-legacy-pair-renders-unchanged",
            "A retained legacy pair opens with its own identities and is never rewritten by the editor");
        var legacy = LegacyPair("e2-legacy");
        var legacySession = SyncPairEditorSession.ForExistingPair(legacy, out var legacyValidation);
        Require(legacyValidation.IsValid, "E26 the retained legacy pair maps without a refusal");
        Require(legacySession.EditingPairId == legacy.Id && legacySession.Direction == legacy.Direction,
            "E26 the session keeps the retained pair id and direction");
        Require(legacySession.Draft(SyncPairSide.Left).Kind == SyncEndpointKind.Local &&
                legacySession.Draft(SyncPairSide.Left).LocalPath == legacy.LocalRoot &&
                legacySession.Draft(SyncPairSide.Left).LocalIdentity ==
                new SyncLocalEndpointIdentity(legacy.LocalVolumeSerialNumber, legacy.LocalRootFileId),
            "E26 the left side opens as the retained local folder with its stable identity");
        Require(legacySession.Draft(SyncPairSide.Right).Kind == SyncEndpointKind.Dropbox &&
                legacySession.Draft(SyncPairSide.Right).ConnectionId == legacy.ConnectionId &&
                legacySession.Draft(SyncPairSide.Right).FolderId == legacy.DropboxFolderId &&
                legacySession.Draft(SyncPairSide.Right).FolderPath == legacy.DropboxRoot,
            "E26 the right side opens as the retained cloud folder on its own account");
        Require(legacySession.TryBuild(out var legacyBuilt, out var legacyBuiltValidation) &&
                legacyBuiltValidation.IsValid && SyncEndpointsCompatibility.ToSyncPair(legacyBuilt!, legacy) == legacy,
            "E26 the opened draft writes the whole retained pair back byte for byte");
        var legacyMapped = SyncEndpointsCompatibility.FromSyncPair(legacy);
        Require(legacySession.RecordOf(SyncPairSide.Left) == SyncEndpointRecord.From(legacyMapped.Endpoints!.Left) &&
                legacySession.RecordOf(SyncPairSide.Right) == SyncEndpointRecord.From(legacyMapped.Endpoints!.Right),
            "E26 each side's persisted record is exactly the retained identity, with no invented member");
        Require(legacySession.ValidateForPersistence().IsValid,
            "E26 a retained legacy pair is already persistence-ready");

        Claim("E26-legacy-verified-namespace", "Legacy editing fills only the exact verified account namespace");
        var completedLegacy = SyncPairEditorSession.ForExistingPair(legacy, 12, out var completedValidation,
            [dropboxTwo, googleOne, dropboxOne]);
        Require(completedValidation.IsValid && completedLegacy.Generation == 12 &&
                completedLegacy.Draft(SyncPairSide.Right).NamespaceId == dropboxOne.NamespaceId &&
                completedLegacy.Draft(SyncPairSide.Right).ConnectionId == legacy.ConnectionId &&
                completedLegacy.Draft(SyncPairSide.Right).FolderId == legacy.DropboxFolderId &&
                completedLegacy.Draft(SyncPairSide.Right).FolderPath == legacy.DropboxRoot &&
                completedLegacy.RecordOf(SyncPairSide.Left) == legacySession.RecordOf(SyncPairSide.Left) &&
                completedLegacy.Direction == legacy.Direction && legacy.EndpointDocument is null,
            "Exact verified binding completes the draft without changing endpoints, direction or saved legacy state");
        Require(completedLegacy.TryBuild(out var completedEndpoints, out _) &&
                SyncEndpointPairDraft.Create(completedEndpoints!, legacy.Name, legacy).RightEndpoint!.NamespaceId == dropboxOne.NamespaceId,
            "The actual shared save-draft boundary receives the verified namespace");
        Require(!completedLegacy.RequiresFirstSyncReview(legacy),
            "Completing legacy namespace metadata is not a folder rebind");
        foreach (var rejected in new SyncVerifiedAccountBinding[][]
        {
            [], [dropboxTwo], [googleOne with { ConnectionId = legacy.ConnectionId }],
            [dropboxOne, dropboxOne with { NamespaceId = "ambiguous" }],
            [dropboxOne with { NamespaceId = " " }]
        })
        {
            var unresolved = SyncPairEditorSession.ForExistingPair(legacy, 12, out _, rejected);
            Require(unresolved.Draft(SyncPairSide.Right).NamespaceId is null &&
                    unresolved.Draft(SyncPairSide.Right).FolderId == legacy.DropboxFolderId,
                "Missing, foreign, wrong-provider, ambiguous or empty bindings cannot invent a namespace");
        }
        var explicitDraft = SyncEndpointPairDraft.Create(completedEndpoints!, legacy.Name, legacy);
        foreach (var namespaceId in new string?[] { null, "retained-other-namespace" })
        {
            var explicitPair = explicitDraft with { RightEndpoint = explicitDraft.RightEndpoint! with { NamespaceId = namespaceId } };
            var explicitSession = SyncPairEditorSession.ForExistingPair(explicitPair, 12, out _, [dropboxOne]);
            Require(explicitSession.Draft(SyncPairSide.Right).NamespaceId == namespaceId,
                "An explicit endpoint document is never silently repaired or rebound from an account list");
        }

        Claim("E27-rebind-versus-rename",
            "An endpoint change is an explicit rebind; a rename or direction change is not");
        var rebindSession = SyncPairEditorSession.ForExistingPair(legacy, out _);
        Require(rebindSession.ClassifyAgainst(legacy) == SyncPairEditIntent.SelectionOnly &&
                !rebindSession.RequiresFirstSyncReview(legacy),
            "E27 opening a retained pair and changing nothing is not a rebind");
        rebindSession.SetDirection(SyncMode.DownloadOnly);
        Require(rebindSession.ClassifyAgainst(legacy) == SyncPairEditIntent.SelectionOnly &&
                !rebindSession.RequiresFirstSyncReview(legacy),
            "E27 a direction change keeps both identities and is not a rebind");
        Require(rebindSession.TryBuild(out var directionOnly, out _) &&
                SameStore(directionOnly!, legacyMapped.Endpoints!),
            "E27 the direction change leaves both endpoint identities exactly as they were");
        rebindSession.SetCloudFolder(SyncPairSide.Right, "id:folder-moved", "/Team/Moved");
        Require(rebindSession.ClassifyAgainst(legacy) == SyncPairEditIntent.Rebind &&
                rebindSession.RequiresFirstSyncReview(legacy),
            "E27 changing a side's folder identity requires the first-sync review");
        var providerSession = SyncPairEditorSession.ForExistingPair(legacy, out _);
        providerSession.SetKind(SyncPairSide.Right, SyncEndpointKind.GoogleDrive);
        providerSession.SetCloudAccount(SyncPairSide.Right, googleOne);
        providerSession.SetCloudFolder(SyncPairSide.Right, "gd-folder-1", "/Team/Project");
        Require(providerSession.RequiresFirstSyncReview(legacy),
            "E27 changing the provider of a side is a rebind even when the folder name is unchanged");
        var renameSession = SyncPairEditorSession.ForExistingPair(legacy, out _);
        Require(renameSession.ClassifyAgainst(legacy) == SyncPairEditIntent.SelectionOnly,
            "E27 a cosmetic rename alone never becomes a rebind");
        var changedPair = legacy with { PolicyRevision = checked(legacy.PolicyRevision + 1) };
        var staleDraft = renameSession.ExtractDraft(changedPair);
        Require(staleDraft.EditingPairId == legacy.Id && staleDraft.ExpectedPolicyRevision == legacy.PolicyRevision &&
                staleDraft.ExpectedPolicyRevision != changedPair.PolicyRevision,
            "a draft retains the opening pair revision even when extraction is handed a newer CLI-edited pair");

        Claim("E28-cross-pair-protection",
            "A draft reusing or nesting another retained pair's endpoint is refused before any effect");
        var retainedPairs = new List<SyncPairEndpoints> { legacyMapped.Endpoints! };
        var duplicate = SyncPairEditorSession.ForNewPair();
        Complete(duplicate, SyncPairSide.Left, SyncEndpointKind.Local, legacy.LocalRoot,
            legacy.LocalVolumeSerialNumber, legacy.LocalRootFileId, dropboxOne, googleOne);
        Complete(duplicate, SyncPairSide.Right, SyncEndpointKind.Dropbox, localRight, dropboxOne, googleOne);
        duplicate.SetCloudFolder(SyncPairSide.Right, "id:folder-other", "/Team/Other");
        Require(duplicate.ValidateAgainstExisting(retainedPairs).Errors
                .Contains(SyncEndpointError.DuplicateEndpointAcrossPairs),
            "E28 reusing a retained pair's exact local folder is refused");
        var nestedUnder = SyncPairEditorSession.ForNewPair();
        Complete(nestedUnder, SyncPairSide.Left, SyncEndpointKind.Local, legacy.LocalRoot + "/Child",
            legacy.LocalVolumeSerialNumber, legacy.LocalRootFileId + 1, dropboxOne, googleOne);
        Complete(nestedUnder, SyncPairSide.Right, SyncEndpointKind.Dropbox, localRight, dropboxOne, googleOne);
        nestedUnder.SetCloudFolder(SyncPairSide.Right, "id:folder-other", "/Team/Other");
        Require(nestedUnder.ValidateAgainstExisting(retainedPairs).Errors
                .Contains(SyncEndpointError.OverlappingEndpointAcrossPairs),
            "E28 nesting inside a retained pair's local folder is refused");
        Require(duplicate.ValidateAgainstExisting([]).IsValid,
            "E28 the same draft is accepted when no other pair is retained");

        Claim("E29-direction-labels",
            "The three directions are presented as Two-way, Left to right and Right to left");
        Require(SyncEndpointPresentation.DirectionLabel(SyncMode.TwoWay) == "Two-way" &&
                SyncEndpointPresentation.DirectionLabel(SyncMode.UploadOnly) == "Left to right" &&
                SyncEndpointPresentation.DirectionLabel(SyncMode.DownloadOnly) == "Right to left",
            "E29 the three user-facing direction labels match the agreed wording");
        Require(SyncEndpointPresentation.DirectionChoices.Select(choice => (int)choice.Mode)
                .SequenceEqual(new[] { 0, 1, 2 }),
            "E29 the presented order is the persisted SyncMode order, so no stored value is remapped");
        var directionSession = SyncPairEditorSession.ForNewPair();
        Complete(directionSession, SyncPairSide.Left, SyncEndpointKind.Local, localLeft, dropboxOne, googleOne);
        Complete(directionSession, SyncPairSide.Right, SyncEndpointKind.Dropbox, localRight, dropboxOne, googleOne);
        directionSession.SetDirection(SyncMode.UploadOnly);
        Require(directionSession.TryBuild(out var leftToRight, out _) && leftToRight!.Direction == SyncMode.UploadOnly,
            "E29 choosing Left to right stores the retained UploadOnly meaning");
        directionSession.SetDirection(SyncMode.DownloadOnly);
        Require(directionSession.TryBuild(out var rightToLeft, out _) && rightToLeft!.Direction == SyncMode.DownloadOnly &&
                SameStore(rightToLeft, leftToRight!),
            "E29 choosing Right to left stores DownloadOnly and never swaps the two endpoints");

        Claim("E30-provider-aware-presentation",
            "Endpoint labels, icons and account naming are provider-aware and reusable from one place");
        Require(SyncEndpointPresentation.ShortLabel(SyncEndpointKind.Local) == "Local" &&
                SyncEndpointPresentation.ShortLabel(SyncEndpointKind.Dropbox) == "Dropbox" &&
                SyncEndpointPresentation.ShortLabel(SyncEndpointKind.GoogleDrive) == "Google Drive",
            "E30 each kind has exactly one compact label");
        Require(SyncEndpointPresentation.IconTemplateKey(SyncEndpointKind.Local) == "FolderIconTemplate" &&
                SyncEndpointPresentation.IconTemplateKey(SyncEndpointKind.Dropbox) == "DropboxIconTemplate" &&
                SyncEndpointPresentation.IconTemplateKey(SyncEndpointKind.GoogleDrive) == "GoogleDriveIconTemplate",
            "E30 icons reuse the existing resource templates instead of newly drawn brand marks");
        Require(SyncEndpointPresentation.SectionTitle(SyncPairSide.Left, SyncEndpointKind.GoogleDrive) !=
                SyncEndpointPresentation.SectionTitle(SyncPairSide.Right, SyncEndpointKind.GoogleDrive),
            "E30 the two sides are titled distinctly for the same provider");
        Require(SyncEndpointPresentation.EndpointDisplay(
                    SyncEndpointRecord.From(SyncEndpointsCompatibility.FromSyncPair(LegacyPair("e2-google") with
                    {
                        Provider = SyncProviders.GoogleDrive, ConnectionId = "conn-google-1",
                        DropboxFolderId = "gd-folder-1", DropboxRoot = "/Team"
                    }).Endpoints!.Right!)) == "Google Drive: /Team",
            "E30 a Google Drive endpoint is described by Google Drive and never by Dropbox");
        var dropboxDisplay = SyncEndpointPresentation.EndpointDisplay(SyncEndpoint.Local("C:/SyncShark/E2/Left"));
        Require(!dropboxDisplay.Contains("Dropbox", StringComparison.Ordinal) &&
                dropboxDisplay.StartsWith("Local:", StringComparison.Ordinal),
            "E30 a local endpoint is never described with a cloud provider name");
        Require(SyncEndpointPresentation.AccountLabel("Dropbox", "Alex Holder", "alex@example.com") ==
                "alex@example.com (Alex Holder)" &&
                SyncEndpointPresentation.AccountLabel("Google Drive", null, "alex@example.com") ==
                "alex@example.com" &&
                SyncEndpointPresentation.AccountLabel("Google Drive", "Alex", null) == "Alex",
            "E30 account naming keeps the established compact email-first form");
        foreach (var leftKind in new[] { SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
        foreach (var rightKind in new[] { SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
            Require(SyncEndpointPresentation.BindingSupported(leftKind, rightKind) &&
                    SyncEndpointPresentation.BindingSupportReason(leftKind, rightKind) is null &&
                    SyncEndpointPresentation.BindingRuntimeHook(leftKind, rightKind) is null,
                "E30 " + leftKind + "/" + rightKind +
                " reports the implemented ordered runtime capability without an obsolete missing-hook reason");

        CheckMissingLocalIdentity(dropboxOne);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "PairEditorChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = root,
            ["assertions"] = _assertions,
            ["cases"] = Results
        };
        File.WriteAllText(Path.Combine(root, "pair-editor-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS pair editor: " + _assertions +
            " assertions; nine ordered combinations author and validate, one side's kind change clears only that side, " +
            "cancel produces no pair, a retained legacy pair renders unchanged, an endpoint change is a rebind while a rename is not, " +
            "cross-pair reuse and nesting are refused and presentation is provider-aware.");
        return Task.CompletedTask;
    }

    private static void CheckMissingLocalIdentity(SyncVerifiedAccountBinding account)
    {
        // A retained legacy pair whose local endpoint carries no stable identity must never throw out of the
        // editor: the runtime refuses it, and SafeMap turns that exact refusal into validation the editor can
        // show. The retained local path, provider, account and folder stay exactly as saved, and no identity is
        // invented to make the pair look valid.
        Claim("E26-missing-local-identity",
            "A retained pair with an identity-less local endpoint is refused as validation and never as an exception");
        var retainedLocal = "C:/SyncShark/E2/RetainedFolder";
        var malformed = new SyncPair("editor-missing-identity", "Retained pair", retainedLocal, 0, 1, "/retained", "id:retained",
            ConnectionId: account.ConnectionId, Provider: SyncProviders.Dropbox);
        var originalPair = JsonSerializer.Serialize(malformed);
        Require(malformed.EndpointDocument is null,
            "E26 a legacy pair with no recorded endpoint document keeps a null document");
        var retainedSession = SyncPairEditorSession.ForExistingPair(malformed, out var retainedValidation);
        Require(!retainedValidation.IsValid &&
                retainedValidation.Errors.Contains(SyncEndpointError.MissingLocalIdentity),
            "E26 the editor opens with the runtime's MissingLocalIdentity refusal as validation: " + retainedValidation.Describe());
        var leftDraft = retainedSession.Draft(SyncPairSide.Left);
        var rightDraft = retainedSession.Draft(SyncPairSide.Right);
        Require(retainedSession.EditingPairId == malformed.Id &&
                leftDraft.Kind == SyncEndpointKind.Local && leftDraft.LocalPath == retainedLocal &&
                leftDraft.LocalIdentity is null && rightDraft.Kind == SyncEndpointKind.Dropbox &&
                rightDraft.ConnectionId == account.ConnectionId && rightDraft.FolderPath == "/retained" &&
                rightDraft.FolderId == "id:retained" && JsonSerializer.Serialize(malformed) == originalPair,
            "E26 the refusal preserves the retained local path, provider, account and folder and leaves the pair unchanged");
        Require(retainedSession.ValidateForPersistence().Errors.Contains(SyncEndpointError.MissingLocalIdentity),
            "E26 ValidateForPersistence refuses the pair instead of persisting an identity-less local endpoint");
        var stillRefused = false;
        try { SyncEndpointPairRuntime.Resolve(malformed, malformed.EndpointDocument); }
        catch (SyncEndpointRuntimeRefusedException error)
        {
            stillRefused = error.Validation.Errors.Contains(SyncEndpointError.MissingLocalIdentity);
        }
        Require(stillRefused,
            "E26 direct runtime resolution still refuses the identity-less local endpoint before any effect");
        Require(JsonSerializer.Serialize(malformed) == originalPair,
            "E26 validation and direct runtime refusal leave every retained pair field unchanged");
    }

    private static void Complete(SyncPairEditorSession session, SyncPairSide side, SyncEndpointKind kind,
        string localPath, SyncVerifiedAccountBinding dropbox, SyncVerifiedAccountBinding google)
        => Complete(session, side, kind, localPath, 1234567890u, 42ul, dropbox, google);

    private static void Complete(SyncPairEditorSession session, SyncPairSide side, SyncEndpointKind kind,
        string localPath, uint volumeSerialNumber, ulong rootFileId, SyncVerifiedAccountBinding dropbox,
        SyncVerifiedAccountBinding google)
    {
        session.SetKind(side, kind);
        switch (kind)
        {
            case SyncEndpointKind.Local:
                session.SetLocalFolder(side, localPath, volumeSerialNumber, rootFileId);
                break;
            case SyncEndpointKind.Dropbox:
                session.SetCloudAccount(side, dropbox);
                session.SetCloudFolder(side, "id:folder-" + (side == SyncPairSide.Left ? "left" : "right"),
                    "/Team/" + (side == SyncPairSide.Left ? "Left" : "Right"));
                break;
            default:
                session.SetCloudAccount(side, google);
                session.SetCloudFolder(side, "gd-folder-" + (side == SyncPairSide.Left ? "left" : "right"),
                    "/Team/" + (side == SyncPairSide.Left ? "Left" : "Right"));
                break;
        }
    }

    private static bool SameStore(SyncPairEndpoints first, SyncPairEndpoints second) =>
        SyncPairEditorSession.SameEndpoint(first.Left, second.Left) &&
        SyncPairEditorSession.SameEndpoint(first.Right, second.Right);

    private static SyncPair LegacyPair(string name) => new(name, name, "C:/SyncShark/E2/Legacy", 1234567890u, 42ul,
        "/Team/Project", "id:folder-legacy", ConnectionId: "conn-dropbox-1");

    private static void Claim(string id, string requirement) => Results.Add(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["requirement"] = requirement,
        ["observed"] = "pending"
    });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        _assertions++;
        var current = Results[^1];
        var observed = (string?)current["observed"];
        current["observed"] = observed == "pending" ? message : observed + " | " + message;
        current["state"] = "observed";
        Console.WriteLine("PASS " + message);
    }
}
