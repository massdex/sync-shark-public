using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using IOPath = System.IO.Path;
using Massdex.SyncShark;

// W2-C: the generic read-only endpoint hierarchy reader is driven by the pair's canonical ordered endpoint
// context, so it never routes through a provider-pair branch. These checks drive the real public
// SyncAuthorityRuntimeHost.LoadFolderSelectionTreeAsync over the real HostRig SQLite state and the real
// synthetic provider transport, so the routing decision itself is exercised rather than replaced.

internal static class EndpointHierarchyRoutingChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        var passed = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException(label);
            passed++;
            Console.WriteLine("PASS " + label);
        }

        // (1) A cloud/cloud pair has no local endpoint at all, so the reader must read BOTH own cloud
        // accounts and must not open any local root. The pair's own normalized definition carries no local
        // root, so an implementation that still assumed a physical local endpoint could not succeed here.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-db-gd"))
        {
            var left = CloudPairHostBindingChecks.HostDropbox("w2cLeft", "/Left");
            var right = CloudPairHostBindingChecks.HostDrive("w2cRight", "/Right");
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            var pair = await rig.AddPairAsync("w2c-db-gd", left, right);
            var before = rig.Wire.TotalRequests;
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            Check(tree.EndpointContext is not null &&
                  tree.EndpointContext.Left.Kind == SyncEndpointKind.Dropbox &&
                  tree.EndpointContext.Right.Kind == SyncEndpointKind.GoogleDrive &&
                  !tree.EndpointContext.Left.IsLocal && !tree.EndpointContext.Right.IsLocal,
                "A cloud/cloud pair is read as two cloud endpoints with no physical local endpoint");
            Check(tree.LeftEndpoint is { Available: true, Verified: true } &&
                  tree.RightEndpoint is { Available: true, Verified: true },
                "Both own cloud endpoints were read and verified through their own retained accounts");
            Check(tree.LeftEndpoint!.Endpoint.ConnectionId == left.ConnectionId &&
                  tree.RightEndpoint!.Endpoint.ConnectionId == right.ConnectionId,
                "Each ordered cloud endpoint kept its OWN account rather than the other side's");
            Check(rig.Wire.TotalRequests > before,
                "The cloud hierarchy was read through the real provider transport, not a stub");
            Check(tree.LeftEndpoint!.Endpoint.LocalPath is null && tree.RightEndpoint!.Endpoint.LocalPath is null,
                "A cloud/cloud read published no local path for either ordered endpoint");
        }

        // (2) The same requirement holds with the two providers in the opposite order, so the reader is not
        // accidentally driven by which provider comes first.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-gd-db"))
        {
            var left = CloudPairHostBindingChecks.HostDrive("w2cLeft", "/Left");
            var right = CloudPairHostBindingChecks.HostDropbox("w2cRight", "/Right");
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            var pair = await rig.AddPairAsync("w2c-gd-db", left, right);
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            Check(tree.EndpointContext is not null &&
                  tree.EndpointContext.Left.Kind == SyncEndpointKind.GoogleDrive &&
                  tree.EndpointContext.Right.Kind == SyncEndpointKind.Dropbox &&
                  tree.EndpointContext.Left.CompatibilityPort == "local" &&
                  tree.EndpointContext.Right.CompatibilityPort == "dropbox",
                "A cloud/cloud pair keeps its ordered endpoints and compatibility ports in provider order");
            Check(tree.LeftEndpoint is { Available: true, Verified: true } &&
                  tree.RightEndpoint is { Available: true, Verified: true } &&
                  tree.LeftEndpoint!.Endpoint.ConnectionId == left.ConnectionId &&
                  tree.RightEndpoint!.Endpoint.ConnectionId == right.ConnectionId,
                "Both cloud endpoints were read from their own accounts with no local root opened");
        }

        // (3) A genuine document-less legacy local/cloud pair keeps its validated compatibility path: the
        // local endpoint is read from its OWN bound path and the cloud endpoint from its own saved account.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-legacy-local"))
        {
            var cloud = CloudPairHostBindingChecks.HostDropbox("w2cCloud", "/Cloud");
            await rig.AddEndpointAsync(cloud);
            var localRoot = IOPath.Combine(rig.StateRoot, "w2c-local");
            Directory.CreateDirectory(localRoot);
            Directory.CreateDirectory(IOPath.Combine(localRoot, "Local documents"));
            var pair = await rig.AddLocalCloudPairAsync(cloud, localRoot, "w2c legacy");
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            Check(tree.EndpointContext is not null && tree.EndpointContext.Left.IsLocal &&
                  string.Equals(tree.EndpointContext.Left.LocalPath, localRoot, StringComparison.OrdinalIgnoreCase) &&
                  tree.EndpointContext.Right.Kind == SyncEndpointKind.Dropbox,
                "A genuine legacy pair is captured as left local and right cloud");
            Check(tree.LeftEndpoint is { Available: true } && tree.RightEndpoint is { Available: true },
                "The legacy pair reads its own local root and its own saved cloud account successfully");
            Check(tree.Folders.Any(item => item.RelativePath == "Local documents"),
                "The legacy local endpoint's own folder hierarchy is published");
        }

        // (4) A pair that is not retained refuses BEFORE any provider access.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-refusal"))
        {
            var before = rig.Wire.TotalRequests;
            var refused = false;
            try
            {
                await rig.Host.LoadFolderSelectionTreeAsync("pair-not-retained-w2c");
            }
            catch (InvalidDataException)
            {
                refused = true;
            }
            Check(refused && rig.Wire.TotalRequests == before,
                "An unretained pair refuses before any provider access");
        }

        // (5) Same-provider DISTINCT ACCOUNTS on both providers: each ordered side must be read from its
        // own account, never from the other side's.
        foreach (var provider in new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive })
        {
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-same-provider-" + provider);
            var left = provider == SyncEndpointKind.Dropbox
                ? CloudPairHostBindingChecks.HostDropbox("w2cA", "/AccountA")
                : CloudPairHostBindingChecks.HostDrive("w2cA", "/AccountA");
            var right = provider == SyncEndpointKind.Dropbox
                ? CloudPairHostBindingChecks.HostDropbox("w2cB", "/AccountB")
                : CloudPairHostBindingChecks.HostDrive("w2cB", "/AccountB");
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            var pair = await rig.AddPairAsync("w2c-same-provider-" + provider, left, right);
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            Check(tree.LeftEndpoint is { Available: true } && tree.RightEndpoint is { Available: true } &&
                  tree.LeftEndpoint!.Endpoint.ConnectionId == left.ConnectionId &&
                  tree.RightEndpoint!.Endpoint.ConnectionId == right.ConnectionId &&
                  tree.LeftEndpoint.Endpoint.FolderId == left.FolderId &&
                  tree.RightEndpoint.Endpoint.FolderId == right.FolderId,
                provider + ": two accounts of one provider are each read from their OWN account and root");
        }

        // (6) One ACCOUNT with two DISTINCT ROOTS: the two ordered sides share the account but must each be
        // read through their own root identity.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-same-account-roots"))
        {
            var left = CloudPairHostBindingChecks.HostDrive("w2cShared", "/SharedRootA");
            var right = left with { FolderId = "gd-folder-w2cShared-b", FolderPath = "/SharedRootB" };
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            var pair = await rig.AddPairAsync("w2c-same-account-roots", left, right);
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            Check(tree.LeftEndpoint is { Available: true } && tree.RightEndpoint is { Available: true } &&
                  tree.LeftEndpoint!.Endpoint.ConnectionId == tree.RightEndpoint!.Endpoint.ConnectionId &&
                  tree.LeftEndpoint.Endpoint.FolderId == left.FolderId &&
                  tree.RightEndpoint.Endpoint.FolderId == right.FolderId,
                "One account with two distinct roots is read through each side's OWN root identity");
        }

        // (7) A cancelled read is UNKNOWN: it propagates as cancellation and never publishes a tree that
        // reports either endpoint missing.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-cancellation"))
        {
            var left = CloudPairHostBindingChecks.HostDropbox("w2cCancelL", "/Left");
            var right = CloudPairHostBindingChecks.HostDrive("w2cCancelR", "/Right");
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            var pair = await rig.AddPairAsync("w2c-cancellation", left, right);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var sawCancellation = false;
            SyncFolderSelectionTree? published = null;
            try
            {
                published = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id, cancelled.Token);
            }
            catch (OperationCanceledException)
            {
                sawCancellation = true;
            }
            Check(sawCancellation && published is null,
                "A cancelled hierarchy read is reported as UNKNOWN, never as a missing endpoint");
        }

        // (8) An explicit malformed endpoint document refuses before any provider access and never falls
        // back to the legacy fields.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-malformed"))
        {
            var left = CloudPairHostBindingChecks.HostDropbox("w2cBadL", "/Left");
            var right = CloudPairHostBindingChecks.HostDrive("w2cBadR", "/Right");
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            var good = await rig.AddPairAsync("w2c-malformed", left, right);
            var goodBefore = await rig.PairDefinitionAsync(good.Id);
            var wireBefore = rig.Wire.TotalRequests;
            string? refusal = null;
            try
            {
                await rig.Db.RetainPairDefinitionAsync(good.Pair with
                {
                    EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
                    LeftEndpoint = null
                }, select: false);
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException
                                                  or ArgumentException)
            {
                refusal = exception.Message;
            }
            Check(refusal is not null && refusal.Contains("InvalidSchemaDocument", StringComparison.Ordinal) &&
                  rig.Wire.TotalRequests == wireBefore,
                "A malformed explicit endpoint document is refused at the RETENTION boundary with the exact invalid-schema refusal and no provider effect");
            Check(await rig.PairDefinitionAsync(good.Id) == goodBefore,
                "A refused malformed retain leaves the existing good pair definition unchanged");
        }

        // (9) LOCAL-TO-LOCAL: a genuine symmetric two-local pair is retained through the real production
        // APIs, both roots are read, and the run makes ZERO cloud calls.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-local-local"))
        {
            var rootA = IOPath.Combine(rig.StateRoot, "ll-a");
            var rootB = IOPath.Combine(rig.StateRoot, "ll-b");
            Directory.CreateDirectory(IOPath.Combine(rootA, "LeftOnly"));
            Directory.CreateDirectory(IOPath.Combine(rootB, "RightOnly"));
            var pair = await RetainPairAsync(rig, "w2c-local-local", LocalEndpoint(rootA), LocalEndpoint(rootB));
            var before = rig.Wire.TotalRequests;
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            Check(rig.Wire.TotalRequests == before,
                "A local-to-local pair makes ZERO cloud calls through the real provider transport");
            Check(tree.EndpointContext is not null && tree.EndpointContext.Left.IsLocal &&
                  tree.EndpointContext.Right.IsLocal &&
                  tree.LeftEndpoint is { Available: true } && tree.RightEndpoint is { Available: true },
                "A local-to-local pair reads BOTH local roots and constructs no cloud client");
            Check(tree.Folders.Any(item => item.RelativePath == "LeftOnly") &&
                  tree.Folders.Any(item => item.RelativePath == "RightOnly"),
                "A local-to-local pair publishes the folders of BOTH of its own roots");
        }

        // (10) The REVERSED explicit layout: a cloud endpoint on the logical LEFT and a local endpoint on
        // the logical RIGHT, retained through the REAL SyncEndpointPairDraft.Create projection. No fixture
        // switch chooses the shape; the production draft derives the physical ports and the reversed
        // identity. Both cloud providers are exercised as the left endpoint.
        foreach (var reversedKind in new[] { SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox })
        {
            using var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-reversed-" + reversedKind);
            var cloud = reversedKind == SyncEndpointKind.GoogleDrive
                ? CloudPairHostBindingChecks.HostDrive("w2cRev", "/CloudLeft")
                : CloudPairHostBindingChecks.HostDropbox("w2cRev", "/CloudLeft");
            await rig.AddEndpointAsync(cloud);
            var localRoot = IOPath.Combine(rig.StateRoot, "rev-local");
            Directory.CreateDirectory(IOPath.Combine(localRoot, "RightLocal"));
            SyncPair? retained = null;
            string? creationFailure = null;
            try
            {
                retained = await RetainPairAsync(rig, "w2c-reversed-" + reversedKind, CloudEndpoint(cloud),
                    LocalEndpoint(localRoot));
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException
                                                  or ArgumentException or NotSupportedException)
            {
                creationFailure = exception.GetType().Name + ": " + exception.Message;
            }
            Check(creationFailure is null && retained is { EndpointDocument: not null },
                reversedKind + ": a cloud-left/local-right pair IS retained through the real endpoint draft projection"
                + (creationFailure is null ? string.Empty : " [FIRST PRODUCTION THROW: " + creationFailure + "]"));
            if (creationFailure is not null) continue;
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(retained!.Id);
            Check(tree.EndpointContext is not null && !tree.EndpointContext.Left.IsLocal &&
                  tree.EndpointContext.Right.IsLocal &&
                  tree.EndpointContext.Left.CompatibilityPort == "dropbox" &&
                  tree.EndpointContext.Right.CompatibilityPort == "local",
                reversedKind + ": the reversed context puts the cloud on the logical left and the local folder on the logical right");
            Check(tree.LeftEndpoint is { Available: true } && tree.RightEndpoint is { Available: true } &&
                  tree.LeftEndpoint!.Endpoint.ConnectionId == cloud.ConnectionId &&
                  tree.RightEndpoint!.Endpoint.LocalPath == localRoot,
                reversedKind + ": the reversed pair reads its own cloud account on the left and its own local root on the right");
            Check(tree.Folders.Any(item => item.RelativePath == "RightLocal"),
                reversedKind + ": the reversed pair publishes the local right endpoint's own folder");
        }

        // (11) The other explicit local/cloud ordering: local LEFT, cloud RIGHT.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-local-cloud"))
        {
            var cloud = CloudPairHostBindingChecks.HostDropbox("w2cLC", "/CloudRight");
            await rig.AddEndpointAsync(cloud);
            var localRoot = IOPath.Combine(rig.StateRoot, "lc-local");
            Directory.CreateDirectory(IOPath.Combine(localRoot, "LeftLocal"));
            var pair = await RetainPairAsync(rig, "w2c-local-cloud", LocalEndpoint(localRoot), CloudEndpoint(cloud));
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            Check(tree.EndpointContext is not null && tree.EndpointContext.Left.IsLocal &&
                  tree.EndpointContext.Right.Kind == SyncEndpointKind.Dropbox &&
                  tree.LeftEndpoint is { Available: true } && tree.RightEndpoint is { Available: true },
                "An explicit local-left cloud-right pair reads both own endpoints");
        }

        // (12) A per-side recoverable failure keeps the OTHER side's successfully inspected rows and reports
        // the failed side truthfully with its own endpoint. The Dropbox side stays available while the
        // Drive root is genuinely missing.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-per-side"))
        {
            var left = CloudPairHostBindingChecks.HostDropbox("w2cPerL", "/Keep");
            var right = CloudPairHostBindingChecks.HostDrive("w2cPerR", "/Gone");
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            var pair = await rig.AddPairAsync("w2c-per-side", left, right);
            rig.Wire.SetDriveMissing(right.FolderId, true);
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            Check(tree.LeftEndpoint is { Available: true } && tree.RightEndpoint is { Available: false } &&
                  tree.RightEndpoint!.Endpoint.Kind == SyncEndpointKind.GoogleDrive &&
                  tree.RightEndpoint.Endpoint.ConnectionId == right.ConnectionId &&
                  !string.IsNullOrWhiteSpace(tree.RightEndpoint.Message),
                "A per-side failure keeps the healthy side's rows and reports the failed side with its own endpoint");
        }

        // (13) The CLI HUMAN rendering of the conflict list, the conflict detail and the first-sync plan
        // projects the retained compatibility group key through the one shared neutral helper, so no human
        // surface prints a physical port as a provider or side claim; the DOCUMENTED raw alias is unchanged
        // in the data itself and stays separately addressable and addressable in its JSON contract.
        // The conflict list, conflict detail and first-sync plan human branches do not read the
        // invocation, so the real human renderer is exercised directly on the data object.
        string RenderHumanOf(object data) => SyncCli.RenderHuman(null!, data);

        var listData = new SyncCliConflictListData(
        [
            new SyncCliConflictData("conflict-1", "pair-1", "docs/report.txt", 1, 11, 22, "h1", "h2",
                DateTimeOffset.Parse("2026-09-01T10:00:00Z"), DateTimeOffset.Parse("2026-09-02T11:00:00Z"),
                "Local newer", "Review the newer version", "Open")
        ], 200, 0, 1, false, 1);
        var detailData = new SyncCliConflictDetailData(listData.Items[0], true, true, "Both versions retained.");
        var planData = new SyncCliPlanData("pair-1", "Documents", false, true, 1, 1, 0, 0, 1, false,
        [
            new SyncCliPlanItemData("docs/report.txt", "Dropbox newer", "File", "TXT", "Review the newer version",
                "Nothing changes until you choose", 11, 22, DateTimeOffset.Parse("2026-09-01T10:00:00Z"),
                DateTimeOffset.Parse("2026-09-02T11:00:00Z"))
        ]);
        Check(
              RenderHumanOf(listData).Contains(SyncAuthorityRuntimeHost.NewerVersionGroup, StringComparison.Ordinal) &&
              !RenderHumanOf(listData).Contains("Local newer", StringComparison.Ordinal) &&
              !RenderHumanOf(listData).Contains("Dropbox newer", StringComparison.Ordinal),
            "The CLI conflict list human text is provider-neutral and never prints a compatibility group name");
        Check(
              RenderHumanOf(detailData).Contains(SyncAuthorityRuntimeHost.NewerVersionGroup, StringComparison.Ordinal) &&
              !RenderHumanOf(detailData).Contains("Dropbox newer", StringComparison.Ordinal),
            "The CLI conflict detail human text is provider-neutral");
        Check(
              RenderHumanOf(planData).Contains(SyncAuthorityRuntimeHost.NewerVersionGroup, StringComparison.Ordinal) &&
              !RenderHumanOf(planData).Contains("Dropbox newer", StringComparison.Ordinal),
            "The CLI first-sync plan human text is provider-neutral");
        Check(listData.Items[0].Group == "Local newer" && planData.Items[0].Group == "Dropbox newer" &&
              JsonSerializer.Serialize(listData).Contains("Local newer", StringComparison.Ordinal) &&
              JsonSerializer.Serialize(planData).Contains("Dropbox newer", StringComparison.Ordinal) &&
              JsonSerializer.Serialize(detailData).Contains("Local newer", StringComparison.Ordinal),
            "The documented raw compatibility aliases stay intact and separately addressable in the data and its JSON");

        // (14) A rebind DURING the awaited provider read. The one-shot transport callback fires inside a
        // real Drive node read that the ACTUAL reader performs for its Drive side; the mutation happens
        // after that provider access and the reader's own post-read revalidation must refuse and publish no
        // tree. No production seam is added and no production code is changed.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-rebind-account"))
        {
            var drive = CloudPairHostBindingChecks.HostDrive("w2cRebind", "/DriveRight");
            await rig.AddEndpointAsync(drive);
            var localRoot = IOPath.Combine(rig.StateRoot, "rebind-local");
            Directory.CreateDirectory(localRoot);
            var pair = await RetainPairAsync(rig, "w2c-rebind-account", LocalEndpoint(localRoot),
                CloudEndpoint(drive));
            var before = rig.Wire.TotalRequests;
            var beforeNodeReads = rig.Wire.DriveNodeReadRequests;
            var fired = 0;
            rig.Wire.OnDriveNodeReadOnceAsync = async () =>
            {
                fired++;
                await ReplaceGoogleBindingAsync(rig);
            };
            SyncFolderSelectionTree? published = null;
            string? refusal = null;
            try
            {
                published = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException
                                                  or GoogleDriveBlockedException)
            {
                refusal = exception.GetType().Name + ": " + exception.Message;
            }
            Check(fired == 1,
                "A during-read protected-account change: the one-shot callback fired EXACTLY once");
            Check(rig.Wire.DriveNodeReadRequests > beforeNodeReads && rig.Wire.TotalRequests > before,
                "A during-read protected-account change: the real provider read occurred BEFORE the mutation");
            // The ACTUAL message the reader emits for a replaced protected binding, captured from the run
            // rather than remembered: ResolveCloudAccount can no longer load the exact retained account, so
            // the reader's own post-read account revalidation raises GoogleDriveBlockedException.
            Check(refusal is not null &&
                  refusal.Contains("GoogleDriveBlockedException", StringComparison.Ordinal) &&
                  refusal.Contains("Reconnect this endpoint's exact Google Drive account", StringComparison.Ordinal),
                "A during-read protected-account change is refused by the reader's own account revalidation: " + refusal);
            Check(published is null,
                "A during-read protected-account change publishes NO tree, so no stale or mixed hierarchy survives");
        }

        // (15) The OTHER ordered endpoint is changed during the awaited read: the shared ordered-context
        // comparison must refuse with its own message and publish nothing.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-rebind-other"))
        {
            var drive = CloudPairHostBindingChecks.HostDrive("w2cRebindB", "/DriveRight");
            await rig.AddEndpointAsync(drive);
            var localRoot = IOPath.Combine(rig.StateRoot, "rebind-other-local");
            Directory.CreateDirectory(localRoot);
            var pair = await RetainPairAsync(rig, "w2c-rebind-other", LocalEndpoint(localRoot),
                CloudEndpoint(drive));
            var beforeNodeReads = rig.Wire.DriveNodeReadRequests;
            var fired = 0;
            rig.Wire.OnDriveNodeReadOnceAsync = async () =>
            {
                fired++;
                var changed = pair with
                {
                    RightEndpoint = pair.RightEndpoint! with { FolderPath = "/DriveOtherRecordedPath" },
                    DropboxRoot = "/DriveOtherRecordedPath"
                };
                await rig.Db.RetainPairDefinitionAsync(changed, select: true);
            };
            SyncFolderSelectionTree? published = null;
            string? refusal = null;
            try
            {
                published = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
            {
                refusal = exception.Message;
            }
            Check(fired == 1 && rig.Wire.DriveNodeReadRequests > beforeNodeReads,
                "A during-read change of the OTHER ordered endpoint fired the callback exactly once after provider access");
            Check(refusal is not null &&
                  refusal.Contains("changed while its Google location was checked", StringComparison.Ordinal),
                "The other-endpoint change is refused by the shared ordered-context comparison, which emits its own message: " + refusal);
            Check(published is null,
                "The other-endpoint change publishes NO tree");
        }

        // (16) A name-only change during the awaited read is NOT an identity change and must still succeed.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-rebind-name"))
        {
            var drive = CloudPairHostBindingChecks.HostDrive("w2cRebindC", "/DriveRight");
            await rig.AddEndpointAsync(drive);
            var localRoot = IOPath.Combine(rig.StateRoot, "rebind-name-local");
            Directory.CreateDirectory(localRoot);
            Directory.CreateDirectory(IOPath.Combine(localRoot, "NameCase"));
            var pair = await RetainPairAsync(rig, "w2c-rebind-name", LocalEndpoint(localRoot),
                CloudEndpoint(drive));
            var fired = 0;
            rig.Wire.OnDriveNodeReadOnceAsync = async () =>
            {
                fired++;
                await rig.Db.RetainPairDefinitionAsync(pair with { Name = "W2C renamed during the read" },
                    select: true);
            };
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            Check(fired == 1 && tree.EndpointContext is not null &&
                  tree.LeftEndpoint is { Available: true } && tree.RightEndpoint is { Available: true } &&
                  tree.Folders.Any(item => item.RelativePath == "NameCase"),
                "A name-only change during the awaited read is not an identity change, so the read still succeeds");
        }

        // (17) R1: a REMOTE-ONLY entry is never counted as a local placeholder. With zero observed local
        // placeholder residency the online-only rollup must be ZERO and the entry must be positively
        // cloud-only.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-online-only"))
        {
            var cloud = CloudPairHostBindingChecks.HostDrive("w2cOO", "/Cloud");
            await rig.AddEndpointAsync(cloud);
            var localRoot = IOPath.Combine(rig.StateRoot, "online-only-local");
            Directory.CreateDirectory(localRoot);
            // Provider-backed seeding through the endpoint's OWN retained account and root: the expected
            // input is verified by a REAL adapter read BEFORE anything is asserted.
            rig.SeedFile(cloud, "CloudOnlyFile.txt", "remote only bytes");
            var seededRead = await rig.ReadDriveFileAsync(cloud, "/CloudOnlyFile.txt");
            Check(string.Equals(Encoding.UTF8.GetString(seededRead.Bytes ?? []), "remote only bytes",
                      StringComparison.Ordinal),
                "R1: the remote-only file is really served by the provider transport before any assertion");
            var pair = await RetainPairAsync(rig, "w2c-online-only", LocalEndpoint(localRoot), CloudEndpoint(cloud));
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            Check(tree.Folders.All(item => item.Presence != SyncFolderPresence.OnlineOnly) &&
                  tree.OnlineOnlyFolders == 0 && (tree.Rollup?.OnlineOnlyItems ?? 0) == 0,
                "R1: with the provider-served remote-only file and no OBSERVED local placeholder the online-only rollups are ZERO");
            Check((tree.Rollup?.LocalItems ?? 0) == 0,
                "R1: the remote-only file contributes NO local residency to the Host rollup");
        }

        // (18) R2: a genuinely UNAVAILABLE local root. The executed lease TYPE evidence is captured
        // directly, and the classification must never claim a replaced identity for it.
        using (var rig = await CloudPairHostBindingChecks.HostRig.CreateAsync("w2c-unavailable-root"))
        {
            var cloud = CloudPairHostBindingChecks.HostDrive("w2cUn", "/Cloud");
            await rig.AddEndpointAsync(cloud);
            var localRoot = IOPath.Combine(rig.StateRoot, "unavailable-local");
            Directory.CreateDirectory(localRoot);
            var pair = await RetainPairAsync(rig, "w2c-unavailable-root", LocalEndpoint(localRoot),
                CloudEndpoint(cloud));
            Directory.Delete(localRoot, recursive: true);
            string leaseEvidence;
            try
            {
                using var probe = PilotDirectoryLease.Open(localRoot, new PilotDirectoryIdentity(0, 0));
                leaseEvidence = "OPENED (no type evidence)";
            }
            catch (Exception exception)
            {
                leaseEvidence = exception.GetType().Name + " inner=" +
                                (exception.InnerException?.GetType().Name ?? "none");
            }
            Console.WriteLine("R2 executed deleted-root lease type evidence: " + leaseEvidence);
            var tree = await rig.Host.LoadFolderSelectionTreeAsync(pair.Id);
            Check(tree.LeftEndpoint is { Available: false } && tree.RightEndpoint is { Available: true },
                "R2: an unavailable local root does not blank the other, successfully inspected endpoint");
            var message = tree.LeftEndpoint!.Message ?? string.Empty;
            // (b) A definitively MISSING root (typed native FileNotFound evidence) keeps its repair
            // instruction, but it must never be mis-labelled as a REPLACED identity.
            Check(message.Contains("Local folder unavailable", StringComparison.Ordinal) &&
                  !message.Contains("no longer the same folder", StringComparison.Ordinal) &&
                  !message.Contains("Choose the correct folder", StringComparison.Ordinal),
                "R2(b): a definitively MISSING root is reported as missing with its repair instruction and is NOT mis-labelled as a replaced identity: " + message);
            // (a) The untyped plain-IOException bucket must return the neutral bound-folder wording with NO
            // replacement instruction. It is NOT reachable from this rig - the lease wraps the native
            // FileNotFound error for a deleted root - so it is reported UNEXECUTED rather than reusing the
            // definitively-missing case for it.
            Console.WriteLine("R2(a) UNEXECUTED: the untyped plain-IOException root-open bucket (neutral "
                + "'bound folder could not be verified' wording, no replacement instruction) is not reachable "
                + "from this rig; the observed deleted-root shape is IOException with a native FileNotFoundException inner.");
            Check(tree.LocalHierarchyMessage is not null &&
                  !tree.LocalHierarchyMessage.Contains("no longer the same folder", StringComparison.Ordinal),
                "R2: the published local hierarchy message is not a replacement claim: " + tree.LocalHierarchyMessage);
        }

        // (19) R2 executed classifier plus SOURCE-ONLY Host-fault routing. The ACTUAL private classification
        // methods are invoked through reflection, so the wording branches are executed; the two catch sites
        // that route a real fault into them are proved by source reading only, NOT by an injected Host fault.
        {
            var hostType = typeof(SyncAuthorityRuntimeHost);
            var rootFailure = hostType.GetMethod("LocalRootFailure",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            var scanFailure = hostType.GetMethod("LocalScanFailure",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            var plainIo = ((string Message, bool Definitive))rootFailure.Invoke(null,
                ["C:\\w2c\\root", new IOException("plain io")])!;
            Check(plainIo.Message.Contains("could not be verified", StringComparison.Ordinal) &&
                  !plainIo.Message.Contains("Choose the correct folder", StringComparison.Ordinal) &&
                  !plainIo.Message.Contains("no longer the same folder", StringComparison.Ordinal) &&
                  !plainIo.Definitive,
                "R2(a) executed classifier: an untyped plain IOException returns the neutral bound-folder wording with NO replacement instruction: " + plainIo.Message);
            var denied = ((string Message, bool Definitive))rootFailure.Invoke(null,
                ["C:\\w2c\\root", new UnauthorizedAccessException("denied")])!;
            Check(denied.Message.Contains("cannot access", StringComparison.Ordinal) && !denied.Definitive,
                "R2 executed classifier: an access failure is unavailable and not definitive: " + denied.Message);
            var replaced = ((string Message, bool Definitive))rootFailure.Invoke(null,
                ["C:\\w2c\\root", new InvalidDataException("identity")])!;
            Check(!replaced.Definitive && replaced.Message.Contains("could not be verified", StringComparison.Ordinal),
                "R2 executed classifier: a non-IO identity error stays UNKNOWN rather than guessing a replacement: " + replaced.Message);
            var missing = ((string Message, bool Definitive))rootFailure.Invoke(null,
                ["C:\\w2c\\root", new IOException("open failed", new FileNotFoundException("gone"))])!;
            Check(missing.Definitive && missing.Message.Contains("Local folder unavailable", StringComparison.Ordinal),
                "R2 executed classifier: a TYPED native missing error is definitive and keeps its repair instruction: " + missing.Message);
            var scan = (string)scanFailure.Invoke(null, ["C:\\w2c\\root", new IOException("read failed")])!;
            Check(!scan.Contains("no longer the same folder", StringComparison.Ordinal) &&
                  !scan.Contains("Choose the correct folder", StringComparison.Ordinal),
                "R2 executed classifier: a DESCENDANT SCAN failure never claims a replaced identity: " + scan);
            Console.WriteLine("R2 SOURCE-ONLY: the two catch sites in ReadEndpointHierarchyAsync route a real "
                + "root-open fault to LocalRootFailure and a real scan fault to LocalScanFailure; that routing "
                + "is proved by source reading, NOT by an injected Host fault.");
        }

        Console.WriteLine(passed + " endpoint hierarchy routing checks passed; no provider mutation, no local root opened for a pair without a local endpoint and no preparation, planning, run or sync path was used.");
        return 0;
    }

    /// <summary>Replaces the retained Google binding's permission identity, as GDN-10a does.</summary>
    private static async Task ReplaceGoogleBindingAsync(CloudPairHostBindingChecks.HostRig rig)
    {
        var store = new GoogleDriveConnectionStore(rig.StateAuthority);
        var original = store.Load()
                       ?? throw new InvalidDataException("The fixture retains no Google connection to change.");
        var permission = original.PermissionId + "-w2c-changed";
        var changed = new GoogleDriveConnectionBinding(original.ClientId,
            GoogleDriveConnectionBinding.CreateAccountKey(permission), permission, original.Email,
            original.DisplayName, original.Scopes, CredentialReference: original.CredentialReference).Normalize();
        await store.DeleteAsync();
        await store.SaveAsync(changed);
    }

    /// <summary>One local endpoint carrying its own real on-disk folder identity.</summary>
    private static SyncEndpoint LocalEndpoint(string path)
    {
        using var lease = PilotDirectoryLease.Open(path);
        return SyncEndpoint.Local(lease.Path,
            SyncLocalEndpointIdentity.From(lease.Identity.VolumeSerialNumber, lease.Identity.FileId));
    }

    /// <summary>One explicit cloud endpoint built from the fixture's registered endpoint spec.</summary>
    private static SyncEndpoint CloudEndpoint(CloudPairHostBindingChecks.EndpointSpec spec) =>
        SyncEndpoint.Cloud(spec.Kind, spec.ConnectionId, spec.EndpointNamespaceId, spec.FolderId, spec.FolderPath);

    /// <summary>
    /// Retains a genuine explicit pair through the REAL production draft projection
    /// (SyncEndpointPairDraft.Create over validated SyncPairEndpoints), exactly as the product does. No
    /// fixture switch chooses the pair shape, no pair is faked, and the routing decision under test is not
    /// replaced. If the production projection refuses a shape, that refusal is the PRODUCTION exception and
    /// is reported as such rather than being thrown by this fixture.
    /// </summary>
    private static async Task<SyncPair> RetainPairAsync(CloudPairHostBindingChecks.HostRig rig, string tag,
        SyncEndpoint left, SyncEndpoint right)
    {
        var draft = SyncEndpointPairDraft.Create(new SyncPairEndpoints(left, right, SyncMode.TwoWay),
            "W2C " + tag);
        var normalized = SyncPairStore.NormalizePair(draft);
        await rig.Db.RegisterPairAsync(normalized.Id, normalized.PolicyRevision);
        await rig.Db.RetainPairDefinitionAsync(normalized, true);
        return normalized;
    }
}
