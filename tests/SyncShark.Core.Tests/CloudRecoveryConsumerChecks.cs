using System.Runtime.Versioning;
using Massdex.SyncShark;
using Rig = CloudPairHostBindingChecks.HostRig;
using Spec = CloudPairHostBindingChecks.EndpointSpec;
using Fault = CloudPairHostBindingChecks.HostWire.RecoveryMemberFault;

[SupportedOSPlatform("windows")]
internal static class CloudRecoveryConsumerChecks
{
    private static int _checks;
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use a fresh Recovery consumer fixture.");
        Directory.CreateDirectory(root);
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        _checks = 0;
        foreach (var malformed in new[] { "[1]", "null", "{", "{\"identityKind\":null}",
                     "{\"identityKind\":3}", "{\"identityKind\":\"unknown\"}" })
            await Refused(() => Task.FromResult(SyncAuthorityCloudRecoveryDescriptor.TryParse(malformed, out _)),
                "Malformed explicit identity cannot fall back to legacy");
        Check(!SyncAuthorityCloudRecoveryDescriptor.TryParse("{\"FileId\":\"legacy\"}", out _),
            "Genuine legacy object keeps legacy classification");
        foreach (var left in new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
        foreach (var right in new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
            await JourneyAsync(left, right);
        await DefaultRecoveryMemberAsync();
        await CloudRecoveryRetentionDependencyChecks.RunAsync(Path.Combine(root, "retention-dependencies"));
        Console.WriteLine("PASS shared cloud Recovery consumers: " + _checks + " assertions");
    }

    private static Spec Endpoint(SyncEndpointKind kind, string name) => kind == SyncEndpointKind.Dropbox
        ? CloudPairHostBindingChecks.HostDropbox(name, "/Recovery test " + name)
        : CloudPairHostBindingChecks.HostDrive(name, "/Recovery test " + name);

    private static SyncPair Draft(Spec left, Spec right, string name, SyncPair? prior = null) =>
        SyncEndpointPairDraft.Create(new SyncPairEndpoints(
            SyncEndpoint.Cloud(left.Kind, left.ConnectionId, left.EndpointNamespaceId, left.FolderId, left.FolderPath),
            SyncEndpoint.Cloud(right.Kind, right.ConnectionId, right.EndpointNamespaceId, right.FolderId, right.FolderPath),
            SyncMode.TwoWay), name, prior);

    private static async Task JourneyAsync(SyncEndpointKind leftKind, SyncEndpointKind rightKind)
    {
        var tag = leftKind + "-" + rightKind;
        using var rig = await Rig.CreateAsync("recovery-" + tag);
        var left = Endpoint(leftKind, tag + "-L");
        var right = Endpoint(rightKind, tag + "-R");
        await rig.AddEndpointAsync(left);
        await rig.AddEndpointAsync(right);
        foreach (var path in new[] { "left-one.txt", "left-two.txt", "right.txt" })
        {
            rig.SeedFile(left, path, path == "left-two.txt" ? "" : "Original " + path);
            rig.SeedFile(right, path, path == "left-two.txt" ? "" : "Original " + path);
        }
        var bound = await rig.Host.BindEndpointPairAsync(Draft(left, right, tag) with { RecoveryRetentionDays = 90 });
        await rig.Host.RefreshBootstrapInventoryAsync(bound.Pair.Id);
        await rig.Host.ApproveBootstrapMergeAsync(bound.Pair.Id);
        var enabled = await rig.Host.SetPairEnabledAsync(bound.Pair.Id, true);
        var pair = enabled.Pairs.Single(p => p.Id == bound.Pair.Id);
        await Run(rig, pair);
        rig.SeedFile(left, "left-one.txt", "Changed left one");
        rig.SeedFile(left, "left-two.txt", "Changed left two");
        rig.SeedFile(right, "right.txt", "Changed right");
        await Run(rig, pair);

        var artifacts = (await rig.Db.LoadRecoveryArtifactsAsync()).Where(a => a.PairId == pair.Id).ToArray();
        Check(artifacts.Length == 3, tag + ": real update path produced three predecessor artifacts");
        var beforeRequests = rig.Wire.TotalRequests;
        var catalog = await rig.Host.LoadRecoveryCatalogAsync(pair.Id);
        var page = await rig.Host.LoadRecoveryCatalogPageAsync(pair.Id);
        Check(rig.Wire.TotalRequests == beforeRequests, tag + ": both catalog routes are metadata-only");
        Check(catalog.Select(i => i.Key).Order().SequenceEqual(page.Items.Select(i => i.Key).Order()),
            tag + ": paged and unpaged catalog have the same items");
        foreach (var item in catalog)
        {
            var artifact = artifacts.Single(a => a.RecoveryArtifactId == item.Key);
            Check(SyncAuthorityCloudRecoveryDescriptor.TryParse(artifact.IdentityJson, out var descriptor),
                tag + ": typed artifact retained");
            var expected = descriptor!.LogicalSide == SyncAuthorityCloudSides.LogicalLeft ? left : right;
            Check(item.Provider == expected.Provider && item.ConnectionId == expected.ConnectionId &&
                  item.LogicalSide == descriptor.LogicalSide && !item.Health.StartsWith("Blocked"),
                tag + ": catalog binds original provider/account/side: " + item.Health);
            // CC4E correction: the retention, non-removal and identity guarantees are unchanged, and the exact
            // supported, retained, metadata-valid typed cloud file route this catalogue resolution bound is now
            // eligible to restore. CanRestore stays presentation: the action revalidates every authority.
            Check(!item.CanRemove && item.CanRestore && item.CanRestoreCopy &&
                  item.Provider == expected.Provider && item.ConnectionId == expected.ConnectionId,
                tag + ": retention enforced and the exact supported typed route is eligible to restore");
            await Refused(() => rig.Host.RemoveRecoveryCopyAsync(item with { CanRemove = true }),
                tag + ": caller cannot bypass retention");
        }
        Check(rig.Wire.RecoveryDeleteRequests == 0, tag + ": protected attempts issue zero delete requests");
        foreach (var item in catalog)
            await VerifyDownloadAsync(rig, item, left, right);
        await SupplementalDownloadRefusalsAsync(rig, pair, catalog, left, right);
        var edited = await rig.Host.BindEndpointPairAsync(Draft(left, right, tag, pair) with { RecoveryRetentionDays = null },
            pair.Id, pair.PolicyRevision);
        pair = edited.Pair;
        catalog = await rig.Host.LoadRecoveryCatalogAsync(pair.Id);
        Check(catalog.All(i => i.CanRemove), tag + ": explicit retention policy change enables manual cleanup");

        // Persist deliberately invalid descriptors through the real artifact registration entry, never
        // by replacing the resolver/decision. Their joins must refuse before any provider request.
        var original = artifacts[0];
        SyncAuthorityCloudRecoveryDescriptor.TryParse(original.IdentityJson, out var originalDescriptor);
        var operation = await rig.Db.LoadOperationAsync(original.OperationId);
        var allocation = operation.Allocations.Single(a => a.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        var invalid = new[]
        {
            originalDescriptor! with { RecoveryCorpusId = "foreign-corpus" },
            originalDescriptor! with { ProviderAccountId = "foreign-account" },
            originalDescriptor! with { EndpointRootBindingId = "foreign-root" },
            originalDescriptor! with { RecoveryRootBindingId = "foreign-recovery-root" },
            originalDescriptor! with { NativeAlgorithm = "foreign-hash" },
            originalDescriptor! with { RetainedFileId = "foreign-file" },
            originalDescriptor! with { LogicalSide = originalDescriptor!.LogicalSide == "local" ? "dropbox" : "local" }
        };
        for (var index = 0; index < invalid.Length; index++)
            await rig.Db.RegisterRecoveryArtifactAsync("invalid-recovery-" + index, original.OperationId,
                original.ObjectId, allocation.AllocationId, "dropbox", invalid[index].Serialize(), original.ContentHash,
                original.Size);
        beforeRequests = rig.Wire.TotalRequests;
        var invalidRows = (await rig.Host.LoadRecoveryCatalogAsync(pair.Id)).Where(i => i.Key.StartsWith("invalid-")).ToArray();
        Check(invalidRows.Length == invalid.Length && invalidRows.All(i => !i.CanRemove && i.Health.StartsWith("Blocked")),
            tag + ": malformed authority remains visible as unavailable");
        Check(invalidRows.All(i => !i.CanRestore),
            tag + ": malformed, rebound or disconnected typed items stay visible but disabled for restore");
        foreach (var invalidRow in invalidRows)
            await Refused(() => rig.Host.RemoveRecoveryCopyAsync(invalidRow with { CanRemove = true }),
                tag + ": retained descriptor mismatch refuses cleanup");
        Check(rig.Wire.TotalRequests == beforeRequests, tag + ": mismatched retained authority makes no provider requests");

        foreach (var item in catalog) rig.Wire.AllowedRecoveryDeletionIds.Add(item.Identity!);
        var first = catalog[0];
        await rig.Host.RemoveRecoveryCopyAsync(first with { IdentityJson = "{}", Identity = "untrusted", Location = "/",
            Provider = "untrusted", Source = SyncRecoverySource.LocalArchive, CanRemove = false });
        Check(rig.Wire.RecoveryDeleteRequests == 1, tag + ": cleanup reloads authority instead of caller-supplied fields");
        await rig.Host.RemoveRecoveryCopyAsync(first);
        Check(rig.Wire.RecoveryDeleteRequests == 1, tag + ": released replay issues no second effect");

        var uncertain = catalog[1];
        rig.Wire.LoseNextRecoveryDeleteResponse = true;
        await Refused(() => rig.Host.RemoveRecoveryCopyAsync(uncertain), tag + ": lost delete response is not success");
        Check((await rig.Db.LoadRecoveryArtifactsAsync()).Single(a => a.RecoveryArtifactId == uncertain.Key).Status == "release_authorized",
            tag + ": unknown delete is not falsely marked released");
        var deletes = rig.Wire.RecoveryDeleteRequests;
        await rig.Host.RemoveRecoveryCopyAsync(uncertain);
        Check(rig.Wire.RecoveryDeleteRequests == deletes &&
              (await rig.Db.LoadRecoveryArtifactsAsync()).Single(a => a.RecoveryArtifactId == uncertain.Key).Status == "released",
            tag + ": authorized resume verifies absence without repeating deletion");

        var changed = catalog[2];
        SyncAuthorityCloudRecoveryDescriptor.TryParse(changed.IdentityJson, out var changedDescriptor);
        var changedSpec = changedDescriptor!.LogicalSide == "local" ? left : right;
        ISyncRemoteStore changedStore = changedSpec.Kind == SyncEndpointKind.Dropbox
            ? rig.DropboxClient(changedSpec) : rig.DriveClient(changedSpec);
        var changedBinding = new SyncRemoteRecoveryRootBinding(changedDescriptor.RecoveryPath,
            changedDescriptor.RecoveryFolderId, changedDescriptor.RecoveryNamespaceId);
        var changedMember = new SyncArchiveResult(changedDescriptor.RetainedFileId, changedDescriptor.RetainedRevision!,
            changedDescriptor.RetainedPath, changedDescriptor.Size, changedDescriptor.ContentHash!);
        rig.Wire.DownloadBodyForTest = (id, bytes) =>
        {
            if (id == changed.Identity) rig.Wire.ChangeRecoveryMember(id);
            return bytes;
        };
        try
        {
            using var destination = new MemoryStream();
            await Refused(() => changedStore.DownloadRecoveryAsync(changedBinding, changedMember, destination),
                tag + ": post-read changed archive is not accepted");
        }
        finally { rig.Wire.DownloadBodyForTest = null; }
        await Refused(() => rig.Host.RemoveRecoveryCopyAsync(changed), tag + ": changed member is untouched");
        Check(rig.Wire.RecoveryDeleteRequests == deletes &&
              (await rig.Db.LoadRecoveryArtifactsAsync()).Single(a => a.RecoveryArtifactId == changed.Key).Status == "retained",
            tag + ": changed member never reaches authorization or deletion");
        Console.WriteLine("PASS " + tag + " actual Host Recovery catalog and cleanup");
    }

    private static async Task VerifyDownloadAsync(Rig rig, SyncRecoveryCatalogItem item, Spec left, Spec right)
    {
        SyncAuthorityCloudRecoveryDescriptor.TryParse(item.IdentityJson, out var descriptor);
        var recorded = descriptor!;
        var spec = recorded.LogicalSide == "local" ? left : right;
        ISyncRemoteStore store = spec.Kind == SyncEndpointKind.Dropbox ? rig.DropboxClient(spec) : rig.DriveClient(spec);
        var binding = new SyncRemoteRecoveryRootBinding(recorded.RecoveryPath, recorded.RecoveryFolderId,
            recorded.RecoveryNamespaceId);
        var expected = new SyncArchiveResult(recorded.RetainedFileId, recorded.RetainedRevision!,
            recorded.RetainedPath, recorded.Size, recorded.ContentHash!);
        using var destination = new MemoryStream();
        var receipt = await store.DownloadRecoveryAsync(binding, expected, destination);
        var bytes = System.Text.Encoding.UTF8.GetBytes(recorded.OriginalPath == "left-two.txt"
            ? "" : "Original " + recorded.OriginalPath);
        Check(destination.ToArray().SequenceEqual(bytes) && receipt.FileId == expected.FileId &&
              receipt.Revision == expected.Revision && receipt.Size == expected.Size &&
              receipt.ContentHash == expected.ContentHash, "Exact native archive streaming including zero-length content");
        foreach (var wrong in new[] { expected with { FileId = "foreign-file" },
                     expected with { Revision = "foreign-revision" }, expected with { Size = expected.Size + 1 },
                     expected with { ContentHash = new string('0', 64) },
                     expected with { RecoveryPath = "../foreign" } })
            await Refused(() => store.DownloadRecoveryAsync(binding, wrong, destination), "Changed archive identity refused");
        foreach (var wrong in new[] { binding with { RootFolderId = "foreign-root" },
                     binding with { RootNamespaceId = "foreign-account" } })
            await Refused(() => store.DownloadRecoveryAsync(wrong, expected, destination), "Crossed Recovery binding refused");
        foreach (var fault in new[] { "oversized", "truncated", "corrupt", "interrupted" })
        {
            if (expected.Size == 0 && fault is "truncated" or "corrupt") continue;
            rig.Wire.DownloadBodyForTest = (id, source) => id != expected.FileId ? source : fault switch
            {
                "oversized" => source.Concat(new byte[] { 1 }).ToArray(),
                "truncated" => source[..^1],
                "corrupt" => source.Select(value => (byte)(value ^ 1)).ToArray(),
                _ => throw new IOException("Synthetic transport interruption")
            };
            try { await Refused(() => store.DownloadRecoveryAsync(binding, expected, destination), "Bad body: " + fault); }
            finally { rig.Wire.DownloadBodyForTest = null; }
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await store.DownloadRecoveryAsync(binding, expected, destination, cancellation.Token);
            throw new InvalidOperationException("Cancelled archive download was accepted.");
        }
        catch (OperationCanceledException) { Check(true, "Archive cancellation propagates"); }
        await Refused(() => store.DownloadAsync(new PilotRemoteEntry(recorded.OriginalPath, PilotEntryKind.File,
            expected.FileId, expected.Revision, expected.Size, expected.ContentHash), destination),
            "Ordinary pair-root download still rejects an archive outside its selected sync root");
    }

    /// <summary>
    /// One retained Recovery catalogue entry as the real adapter must be handed it: the endpoint's own live
    /// provider client, the recorded bound Recovery root, the exact retained archive identity and the bytes
    /// that identity was retained from.
    /// </summary>
    private sealed record DownloadTarget(ISyncRemoteStore Store, SyncRemoteRecoveryRootBinding Binding,
        SyncArchiveResult Member, string Identity, byte[] ExpectedBytes);

    /// <summary>
    /// The supplemental refusal cases for one pair's own retained copies: the recorded-root and account or
    /// corpus bindings, a transport that fails or is cancelled after reading started, and each individual
    /// post-read identity change. Every mutation is a synthetic provider response or state change and is
    /// restored, so no case can mask another.
    /// </summary>
    private static async Task SupplementalDownloadRefusalsAsync(Rig rig, SyncPair pair,
        IReadOnlyList<SyncRecoveryCatalogItem> catalog, Spec left, Spec right)
    {
        var writesBefore = rig.Wire.Writes.Total;
        var deletesBefore = rig.Wire.RecoveryDeleteRequests;
        var catalogBefore = CatalogState(await rig.Host.LoadRecoveryCatalogAsync(pair.Id));
        var artifactsBefore = ArtifactState(await rig.Db.LoadRecoveryArtifactsAsync(), pair.Id);

        foreach (var spec in new[] { left, right })
        {
            var item = MemberFor(catalog, spec, left, right);
            if (item is null) continue;
            var target = Target(rig, item, spec);
            if (spec.Kind == SyncEndpointKind.Dropbox) await DropboxRefusalsAsync(rig, target);
            else await DriveRefusalsAsync(rig, target);
        }

        Check(rig.Wire.Writes.Total == writesBefore, "Supplemental refusals issue no provider write effect");
        Check(rig.Wire.RecoveryDeleteRequests == deletesBefore,
            "Supplemental refusals issue no recovery delete request");
        Check(CatalogState(await rig.Host.LoadRecoveryCatalogAsync(pair.Id)).SequenceEqual(catalogBefore),
            "The Recovery catalogue is unchanged by the supplemental refusals");
        Check(ArtifactState(await rig.Db.LoadRecoveryArtifactsAsync(), pair.Id).SequenceEqual(artifactsBefore),
            "The Recovery allocation state is unchanged by the supplemental refusals");
    }

    private static SyncRecoveryCatalogItem? MemberFor(IReadOnlyList<SyncRecoveryCatalogItem> catalog, Spec spec,
        Spec left, Spec right)
    {
        foreach (var candidate in catalog)
        {
            if (candidate.Identity is null) continue;
            if (!SyncAuthorityCloudRecoveryDescriptor.TryParse(candidate.IdentityJson, out var descriptor) ||
                descriptor is null) continue;
            var side = string.Equals(descriptor.LogicalSide, SyncAuthorityCloudSides.LogicalLeft,
                StringComparison.Ordinal) ? left : right;
            if (side != spec) continue;
            if (descriptor.Size <= 0) continue;
            return candidate;
        }
        return null;
    }

    private static DownloadTarget Target(Rig rig, SyncRecoveryCatalogItem item, Spec spec)
    {
        SyncAuthorityCloudRecoveryDescriptor.TryParse(item.IdentityJson, out var descriptor);
        var recorded = descriptor!;
        Check(string.Equals(item.Identity, recorded.RetainedFileId, StringComparison.Ordinal),
            "The catalogue identity is the retained native Recovery object the transport serves");
        var store = spec.Kind == SyncEndpointKind.Dropbox
            ? (ISyncRemoteStore)rig.DropboxClient(spec)
            : rig.DriveClient(spec);
        var binding = new SyncRemoteRecoveryRootBinding(recorded.RecoveryPath, recorded.RecoveryFolderId,
            recorded.RecoveryNamespaceId);
        var member = new SyncArchiveResult(recorded.RetainedFileId, recorded.RetainedRevision!,
            recorded.RetainedPath, recorded.Size, recorded.ContentHash!);
        var bytes = System.Text.Encoding.UTF8.GetBytes(recorded.OriginalPath == "left-two.txt"
            ? "" : "Original " + recorded.OriginalPath);
        Check(bytes.LongLength == recorded.Size, "The retained reviewed bytes carry the recorded retained length");
        return new DownloadTarget(store, binding, member, recorded.RetainedFileId, bytes);
    }

    private static async Task DropboxRefusalsAsync(Rig rig, DownloadTarget target)
    {
        var downloadsBefore = rig.Wire.DropboxDownloadRequests;
        rig.Wire.FaultDropboxRecoveryMember(target.Identity, Fault.Membership);
        try
        {
            using var destination = new MemoryStream();
            var error = await RefusedWith(() => target.Store.DownloadRecoveryAsync(target.Binding, target.Member,
                    destination),
                "Dropbox: a retained native id moved outside the recorded Recovery root is refused");
            Report("Dropbox moved-member refusal", error);
            Check(error.Message.Contains("moved outside its retained membership", StringComparison.Ordinal),
                "Dropbox: the moved member reached the recorded-root membership validation");
            Check(destination.Length == 0, "Dropbox: the moved recovery copy accepts no bytes");
            Check(rig.Wire.DropboxDownloadRequests == downloadsBefore,
                "Dropbox: the moved recovery copy is refused before any content read");
        }
        finally { rig.Wire.ClearDropboxRecoveryMemberFault(target.Identity); }

        rig.Wire.CurrentAccountOverride = ("dbid-supplemental-foreign", "ns-supplemental-foreign",
            "ns-supplemental-foreign-home");
        try
        {
            using var destination = new MemoryStream();
            var error = await RefusedWith(() => target.Store.DownloadRecoveryAsync(target.Binding, target.Member,
                    destination),
                "Dropbox: the current account no longer matching the binding is refused");
            Report("Dropbox changed-account refusal", error);
            Check(error.Message.Contains("account or namespace differs", StringComparison.Ordinal),
                "Dropbox: the changed account reached the bound account/namespace validation");
            Check(destination.Length == 0, "Dropbox: the changed-account refusal accepts no bytes");
            Check(rig.Wire.DropboxDownloadRequests == downloadsBefore,
                "Dropbox: the changed-account refusal precedes any content read");
        }
        finally { rig.Wire.CurrentAccountOverride = null; }

        await MidStreamRefusalsAsync(rig, target, dropbox: true);
        await PostReadFaultsAsync(rig, target, dropbox: true);
    }

    private static async Task DriveRefusalsAsync(Rig rig, DownloadTarget target)
    {
        var mediaBefore = rig.Wire.DriveMediaReadRequests;
        rig.Wire.FaultDriveRecoveryMember(target.Identity, Fault.Membership);
        try
        {
            using var destination = new MemoryStream();
            var error = await RefusedWith(() => target.Store.DownloadRecoveryAsync(target.Binding, target.Member,
                    destination),
                "Google Drive: a retained native id moved outside the recorded Recovery root is refused");
            Report("Drive moved-member refusal", error);
            Check(error.Message.Contains("exact retained Google Drive recovery copy changed",
                    StringComparison.Ordinal),
                "Drive: the moved member reached the retained-copy membership validation");
            Check(destination.Length == 0, "Drive: the moved recovery copy accepts no bytes");
            Check(rig.Wire.DriveMediaReadRequests == mediaBefore,
                "Drive: the moved recovery copy is refused before any media read");
        }
        finally { rig.Wire.ClearDriveRecoveryMemberFault(target.Identity); }

        rig.Wire.FaultDriveRecoveryMember(target.Identity, Fault.Corpus, "drive-corpus-supplemental");
        try
        {
            using var destination = new MemoryStream();
            var error = await RefusedWith(() => target.Store.DownloadRecoveryAsync(target.Binding, target.Member,
                    destination),
                "Google Drive: the member corpus no longer matching the binding is refused");
            Report("Drive changed-corpus refusal", error);
            Check(error.Message.Contains("bound to My Drive", StringComparison.Ordinal),
                "Drive: the changed corpus reached the pair's own corpus membership validation");
            Check(destination.Length == 0, "Drive: the changed-corpus refusal accepts no bytes");
            Check(rig.Wire.DriveMediaReadRequests == mediaBefore,
                "Drive: the changed-corpus refusal precedes any media read");
        }
        finally { rig.Wire.ClearDriveRecoveryMemberFault(target.Identity); }

        await MidStreamRefusalsAsync(rig, target, dropbox: false);
        await PostReadFaultsAsync(rig, target, dropbox: false);
    }

    private static int Reads(Rig rig, bool dropbox) =>
        dropbox ? rig.Wire.DropboxDownloadRequests : rig.Wire.DriveMediaReadRequests;

    /// <summary>
    /// A bounded transport that fails after the first bytes of the declared object have been read, and then a
    /// real cancellation after reading has started. Both must refuse and return no accepted result.
    /// </summary>
    private static async Task MidStreamRefusalsAsync(Rig rig, DownloadTarget target, bool dropbox)
    {
        Check(target.ExpectedBytes.Length > 0, "The mid-stream refusal fixture needs nonempty reviewed bytes");

        var readsBefore = Reads(rig, dropbox);
        rig.Wire.RecoveryTransportStreamForTest = id => id == target.Identity
            ? new FaultingRecoveryStream(target.ExpectedBytes, 1) : null;
        try
        {
            using var destination = new MemoryStream();
            var error = await RefusedWith(() => target.Store.DownloadRecoveryAsync(target.Binding, target.Member,
                    destination),
                "A transport failing after the first read byte returns no accepted recovery result");
            Report("mid-stream failure refusal", error);
            Check(Reads(rig, dropbox) == readsBefore + 1,
                "The failing transport reached the adapter's own content read branch");
            Check(destination.Length == 1,
                "Only the first read byte is present and the partial content is not accepted");
        }
        finally { rig.Wire.RecoveryTransportStreamForTest = null; }

        var readsBeforeCancel = Reads(rig, dropbox);
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        rig.Wire.RecoveryTransportStreamForTest = id => id == target.Identity
            ? new CancellingRecoveryStream(target.ExpectedBytes, 1, firstRead) : null;
        try
        {
            using var destination = new MemoryStream();
            var pending = target.Store.DownloadRecoveryAsync(target.Binding, target.Member, destination,
                cancellation.Token);
            await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(60));
            cancellation.Cancel();
            try
            {
                await pending;
                throw new InvalidOperationException("A cancelled recovery download was accepted.");
            }
            catch (OperationCanceledException) { Check(true, "A cancellation after reading started is refused"); }
            Check(Reads(rig, dropbox) == readsBeforeCancel + 1,
                "The cancelled read reached the adapter's own content read branch");
            Check(destination.Length <= 1,
                "Only bytes read before cancellation are present and the content is not accepted");
        }
        finally { rig.Wire.RecoveryTransportStreamForTest = null; }
    }

    /// <summary>
    /// Each individual post-read change of one retained member's own identity: its native id, its length, its
    /// checksum and its membership. The reviewed body is read first and the exact provider response is then
    /// changed, so only the adapter's own post-read validation can refuse it.
    /// </summary>
    private static async Task PostReadFaultsAsync(Rig rig, DownloadTarget target, bool dropbox)
    {
        Check(target.ExpectedBytes.Length > 0, "The post-read refusal fixture needs nonempty reviewed bytes");
        foreach (var fault in new[] { Fault.NativeId, Fault.Size, Fault.Checksum, Fault.Membership })
        {
            var readsBefore = Reads(rig, dropbox);
            rig.Wire.DownloadBodyForTest = (id, bytes) =>
            {
                if (id == target.Identity)
                {
                    if (dropbox) rig.Wire.FaultDropboxRecoveryMember(target.Identity, fault);
                    else rig.Wire.FaultDriveRecoveryMember(target.Identity, fault);
                }
                return bytes;
            };
            try
            {
                using var destination = new MemoryStream();
                var error = await RefusedWith(() => target.Store.DownloadRecoveryAsync(target.Binding,
                        target.Member, destination),
                    "A post-read " + fault + " change is refused");
                Report("post-read " + fault + " refusal", error);
                Check(Reads(rig, dropbox) == readsBefore + 1,
                    "The reviewed body was read before the altered " + fault + " response was validated");
                Check(destination.ToArray().SequenceEqual(target.ExpectedBytes),
                    "The bytes read before the " + fault + " change were the reviewed bytes and were not accepted");
            }
            finally
            {
                rig.Wire.DownloadBodyForTest = null;
                if (dropbox) rig.Wire.ClearDropboxRecoveryMemberFault(target.Identity);
                else rig.Wire.ClearDriveRecoveryMemberFault(target.Identity);
            }
        }
    }

    /// <summary>
    /// A store that implements only the required members and declares no recovery download of its own, so the
    /// interface's own default fail-closed member is the one every recovery read must reach.
    /// </summary>
    private static async Task DefaultRecoveryMemberAsync()
    {
        var binding = new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery", "folder-default",
            "namespace-default");
        var member = new SyncArchiveResult("id-default", "rev-default",
            "/Apps/Sync Shark/Recovery/default-recovery-copy.bin", 4, new string('a', 64));
        // The recovery read is reached through the interface, exactly as a future adapter is consumed, so the
        // interface's own default member is the one every caller inherits.
        var minimal = new MinimalSyncRemoteStore();
        ISyncRemoteStore store = minimal;
        using var destination = new MemoryStream();
        try
        {
            await store.DownloadRecoveryAsync(binding, member, destination);
            throw new InvalidOperationException("A store without its own recovery download was accepted.");
        }
        catch (NotSupportedException exception)
        {
            Report("default fail-closed recovery member", exception);
            Check(exception.Message.Contains("cannot read an exact retained recovery copy", StringComparison.Ordinal),
                "A minimally implemented store uses the interface default fail-closed recovery member");
        }
        Check(destination.Length == 0, "The default fail-closed recovery member writes zero bytes");
        Check(minimal.DownloadCalls == 0,
            "The default fail-closed recovery member never calls the ordinary DownloadAsync");
    }

