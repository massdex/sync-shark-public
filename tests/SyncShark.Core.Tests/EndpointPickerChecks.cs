using System.IO;
using System.Text.Json;
using Massdex.SyncShark;

// Focused proof for the E2 per-side endpoint pickers (EP-03).
//
// Every assertion here is a production decision taken by SyncPairEditorSession, SyncEndpointSidePicker,
// SyncEndpointAccountChoices or SyncEndpointPresentation. The window routes its controls into exactly
// these methods, so the picker behaviour proved here is the behaviour the editor runs. The local folder
// identity is read through the product's real PilotDirectoryLease, so a real folder succeeds and a
// missing folder, a plain file or an unverifiable folder is refused by the same code the editor uses.
//
// The folder-listing fakes below only supply the provider input a provider would supply; they replace no
// decision. Nothing here performs a provider call, a binding, a transfer or a write.
internal static class EndpointPickerChecks
{
    private static readonly List<Dictionary<string, object?>> Results = [];
    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        Results.Clear();
        _assertions = 0;

        var leftFolder = Path.Combine(root, "PickerLocalLeft");
        var rightFolder = Path.Combine(root, "PickerLocalRight");
        var plainFile = Path.Combine(root, "PickerNotAFolder.txt");
        Directory.CreateDirectory(leftFolder);
        Directory.CreateDirectory(rightFolder);
        await File.WriteAllTextAsync(plainFile, "this is a file, not a folder");
        var missingFolder = Path.Combine(root, "PickerMissingFolder");

        var dropboxOne = new SyncVerifiedAccountBinding(SyncProviders.Dropbox, "conn-dropbox-1", "ns:root-1", true);
        var dropboxTwo = new SyncVerifiedAccountBinding(SyncProviders.Dropbox, "conn-dropbox-2", "ns:root-2", false);
        var sharedNameOne = new SyncVerifiedAccountBinding(SyncProviders.Dropbox, "conn-shared-a", "ns:shared-a", false);
        var sharedNameTwo = new SyncVerifiedAccountBinding(SyncProviders.Dropbox, "conn-shared-b", "ns:shared-b", false);
        var googleOne = new SyncVerifiedAccountBinding(SyncProviders.GoogleDrive, "conn-google-1", "gd-ns-1", false);
        var googleTwo = new SyncVerifiedAccountBinding(SyncProviders.GoogleDrive, "conn-google-2", "gd-ns-2", false);

        Claim("EPP-01-local-picker",
            "Each side has its own local folder picker that reads the folder's real volume and root identity");
        var localSession = SyncPairEditorSession.ForNewPair(1);
        var leftPicker = localSession.PickerFor(SyncPairSide.Left);
        Require(!leftPicker.IsCloud && !leftPicker.CanOpenSelectedFolder,
            "EPP-01 a fresh local side names no folder, so its selected path is not clickable");
        var appliedLeft = leftPicker.ApplyLocalFolder(leftPicker.BeginPick(), leftFolder);
        Require(appliedLeft.Applied && localSession.Draft(SyncPairSide.Left).LocalPath == leftFolder,
            "EPP-01 the chosen folder on this computer is applied to the left side");
        Require(localSession.Draft(SyncPairSide.Left).LocalIdentity is { IsComplete: true },
            "EPP-01 the applied local folder carries its real volume serial and root file id");
        Require(leftPicker.CanOpenSelectedFolder,
            "EPP-01 a chosen path becomes clickable while an empty one stays unclickable");
        var missingOutcome = leftPicker.ApplyLocalFolder(leftPicker.BeginPick(), missingFolder);
        Require(missingOutcome.State == SyncEndpointPickerState.Refused &&
                missingOutcome.Validation.Errors.Contains(SyncEndpointError.InvalidLocalPath),
            "EPP-01 a folder that does not exist cannot be verified and is refused");
        Require(localSession.Draft(SyncPairSide.Left).LocalPath == leftFolder,
            "EPP-01 the refused folder leaves the side's previous choice exactly as it was");
        Require(!leftPicker.ApplyLocalFolder(leftPicker.BeginPick(), plainFile).Applied,
            "EPP-01 a plain file is not accepted as a folder on this computer");
        Require(localSession.Draft(SyncPairSide.Right).Kind == SyncEndpointKind.Dropbox &&
                localSession.Draft(SyncPairSide.Right).LocalPath is null,
            "EPP-01 the left side's pick never touches the right side");

