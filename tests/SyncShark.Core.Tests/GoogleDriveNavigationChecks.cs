using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// Fake HTTP supplies protocol inputs only; all account, path, corpus, URL and readback decisions
// execute the production client. Host target validation is also exercised without opening a database.
internal static class GoogleDriveNavigationChecks
{
    internal static async Task RunAsync()
    {
        await SuccessfulLocationsAsync();
        await AccountAndPathRejectionsAsync();
        await RootAndLookupRejectionsAsync();
        await UrlRejectionsAsync();
        await ChangedIdentityRejectionsAsync();
        await PaginationAsync();
        HostTargetChecks();
        Console.WriteLine("PASS Google navigation protocol checks: exact accounts/corpora, provider URLs/resource keys, bounded paths, duplicate/move/stale/shortcut rejection without mutations");
    }

    private static GoogleDriveConnectionBinding Binding(string permission = "navigation-account-one", bool shared = false) =>
        new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey(permission), permission, "fixture@example.invalid",
            "Synthetic navigation account", [GoogleDriveProviderConfiguration.DriveScope],
            CredentialReference: "synthetic-only", SharedDrivesEnabled: shared).Normalize();

    private static GoogleDrivePilotClient Client(HttpClient http, GoogleDriveConnectionBinding binding,
        string? corpus = null) => new(http, new Tokens(), binding, "root-folder", "/Selected",
        sharedDriveId: corpus, allowSharedDrives: binding.SharedDrivesEnabled);

    private static Node Root(string? drive = null) => new("root-folder", "Selected", [],
        "application/vnd.google-apps.folder", "https://drive.google.com/drive/folders/root-folder", Drive: drive);

    private static Node Blob(string id = "file-one", string? drive = null) => new(id, "example.bin", ["root-folder"],
        "application/octet-stream", "https://drive.google.com/file/d/" + id + "/view", Drive: drive);

    private static async Task SuccessfulLocationsAsync()
    {
        var items = new Node?[]
        {
            null,
            Blob(),
            Blob() with { ResourceKey = "key-with/slash", Url = "https://drive.google.com/open?id=file-one&usp=drive_link#preview" },
            Blob() with { ResourceKey = "retained-key", Url = "https://drive.google.com/file/d/file-one/view?resourcekey=retained-key&usp=sharing" },
            new("native-one", "Document", ["root-folder"], "application/vnd.google-apps.document",
                "https://docs.google.com/document/d/native-one/edit"),
            new("shortcut-one", "Shortcut", ["root-folder"], "application/vnd.google-apps.shortcut",
                "https://drive.google.com/file/d/shortcut-one/view", Target: "different-target")
        };
        foreach (var item in items)
        {
            using var script = new Script(); using var http = new HttpClient(script); var binding = Binding();
            Node[] chain = item is null ? [] : [item];
            Workflow(script, binding, Root(), chain);
            var value = await Client(http, binding).GetWebLocationAsync(binding.ConnectionId, item?.Name ?? ".");
            var expected = item?.Url ?? Root().Url;
            if (item?.ResourceKey == "key-with/slash")
                expected = "https://drive.google.com/open?id=file-one&usp=drive_link&resourcekey=key-with%2Fslash#preview";
            Require(value == expected, "the provider URL is retained, with only a missing resource key appended");
            script.Done();
        }

        // A nested exact path resolves only named children; no broad inventory or target dereference.
        using (var script = new Script())
        using (var http = new HttpClient(script))
        {
            var binding = Binding();
            var folder = new Node("nested-folder", "Nested", ["root-folder"], "application/vnd.google-apps.folder",
                "https://drive.google.com/drive/folders/nested-folder");
            var file = Blob() with { Parents = ["nested-folder"] };
            Workflow(script, binding, Root(), [folder, file]);
            Require(await Client(http, binding).GetWebLocationAsync(binding.ConnectionId, "Nested/example.bin") == file.Url,
                "nested path returns the selected child URL");
            script.Done();
        }

        // Identical names on two accounts cannot select a global account or cached URL.
        foreach (var permission in new[] { "navigation-account-one", "navigation-account-two" })
        {
            using var script = new Script(); using var http = new HttpClient(script); var binding = Binding(permission);
            var item = Blob(permission + "-file"); Workflow(script, binding, Root(), [item]);
            var resolved = await Client(http, binding).ResolveWebLocationAsync(binding.ConnectionId, item.Name);
            Require(resolved.WebViewLink == item.Url && resolved.FileId == item.Id &&
                resolved.ConnectionId == binding.ConnectionId && resolved.NamespaceId == binding.PermissionId &&
                resolved.RootFolderId == "root-folder" && resolved.RelativePath == item.Name &&
                resolved.SharedDriveId is null && resolved.Kind == GoogleDriveEntryKind.Blob && resolved.Version == "1",
                "each account resolves its own exact file id and sharing namespace"); script.Done();
        }
        using (var script = new Script())
        using (var http = new HttpClient(script))
        {
            var binding = Binding(shared: true); var root = Root("drive-one"); var item = Blob(drive: "drive-one");
            Workflow(script, binding, root, [item]);
            var resolved = await Client(http, binding, "drive-one").ResolveWebLocationAsync(binding.ConnectionId, item.Name);
            Require(resolved.WebViewLink == item.Url && resolved.NamespaceId == "drive-one" &&
                resolved.SharedDriveId == "drive-one" && resolved.ConnectionId == binding.ConnectionId,
                "explicitly enabled exact shared corpus succeeds without substituting the My Drive namespace"); script.Done();
        }
        foreach (var metadataKey in new string?[] { null, "provider-key" })
        {
            using var script = new Script(); using var http = new HttpClient(script); var binding = Binding();
            var item = Blob() with { ResourceKey = metadataKey, Url = "https://drive.google.com/file/d/file-one/view?resourcekey=provider-key" };
            Workflow(script, binding, Root(), [item]);
            var resolved = await Client(http, binding).ResolveWebLocationAsync(binding.ConnectionId, item.Name);
            Require(resolved.ResourceKey == "provider-key" && resolved.WebViewLink == item.Url,
                "resolved identity retains the provider resource key from metadata or the validated URL"); script.Done();
        }
    }

    private static async Task AccountAndPathRejectionsAsync()
    {
        using var script = new Script(); using var http = new HttpClient(script); var binding = Binding();
        var client = Client(http, binding);
        await RejectAsync(() => client.GetWebLocationAsync(Binding("other").ConnectionId, "."));
        await RejectAsync(() => client.GetWebLocationAsync(binding.PermissionId, "."));
        foreach (var path in new[] { "", "/escape", "../escape", "folder/../escape", "C:\\escape", "folder//file", "folder/./file", "folder\\..\\file" })
            await RejectAsync(() => client.GetWebLocationAsync(binding.ConnectionId, path));
        script.Done();
        About(script, Binding("different-token-account"));
        await RejectAsync(() => client.GetWebLocationAsync(binding.ConnectionId, "."));
        script.Done();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await client.GetWebLocationAsync(binding.ConnectionId, ".", cancelled.Token); }
        catch (OperationCanceledException) { script.Done(); return; }
        throw new InvalidOperationException("Google navigation checks: cancellation was ignored");
    }

    private static async Task RootAndLookupRejectionsAsync()
    {
        foreach (var root in new[]
        {
            Root() with { Id = "other-root" }, Root() with { Trashed = true },
            Root() with { Mime = "application/octet-stream" }, Root() with { Version = "" }, Root("unapproved-drive")
        })
        {
            using var script = new Script(); using var http = new HttpClient(script); var binding = Binding();
            About(script, binding); Get(script, "root-folder", root);
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, ".")); script.Done();
        }
        using (var script = new Script())
        using (var http = new HttpClient(script))
        {
            var binding = Binding(shared: true); About(script, binding); Get(script, "root-folder", Root("drive-two"));
            await RejectAsync(() => Client(http, binding, "drive-one").GetWebLocationAsync(binding.ConnectionId, ".")); script.Done();
        }
        foreach (var matches in new[]
        {
            Array.Empty<Node>(), new[] { Blob(), Blob("duplicate-file") },
            new[] { Blob() with { Name = "wrong-name" } }, new[] { Blob() with { Parents = ["outside-root"] } },
            new[] { Blob(drive: "outside-drive") }, new[] { Blob() with { Trashed = true } },
            new[] { Blob() with { Version = "" } }, new[] { Blob() with { Id = "root-folder" } }
        })
        {
            using var script = new Script(); using var http = new HttpClient(script); var binding = Binding();
            About(script, binding); Get(script, "root-folder", Root()); List(script, "root-folder", "example.bin", matches);
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, "example.bin")); script.Done();
        }
        using (var script = new Script())
        using (var http = new HttpClient(script))
        {
            var binding = Binding(); About(script, binding); Get(script, "root-folder", Root());
            var shortcut = Blob() with { Name = "Shortcut", Mime = "application/vnd.google-apps.shortcut", Target = "target-folder" };
            List(script, "root-folder", "Shortcut", [shortcut]);
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, "Shortcut/child")); script.Done();
        }
        using (var script = new Script())
        using (var http = new HttpClient(script))
        {
            var binding = Binding(); About(script, binding); Get(script, "root-folder", Root());
            List(script, "root-folder", "example.bin", [Blob()]); script.Add("files/file-one", "{}", HttpStatusCode.NotFound);
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, "example.bin")); script.Done();
        }
    }

    private static async Task UrlRejectionsAsync()
    {
        foreach (var url in new string?[]
        {
            null, "http://drive.google.com/file/d/file-one/view", "https://user@drive.google.com/file/d/file-one/view",
            "https://drive.google.com:444/file/d/file-one/view", "https://drive.google.com.evil.invalid/file/d/file-one/view",
            "https://evil.invalid/file/d/file-one/view", "https://drive.google.com/file/d/another-file/view",
            "https://drive.google.com/open?id=file-one&id=other", "https://drive.google.com/file/d/file-one/view?id=other",
            "https://docs.google.com/unknown/d/file-one/edit", "https://drive.google.com/file/d/file-one/view?resourcekey=wrong",
            "https://drive.google.com/file/d/file-one/view?resourcekey=key&resourcekey=key"
        })
        {
            using var script = new Script(); using var http = new HttpClient(script); var binding = Binding();
            var file = Blob() with { Url = url, ResourceKey = "key" };
            Prefix(script, binding, Root(), [file]);
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, file.Name)); script.Done();
        }
        using (var script = new Script())
        using (var http = new HttpClient(script))
        {
            var binding = Binding();
            var shortcut = Blob() with { Mime = "application/vnd.google-apps.shortcut", Target = "target-file",
                Url = "https://drive.google.com/file/d/target-file/view" };
            Prefix(script, binding, Root(), [shortcut]);
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, shortcut.Name)); script.Done();
        }
    }

    private static async Task ChangedIdentityRejectionsAsync()
    {
        foreach (var changed in new[]
        {
            Blob() with { Id = "different-id" }, Blob() with { Version = "2" },
            Blob() with { Parents = ["outside-root"] }, Blob(drive: "other-drive"), Blob() with { Trashed = true }
        })
        {
            using var script = new Script(); using var http = new HttpClient(script); var binding = Binding();
            About(script, binding); Get(script, "root-folder", Root()); List(script, "root-folder", "example.bin", [Blob()]);
            Get(script, "file-one", changed);
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, "example.bin")); script.Done();
        }
        foreach (var changedRoot in new[] { Root() with { Version = "2" }, Root("moved-drive"), Root() with { Parents = ["new-parent"] } })
        {
            using var script = new Script(); using var http = new HttpClient(script); var binding = Binding();
            Prefix(script, binding, Root(), [Blob()]); Get(script, "root-folder", changedRoot);
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, "example.bin")); script.Done();
        }
        using (var script = new Script())
        using (var http = new HttpClient(script))
        {
            var binding = Binding(); Prefix(script, binding, Root(), [Blob()]); Get(script, "root-folder", Root());
            List(script, "root-folder", "example.bin", [Blob(), Blob("late-duplicate")]);
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, "example.bin")); script.Done();
        }
        using (var script = new Script())
        using (var http = new HttpClient(script))
        {
            var binding = Binding(); Prefix(script, binding, Root(), [Blob()]); Get(script, "root-folder", Root());
            List(script, "root-folder", "example.bin", [Blob()]); Get(script, "file-one", Blob() with { ResourceKey = "changed-key" });
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, "example.bin")); script.Done();
        }
        using (var script = new Script())
        using (var http = new HttpClient(script))
        {
            var binding = Binding();
            var ancestor = new Node("ancestor", "Folder", ["root-folder"], "application/vnd.google-apps.folder", null);
            var file = Blob() with { Parents = ["ancestor"] };
            Prefix(script, binding, Root(), [ancestor, file]); Get(script, "root-folder", Root());
            List(script, "root-folder", "Folder", [ancestor with { Parents = ["outside-root"] }]);
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, "Folder/example.bin")); script.Done();
        }
    }

    private static async Task PaginationAsync()
    {
        using (var script = new Script())
        using (var http = new HttpClient(script))
        {
            var binding = Binding(); About(script, binding); Get(script, "root-folder", Root());
            List(script, "root-folder", "example.bin", [], next: "next-page");
            List(script, "root-folder", "example.bin", [Blob()], page: "next-page"); Get(script, "file-one", Blob());
            Get(script, "root-folder", Root()); List(script, "root-folder", "example.bin", [Blob()]); Get(script, "file-one", Blob());
            Require(await Client(http, binding).GetWebLocationAsync(binding.ConnectionId, "example.bin") == Blob().Url,
                "empty matching page is followed without inferring absence"); script.Done();
        }
        foreach (var incomplete in new[] { true, false })
        {
            using var script = new Script(); using var http = new HttpClient(script); var binding = Binding();
            About(script, binding); Get(script, "root-folder", Root());
            if (incomplete) List(script, "root-folder", "example.bin", [Blob()], incomplete: true);
            else
            {
                List(script, "root-folder", "example.bin", [Blob()], next: "duplicates");
                List(script, "root-folder", "example.bin", [Blob("other-id")], page: "duplicates");
            }
            await RejectAsync(() => Client(http, binding).GetWebLocationAsync(binding.ConnectionId, "example.bin")); script.Done();
        }
    }

    private static void HostTargetChecks()
    {
        var binding = Binding();
        var pair = new SyncPair("navigation-pair", "Synthetic", "C:\\synthetic", 1, 2, "/Selected", "root-folder",
            ConnectionId: binding.ConnectionId, Provider: SyncProviders.GoogleDrive);
        SyncAuthorityRuntimeHost.ValidateGoogleNavigationTarget(pair, binding.ConnectionId);
        foreach (var invalid in new[] { pair with { Provider = SyncProviders.Dropbox }, pair with { ConnectionId = "other-account" },
                     pair with { DropboxFolderId = "" } })
        {
            try { SyncAuthorityRuntimeHost.ValidateGoogleNavigationTarget(invalid, binding.ConnectionId); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException) { continue; }
            throw new InvalidOperationException("Google navigation checks: host accepted a foreign or unbound pair");
        }
    }

    private static void Workflow(Script script, GoogleDriveConnectionBinding binding, Node root, Node[] chain)
    {
        Prefix(script, binding, root, chain); Get(script, root.Id, root); Chain(script, root, chain);
        if (chain.Length != 0) Get(script, chain[^1].Id, chain[^1]);
    }
    private static void Prefix(Script script, GoogleDriveConnectionBinding binding, Node root, Node[] chain)
    {
        About(script, binding); Get(script, root.Id, root); Chain(script, root, chain);
        if (chain.Length != 0) Get(script, chain[^1].Id, chain[^1]);
    }
    private static void Chain(Script script, Node root, Node[] chain)
    {
        var parent = root.Id;
        foreach (var item in chain) { List(script, parent, item.Name, [item]); parent = item.Id; }
    }
    private static void About(Script script, GoogleDriveConnectionBinding binding) => script.Add("about",
        JsonSerializer.Serialize(new { user = new { permissionId = binding.PermissionId, emailAddress = "fixture@example.invalid" }, storageQuota = new { } }));
    private static void Get(Script script, string expectedId, Node node) => script.Add("files/" + expectedId,
        JsonSerializer.Serialize(node.Metadata()), inspect: request =>
            Require(Query(request, "fields")?.Contains("webViewLink,resourceKey", StringComparison.Ordinal) == true,
                "navigation asks for provider web URL and resource key"));
    private static void List(Script script, string parent, string name, Node[] nodes, string? next = null,
        string? page = null, bool incomplete = false) => script.Add("files", JsonSerializer.Serialize(new
        { files = nodes.Select(node => node.Metadata()).ToArray(), nextPageToken = next, incompleteSearch = incomplete }), inspect: request =>
        {
            Require(Query(request, "q") == "'" + parent + "' in parents and name = '" + name + "' and trashed = false",
                "only the exact named child in the exact parent is looked up");
            Require(Query(request, "pageToken") == page, "opaque page continuation is preserved");
        });
    private static string? Query(HttpRequestMessage request, string key) => request.RequestUri!.Query.TrimStart('?')
        .Split('&').Select(part => part.Split('=', 2)).Where(pair => Uri.UnescapeDataString(pair[0]) == key)
        .Select(pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "").FirstOrDefault();
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is GoogleDriveBlockedException or InvalidOperationException or InvalidDataException or ArgumentException or HttpRequestException) { return; }
        throw new InvalidOperationException("Google navigation checks: unsafe input was accepted");
    }
    private static void Require(bool condition, string reason)
    { if (!condition) throw new NavigationFixtureException("Google navigation checks: " + reason); }
    // Harness failures must never satisfy a production safe-rejection assertion.
    private sealed class NavigationFixtureException(string message) : Exception(message);
    private sealed record Node(string Id, string Name, string[] Parents, string Mime, string? Url,
        string Version = "1", string? Drive = null, string? ResourceKey = null, bool Trashed = false, string? Target = null)
    {
        internal object Metadata() => new
        {
            id = Id, name = Name, parents = Parents, mimeType = Mime, webViewLink = Url, version = Version,
            driveId = Drive, resourceKey = ResourceKey, trashed = Trashed, size = "5",
            capabilities = new { canDownload = true },
            shortcutDetails = Target is null ? null : new { targetId = Target, targetMimeType = "application/octet-stream" }
        };
    }
    private sealed class Tokens : IGoogleDriveAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult("synthetic-not-a-credential"); }
    }
    private sealed class Script : HttpMessageHandler
    {
        private readonly Queue<Step> _steps = new();
        internal void Add(string path, string body, HttpStatusCode status = HttpStatusCode.OK,
            Action<HttpRequestMessage>? inspect = null) => _steps.Enqueue(new(path, body, status, inspect));
        internal void Done() => Require(_steps.Count == 0, "all expected HTTP responses were consumed");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_steps.TryDequeue(out var step)) throw new NavigationFixtureException("Unexpected navigation request");
            Require(request.Method == HttpMethod.Get && request.Content is null, "navigation is strictly read-only");
            Require(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "www.googleapis.com" &&
                request.RequestUri.AbsolutePath == "/drive/v3/" + step.Path, "fixed API origin and exact resource id");
            Require(request.Headers.Authorization?.Scheme == "Bearer", "only the protected token callback supplies authorization");
            step.Inspect?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(step.Status) { Content = new StringContent(step.Body, Encoding.UTF8, "application/json") });
        }
        private sealed record Step(string Path, string Body, HttpStatusCode Status, Action<HttpRequestMessage>? Inspect);
    }
}
