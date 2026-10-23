using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// Tests the production WindowsCloudFileIdentity DTO and provider-aware hash/binding helpers only.
// No CfApi, scanner substitution, placeholder creation, filesystem writes or user-file access.
internal static class CloudPlaceholderIdentityChecks
{
    internal static async Task RunAsync(string scratchRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        // Kept for the common fixture entrypoint; this packet needs no scratch allocation.
        DurableBinding();
        LegacyDropbox();
        GoogleIdentity();
        BindingAndStampRejections();
        FactoryProviderRejections();
        RootAndFileKinds();
        LegacyRootRefresh();
        MalformedInputs();
        await RealContentAlgorithmsAsync();
        Console.WriteLine("PASS cloud identity serialization, legacy Dropbox bytes, explicit Google SHA-256, factory/provider/pair/algorithm rejection and safe legacy-root refresh; native scanner/CFAPI not exercised");
    }

    private static void DurableBinding()
    {
        var binding = new WindowsCloudFilesBinding("pair", SyncProviders.Dropbox, "account", @"F:\test", 7, 42, "/test", "id:root");
        var state = new SyncCloudFileState(".", SyncCloudFileAvailability.AvailableOffline,
            false, true, 0, 0, null, null, null);
        var root = new WindowsCloudPlaceholderInfo(RoundTrip(binding.RootIdentity()), state, 42, 42, true, 0);
        binding.RequireRoot(root);
        foreach (var wrong in new[] { binding with { PairId = "other" }, binding with { Provider = SyncProviders.GoogleDrive },
                     binding with { AccountId = "other" }, binding with { RemoteRoot = "/other" },
                     binding with { RemoteRootId = "id:other" }, binding with { RootFileId = 43 } })
        {
            try { wrong.RequireRoot(root); throw new Exception("Wrong durable binding was accepted."); }
            catch (IOException) { }
        }
        try { binding.RequireSame(binding with { VolumeSerialNumber = 8 }); throw new Exception("Wrong volume accepted."); }
        catch (InvalidOperationException) { }
    }