        Claim("EPP-02-nine-combinations",
            "Every ordered kind combination is authored through the two side-bound pickers");
        var kinds = new[] { SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive };
        var combinations = 0;
        foreach (var leftKind in kinds)
        foreach (var rightKind in kinds)
        {
            var session = SyncPairEditorSession.ForNewPair(2);
            CompleteThroughPickers(session, SyncPairSide.Left, leftKind, leftFolder, dropboxOne, googleOne);
            CompleteThroughPickers(session, SyncPairSide.Right, rightKind, rightFolder, dropboxTwo, googleTwo);
            var draft = session.ExtractDraft();
            Require(draft.Endpoints is not null && draft.Left!.Kind == leftKind && draft.Right!.Kind == rightKind,
                "EPP-02 " + leftKind + "/" + rightKind + " extracts both endpoints in order");
            // The accepted all-provider runtime binds every valid, distinct, fully identified ordered
            // combination, so all nine drafts are saveable with no unsupported reason and no remaining
            // hook. This states that approved expectation directly and never derives it from the
            // production binding predicate.
            Require(draft.CanSave && draft.Validation.IsValid &&
                    draft.UnsupportedBindingReason is null && draft.RuntimeHook is null,
                "EPP-02 " + leftKind + "/" + rightKind + " is a saveable complete draft with no remaining hook");
            if (leftKind == SyncEndpointKind.Local && rightKind == SyncEndpointKind.Local)
            {
                Require(draft.Left!.LocalPath == leftFolder && draft.Right!.LocalPath == rightFolder &&
                        draft.Left.LocalIdentity is { IsComplete: true } &&
                        draft.Right.LocalIdentity is { IsComplete: true },
                    "EPP-02 local/local keeps two different real folders with their own identities");
            }
            combinations++;
        }
        Require(combinations == 9, "EPP-02 all nine ordered combinations were exercised through the pickers");

        Claim("EPP-03-account-independence",
            "Two accounts that share a display name stay two accounts and each side keeps its own");
        var offered = SyncEndpointAccountChoices.ForKind(SyncEndpointKind.Dropbox,
            new[] { sharedNameOne, sharedNameTwo, googleOne });
        Require(offered.Count == 2 && offered.Select(item => item.ConnectionId).Distinct().Count() == 2,
            "EPP-03 both same-named Dropbox accounts are offered as two distinct choices");
        Require(SyncEndpointAccountChoices.ForKind(SyncEndpointKind.Dropbox,
                new[] { sharedNameOne, sharedNameTwo, googleOne })
            .All(item => item.ConnectionId != googleOne.ConnectionId),
            "EPP-03 a Google account is never offered to a Dropbox side");
        Require(SyncEndpointAccountChoices.ForKind(SyncEndpointKind.Local, new[] { sharedNameOne }).Count == 0,
            "EPP-03 a local side is offered no account at all");
        Require(SyncEndpointAccountChoices.ForKind(SyncEndpointKind.Dropbox,
                new[] { sharedNameOne, sharedNameOne with { NamespaceId = "ns:other" } }).Count == 1,
            "EPP-03 one connection id is offered once even when it arrives twice");
        var pairSession = SyncPairEditorSession.ForNewPair(3);
        pairSession.SetKind(SyncPairSide.Left, SyncEndpointKind.Dropbox);
        pairSession.SetCloudAccount(SyncPairSide.Left, sharedNameOne);
        pairSession.SetCloudAccount(SyncPairSide.Right, sharedNameTwo);
        Require(pairSession.Draft(SyncPairSide.Left).ConnectionId == "conn-shared-a" &&
                pairSession.Draft(SyncPairSide.Right).ConnectionId == "conn-shared-b",
            "EPP-03 the two sides hold two different accounts that share a display name");
        var pairLeftPicker = pairSession.PickerFor(SyncPairSide.Left);
        pairLeftPicker.ApplyCloudFolder(pairLeftPicker.BeginPick(), "id:left-only", "/Shared/Left");
        Require(pairSession.Draft(SyncPairSide.Left).FolderId == "id:left-only" &&
                pairSession.Draft(SyncPairSide.Right).FolderId is null,
            "EPP-03 choosing a folder on one side never sets the other side's folder");

