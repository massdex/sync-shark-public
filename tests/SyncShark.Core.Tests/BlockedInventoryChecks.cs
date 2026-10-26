using System.Net;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

internal static class BlockedInventoryChecks
{
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh bounded test directory; never reset retained state.");
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "files");
        Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, "prior.txt"), "content");
        using var lease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair("pair-invalid", "Invalid-name fixture", local, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/Pilot", "id:pilot-folder");
        using var wire = new InventoryWire();
        using var http = new HttpClient(wire);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, "id:pilot-folder");
        var db = new SyncAuthorityDatabase(Path.Combine(root, "state.db"));
        await db.InitializeAsync("installation-invalid", "profile-invalid", "invalid-name-check", "{}");
        await db.RegisterPairAsync(pair.Id, 1);
        await db.RegisterRootBindingAsync("root-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { lease.Identity.VolumeSerialNumber, lease.Identity.FileId }), local);
        await db.RegisterRootBindingAsync("root-cloud", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:pilot-folder\"}", "/Pilot");
        var progress = new List<SyncComparisonProgress>();
        var observer = new SyncAuthorityObservationCoordinator(pair, client, db, "root-local", "root-cloud",
            item => { lock (progress) progress.Add(item); return Task.CompletedTask; });
        await observer.ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(db).AdoptAsync(pair.Id);
        wire.Unsupported = true;
        var listed = await client.ListEntriesAsync();
        Require(listed.Count == 5 && listed.Count(item => item.TransferBlockedReason is not null) == 4,
            "Provider retains each unsupported item alongside its healthy sibling.");
        Require(listed.Any(item => item.RelativePath == "stamp+01:00.json" && item.TransferBlockedReason!.Contains("Rename")),
            "Exact provider name and practical remediation are retained.");
        foreach (var item in listed.Where(item => item.TransferBlockedReason is not null && item.RelativePath != "cloud-note.paper"))
        {
            var rejected = false;
            try { _ = PilotPath.ResolveContainedPath(local, item.RelativePath); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "Unsupported metadata never becomes a filesystem path.");
        }
        foreach (var path in new[] { "../escape", "a/../escape", "/outside", "a\\b", "a\nb", "a//b" })
        {
            var rejected = false;
            try { _ = PilotPath.NormalizeInventoryPath(path); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "Metadata containment and log-injection rejection: " + JsonSerializer.Serialize(path));
        }
        var scanned = await observer.ObserveAsync("manual");
        var review = await db.LoadBootstrapEvidenceAsync(pair.Id) ?? throw new Exception("No review");
        Require(review.Items.Any(item => item.RelativePath == "cloud-note.paper" &&
            item.Group == "Unreliable or missing" && item.BlockedReason?.Contains("Export") == true),
            "Non-downloadable cloud document retains its exact path and export remediation, not a false download recommendation.");
        Require(review.Items.Single(item => item.RelativePath == "healthy.txt").Group == "Dropbox only",
            "Healthy independent file is still a downloadable recommendation.");
        foreach (var path in new[] { "stamp+01:00.json", "trailing.", "NUL.txt", "prior.txt" })
            Require(review.Items.Any(item => item.RelativePath == path && item.Group == "Unreliable or missing" &&
                item.BlockedReason?.Contains("Windows") == true), "Blocked review reason and prior native-identity protection: " + path);
        var uncertainties = await db.LoadCompleteUncertainPathsAsync(pair.Id, "dropbox", scanned.DropboxScan.ScanGenerationId);
        Require(uncertainties.Contains("prior.txt") && uncertainties.Contains("stamp+01:00.json"),
            "A rename to unsupported cloud name is not inferred to be deletion.");
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id, null, null,
            "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        var planner = new SyncAuthorityReconciliationPlanner(db,
            new SyncAuthorityPlannerOptions(1, "profile-invalid", "control-invalid", "control-invalid", EnableDeletionEffects: true), admission: admission);
        var planned = await planner.PlanAsync(pair.Id);
        Require(planned.Any(item => item.RelativePath == "healthy.txt" && item.Kind == SyncAuthorityPlannerOutcomeKind.Operation),
            "Real planner continues the healthy sibling.");
        Require(planned.Where(item => item.RelativePath != "healthy.txt").All(item => item.OperationId is null),
            "No effect is planned for unsupported names or earlier native-identity aliases.");
        Require(await File.ReadAllTextAsync(Path.Combine(local, "prior.txt")) == "content" && wire.Effects == 0,
            "Local originals and all provider contents remain unchanged by inventory/planning.");
        Require(progress.Count(item => item.Phase == "Cloud Item Blocked") == 4,
            "Every blocked cloud item emits a live progress/log record.");
        Require(wire.Continuations == 2,
            "Both real full inventory calls follow an undersized page to its final cursor.");
        wire.Escape = true;
        var escaped = false;
        try { await client.ListEntriesAsync(); } catch (InvalidDataException) { escaped = true; }
        Require(escaped, "Out-of-root provider response still fails closed.");
        Console.WriteLine("PASS unsupported cloud inventory: exact reasons, independent valid planning, unsafe effect rejection, prior identity protection, complete review, live logs, containment and zero provider mutations.");
    }

    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    private sealed class InventoryWire : HttpMessageHandler
    {
        internal bool Unsupported, Escape;
        internal int Effects;
        internal int Continuations;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object Entry(string path, string id, bool downloadable = true) => new Dictionary<string, object>
            {
                [".tag"] = "file", ["id"] = id, ["rev"] = "r1", ["size"] = 7,
                ["path_display"] = (Escape ? "/Outside/" : "/Pilot/") + path,
                ["content_hash"] = DropboxContentHash.Compute(Encoding.UTF8.GetBytes("content")),
                ["client_modified"] = "2026-10-10T00:00:00Z", ["is_downloadable"] = downloadable
            };
            object payload;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/2/users/get_current_account":
                    payload = new { account_id = "account-id", root_info = new { root_namespace_id = "root-namespace", home_namespace_id = "home-namespace" } };
                    break;
                case "/2/files/get_metadata":
                    payload = new Dictionary<string, object> { [".tag"] = "folder", ["id"] = "id:pilot-folder", ["name"] = "Pilot", ["path_display"] = "/Pilot" };
                    break;
                case "/2/files/list_folder":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)))
                        Require(body.RootElement.GetProperty("limit").GetInt32() == 2000 &&
                            body.RootElement.GetProperty("recursive").GetBoolean() &&
                            body.RootElement.GetProperty("include_mounted_folders").GetBoolean() &&
                            body.RootElement.GetProperty("include_non_downloadable_files").GetBoolean(),
                            "Inventory requests the supported page size without narrowing complete inventory semantics.");
                    payload = new { entries = Unsupported
                        ? new[] { Entry("stamp+01:00.json", "id:prior"), Entry("healthy.txt", "id:healthy") }
                        : new[] { Entry("prior.txt", "id:prior") }, cursor = "fixture-next", has_more = Unsupported };
                    break;
                case "/2/files/list_folder/continue":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)))
                        Require(body.RootElement.GetProperty("cursor").GetString() == "fixture-next",
                            "Continuation uses exactly the preceding page's cursor.");
                    Continuations++;
                    payload = new { entries = new[] { Entry("trailing.", "id:dot"), Entry("NUL.txt", "id:nul"), Entry("cloud-note.paper", "id:paper", false) },
                        cursor = "complete", has_more = false };
                    break;
                default:
                    Effects++;
                    throw new InvalidOperationException("Unexpected provider effect in inventory proof: " + request.RequestUri.AbsolutePath);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        }
    }
}