    private static readonly string LegacyHash = new('a', 64);
    private static readonly PilotRemoteEntry LegacyEntry = new("folder/example.bin", PilotEntryKind.File,
        "id:legacy-file", "legacy-revision", 7, LegacyHash);
    private static readonly PilotRemoteEntry GoogleEntry = new("folder/example.bin", PilotEntryKind.File,
        "google-file", "[\"google-file\",\"17\"]", 7, new string('b', 64),
        new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero));

    private static void LegacyDropbox()
    {
        var originalRoot = Encoding.UTF8.GetBytes("""{"Marker":"massdex.sync-shark.cloud-files.v1","SchemaVersion":1,"PairId":"legacy-pair","RelativePath":".","ObjectKind":"root","DropboxId":null,"DropboxRevision":null,"LogicalSize":0,"ContentHash":null,"ModifiedUtc":null}""");
        var originalFile = Encoding.UTF8.GetBytes($$"""{"Marker":"massdex.sync-shark.cloud-files.v1","SchemaVersion":1,"PairId":"legacy-pair","RelativePath":"folder/example.bin","ObjectKind":"file","DropboxId":"id:legacy-file","DropboxRevision":"legacy-revision","LogicalSize":7,"ContentHash":"{{LegacyHash}}","ModifiedUtc":null}""");
        var root = Parse(originalRoot);
        var file = Parse(originalFile);
        Require(root.Provider is null && root.ContentAlgorithm is null && root.ObjectKind == "root", "legacy root retains absent optional stamps");
        Require(file.Provider is null && file.ContentAlgorithm is null && file.ObjectKind == "file", "legacy file retains absent optional stamps");
        foreach (var item in new[] { root, file })
        {
            Require(item.EffectiveProvider == SyncProviders.Dropbox &&
                item.EffectiveContentAlgorithm == SyncContentIdentity.DropboxBlockSha256, "missing historical stamps mean Dropbox block hash only");
            Require(item.IsBoundTo("legacy-pair", null) && item.IsBoundTo("legacy-pair", SyncProviders.Dropbox), "historical default and explicit Dropbox pair binding agree");
            Require(!item.IsBoundTo("legacy-pair", SyncProviders.GoogleDrive), "legacy hashes cannot be relabelled as Google SHA-256");
        }
        Require(root.Serialize().SequenceEqual(originalRoot) && file.Serialize().SequenceEqual(originalFile), "legacy serialized bytes are preserved exactly");
        Require(WindowsCloudFileIdentity.Root("legacy-pair").Serialize().SequenceEqual(originalRoot), "default root factory retains historical wire bytes");
        Require(WindowsCloudFileIdentity.FromRemote("legacy-pair", LegacyEntry).Serialize().SequenceEqual(originalFile), "default file factory retains historical wire bytes");
        Require(WindowsCloudFileIdentity.FromRemote("legacy-pair", LegacyEntry, SyncProviders.Dropbox).Serialize().SequenceEqual(originalFile), "explicit Dropbox does not introduce incompatible stamp fields");
        using var wire = JsonDocument.Parse(file.Serialize());
        Require(!wire.RootElement.TryGetProperty("Provider", out _) && !wire.RootElement.TryGetProperty("ContentAlgorithm", out _) &&
            !wire.RootElement.TryGetProperty("EffectiveProvider", out _) && !wire.RootElement.TryGetProperty("EffectiveContentAlgorithm", out _),
            "optional legacy stamps and computed properties are not emitted");
    }

    private static void GoogleIdentity()
    {
        var root = RoundTrip(WindowsCloudFileIdentity.Root("google-pair", SyncProviders.GoogleDrive));
        var file = RoundTrip(WindowsCloudFileIdentity.FromRemote("google-pair", GoogleEntry, SyncProviders.GoogleDrive));
        foreach (var item in new[] { root, file })
        {
            Require(item.Provider == SyncProviders.GoogleDrive && item.ContentAlgorithm == SyncContentIdentity.Sha256,
                "Google identities carry explicit provider and SHA-256 stamps");
            Require(item.IsBoundTo("google-pair", SyncProviders.GoogleDrive), "Google identity binds its exact pair and algorithm");
            using var wire = JsonDocument.Parse(item.Serialize());
            Require(wire.RootElement.GetProperty("Provider").GetString() == SyncProviders.GoogleDrive &&
                wire.RootElement.GetProperty("ContentAlgorithm").GetString() == SyncContentIdentity.Sha256, "Google stamps are present on the wire");
        }
        Require(file.DropboxId == GoogleEntry.Id && file.DropboxRevision == GoogleEntry.Revision &&
            file.ContentHash == GoogleEntry.ContentHash && file.LogicalSize == GoogleEntry.Size &&
            file.ModifiedUtc == GoogleEntry.ModifiedUtc && file.RelativePath == GoogleEntry.RelativePath,
            "compatibility-named fields retain the exact Google item id, revision, size, checksum, date and path");
        Require(WindowsCloudFileIdentity.FromRemote("google-pair", GoogleEntry with { RelativePath = "folder\\example.bin" },
            SyncProviders.GoogleDrive).RelativePath == "folder/example.bin", "factory uses the production relative-path normalization");
        var empty = GoogleEntry with { Size = 0, ContentHash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant() };
        Require(RoundTrip(WindowsCloudFileIdentity.FromRemote("google-pair", empty, SyncProviders.GoogleDrive)).LogicalSize == 0,
            "zero-byte files have a legitimate SHA-256 identity even though range delivery requires a positive range");
    }

    private static void BindingAndStampRejections()
    {
        var google = WindowsCloudFileIdentity.FromRemote("google-pair", GoogleEntry, SyncProviders.GoogleDrive);
        var dropbox = WindowsCloudFileIdentity.FromRemote("legacy-pair", LegacyEntry);
        Require(!google.IsBoundTo("other-pair", SyncProviders.GoogleDrive) &&
            !google.IsBoundTo("GOOGLE-PAIR", SyncProviders.GoogleDrive), "pair identity is exact and case-sensitive");
        Require(!google.IsBoundTo("google-pair", SyncProviders.Dropbox) && !google.IsBoundTo("google-pair", null),
            "Google cannot use a Dropbox/default consumer binding");
        Require(!google.IsBoundTo("google-pair", "future-provider") &&
            !dropbox.IsBoundTo("legacy-pair", "future-provider"), "unknown expected providers do not bind retained identities");
        Require(google.IsBoundTo("google-pair", " GOOGLE-DRIVE "), "known expected provider normalization is deliberate");

        foreach (var malformed in new[]
        {
            google with { Provider = "future-provider" }, google with { Provider = "" },
            google with { Provider = "GOOGLE-DRIVE" }, google with { Provider = "google-drive " },
            google with { Provider = null }, google with { ContentAlgorithm = null },
            google with { ContentAlgorithm = "future-hash" }, google with { ContentAlgorithm = "SHA256" },
            google with { ContentAlgorithm = SyncContentIdentity.Md5 },
            google with { ContentAlgorithm = SyncContentIdentity.DropboxBlockSha256 },
            dropbox with { ContentAlgorithm = SyncContentIdentity.Sha256 },
            dropbox with { ContentAlgorithm = "future-hash" }
        }) Reject(malformed.Serialize(), "unknown, missing or crossed provider/algorithm stamps are rejected");

        // The wire format can explicitly identify Dropbox too; this is distinct from legacy emission.
        Require(RoundTrip(dropbox with { Provider = SyncProviders.Dropbox,
            ContentAlgorithm = SyncContentIdentity.DropboxBlockSha256 }).IsBoundTo("legacy-pair", SyncProviders.Dropbox),
            "explicit valid Dropbox stamp remains readable");
        Require(!SyncContentIdentity.IdentitiesMatch(SyncContentIdentity.DropboxBlockSha256, LegacyHash,
            SyncContentIdentity.Sha256, LegacyHash), "even identical hex strings do not cross algorithm identities");
    }

    private static void FactoryProviderRejections()
    {
        foreach (var provider in new[] { "future-provider", " FUTURE-PROVIDER ", "google-drive-v2" })
        {
            RequireInvalidData(() => WindowsCloudFileIdentity.Root("pair", provider),
                "root factory rejects unknown providers instead of emitting legacy Dropbox bytes");
            RequireInvalidData(() => WindowsCloudFileIdentity.FromRemote("pair", GoogleEntry, provider),
                "file factory rejects unknown providers instead of mislabelling the checksum");
            RequireInvalidData(() => WindowsCloudFileIdentity.FromRemote("pair",
                new PilotRemoteEntry("folder", PilotEntryKind.Directory, "folder-id", null, 0, null), provider),
                "directory factory has the same unknown-provider rejection");
        }
        Require(RoundTrip(WindowsCloudFileIdentity.Root("pair", " GOOGLE-DRIVE "))
                .IsBoundTo("pair", SyncProviders.GoogleDrive) &&
            RoundTrip(WindowsCloudFileIdentity.FromRemote("pair", GoogleEntry, " GOOGLE-DRIVE "))
                .IsBoundTo("pair", SyncProviders.GoogleDrive),
            "factory normalization still accepts the known Google provider");
    }

    private static void LegacyRootRefresh()
    {
        // Drive this exact production admission predicate only with parsed identities, as the
        // registration consumers do. It permits a status-only root refresh, never file adoption.
        var legacy = RoundTrip(WindowsCloudFileIdentity.Root("retained-pair"));
        Require(legacy.CanRefreshRootFor("retained-pair", SyncProviders.GoogleDrive),
            "the same retained pair may refresh an empty historical status-only Google root");
        Require(legacy.CanRefreshRootFor("retained-pair", SyncProviders.Dropbox),
            "normal legacy Dropbox root refresh remains supported");
        Require(!legacy.IsBoundTo("retained-pair", SyncProviders.GoogleDrive) &&
            legacy.Provider is null && legacy.ContentAlgorithm is null,
            "refresh eligibility neither mutates old bytes nor claims their hash is Google SHA-256");
        Require(!legacy.CanRefreshRootFor("other-pair", SyncProviders.GoogleDrive) &&
            !legacy.CanRefreshRootFor("RETAINED-PAIR", SyncProviders.GoogleDrive) &&
            !legacy.CanRefreshRootFor("retained-pair", "future-provider"),
            "legacy-root refresh refuses foreign/case-changed pairs and unknown target providers");

        var google = RoundTrip(WindowsCloudFileIdentity.Root("retained-pair", SyncProviders.GoogleDrive));
        var explicitDropbox = RoundTrip(legacy with
            { Provider = SyncProviders.Dropbox, ContentAlgorithm = SyncContentIdentity.DropboxBlockSha256 });
        Require(google.CanRefreshRootFor("retained-pair", SyncProviders.GoogleDrive) &&
            explicitDropbox.CanRefreshRootFor("retained-pair", SyncProviders.Dropbox),
            "correctly stamped roots can refresh for their own provider");
        Require(!google.CanRefreshRootFor("retained-pair", SyncProviders.Dropbox) &&
            !explicitDropbox.CanRefreshRootFor("retained-pair", SyncProviders.GoogleDrive) &&
            !RoundTrip(legacy with { Provider = SyncProviders.Dropbox })
                .CanRefreshRootFor("retained-pair", SyncProviders.GoogleDrive) &&
            !RoundTrip(legacy with { ContentAlgorithm = SyncContentIdentity.DropboxBlockSha256 })
                .CanRefreshRootFor("retained-pair", SyncProviders.GoogleDrive),
            "any explicit Dropbox/provider hash stamp prevents cross-provider legacy adoption");

        foreach (var provider in new[] { SyncProviders.Dropbox, SyncProviders.GoogleDrive })
        {
            var file = RoundTrip(WindowsCloudFileIdentity.FromRemote("retained-pair", GoogleEntry, provider));
            var folder = RoundTrip(WindowsCloudFileIdentity.FromRemote("retained-pair",
                new PilotRemoteEntry("folder", PilotEntryKind.Directory, "folder-id", null, 0, null), provider));
            foreach (var target in new[] { SyncProviders.Dropbox, SyncProviders.GoogleDrive })
                Require(!file.CanRefreshRootFor("retained-pair", target) &&
                    !folder.CanRefreshRootFor("retained-pair", target),
                    "file and child-directory identities can never take the legacy-root refresh route");
        }

        foreach (var malformed in new[]
        {
            legacy with { RelativePath = "folder" }, legacy with { DropboxId = "file-id" },
            legacy with { DropboxRevision = "revision" }, legacy with { LogicalSize = 1 },
            legacy with { ContentHash = LegacyHash }, legacy with { ModifiedUtc = DateTimeOffset.UnixEpoch }
        })
        {
            Reject(malformed.Serialize(), "a root carrying path/content metadata is not a status-only root");
            Require(!malformed.CanRefreshRootFor("retained-pair", SyncProviders.GoogleDrive),
                "the refresh predicate independently refuses nonempty legacy root metadata");
        }
    }

    private static void RootAndFileKinds()
    {
        foreach (var provider in new[] { SyncProviders.Dropbox, SyncProviders.GoogleDrive })
        {
            var root = RoundTrip(WindowsCloudFileIdentity.Root("pair", provider));
            var file = RoundTrip(WindowsCloudFileIdentity.FromRemote("pair", GoogleEntry, provider));
            var folder = RoundTrip(WindowsCloudFileIdentity.FromRemote("pair",
                new PilotRemoteEntry("folder", PilotEntryKind.Directory, "folder-id", null, 0, null), provider));
            Require(root.ObjectKind == "root" && root.RelativePath == "." && root.DropboxId is null,
                "root factory creates a root marker rather than a fetchable file identity");
            Require(!root.IsBoundTo("other-pair", provider), "a root from another pair does not bind");
            Require(file.ObjectKind == "file" && folder.ObjectKind == "directory" && folder.ContentHash is null,
                "file and directory factories preserve distinct production kinds");
            Reject((root with { ObjectKind = "file" }).Serialize(), "dot-root cannot be parsed as a file");
            Reject((root with { ObjectKind = "directory" }).Serialize(), "dot-root cannot be parsed as a child directory");
            Reject((file with { ObjectKind = "root" }).Serialize(), "a child path cannot be parsed as a sync root");
            Reject((root with { RelativePath = "folder" }).Serialize(), "a root requires its exact dot path");
            Reject((file with { ObjectKind = "shortcut" }).Serialize(), "unknown native object kinds do not become files");
            Reject((root with { Provider = "future-provider" }).Serialize(), "unknown root providers are rejected too");
            Reject((root with { ContentAlgorithm = "future-hash" }).Serialize(), "unknown root hash algorithms cannot register as owned roots");
        }
        // IsBoundTo intentionally checks ownership, not object kind. FetchDataAsync separately
        // requires ObjectKind == file; ScanAsync separately excludes root markers and checks native
        // directory attributes. Do not reproduce those decisions here and claim native coverage.
    }

    private static void MalformedInputs()
    {
        var file = WindowsCloudFileIdentity.FromRemote("pair", GoogleEntry, SyncProviders.GoogleDrive);
        foreach (var invalid in new[]
        {
            file with { Marker = "foreign-product" }, file with { SchemaVersion = 2 },
            file with { PairId = "" }, file with { PairId = " " }, file with { LogicalSize = -1 },
            file with { DropboxId = null }, file with { DropboxId = " " }, file with { DropboxRevision = null },
            file with { DropboxRevision = "" }, file with { ContentHash = null },
            file with { ContentHash = new string('a', 63) }, file with { ContentHash = new string('z', 64) },
            file with { RelativePath = "../escape" }, file with { RelativePath = "folder/../escape" },
            file with { RelativePath = "folder\\example.bin" }, file with { RelativePath = "/folder/example.bin" },
            file with { RelativePath = "folder//example.bin" }, file with { RelativePath = "" }
        }) Reject(invalid.Serialize(), "malformed identity cannot become an owned placeholder");
        foreach (var bytes in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("null"),
            Encoding.UTF8.GetBytes("[]"), Encoding.UTF8.GetBytes("{"), new byte[] { 0xff, 0xfe, 0xff } })
            Reject(bytes, "malformed JSON/UTF-8 returns false without leaking an output identity");
        try { _ = (file with { PairId = new string('x', 5000) }).Serialize(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Cloud identity checks: serialization did not enforce the 4096-byte platform limit.");
    }

    private static async Task RealContentAlgorithmsAsync()
    {
        var input = Encoding.UTF8.GetBytes("abc");
        var sha = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        // A single nonempty Dropbox block: SHA256(concatenated SHA256(block)).
        var block = Convert.ToHexString(SHA256.HashData(SHA256.HashData(input))).ToLowerInvariant();
        Require(sha != block, "the fixture distinguishes whole-file SHA-256 from the Dropbox block algorithm");
        Require(SyncContentIdentity.Compute(SyncProviders.GoogleDrive, input) == sha &&
            SyncContentIdentity.Compute(SyncProviders.Dropbox, input) == block &&
            SyncContentIdentity.Compute(null, input) == block, "production hash dispatch preserves Google and legacy Dropbox semantics");
        using var googleStream = new MemoryStream(input, writable: false);
        using var dropboxStream = new MemoryStream(input, writable: false);
        Require(await SyncContentIdentity.ComputeAsync(SyncProviders.GoogleDrive, googleStream) == sha &&
            await SyncContentIdentity.ComputeAsync(SyncProviders.Dropbox, dropboxStream) == block,
            "the actual async content helper used by PilotLocalScanner selects the bound algorithm");
    }

    private static WindowsCloudFileIdentity Parse(byte[] bytes)
    {
        if (!WindowsCloudFileIdentity.TryDeserialize(bytes, out var identity) || identity is null)
            throw new InvalidOperationException("Cloud identity checks: legitimate identity was rejected.");
        return identity;
    }
    private static WindowsCloudFileIdentity RoundTrip(WindowsCloudFileIdentity identity)
    {
        var restored = Parse(identity.Serialize());
        Require(restored == identity, "serialize/deserialize retains every field");
        return restored;
    }
    private static void Reject(byte[] bytes, string reason) =>
        Require(!WindowsCloudFileIdentity.TryDeserialize(bytes, out var identity) && identity is null, reason);
    private static void RequireInvalidData(Action action, string reason)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Cloud identity checks: " + reason);
    }
    private static void Require(bool value, string reason)
    { if (!value) throw new InvalidOperationException("Cloud identity checks: " + reason); }
}