        Claim("EPP-04-account-change",
            "A cloud side browses only its own account and an account change clears only that side's folder");
        var accountSession = SyncPairEditorSession.ForNewPair(4);
        accountSession.SetKind(SyncPairSide.Right, SyncEndpointKind.GoogleDrive);
        Require(!accountSession.PickerFor(SyncPairSide.Right).CanBrowseCloud &&
                accountSession.PickerFor(SyncPairSide.Right).CloudBrowseRefusal is { Length: > 0 },
            "EPP-04 a cloud side with no account chosen cannot browse and reports the exact reason");
        var wrongProvider = accountSession.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        Require(!wrongProvider.IsValid && wrongProvider.Errors.Contains(SyncEndpointError.UnknownProvider),
            "EPP-04 a Dropbox account is refused for a Google Drive side");
        var noAccountPicker = accountSession.PickerFor(SyncPairSide.Right);
        var noAccount = noAccountPicker.ApplyCloudFolder(noAccountPicker.BeginPick(), "gd-folder-1", "/Team/One");
        Require(noAccount.State == SyncEndpointPickerState.Refused &&
                noAccount.Validation.Errors.Contains(SyncEndpointError.MissingCloudAccount),
            "EPP-04 a cloud folder cannot be chosen before the side has an account");
        accountSession.SetKind(SyncPairSide.Left, SyncEndpointKind.Dropbox);
        accountSession.SetCloudAccount(SyncPairSide.Left, dropboxOne);
        accountSession.PickerFor(SyncPairSide.Left)
            .ApplyCloudFolder(accountSession.PickerFor(SyncPairSide.Left).BeginPick(), "id:left-folder", "/Team/Left");
        Require(accountSession.SetCloudAccount(SyncPairSide.Right, googleOne).IsValid,
            "EPP-04 the matching provider account is accepted for the Google Drive side");
        accountSession.PickerFor(SyncPairSide.Right)
            .ApplyCloudFolder(accountSession.PickerFor(SyncPairSide.Right).BeginPick(), "gd-folder-1", "/Team/One");
        Require(accountSession.Draft(SyncPairSide.Right).FolderId == "gd-folder-1",
            "EPP-04 the Google Drive folder is applied to the Google Drive side");
        accountSession.SetCloudAccount(SyncPairSide.Right, googleTwo);
        Require(accountSession.Draft(SyncPairSide.Right).FolderId is null &&
                accountSession.Draft(SyncPairSide.Right).ConnectionId == "conn-google-2" &&
                accountSession.Draft(SyncPairSide.Left).FolderId == "id:left-folder",
            "EPP-04 changing one side's account clears only that side's folder");

