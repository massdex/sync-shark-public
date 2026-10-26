using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark;

// Focused proof for the next-iteration symmetric endpoint model (N02): the lossless compatibility
// mapping from the saved SyncPair shape and the accepted/refused persisted endpoint identities.
internal static class EndpointModelChecks
{
    private static readonly List<Dictionary<string, object?>> Results = [];
    private static int _assertions;

    internal static async Task RunAsync(string root, string? sourceRoot = null)
    {
        Directory.CreateDirectory(root);
        Results.Clear();
        _assertions = 0;

        var left = SyncEndpoint.Local("C:/SyncShark/Left");
        var right = SyncEndpoint.Local("C:/SyncShark/Right");
        var sibling = SyncEndpoint.Local("C:/SyncShark/LeftExtra");
        var nested = SyncEndpoint.Local("C:/SyncShark/Left/Child");
        var dropbox = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-dropbox-1", "id:dropbox-folder-1", "/Team/Project");
        var secondDropbox = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-dropbox-2", "id:dropbox-folder-2", "/Team/Project");
        var google = SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-google-1", "gd-folder-1", "/Team/Project");

        Claim("E00-physical-layout", "Provider-independent orientation preserves logical direction and real local identity");
        var boundLocal = SyncEndpoint.Local("C:/SyncShark/Left", new SyncLocalEndpointIdentity(1234, 56));
        foreach (var kind in new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
        foreach (var direction in Enum.GetValues<SyncMode>())
        {
            var cloud = SyncEndpoint.Cloud(kind, "account", "namespace", kind == SyncEndpointKind.Dropbox ? "id:folder" : "folder", "/Folder");
            var forward = SyncPairStore.NormalizePair(SyncEndpointPairDraft.Create(new(boundLocal, cloud, direction), "Forward"));
            var reverse = SyncPairStore.NormalizePair(SyncEndpointPairDraft.Create(new(cloud, boundLocal, direction), "Reverse"));
            Require(forward.LocalRoot == reverse.LocalRoot && forward.LocalRootFileId == reverse.LocalRootFileId &&
                forward.Id != reverse.Id && reverse.Direction == direction &&
                reverse.LeftEndpoint?.Kind == kind && reverse.RightEndpoint?.IsLocal == true,
                "E00 " + kind + " " + direction + " keeps real local binding and distinct ordered identity");
            Require(SyncEndpointEngineLayout.EngineDirection(forward) == direction &&
                SyncEndpointEngineLayout.EngineDirection(reverse) == SyncEndpointEngineLayout.Reverse(direction),
                "E00 " + kind + " " + direction + " maps logical direction to physical planner ports once");
            var editor = SyncPairEditorSession.ForExistingPair(reverse, out var accepted);
            Require(accepted.IsValid && editor.KindOf(SyncPairSide.Left) == kind &&
                editor.KindOf(SyncPairSide.Right) == SyncEndpointKind.Local && editor.Direction == direction,
                "E00 " + kind + " " + direction + " reopens ordered endpoints without reversing the stored policy");
            try
            {
                _ = SyncPairStore.NormalizePair(reverse with { LocalRootFileId = 999 });
                throw new InvalidOperationException("A mismatched physical local identity was accepted.");
            }
            catch (InvalidDataException) { Require(true, "E00 mismatched right-local root identity refuses"); }
        }

        Claim("E01-round-trip", "The adapter round-trips every legacy field of a saved pair without rewriting anything");
        var legacyPairs = new[]
        {
            LegacyPair("legacy-default"),
            LegacyPair("legacy-full") with
            {
                Priority = 3,
                PropagateDeletions = true,
                PolicyRevision = 7,
                Enabled = false,
                Direction = SyncMode.UploadOnly,
                FilesOnDemandEnabled = true,
                RecoveryRetentionDays = 14
            },
            LegacyPair("legacy-google") with
            {
                Provider = SyncProviders.GoogleDrive,
                ConnectionId = "conn-google-1",
                DropboxRoot = "/Team",
                DropboxFolderId = "gd-folder-1"
            },
            LegacyPair("legacy-blank-provider") with { Provider = "" },
            LegacyPair("legacy-mixed-case-provider") with { Provider = "Dropbox" },
            LegacyPair("legacy-path-without-folder-id") with { DropboxFolderId = "" },
            LegacyPair("legacy-trailing-separator-root") with { LocalRoot = "C:\\SyncShark\\Left\\" }
        };
        foreach (var pair in legacyPairs)
        {
            var mapped = SyncEndpointsCompatibility.FromSyncPair(pair);
            Require(mapped.Validation.IsValid, "E01 " + pair.Name + " maps without a refusal");
            Require(mapped.Endpoints is not null, "E01 " + pair.Name + " produces both endpoints");
            var restored = SyncEndpointsCompatibility.ToSyncPair(mapped.Endpoints!, pair);
            Require(restored == pair, "E01 " + pair.Name + " round-trips the whole saved pair unchanged");
            Require(restored.Provider == pair.Provider && restored.ConnectionId == pair.ConnectionId &&
                restored.DropboxRoot == pair.DropboxRoot && restored.DropboxFolderId == pair.DropboxFolderId &&
                restored.LocalRoot == pair.LocalRoot && restored.Direction == pair.Direction,
                "E01 " + pair.Name + " preserves each mapped legacy field exactly");
        }

        Claim("E02-local-local", "Local/local valid non-overlap is accepted");
        Require(SyncEndpoints.TryPair(left, right, SyncMode.TwoWay, out var localPair, out var localValidation) &&
            localValidation.IsValid, "E02 local/local non-overlapping pair is accepted");
        Require(localPair is not null && localPair.Left.IsLocal && localPair.Right.IsLocal,
            "E02 both endpoints stay local");
        Require(SyncEndpoints.TryPair(left, sibling, SyncMode.TwoWay, out _, out var siblingValidation) &&
            siblingValidation.IsValid, "E02 folders that only share a text prefix are accepted as non-overlapping");

        Claim("E03-cloud-pairs", "Cloud pairs with distinct accounts are accepted");
        Require(SyncEndpoints.TryPair(dropbox, google, SyncMode.TwoWay, out _, out var mixedCloudValidation) &&
            mixedCloudValidation.IsValid, "E03 Dropbox and Google Drive endpoints with their own account ids are accepted");
        Require(SyncEndpoints.TryPair(dropbox, secondDropbox, SyncMode.TwoWay, out _, out var twoAccountValidation) &&
            twoAccountValidation.IsValid, "E03 a cloud/cloud pair across two accounts of the same provider is accepted");
        Require(SyncEndpoints.TryPair(left, dropbox, SyncMode.TwoWay, out _, out var currentShapeValidation) &&
            currentShapeValidation.IsValid, "E03 the current local/cloud shape is accepted by the same model");

        Claim("E04-same-endpoint", "The same endpoint on both sides is rejected");
        Require(!SyncEndpoints.TryPair(left, SyncEndpoint.Local("c:/syncshark/left"), SyncMode.TwoWay, out var samePair,
                out var sameValidation) && samePair is null &&
            sameValidation.Errors.Contains(SyncEndpointError.SameEndpoint),
            "E04 the same local endpoint on both sides is refused as same endpoint");
        Require(!SyncEndpoints.TryPair(dropbox,
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-dropbox-1", "id:dropbox-folder-1", "/Team/Project"),
                SyncMode.TwoWay, out _, out var sameCloudValidation) &&
            sameCloudValidation.Errors.Contains(SyncEndpointError.SameEndpoint),
            "E04 the same cloud endpoint on both sides is refused as same endpoint");

        Claim("E05-unknown-provider", "An unknown provider is refused and a blank literal still means Dropbox");
        Require(!SyncEndpoints.TryCloud("box", "conn-x", "id:x", "/Team", out var unknownEndpoint) &&
            unknownEndpoint is null && SyncEndpoints.ValidateCloud("box", "conn-x", "id:x", "/Team")
                .Errors.Contains(SyncEndpointError.UnknownProvider), "E05 an unknown provider is refused");
        Require(!SyncEndpoints.TryCloud("dropboxx", "conn-x", "id:x", "/Team", out _),
            "E05 a provider literal that only looks like Dropbox is refused");
        Require(SyncEndpoints.TryCloud("", "conn-dropbox-1", "id:dropbox-folder-1", "/Team/Project", out var defaulted) &&
            defaulted is not null && defaulted.Kind == SyncEndpointKind.Dropbox,
            "E05 a blank legacy provider literal still maps to Dropbox");

        Claim("E06-missing-cloud-identity", "A missing cloud account or folder is refused");
        Require(!SyncEndpoints.TryCloud(SyncProviders.GoogleDrive, "", "gd-folder-1", "/Team", out var noAccount) &&
            noAccount is null && SyncEndpoints.ValidateCloud(SyncProviders.GoogleDrive, " ", "gd-folder-1", "/Team")
                .Errors.Contains(SyncEndpointError.MissingCloudAccount),
            "E06 a cloud endpoint without an account is refused");
        Require(!SyncEndpoints.TryCloud(SyncProviders.GoogleDrive, "conn-google-1", "", "", out var noFolder) &&
            noFolder is null && SyncEndpoints.ValidateCloud(SyncProviders.GoogleDrive, "conn-google-1", null, null)
                .Errors.Contains(SyncEndpointError.MissingCloudFolder),
            "E06 a cloud endpoint without a folder id or path is refused");
        Require(SyncEndpoints.TryCloud(SyncProviders.Dropbox, "conn-dropbox-1", "", "/Team/Project", out var pathOnly) &&
            pathOnly is not null && pathOnly.FolderId is null && pathOnly.FolderPath == "/Team/Project",
            "E06 a folder path without a provider folder id is accepted and keeps its path");

        Claim("E07-overlapping-roots", "Overlapping local roots are refused on folder segments");
        Require(!SyncEndpoints.TryPair(left, nested, SyncMode.TwoWay, out _, out var nestedValidation) &&
            nestedValidation.Errors.Contains(SyncEndpointError.OverlappingLocalRoots),
            "E07 a local folder inside another local endpoint is refused as overlapping");
        Require(!SyncEndpoints.TryPair(nested, left, SyncMode.TwoWay, out _, out var reverseValidation) &&
            reverseValidation.Errors.Contains(SyncEndpointError.OverlappingLocalRoots),
            "E07 the overlap rule is symmetric");
        Require(SyncEndpoints.LocalRootsOverlap(left.LocalPath!, nested.LocalPath!) &&
            !SyncEndpoints.LocalRootsOverlap(left.LocalPath!, sibling.LocalPath!),
            "E07 overlap follows folder segments, not text prefixes");

        Claim("E08-account-identity", "Two Google accounts with the same folder string stay distinct");
        var secondGoogle = SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-google-2", "gd-folder-2", "/Shared Name");
        var firstGoogle = SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-google-1", "gd-folder-1", "/Shared Name");
        Require(!SyncEndpoints.ReferencesSameStore(firstGoogle, secondGoogle),
            "E08 two Google accounts with the same folder string are distinct endpoints");
        Require(SyncEndpoints.TryPair(firstGoogle, secondGoogle, SyncMode.TwoWay, out _, out var googlePairValidation) &&
            googlePairValidation.IsValid, "E08 the two same-named Google folders form a valid cloud/cloud pair");
        Require(!string.Equals(SyncEndpoints.StoreKey(firstGoogle), SyncEndpoints.StoreKey(secondGoogle),
                StringComparison.Ordinal), "E08 the stable connection id is part of the comparison key");
        var sameFolderIdOtherAccount = SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-google-2", "gd-folder-1", "/Shared Name");
        Require(SyncEndpoints.ReferencesSameStore(firstGoogle, sameFolderIdOtherAccount),
            "E08 the same Google object remains identical through another account");
        Require(!SyncEndpoints.ValidatePair(firstGoogle, sameFolderIdOtherAccount).IsValid,
            "E08 sharing access to an object cannot permit syncing it back onto itself");
        Require(SyncEndpoints.StoreKey(firstGoogle) != SyncEndpoints.StoreKey(sameFolderIdOtherAccount),
            "E08 access-route keys still isolate the selected account even for the same object");
        Require(SyncEndpoints.ReferencesSameStore(
                SyncEndpoint.Local("C:/Alias/One", new SyncLocalEndpointIdentity(7, 9)),
                SyncEndpoint.Local("D:/Alias/Two", new SyncLocalEndpointIdentity(7, 9))),
            "E08 local root identity detects aliases independently of display path");

        Claim("E09-direction-identity", "Direction is separate from endpoint identity");
        var uploadOnly = new SyncPairEndpoints(dropbox, google, SyncMode.UploadOnly);
        var twoWay = new SyncPairEndpoints(dropbox, google, SyncMode.TwoWay);
        Require(uploadOnly != twoWay, "E09 direction is carried by the pair");
        Require(SyncEndpoints.ReferencesSameStore(uploadOnly.Left, twoWay.Left) &&
            SyncEndpoints.ReferencesSameStore(uploadOnly.Right, twoWay.Right),
            "E09 changing direction leaves both endpoint identities unchanged");
        Require(SyncEndpoints.ValidatePair(uploadOnly.Left, uploadOnly.Right).IsValid ==
            SyncEndpoints.ValidatePair(twoWay.Left, twoWay.Right).IsValid,
            "E09 pair validation is independent of direction");

        Claim("E11-legacy-implicit-account", "A legacy implicit account is reported rather than guessed");
        var implicitAccount = SyncEndpointsCompatibility.FromSyncPair(LegacyPair("legacy-implicit-account") with
        {
            ConnectionId = ""
        });
        Require(!implicitAccount.Validation.IsValid && implicitAccount.Endpoints is null &&
            implicitAccount.Validation.Errors.Contains(SyncEndpointError.MissingCloudAccount),
            "E11 a legacy pair with no saved connection id is reported as missing its cloud account");

        Claim("E12-additive-persistence", "The versioned endpoint document is written additively and reads back losslessly");
        var savedPair = new JsonObject
        {
            ["Id"] = "legacy-additive",
            ["Name"] = "Legacy additive",
            ["LocalRoot"] = "C:/SyncShark/Left",
            ["LocalVolumeSerialNumber"] = 1234567890u,
            ["LocalRootFileId"] = 42ul,
            ["DropboxRoot"] = "/Team/Project",
            ["DropboxFolderId"] = "id:folder-1",
            ["ConnectionId"] = "conn-dropbox-1",
            ["Provider"] = "dropbox",
            ["Priority"] = 3,
            ["PropagateDeletions"] = false
        };
        var beforeBytes = savedPair.ToJsonString();
        var persistedLeft = SyncEndpoint.Local("C:/SyncShark/Left",
            new SyncLocalEndpointIdentity(1234567890u, 42ul));
        var persistedRight = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-dropbox-1", "ns:root-1",
            "id:folder-1", "/Team/Project");
        SyncEndpointPersistence.WriteAdditive(savedPair,
            new SyncPairEndpoints(persistedLeft, persistedRight, SyncMode.UploadOnly));
        Require(savedPair.ToJsonString() != beforeBytes, "E12 the additive write changes the saved pair object");
        Require(((JsonValue)savedPair["endpointSchemaVersion"]!).GetValue<int>() == SyncEndpointSchema.SymmetricEndpoints,
            "E12 the written document records the current schema version");
        Require(((JsonValue)savedPair["Priority"]!).GetValue<int>() == 3 &&
                (string?)savedPair["Name"] == "Legacy additive" &&
                (string?)savedPair["DropboxFolderId"] == "id:folder-1" &&
                (bool?)savedPair["PropagateDeletions"] == false,
            "E12 every unrelated saved member is preserved exactly by the additive write");
        Require(SyncEndpointPersistence.TryRead(savedPair, out var readDocument).IsValid && readDocument is not null &&
                readDocument.Version == SyncEndpointSchema.SymmetricEndpoints &&
                readDocument.Left is not null && readDocument.Right is not null &&
                readDocument.Left.LocalPath == "C:/SyncShark/Left" &&
                readDocument.Left.LocalVolumeSerialNumber == 1234567890u && readDocument.Left.LocalRootFileId == 42ul &&
                readDocument.Right.ConnectionId == "conn-dropbox-1" &&
                readDocument.Right.NamespaceId == "ns:root-1" && readDocument.Right.FolderId == "id:folder-1",
            "E12 both endpoint identities read back from the written document");
        Require(SyncEndpointContracts.TryCreate(readDocument!.Left!, readDocument.Right!, SyncMode.UploadOnly,
                out var rebuilt, out var rebuiltValidation) && rebuiltValidation.IsValid &&
                rebuilt!.Left == persistedLeft && rebuilt.Right == persistedRight,
            "E12 the read records rebuild the immutable endpoint value model exactly");

        Claim("E13-legacy-document", "An absent endpoint document keeps its legacy meaning and is not an error");
        var legacyJson = JsonSerializer.Serialize(LegacyPair("legacy-typed-wire"));
        Require(!legacyJson.Contains(SyncEndpointSchema.VersionMember, StringComparison.Ordinal) &&
            !legacyJson.Contains(SyncEndpointSchema.LeftMember, StringComparison.Ordinal) &&
            JsonSerializer.Deserialize<SyncPair>(legacyJson)!.EndpointDocument is null,
            "E13 typed legacy serialization does not introduce a new endpoint document");
        foreach (var persistedEndpoint in new[]
        {
            persistedLeft, persistedRight,
            SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-google-1", "google-root-1", "google-folder-1", "/Team")
        })
        {
            var record = SyncEndpointRecord.From(persistedEndpoint);
            var typed = LegacyPair("typed-wire-pair") with
            {
                EndpointSchemaVersion = SyncEndpointSchema.Current,
                LeftEndpoint = SyncEndpointRecord.From(persistedLeft),
                RightEndpoint = record
            };
            var json = JsonSerializer.Serialize(typed);
            var restored = JsonSerializer.Deserialize<SyncPair>(json)!;
            Require(restored == typed, "E13 typed pair retains exact " + persistedEndpoint.Kind + " endpoint identity");
            Require(SyncEndpointPersistence.TryRead(JsonNode.Parse(json)!.AsObject(), out var wire).IsValid &&
                wire == restored.EndpointDocument, "E13 typed " + persistedEndpoint.Kind + " uses the same endpoint wire contract");
        }
        var partialDocument = JsonNode.Parse(legacyJson)!.AsObject();
        partialDocument[SyncEndpointSchema.LeftMember] = JsonSerializer.SerializeToNode(SyncEndpointRecord.From(persistedLeft));
        Require(!SyncEndpointPersistence.TryRead(partialDocument, out _).IsValid,
            "E13 a partial explicit document cannot silently fall back to legacy identities");
        var badEndpointRejected = false;
        try { JsonSerializer.Deserialize<SyncEndpointRecord>("{\"kind\":\"unknown-provider\"}"); }
        catch (JsonException) { badEndpointRejected = true; }
        Require(badEndpointRejected, "E13 typed endpoint deserialization refuses an unknown provider");
        var legacyOnlyDocument = new JsonObject { ["Id"] = "legacy-only", ["LocalRoot"] = "C:/SyncShark/Left" };
        Require(SyncEndpointPersistence.TryRead(legacyOnlyDocument, out var legacyRead).IsValid && legacyRead is not null &&
                legacyRead.Version == SyncEndpointSchema.LegacyImplicit && legacyRead.Left is null && legacyRead.Right is null,
            "E13 an absent document reads as legacy version 1 with no endpoint records");
        Require(SyncEndpointPersistence.TryRead(null, out var missingDocument)
                .Errors.Contains(SyncEndpointError.MissingEndpointDocument) && missingDocument is null,
            "E13 a missing saved pair object is refused with the exact reason");
        var legacyDocumentMapped = SyncEndpointsCompatibility.FromSyncPair(LegacyPair("legacy-document") with { Priority = 5 });
        Require(legacyDocumentMapped.Validation.IsValid && legacyDocumentMapped.Endpoints is not null &&
                legacyDocumentMapped.Endpoints.Left.IsLocal && legacyDocumentMapped.Endpoints.Right.IsCloud,
            "E13 the legacy version still maps left local and right cloud");
        Require(SyncEndpointsCompatibility.ToSyncPair(legacyDocumentMapped.Endpoints!, legacyDocumentMapped.Legacy) ==
                legacyDocumentMapped.Legacy,
            "E13 the legacy round-trip still retains every saved field");

        Claim("E14-schema-version", "An unknown or malformed endpoint document version fails closed");
        Require(SyncEndpointPersistence.TryRead(new JsonObject
        {
            ["endpointSchemaVersion"] = 99,
            ["leftEndpoint"] = new JsonObject { ["kind"] = "local" },
            ["rightEndpoint"] = new JsonObject { ["kind"] = "local" }
        }, out var futureRead).Errors.Contains(SyncEndpointError.UnsupportedSchemaVersion) && futureRead is null,
            "E14 a future endpoint schema version is refused rather than guessed");
        Require(SyncEndpointPersistence.TryRead(new JsonObject { ["endpointSchemaVersion"] = "two" }, out _)
                .Errors.Contains(SyncEndpointError.InvalidSchemaDocument),
            "E14 a non-integer schema version is refused as an invalid document");
        Require(SyncEndpointPersistence.TryRead(new JsonObject
        {
            ["endpointSchemaVersion"] = 2,
            ["leftEndpoint"] = new JsonObject { ["kind"] = "local" }
        }, out _).Errors.Contains(SyncEndpointError.InvalidSchemaDocument),
            "E14 a version 2 document without its right endpoint is refused");
        Require(SyncEndpointPersistence.TryRead(new JsonObject
        {
            ["endpointSchemaVersion"] = 2,
            ["leftEndpoint"] = new JsonObject { ["kind"] = "box" },
            ["rightEndpoint"] = new JsonObject { ["kind"] = "local" }
        }, out _).Errors.Contains(SyncEndpointError.UnknownProvider),
            "E14 an unsupported persisted endpoint kind is refused");

        Claim("E15-persisted-local-identity", "A persisted local endpoint must carry a complete stable identity");
        Require(SyncEndpointContracts.ValidateRecord(new SyncEndpointRecord(SyncEndpointKind.Local, "C:/SyncShark/Left"))
                .Errors.Contains(SyncEndpointError.MissingLocalIdentity),
            "E15 a local record without an identity is refused");
        Require(SyncEndpointContracts.ValidateRecord(new SyncEndpointRecord(SyncEndpointKind.Local, "C:/SyncShark/Left", 0u, 42ul))
                .Errors.Contains(SyncEndpointError.InvalidLocalIdentity),
            "E15 a zero volume serial is refused as an incomplete identity");
        Require(SyncEndpointContracts.ValidateRecord(new SyncEndpointRecord(SyncEndpointKind.Local, "C:/SyncShark/Left",
                1234567890u, 42ul, ConnectionId: "conn-dropbox-1")).Errors.Contains(SyncEndpointError.EndpointKindMismatch),
            "E15 a local record carrying cloud account identity is refused");
        Require(SyncEndpointContracts.ValidateRecord(new SyncEndpointRecord(SyncEndpointKind.Local, "C:/SyncShark/Left",
                1234567890u, 42ul)).IsValid, "E15 a complete local record is accepted");
        Require(SyncEndpointContracts.ValidateLocalIdentity(SyncEndpoint.Local("C:/SyncShark/Draft"))
                .Errors.Contains(SyncEndpointError.MissingLocalIdentity) &&
                SyncEndpointContracts.ValidateLocalIdentity(SyncEndpoint.Local("C:/SyncShark/Draft",
                    new SyncLocalEndpointIdentity(1u, 2ul))).IsValid,
            "E15 the draft identity rule refuses a path-only endpoint and accepts a bound one");

        Claim("E16-persisted-cloud-identity", "A persisted cloud endpoint is validated against its own provider shape");
        Require(SyncEndpointContracts.ValidateRecord(new SyncEndpointRecord(SyncEndpointKind.Dropbox,
                Provider: "", ConnectionId: "conn-1", FolderId: "id:folder-1"))
                .Errors.Contains(SyncEndpointError.UnknownProvider),
            "E16 explicit endpoint records cannot silently inherit the legacy Dropbox default");
        Require(SyncEndpointProviders.All.Select(provider => provider.Kind)
                .SequenceEqual(SyncEndpointPresentation.KindChoices.Select(choice => choice.Kind)),
            "E16 every registered endpoint provider has a shared presentation entry");
        Require(SyncEndpointContracts.ValidateRecord(new SyncEndpointRecord(SyncEndpointKind.GoogleDrive,
                Provider: SyncProviders.Dropbox, ConnectionId: "conn-1", FolderId: "id:folder-1"))
                .Errors.Contains(SyncEndpointError.EndpointKindMismatch),
            "E16 a record whose provider disagrees with its kind is refused");
        Require(SyncEndpointContracts.ValidateRecord(new SyncEndpointRecord(SyncEndpointKind.Dropbox,
                Provider: "box", ConnectionId: "conn-1", FolderId: "id:folder-1"))
                .Errors.Contains(SyncEndpointError.UnknownProvider),
            "E16 an unknown provider literal is refused");
        Require(SyncEndpointContracts.ValidateRecord(new SyncEndpointRecord(SyncEndpointKind.Dropbox,
                Provider: SyncProviders.Dropbox, ConnectionId: "conn-1", FolderId: "/Team/Project"))
                .Errors.Contains(SyncEndpointError.InvalidCloudFolderIdentity),
            "E16 a Dropbox folder identity that is not a stable id is refused");
        Require(SyncEndpointContracts.ValidateRecord(new SyncEndpointRecord(SyncEndpointKind.GoogleDrive,
                Provider: SyncProviders.GoogleDrive, ConnectionId: "conn-1", FolderId: "root"))
                .Errors.Contains(SyncEndpointError.InvalidCloudFolderIdentity),
            "E16 the Google Drive root alias is refused as a folder identity");
        Require(SyncEndpointContracts.ValidateRecord(new SyncEndpointRecord(SyncEndpointKind.GoogleDrive,
                Provider: SyncProviders.GoogleDrive, ConnectionId: "conn-1", FolderId: "gd-folder-1")).IsValid,
            "E16 an opaque Google Drive folder identity is accepted");
        Require(SyncEndpointContracts.ValidateRecord(new SyncEndpointRecord(SyncEndpointKind.Dropbox,
                Provider: SyncProviders.Dropbox, ConnectionId: "conn-1", FolderId: "id:folder-1",
                LocalPath: "C:/SyncShark/Left")).Errors.Contains(SyncEndpointError.EndpointKindMismatch),
            "E16 a cloud record carrying a local path is refused");

        Claim("E17-cross-pair-protection", "A candidate pair is refused when another retained pair owns the store or an overlapping folder");
        var retainedPairs = new[]
        {
            new SyncPairEndpoints(SyncEndpoint.Local("C:/SyncShark/Left", new SyncLocalEndpointIdentity(1234567890u, 42ul)),
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-dropbox-1", "ns:root-1", "id:folder-1", "/Team/Project"),
                SyncMode.TwoWay),
            new SyncPairEndpoints(SyncEndpoint.Local("C:/Other/Left"),
                SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-google-1", "gd-folder-1", "/Shared Name"),
                SyncMode.TwoWay)
        };
        var unrelatedCloud = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-dropbox-9", "id:folder-9", "/Elsewhere");
        Require(SyncEndpointContracts.ValidatePairAgainstExisting(
                SyncEndpoint.Local("D:/Alias/Left", new SyncLocalEndpointIdentity(1234567890u, 42ul)), unrelatedCloud,
                retainedPairs).Errors.Contains(SyncEndpointError.DuplicateEndpointAcrossPairs),
            "E17 another retained pair holding the same local folder identity is refused even at a different path");
        Require(SyncEndpointContracts.ValidatePairAgainstExisting(SyncEndpoint.Local("C:/SyncShark/Left/Child"),
                unrelatedCloud, retainedPairs).Errors.Contains(SyncEndpointError.OverlappingEndpointAcrossPairs),
            "E17 a local folder nested inside a retained pair folder is refused");
        Require(SyncEndpointContracts.ValidatePairAgainstExisting(SyncEndpoint.Local("C:/SyncShark/LeftExtra"),
                unrelatedCloud, retainedPairs).IsValid,
            "E17 a sibling local folder shares only a text prefix and is accepted");
        Require(SyncEndpointContracts.ValidatePairAgainstExisting(
                SyncEndpoint.Local("C:/SyncShark/New"),
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-dropbox-1", "ns:root-1", "id:folder-1", "/Team/Project"),
                retainedPairs).Errors.Contains(SyncEndpointError.DuplicateEndpointAcrossPairs),
            "E17 a cloud folder already retained by another pair is refused");
        Require(SyncEndpointContracts.ValidatePairAgainstExisting(SyncEndpoint.Local("C:/SyncShark/New"),
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-dropbox-1", "ns:root-1", "id:folder-1-child", "/Team/Project/Child"),
                retainedPairs).Errors.Contains(SyncEndpointError.OverlappingEndpointAcrossPairs),
            "E17 a cloud folder nested inside a retained pair folder is refused");
        Require(SyncEndpointContracts.ValidatePairAgainstExisting(SyncEndpoint.Local("C:/SyncShark/New"),
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-dropbox-9", null, "id:folder-9", "/Team/Project"),
                retainedPairs).IsValid,
            "E17 the same folder name on a different unverified account is not a retained overlap");
        Require(SyncEndpointContracts.ValidatePairAgainstExisting(SyncEndpoint.Local("C:/SyncShark/New"), dropbox,
                Array.Empty<SyncPairEndpoints>()).IsValid,
            "E17 no retained pair means no cross-pair refusal");

        Claim("E18-cloud-overlap", "Cloud overlap uses the verified account, and fails closed when ancestry cannot be established");
        var oneAccount = SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-a", "ns:one", "id:f1", "/Team");
        Require(SyncEndpointOverlap.Compare(oneAccount,
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-a", "ns:one", "id:f1", "/Team")) ==
            SyncCloudOverlap.Overlapping, "E18 the same cloud folder on one account overlaps");
        Require(SyncEndpointOverlap.Compare(oneAccount,
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-a", "ns:one", "id:f2", "/Team/Project")) ==
            SyncCloudOverlap.Overlapping, "E18 a nested cloud folder on one account overlaps");
        Require(SyncEndpointOverlap.Compare(oneAccount,
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-b", null, "id:f9", "/Team")) ==
            SyncCloudOverlap.Distinct, "E18 equal folder names on two unverified accounts stay distinct");
        Require(SyncEndpointOverlap.Compare(oneAccount,
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-b", "ns:two", "id:f1", "/Team")) ==
            SyncCloudOverlap.Overlapping, "E18 a shared object cannot be separated by access namespaces");
        Require(SyncEndpointOverlap.Compare(
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-a", "ns:one", "id:f1", null),
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-a", "ns:one", "id:f2", null)) ==
            SyncCloudOverlap.Unverified, "E18 two different opaque folder identities with no path fail closed");
        Require(SyncEndpointOverlap.ValidateCloudOverlap(
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-a", "ns:one", "id:f1", null),
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-a", "ns:one", "id:f2", null))
                .Errors.Contains(SyncEndpointError.UnverifiedCloudNamespace),
            "E18 the unverified comparison reports the exact refusal");
        Require(SyncEndpointOverlap.Compare(
                SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-g1", "gd-folder-1", "/Shared Name"),
                SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-g2", "gd-folder-2", "/Shared Name")) ==
            SyncCloudOverlap.Unverified, "E18 Google display paths and different accounts do not prove ancestry");
        Require(SyncEndpointOverlap.Compare(
                SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-g1", "ns:one", "gd-folder-1", "/A"),
                SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-g2", "ns:two", "gd-folder-1", "/B")) ==
            SyncCloudOverlap.Overlapping, "E18 Google shared identity precedes namespace and display-name checks");
        Require(SyncEndpointOverlap.Compare(
                SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-g1", "gd-folder-1", "/A"),
                SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, "conn-g1", "gd-folder-2", "/B")) ==
            SyncCloudOverlap.Unverified, "E18 different Google names also require verified parent identities");
        Require(SyncEndpointOverlap.FolderPathsOverlap("/Team/Project", "/Team/Project/Child") &&
                !SyncEndpointOverlap.FolderPathsOverlap("/Team/Project", "/Team/ProjectExtra"),
            "E18 provider folder ancestry follows path segments, not text prefixes");
        Require(SyncEndpointContracts.ValidatePair(oneAccount,
                SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-a", "ns:one", "id:f2", "/Team/Project"))
                .Errors.Contains(SyncEndpointError.OverlappingCloudFolders),
            "E18 the pair rule refuses overlapping cloud folders on one account");
        Require(SyncEndpointContracts.ValidatePair(oneAccount, dropbox).IsValid,
            "E18 the pair rule keeps the preserved same-named-folders-on-two-accounts behaviour");

        Claim("E19-legacy-account-binding", "A legacy implicit account resolves only through one verified legacy primary binding");
        var verifiedBindings = new BindingSource(
        [
            new SyncVerifiedAccountBinding(SyncProviders.Dropbox, "conn-dropbox-1", "ns:root-1", true),
            new SyncVerifiedAccountBinding(SyncProviders.Dropbox, "conn-dropbox-2", "ns:root-2", false)
        ]);
        Require(SyncAccountBindings.Resolve(LegacyPair("legacy-bound") with { ConnectionId = "conn-dropbox-2" }, verifiedBindings)
                is { IsResolved: true, ConnectionId: "conn-dropbox-2" },
            "E19 a saved account that is a verified binding resolves");
        Require(SyncAccountBindings.Resolve(LegacyPair("legacy-unverified") with { ConnectionId = "conn-dropbox-9" }, verifiedBindings)
                .Validation.Errors.Contains(SyncEndpointError.UnavailableAccountBinding),
            "E19 a saved account that is not a verified binding is refused");
        var implicitAccountPair = LegacyPair("legacy-implicit") with { ConnectionId = "" };
        Require(SyncAccountBindings.Resolve(implicitAccountPair, verifiedBindings)
                is { IsResolved: true, ConnectionId: "conn-dropbox-1" },
            "E19 an empty account resolves through the single verified legacy primary binding");
        Require(SyncAccountBindings.Resolve(implicitAccountPair, new BindingSource(
            [
                new SyncVerifiedAccountBinding(SyncProviders.Dropbox, "conn-dropbox-1", "ns:root-1", true),
                new SyncVerifiedAccountBinding(SyncProviders.Dropbox, "conn-dropbox-2", "ns:root-2", true)
            ])).Validation.Errors.Contains(SyncEndpointError.AmbiguousAccountBinding),
            "E19 two verified legacy primary bindings are refused as ambiguous instead of choosing the first");
        Require(SyncAccountBindings.Resolve(implicitAccountPair, new BindingSource([]))
                .Validation.Errors.Contains(SyncEndpointError.UnavailableAccountBinding),
            "E19 no verified legacy primary binding is refused as unavailable");

        Claim("E20-retained-original", "The retained original fixture stays byte-identical and keeps its legacy meaning");
        var retainedFixturePath = Path.Combine(root, "legacy-pair-fixture.json");
        await File.WriteAllTextAsync(retainedFixturePath, new JsonObject
        {
            ["Id"] = "retained",
            ["LocalRoot"] = "C:/SyncShark/Left",
            ["Priority"] = 7,
            ["Provider"] = "dropbox"
        }.ToJsonString());
        var retainedBytes = await File.ReadAllBytesAsync(retainedFixturePath);
        var retainedHash = Convert.ToHexString(SHA256.HashData(retainedBytes)).ToLowerInvariant();
        var workingCopy = JsonNode.Parse(await File.ReadAllTextAsync(retainedFixturePath))!.AsObject();
        SyncEndpointPersistence.WriteAdditive(workingCopy, new SyncPairEndpoints(
            SyncEndpoint.Local("C:/SyncShark/Left", new SyncLocalEndpointIdentity(1u, 2ul)),
            SyncEndpoint.Cloud(SyncEndpointKind.Dropbox, "conn-dropbox-1", "ns:root-1", "id:folder-1", "/Team"),
            SyncMode.TwoWay));
        var retainedAfter = await File.ReadAllBytesAsync(retainedFixturePath);
        Require(Convert.ToHexString(SHA256.HashData(retainedAfter)).ToLowerInvariant() == retainedHash,
            "E20 the retained original fixture is byte-identical after the additive write");
        Require(SyncEndpointPersistence.TryRead(
                JsonNode.Parse(Encoding.UTF8.GetString(retainedAfter))!.AsObject(), out var retainedRead).IsValid &&
                retainedRead!.Version == SyncEndpointSchema.LegacyImplicit,
            "E20 the retained original still reads as the legacy document, so rollback needs no reverse migration");
        Require(SyncEndpointPersistence.TryRead(workingCopy, out var workingRead).IsValid &&
                workingRead!.Version == SyncEndpointSchema.SymmetricEndpoints,
            "E20 the working copy carries the version 2 endpoint document");

        // The original isolated-model assertion now tracks the explicitly integrated consumers. This
        // static inventory is not runtime acceptance; each activated route needs its functional proof.
        Claim("E10-activation-boundary",
            "Production references stay within the explicitly integrated endpoint consumer set");
        var productionConsumers = ScanConsumers(sourceRoot, "production");
        var testConsumers = ScanConsumers(sourceRoot, "tests");
        var declaredE2Surface = new[]
        {
            "src/SyncShark.Core/SyncEndpointPresentation.cs",
            "src/SyncShark.Core/SyncPairEditorSession.cs",
            "src/SyncShark.Core/SyncEndpointPickers.cs",
            "src/SyncShark.Core/SyncEndpointPairRuntime.cs",
            "src/SyncShark.Core/SyncContentIdentity.cs",
            "src/SyncShark.Core/SyncAuthorityRuntimeHost.cs",
            "src/SyncShark.Core/SyncAuthorityRuntimeHost.LocalPairs.cs",
            "src/SyncShark.Core/SyncEndpointPairBinding.cs",
            "src/SyncShark.Core/SyncEndpointEngineLayout.cs",
            "src/SyncShark.Core/SyncCliOperations.cs",
            "src/SyncShark.Core/SyncCloudPairConfiguration.cs",
            "apps/SyncShark/MainWindow.xaml.cs",
            "apps/SyncShark/MainWindow.PairEndpointEditor.cs"
        };
        var undeclaredConsumers = productionConsumers.Where(consumer =>
            !declaredE2Surface.Any(file => consumer.StartsWith(file + "::", StringComparison.Ordinal))).ToArray();
        Require(undeclaredConsumers.Length == 0,
            "E10 every production reference to the endpoint model is inside the declared integration surface" +
            (undeclaredConsumers.Length == 0 ? "" : ": " + string.Join(", ", undeclaredConsumers)));
        Console.WriteLine("PASS E10 declared production references: " +
                          (productionConsumers.Count == 0 ? "none" : string.Join(", ", productionConsumers)));
        Console.WriteLine("PASS E10 test-only references: " + (testConsumers.Count == 0 ? "none" : string.Join(", ", testConsumers)));

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "EndpointModelChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = root,
            ["source_root"] = sourceRoot,
            ["assertions"] = _assertions,
            ["production_consumers"] = productionConsumers,
            ["test_consumers"] = testConsumers,
            ["cases"] = Results
        };
        await File.WriteAllTextAsync(Path.Combine(root, "endpoint-model-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS endpoint model: " + _assertions + " assertions; legacy compatibility, explicit wire identities, pair validation and declared consumer inventory. This model suite does not prove runtime support for every endpoint combination.");
    }

    private static SyncPair LegacyPair(string name) => new(name, name, "C:/SyncShark/Left", 1234567890u, 42ul,
        "/Team/Project", "id:folder-1", ConnectionId: "conn-dropbox-1");

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

    private static List<string> ScanConsumers(string? sourceRoot, string area)
    {
        var consumers = new List<string>();
        if (string.IsNullOrWhiteSpace(sourceRoot)) return consumers;
        var root = Path.GetFullPath(sourceRoot);
        var model = Path.GetFullPath(Path.Combine(root, "src", "SyncShark.Core", "SyncEndpoints.cs"));
        foreach (var directory in area == "production" ? new[] { "src", "apps" } : new[] { "tests" })
        {
            var scanned = Path.Combine(root, directory);
            if (!Directory.Exists(scanned)) continue;
            foreach (var file in Directory.EnumerateFiles(scanned, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                if (string.Equals(Path.GetFullPath(file), model, StringComparison.OrdinalIgnoreCase)) continue;
                var text = File.ReadAllText(file);
                foreach (var identifier in new[]
                         {
                             "SyncEndpointKind", "SyncEndpointValidation", "SyncPairEndpoints", "SyncLegacyEndpoints",
                             "SyncEndpointsCompatibility"
                         })
                    if (text.Contains(identifier, StringComparison.Ordinal))
                        consumers.Add(Path.GetRelativePath(root, file).Replace('\\', '/') + "::" + identifier);
            }
        }
        return consumers;
    }
}

/// <summary>The verified account bindings the E19 legacy account rule resolves against.</summary>
internal sealed class BindingSource(IReadOnlyList<SyncVerifiedAccountBinding> bindings) : ISyncVerifiedAccountBindings
{
    public IReadOnlyList<SyncVerifiedAccountBinding> CandidatesFor(string provider) =>
        bindings.Where(item => string.Equals(item.Provider, provider, StringComparison.Ordinal)).ToArray();
}