    private sealed class MinimalSyncRemoteStore : ISyncRemoteStore
    {
        internal int DownloadCalls { get; private set; }

        public Task VerifyRootIdentityAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<PilotRemoteEntry>> ListEntriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PilotRemoteEntry>>([]);

        public Task<PilotUploadResult> UploadCreateOnlyAsync(string relativePath, Stream source,
            string expectedContentHash, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This minimal store fixture writes nothing.");

        public Task VerifyControlNamespaceAsync(SyncRemoteControlNamespaceBinding binding, string installationId,
            string namespaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PilotUploadResult> UploadAsync(string relativePath, Stream source, string expectedContentHash,
            string? expectedRemoteRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This minimal store fixture writes nothing.");

        public Task<SyncDownloadResult> DownloadAsync(PilotRemoteEntry expected, Stream destination,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            throw new NotSupportedException("This minimal store fixture performs no ordinary pair download.");
        }

        public Task<SyncArchiveResult> CopyToRecoveryAsync(PilotRemoteEntry expected, string recoveryPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This minimal store fixture retains nothing.");

        public Task<SyncArchiveResult?> GetRecoveryAsync(string recoveryPath,
            CancellationToken cancellationToken = default) => Task.FromResult<SyncArchiveResult?>(null);

        public Task DeleteAsync(PilotRemoteEntry expected, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This minimal store fixture deletes nothing.");
    }

    /// <summary>
    /// A bounded transport stream that yields the reviewed bytes for one read and then fails, so the real
    /// adapter's own streaming branch observes a failure after at least one byte was read while the response
    /// still declares the whole object's own metadata and length.
    /// </summary>
    private sealed class FaultingRecoveryStream(byte[] bytes, int firstChunk) : Stream
    {
        private bool _yielded;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.LongLength;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => Yield(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Yield(buffer.Span));

        private int Yield(Span<byte> buffer)
        {
            if (_yielded) throw new IOException("Synthetic transport failure after the first bytes were read.");
            _yielded = true;
            var take = Math.Min(Math.Min(firstChunk, buffer.Length), bytes.Length);
            bytes.AsSpan(0, take).CopyTo(buffer);
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A bounded transport stream that yields the reviewed bytes for one read and then waits until the real
    /// caller cancels it, so cancellation after reading has started is observed inside the streaming branch.
    /// </summary>
    private sealed class CancellingRecoveryStream(byte[] bytes, int firstChunk, TaskCompletionSource firstRead)
        : Stream
    {
        private bool _yielded;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.LongLength;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => Yield(buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_yielded) return Yield(buffer.Span);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Synthetic transport cancellation was not signalled.");
        }

        private int Yield(Span<byte> buffer)
        {
            if (_yielded) throw new OperationCanceledException(
                "Synthetic transport cancellation after the first bytes were read.");
            _yielded = true;
            var take = Math.Min(Math.Min(firstChunk, buffer.Length), bytes.Length);
            bytes.AsSpan(0, take).CopyTo(buffer);
            firstRead.TrySetResult();
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string[] CatalogState(IReadOnlyList<SyncRecoveryCatalogItem> catalog) => catalog
        .Select(item => item.Key + "|" + item.Health + "|" + item.CanRemove + "|" + item.CanRestore)
        .Order(StringComparer.Ordinal).ToArray();

    private static string[] ArtifactState(IReadOnlyList<SyncRecoveryArtifactProjection> artifacts, string pairId) =>
        artifacts.Where(artifact => artifact.PairId == pairId)
            .Select(artifact => artifact.RecoveryArtifactId + "|" + artifact.Status)
            .Order(StringComparer.Ordinal).ToArray();

    private static void Report(string label, Exception error) =>
        Console.WriteLine("  " + label + ": " + error.GetType().Name + ": " + error.Message);

    private static async Task<Exception> RefusedWith(Func<Task> action, string label)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                      IOException or ArgumentException or HttpRequestException)
        { Check(true, label); return error; }
        throw new InvalidOperationException("Expected safe refusal: " + label);
    }

    private static async Task Run(Rig rig, SyncPair pair)
    {
        var result = await rig.Host.RunPairsAsync(new[] { pair }, "manual");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            { diagnostic = "public recovery consumer run", result }));
        Check(result.Items.Count == 1 && result.Items[0].FailureMessage is null &&
              result.Items[0].Result is { Blocked: 0, Conflicts: 0 },
            "Actual default Host run completes: " + result.Items.FirstOrDefault()?.FailureMessage);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _checks++;
    }

    private static async Task Refused(Func<Task> action, string label)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or
                                      IOException or ArgumentException or HttpRequestException)
        { Check(true, label); return; }
        throw new InvalidOperationException("Expected safe refusal: " + label);
    }
}