        Claim("EPP-05-stale-browse",
            "A browse result that belongs to an older choice is never applied and never shown");
        var staleSession = SyncPairEditorSession.ForNewPair(5);
        staleSession.SetKind(SyncPairSide.Right, SyncEndpointKind.Dropbox);
        staleSession.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        var stalePicker = staleSession.PickerFor(SyncPairSide.Right);
        var firstTicket = stalePicker.BeginPick();
        var secondTicket = stalePicker.BeginPick();
        Require(!staleSession.IsCurrentPick(firstTicket) && staleSession.IsCurrentPick(secondTicket),
            "EPP-05 a newer browse on the same side supersedes the older ticket");
        var superseded = stalePicker.ApplyCloudFolder(firstTicket, "id:stale", "/Stale");
        Require(superseded.State == SyncEndpointPickerState.Stale &&
                staleSession.Draft(SyncPairSide.Right).FolderId is null,
            "EPP-05 the superseded result is reported stale and writes nothing");
        Require(stalePicker.ApplyCloudFolder(secondTicket, "id:fresh", "/Fresh").Applied &&
                staleSession.Draft(SyncPairSide.Right).FolderId == "id:fresh",
            "EPP-05 the current result is applied");
        var deferred = new DeferredBrowser("id:deferred", "/Deferred");
        var deferredTicket = stalePicker.BeginPick();
        var pendingListing = stalePicker.ListFoldersAsync(deferredTicket, deferred, null, CancellationToken.None);
        staleSession.SetCloudAccount(SyncPairSide.Right, dropboxTwo);
        deferred.Release();
        var lateListing = await pendingListing;
        Require(lateListing.State == SyncEndpointPickerState.Stale && lateListing.Folders.Count == 0,
            "EPP-05 a listing that finishes after the side's account changed is stale and returns no folders");
        Require(!stalePicker.ApplyCloudFolder(deferredTicket, "id:deferred", "/Deferred").Applied,
            "EPP-05 the same superseded ticket cannot apply a folder afterwards");
        staleSession.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        Require(!staleSession.IsCurrentPick(deferredTicket),
            "EPP-05 switching away and back to an account never revives its old browse ticket");
        var beforeKindChange = stalePicker.BeginPick();
        staleSession.SetKind(SyncPairSide.Right, SyncEndpointKind.GoogleDrive);
        staleSession.SetKind(SyncPairSide.Right, SyncEndpointKind.Dropbox);
        staleSession.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        Require(!staleSession.IsCurrentPick(beforeKindChange),
            "EPP-05 switching providers away and back never revives a pending browse");
        var beforeNamespaceChange = stalePicker.BeginPick();
        staleSession.SetCloudAccount(SyncPairSide.Right, dropboxOne with { NamespaceId = "ns:changed" });
        Require(!staleSession.IsCurrentPick(beforeNamespaceChange),
            "EPP-05 a changed namespace invalidates the account's pending folder browse");
        staleSession.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        var currentTicket = stalePicker.BeginPick();
        var currentListing = await stalePicker.ListFoldersAsync(currentTicket,
            new ImmediateBrowser("id:current", "/Current"), null, CancellationToken.None);
        Require(currentListing.Applied && currentListing.Folders.Count == 1 &&
                currentListing.Folders[0].FolderId == "id:current",
            "EPP-05 a listing for the side's current choice is applied");
        var noAccountSession = SyncPairEditorSession.ForNewPair(6);
        noAccountSession.SetKind(SyncPairSide.Right, SyncEndpointKind.GoogleDrive);
        var refusedListing = await noAccountSession.PickerFor(SyncPairSide.Right).ListFoldersAsync(
            noAccountSession.PickerFor(SyncPairSide.Right).BeginPick(),
            new ImmediateBrowser("gd-x", "/x"), null, CancellationToken.None);
        Require(refusedListing.State == SyncEndpointPickerState.Refused && refusedListing.Folders.Count == 0,
            "EPP-05 a side with no account is refused a folder listing instead of being shown one");

        Claim("EPP-06-cancel",
            "A cancelled draft applies no browse result and builds no pair");
        var cancelSession = SyncPairEditorSession.ForNewPair(7);
        cancelSession.SetKind(SyncPairSide.Right, SyncEndpointKind.Dropbox);
        cancelSession.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        var cancelTicket = cancelSession.PickerFor(SyncPairSide.Right).BeginPick();
        cancelSession.Cancel();
        Require(!cancelSession.IsActive && !cancelSession.IsCurrentPick(cancelTicket),
            "EPP-06 a ticket issued before the cancellation is no longer current");
        var afterCancel = cancelSession.PickerFor(SyncPairSide.Right)
            .ApplyCloudFolder(cancelTicket, "id:after-cancel", "/AfterCancel");
        Require(afterCancel.State == SyncEndpointPickerState.Stale &&
                cancelSession.Draft(SyncPairSide.Right).FolderId is null,
            "EPP-06 a cancelled draft never applies a late folder result");
        Require(!cancelSession.ExtractDraft().CanSave && !cancelSession.TryBuild(out _, out _),
            "EPP-06 a cancelled draft cannot be saved and produces no pair");
        var cancelLocal = SyncPairEditorSession.ForNewPair(8);
        var cancelLocalTicket = cancelLocal.PickerFor(SyncPairSide.Left).BeginPick();
        cancelLocal.Cancel();
        Require(cancelLocal.PickerFor(SyncPairSide.Left).ApplyLocalFolder(cancelLocalTicket, leftFolder).State ==
                SyncEndpointPickerState.Stale && cancelLocal.Draft(SyncPairSide.Left).LocalPath is null,
            "EPP-06 a cancelled draft never applies a late folder-on-this-computer result");

        Claim("EPP-07-reopen",
            "A reopened editor starts empty and cannot inherit the previous editor's choice or ticket");
        var firstEditor = SyncPairEditorSession.ForNewPair(9);
        firstEditor.PickerFor(SyncPairSide.Left).ApplyLocalFolder(
            firstEditor.PickerFor(SyncPairSide.Left).BeginPick(), leftFolder);
        firstEditor.SetKind(SyncPairSide.Right, SyncEndpointKind.Dropbox);
        firstEditor.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        firstEditor.PickerFor(SyncPairSide.Right)
            .ApplyCloudFolder(firstEditor.PickerFor(SyncPairSide.Right).BeginPick(), "id:first", "/First");
        var previousEditorTicket = firstEditor.PickerFor(SyncPairSide.Right).BeginPick();
        var reopened = SyncPairEditorSession.ForNewPair(10);
        Require(reopened.Draft(SyncPairSide.Left).LocalPath is null &&
                reopened.Draft(SyncPairSide.Right).ConnectionId is null &&
                reopened.Draft(SyncPairSide.Right).FolderId is null,
            "EPP-07 a reopened editor starts with no endpoint identity from the previous editor");
        Require(reopened.Generation != firstEditor.Generation && !reopened.IsCurrentPick(previousEditorTicket),
            "EPP-07 a ticket from the previous editor is not current in the reopened editor");
        var leaked = reopened.PickerFor(SyncPairSide.Right).ApplyCloudFolder(previousEditorTicket, "id:leaked", "/Leaked");
        Require(leaked.State == SyncEndpointPickerState.Stale && reopened.Draft(SyncPairSide.Right).FolderId is null,
            "EPP-07 the previous editor's ticket cannot write into the reopened editor");
        Require(firstEditor.Draft(SyncPairSide.Right).FolderId == "id:first" &&
                firstEditor.Draft(SyncPairSide.Left).LocalPath == leftFolder,
            "EPP-07 the reopened editor never disturbs the editor that is still open");
        var defaultEditorOne = SyncPairEditorSession.ForNewPair();
        var defaultEditorTwo = SyncPairEditorSession.ForNewPair();
        var defaultTicketOne = defaultEditorOne.PickerFor(SyncPairSide.Left).BeginPick();
        var defaultTicketTwo = defaultEditorTwo.PickerFor(SyncPairSide.Left).BeginPick();
        Require(defaultEditorOne.IsCurrentPick(defaultTicketOne) &&
                defaultEditorTwo.IsCurrentPick(defaultTicketTwo) &&
                !defaultEditorTwo.IsCurrentPick(defaultTicketOne),
            "EPP-07 default generations cannot make independent editor sessions share browse authority");

        Claim("EPP-08-empty-no-op",
            "An empty selection is refused without clearing what the side already names");
        var emptySession = SyncPairEditorSession.ForNewPair(11);
        var emptyLeftPicker = emptySession.PickerFor(SyncPairSide.Left);
        emptyLeftPicker.ApplyLocalFolder(emptyLeftPicker.BeginPick(), leftFolder);
        var emptyLocal = emptyLeftPicker.ApplyLocalFolder(emptyLeftPicker.BeginPick(), "   ");
        Require(emptyLocal.State == SyncEndpointPickerState.Refused &&
                emptyLocal.Validation.Errors.Contains(SyncEndpointError.MissingLocalPath),
            "EPP-08 an empty path on this computer is refused");
        Require(emptySession.Draft(SyncPairSide.Left).LocalPath == leftFolder &&
                !emptyLeftPicker.ApplyLocalFolder(emptyLeftPicker.BeginPick(), null).Applied,
            "EPP-08 the refused empty path leaves the side's previous choice exactly as it was");
        emptySession.SetKind(SyncPairSide.Right, SyncEndpointKind.Dropbox);
        emptySession.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        var emptyRightPicker = emptySession.PickerFor(SyncPairSide.Right);
        emptyRightPicker.ApplyCloudFolder(emptyRightPicker.BeginPick(), "id:keep", "/Keep");
        var emptyCloud = emptyRightPicker.ApplyCloudFolder(emptyRightPicker.BeginPick(), null, null);
        Require(emptyCloud.State == SyncEndpointPickerState.Refused &&
                emptyCloud.Validation.Errors.Contains(SyncEndpointError.MissingCloudFolder) &&
                emptySession.Draft(SyncPairSide.Right).FolderId == "id:keep",
            "EPP-08 an empty cloud folder is refused and the previous folder is kept");
        Require(emptyRightPicker.ApplyLocalFolder(emptyRightPicker.BeginPick(), leftFolder).State ==
                SyncEndpointPickerState.Refused,
            "EPP-08 a folder on this computer cannot be chosen for a cloud side");
        Require(emptyLeftPicker.ApplyCloudFolder(emptyLeftPicker.BeginPick(), "id:x", "/x").State ==
                SyncEndpointPickerState.Refused,
            "EPP-08 a cloud folder cannot be chosen for a local side");

        Claim("EPP-09-extraction",
            "The one extraction reports both endpoints, the direction, the refusal and the remaining hook");
        var extraction = SyncPairEditorSession.ForNewPair(12);
        extraction.PickerFor(SyncPairSide.Left).ApplyLocalFolder(
            extraction.PickerFor(SyncPairSide.Left).BeginPick(), leftFolder);
        extraction.SetKind(SyncPairSide.Right, SyncEndpointKind.Dropbox);
        extraction.SetCloudAccount(SyncPairSide.Right, dropboxOne);
        extraction.PickerFor(SyncPairSide.Right)
            .ApplyCloudFolder(extraction.PickerFor(SyncPairSide.Right).BeginPick(), "id:right", "/Right");
        extraction.SetDirection(SyncMode.UploadOnly);
        var boundDraft = extraction.ExtractDraft();
        Require(boundDraft.Endpoints is not null && boundDraft.Direction == SyncMode.UploadOnly &&
                boundDraft.Endpoints!.Direction == SyncMode.UploadOnly,
            "EPP-09 the extraction returns both endpoints and the chosen direction");
        Require(boundDraft.CanSave && boundDraft.Validation.IsValid &&
                boundDraft.UnsupportedBindingReason is null && boundDraft.RuntimeHook is null,
            "EPP-09 a supported, complete draft extracts as saveable with no remaining hook");
        var noIdentity = SyncPairEditorSession.ForNewPair(13);
        noIdentity.SetLocalFolder(SyncPairSide.Left, leftFolder);
        Require(noIdentity.ExtractDraft().Validation.Errors.Contains(SyncEndpointError.MissingLocalIdentity),
            "EPP-09 a local endpoint without its stable identity is refused for persistence");
        var localOnly = SyncPairEditorSession.ForNewPair(14);
        localOnly.PickerFor(SyncPairSide.Left).ApplyLocalFolder(
            localOnly.PickerFor(SyncPairSide.Left).BeginPick(), leftFolder);
        localOnly.SetKind(SyncPairSide.Right, SyncEndpointKind.Local);
        localOnly.PickerFor(SyncPairSide.Right).ApplyLocalFolder(
            localOnly.PickerFor(SyncPairSide.Right).BeginPick(), rightFolder);
        var localDraft = localOnly.ExtractDraft();
        Require(localDraft.CanSave && localDraft.Validation.IsValid &&
                localDraft.UnsupportedBindingReason is null && localDraft.RuntimeHook is null,
            "EPP-09 local/local extracts as a supported complete draft with no remaining hook");
        var sameFolder = SyncPairEditorSession.ForNewPair(15);
        sameFolder.PickerFor(SyncPairSide.Left).ApplyLocalFolder(
            sameFolder.PickerFor(SyncPairSide.Left).BeginPick(), leftFolder);
        sameFolder.SetKind(SyncPairSide.Right, SyncEndpointKind.Local);
        sameFolder.PickerFor(SyncPairSide.Right).ApplyLocalFolder(
            sameFolder.PickerFor(SyncPairSide.Right).BeginPick(), leftFolder);
        Require(sameFolder.ExtractDraft().Validation.Errors.Contains(SyncEndpointError.SameEndpoint),
            "EPP-09 the same folder chosen on both sides is refused before any effect");
        var cloudCloud = SyncPairEditorSession.ForNewPair(16);
        cloudCloud.SetKind(SyncPairSide.Left, SyncEndpointKind.Dropbox);
        cloudCloud.SetCloudAccount(SyncPairSide.Left, dropboxOne);
        cloudCloud.PickerFor(SyncPairSide.Left)
            .ApplyCloudFolder(cloudCloud.PickerFor(SyncPairSide.Left).BeginPick(), "id:ccl", "/Cloud/Left");
        cloudCloud.SetKind(SyncPairSide.Right, SyncEndpointKind.Dropbox);
        cloudCloud.SetCloudAccount(SyncPairSide.Right, dropboxTwo);
        cloudCloud.PickerFor(SyncPairSide.Right)
            .ApplyCloudFolder(cloudCloud.PickerFor(SyncPairSide.Right).BeginPick(), "id:ccr", "/Cloud/Right");
        var cloudDraft = cloudCloud.ExtractDraft();
        Require(cloudDraft.CanSave && cloudDraft.Validation.IsValid &&
                cloudDraft.UnsupportedBindingReason is null && cloudDraft.RuntimeHook is null,
            "EPP-09 cloud/cloud extracts as a supported complete draft with no remaining hook");
        var cloudLeft = SyncPairEditorSession.ForNewPair(17);
        cloudLeft.SetKind(SyncPairSide.Left, SyncEndpointKind.Dropbox);
        cloudLeft.SetCloudAccount(SyncPairSide.Left, dropboxOne);
        cloudLeft.PickerFor(SyncPairSide.Left)
            .ApplyCloudFolder(cloudLeft.PickerFor(SyncPairSide.Left).BeginPick(), "id:cl", "/Cloud/Left");
        cloudLeft.SetKind(SyncPairSide.Right, SyncEndpointKind.Local);
        cloudLeft.PickerFor(SyncPairSide.Right).ApplyLocalFolder(
            cloudLeft.PickerFor(SyncPairSide.Right).BeginPick(), rightFolder);
        var cloudLeftDraft = cloudLeft.ExtractDraft();
        Require(cloudLeftDraft.CanSave && cloudLeftDraft.Validation.IsValid &&
                cloudLeftDraft.UnsupportedBindingReason is null && cloudLeftDraft.RuntimeHook is null,
            "EPP-09 cloud-on-the-left extracts as a supported complete draft with no remaining hook");

        Claim("EPP-10-legacy-and-rebind",
            "A retained legacy pair opens through its own identity and reports a rebind only when an endpoint changes");
        var legacy = LegacyPair("e2p-legacy");
        var legacySession = SyncPairEditorSession.ForExistingPair(legacy, 18, out var legacyValidation);
        Require(legacyValidation.IsValid,
            "EPP-10 the retained legacy pair maps without a refusal");
        Require(legacySession.PickerFor(SyncPairSide.Right).ConnectionId == legacy.ConnectionId &&
                legacySession.Draft(SyncPairSide.Right).FolderId == legacy.DropboxFolderId &&
                legacySession.Draft(SyncPairSide.Left).LocalPath == legacy.LocalRoot,
            "EPP-10 each side opens with its own retained account and folder");
        var unchanged = legacySession.ExtractDraft(legacy, []);
        Require(unchanged.Intent == SyncPairEditIntent.SelectionOnly && unchanged.CanSave &&
                unchanged.Endpoints is not null &&
                SyncEndpointsCompatibility.ToSyncPair(unchanged.Endpoints!, legacy) == legacy,
            "EPP-10 an unchanged retained pair is saveable, selection-only and writes back byte for byte");
        legacySession.SetDirection(SyncMode.DownloadOnly);
        Require(legacySession.ExtractDraft(legacy, []).Intent == SyncPairEditIntent.SelectionOnly,
            "EPP-10 a direction change alone is never a rebind");
        var legacyRightPicker = legacySession.PickerFor(SyncPairSide.Right);
        legacyRightPicker.ApplyCloudFolder(legacyRightPicker.BeginPick(), "id:moved", "/Team/Moved");
        var moved = legacySession.ExtractDraft(legacy, []);
        Require(moved.Intent == SyncPairEditIntent.Rebind && moved.CanSave,
            "EPP-10 choosing a different folder on one retained side is a rebind and stays saveable");
        Require(legacySession.ExtractDraft().Intent == SyncPairEditIntent.Rebind,
            "EPP-10 a retained draft without its retained pair fails closed to a rebind");

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "EndpointPickerChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = root,
            ["assertions"] = _assertions,
            ["cases"] = Results
        };
        File.WriteAllText(Path.Combine(root, "endpoint-picker-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS endpoint pickers: " + _assertions +
            " assertions; each side owns its own kind, account, folder and browse ticket, a superseded or " +
            "late result is refused rather than applied, an empty path changes nothing, cancel and reopen " +
            "leak nothing, and a changed endpoint is a rebind while a retained pair writes back unchanged.");
    }

    /// <summary>
    /// Completes one side through the side-bound picker exactly as the editor does: the kind is chosen
    /// first, the account second and the folder last, and each browse gets its own ticket.
    /// </summary>
    private static void CompleteThroughPickers(SyncPairEditorSession session, SyncPairSide side,
        SyncEndpointKind kind, string localPath, SyncVerifiedAccountBinding dropbox,
        SyncVerifiedAccountBinding google)
    {
        session.SetKind(side, kind);
        var picker = session.PickerFor(side);
        switch (kind)
        {
            case SyncEndpointKind.Local:
                picker.ApplyLocalFolder(picker.BeginPick(), localPath);
                break;
            case SyncEndpointKind.Dropbox:
                session.SetCloudAccount(side, dropbox);
                picker.ApplyCloudFolder(picker.BeginPick(),
                    "id:folder-" + (side == SyncPairSide.Left ? "left" : "right"),
                    "/Team/" + (side == SyncPairSide.Left ? "Left" : "Right"));
                break;
            default:
                session.SetCloudAccount(side, google);
                picker.ApplyCloudFolder(picker.BeginPick(),
                    "gd-folder-" + (side == SyncPairSide.Left ? "left" : "right"),
                    "/Team/" + (side == SyncPairSide.Left ? "Left" : "Right"));
                break;
        }
    }

    private static SyncPair LegacyPair(string name) => new(name, name, "C:/SyncShark/E2P/Legacy", 1234567890u, 42ul,
        "/Team/Project", "id:folder-legacy", ConnectionId: "conn-dropbox-1");

    /// <summary>A transport that answers immediately with one folder, standing in for the provider.</summary>
    private sealed class ImmediateBrowser(string entryId, string entryPath) : IDropboxFolderBrowserService
    {
        public Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DropboxFolderPage(
                new[] { new DropboxFolderEntry("Entry", entryPath, entryId) }, null));

        public Task<DropboxFolderPage> ContinueFoldersAsync(string cursor,
            CancellationToken cancellationToken = default) => throw Unsupported();

        public Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
            CancellationToken cancellationToken = default) => throw Unsupported();

        public Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
            CancellationToken cancellationToken = default) => throw Unsupported();

        public IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
            CancellationToken cancellationToken = default) => throw Unsupported();

        public Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name,
            CancellationToken cancellationToken = default) => throw Unsupported();
    }

    /// <summary>
    /// A transport whose listing completes only when the check releases it, so a result can be made to
    /// arrive after the side has already changed.
    /// </summary>
    private sealed class DeferredBrowser(string entryId, string entryPath) : IDropboxFolderBrowserService
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public async Task<DropboxFolderPage> ListFoldersAsync(string? folderId = null,
            CancellationToken cancellationToken = default)
        {
            await _gate.Task.ConfigureAwait(false);
            return new DropboxFolderPage(new[] { new DropboxFolderEntry("Late", entryPath, entryId) }, null);
        }

        public Task<DropboxFolderPage> ContinueFoldersAsync(string cursor,
            CancellationToken cancellationToken = default) => throw Unsupported();

        public Task<DropboxFolderPage> SearchFoldersAsync(string query, string? folderId = null,
            CancellationToken cancellationToken = default) => throw Unsupported();

        public Task<DropboxFolderPage> ContinueFolderSearchAsync(string cursor,
            CancellationToken cancellationToken = default) => throw Unsupported();

        public IAsyncEnumerable<DropboxFolderEntry> EnumerateFoldersAsync(string? folderId = null,
            CancellationToken cancellationToken = default) => throw Unsupported();

        public Task<DropboxFolderEntry> CreateFolderAsync(string? parentFolderId, string name,
            CancellationToken cancellationToken = default) => throw Unsupported();
    }

    private static NotSupportedException Unsupported() =>
        new("The endpoint picker checks supply folder listings only.");

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
