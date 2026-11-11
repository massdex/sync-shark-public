using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

internal static class PublicUpdateChecks
{
    private const string Current = "0.3.18+fixture-source";
    private const string Api = "https://api.github.com/repos/massdex/sync-shark-releases/releases?per_page=100&page=1";
    private static string Asset(string version, string name) => $"https://github.com/{SyncPublicUpdates.Repository}/releases/download/v{version}/{name}";
    private static byte[] Installer(string version, string previous = Current) => Encoding.UTF8.GetBytes(
        "# SyncSharkUpdateContract: " + JsonSerializer.Serialize(new { product = "Massdex.SyncShark", version, previousVersion = previous }) +
        "\nthrow 'This fixture installer must NEVER execute'\n");
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static byte[] Archive(byte[] installer, string? extra = null, bool duplicate = false, bool symlink = false,
        bool manyParents = false, bool updater = false)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] body)
            {
                var entry = zip.CreateEntry(name);
                if (symlink && name.StartsWith("package/signed-candidate/")) entry.ExternalAttributes = unchecked((int)(0xA000u << 16));
                using var output = entry.Open(); output.Write(body);
            }
            Add("scripts/Install-SyncShark.ps1", installer);
            Add("scripts/SyncShark.Release.psm1", "# fixture module; never execute"u8.ToArray());
            if (updater) Add("scripts/SyncSharkUpdater.exe", "fixture updater; never execute"u8.ToArray());
            Add("package/unsigned-candidate-framework-manifest.json", "{}"u8.ToArray());
            Add("package/signed-candidate/SyncShark.exe", "fixture bytes; not an executable"u8.ToArray());
            if (extra is not null) Add(extra, "unsafe"u8.ToArray());
            if (duplicate) Add("scripts/install-syncshark.PS1", installer);
            if (manyParents)
                for (var index = 0; index < 175; index++)
                    Add($"package/signed-candidate/group-{index}/nested/library.dll", []);
        }
        return bytes.ToArray();
    }

    private static object Release(string version, byte[] installer, byte[] archive, string previous = Current,
        bool prerelease = false, bool draft = false, string? archiveUrl = null, string? digest = null) => new
    {
        tag_name = "v" + version, draft, prerelease,
        assets = new object[] {
            new { name="Install-SyncShark.ps1", state="uploaded", size=installer.Length, browser_download_url=Asset(version,"Install-SyncShark.ps1") },
            new { name=$"SyncShark-v{version}-win-x64.zip", state="uploaded", size=archive.Length,
                browser_download_url=archiveUrl ?? Asset(version,$"SyncShark-v{version}-win-x64.zip"), digest=digest ?? "sha256:" + Hash(archive) }
        }
    };

    internal static async Task RunAsync(bool liveRead)
    {
        if (liveRead)
        {
            using var live = new SyncPublicUpdates();
            var result = await live.FindAsync(Current);
            Require(result is null, "Real unauthenticated registered public channel is reachable and has no published compatible release yet");
            return;
        }
        Directory.CreateDirectory(SyncSharkRuntimePaths.ReplacementStagingRoot);
        var installer = Installer("0.3.19"); var archive = Archive(installer);
        var newer = Installer("0.3.20", "0.3.19+intermediate-source");
        var handler = new Handler();
        handler.Json(Api, new[] { Release("0.3.20", newer, archive), Release("0.3.19", installer, archive) });
        handler.Bytes(Asset("0.3.20", "Install-SyncShark.ps1"), newer);
        handler.Bytes(Asset("0.3.19", "Install-SyncShark.ps1"), installer);
        handler.Bytes(Asset("0.3.19", "SyncShark-v0.3.19-win-x64.zip"), archive);
        using var client = new SyncPublicUpdates(handler);
        var update = await client.FindAsync(Current) ?? throw new Exception("Expected compatible release");
        Require(update.Version == "0.3.19", "Discovery chooses the exact compatible predecessor, not an incompatible newer release");
        Require(!handler.ArchiveRequested, "Automatic discovery reads metadata and the small installer header, not the full package");
        Require(handler.NoCredentials, "Public update requests have no Authorization, Cookie or provider credentials");
        string? allocated = null;
        using (var downloaded = await client.DownloadAsync(update, Current, allocated: path => { allocated = path; return Task.CompletedTask; }))
        {
            Require(File.ReadAllBytes(downloaded.Installer).SequenceEqual(installer), "Real downloader and extractor retain exact compatible installer bytes without executing them");
            Require(allocated == downloaded.Directory && Directory.GetParent(allocated)!.FullName == SyncSharkRuntimePaths.ReplacementStagingRoot,
                "The one download is recorded before effects inside the existing staging allocation");
        }
        Require(!Directory.Exists(allocated), "Normal download disposal removes exactly its files and empty directories");

        var retryCount = 0;
        handler.Replace(Asset("0.3.19", "SyncShark-v0.3.19-win-x64.zip"), () => ++retryCount == 1
            ? new(HttpStatusCode.ServiceUnavailable) : Handler.Response(archive));
        await RejectAsync(() => client.DownloadAsync(update, Current), "A server failure is recoverable without changing the installation");
        using (var retry = await client.DownloadAsync(update, Current))
            Require(File.Exists(retry.Installer), "A retry after the same download failure succeeds normally");

        handler.Bytes(Asset("0.3.19", "SyncShark-v0.3.19-win-x64.zip"), archive);
        await RejectAsync(() => client.DownloadAsync(update with { ArchiveSha256 = new string('0', 64) }, Current), "Wrong archive checksum is rejected and cleaned");
        await RejectAsync(() => client.DownloadAsync(update with { InstallerSha256 = new string('0', 64) }, Current), "Changed installer identity is rejected and cleaned");
        await RejectAsync(() => client.DownloadAsync(update, "0.3.18+wrong-source"), "Exact source predecessor is rechecked after download");
        await RejectAsync(() => client.DownloadAsync(update with { Archive = new Uri("https://example.org/package.zip") }, Current), "An arbitrary asset URL is refused before network access");

        foreach (var unsafeName in new[] { "../outside.txt", "scripts/../../outside.txt", "package/signed-candidate/bad:stream.dll",
            "package/signed-candidate/NUL.dll", "package/signed-candidate/source.cs", "package/signed-candidate/debug.pdb", "unrelated.txt",
            "package/signed-candidate/a/b/c/d/e/f/g/library.dll", "package/signed-candidate/SyncShark.exe/child.dll",
            "scripts/OtherUpdater.exe", "scripts/SyncSharkUpdater.ps1", "scripts/syncsharkupdater.exe",
            "scripts/SyncSharkUpdater.exe/child.dll" })
        {
            var bad = Archive(installer, unsafeName);
            handler.Bytes(Asset("0.3.19", "SyncShark-v0.3.19-win-x64.zip"), bad);
            await RejectAsync(() => client.DownloadAsync(update with { ArchiveBytes = bad.Length, ArchiveSha256 = Hash(bad) }, Current), "Unsafe archive member rejected: " + unsafeName);
        }
        foreach (var bad in new[] { Archive(installer, duplicate: true), Archive(installer, symlink: true) })
        {
            handler.Bytes(Asset("0.3.19", "SyncShark-v0.3.19-win-x64.zip"), bad);
            await RejectAsync(() => client.DownloadAsync(update with { ArchiveBytes = bad.Length, ArchiveSha256 = Hash(bad) }, Current), "Case-colliding or symbolic-link archive members are refused");
        }
        foreach (var bad in new[] { Archive(installer, manyParents: true), Archive(new byte[SyncPublicUpdates.MaxInstallerBytes + 1]) })
        {
            handler.Bytes(Asset("0.3.19", "SyncShark-v0.3.19-win-x64.zip"), bad);
            await RejectAsync(() => client.DownloadAsync(update with { ArchiveBytes = bad.Length, ArchiveSha256 = Hash(bad) }, Current),
                "Implicit parent count and installer length remain within the download and crash-cleanup limits");
        }
        Require(!Directory.EnumerateFileSystemEntries(SyncSharkRuntimePaths.ReplacementStagingRoot).Any(), "Rejected downloads leave no accumulating staging files");
        handler.Bytes(Asset("0.3.19", "SyncShark-v0.3.19-win-x64.zip"), archive);
        var retained = await client.DownloadAsync(update, Current); var retainedPath = retained.Directory;
        retained.ReleaseToInstaller(); retained.Dispose();
        Require(File.Exists(retained.Installer), "Installer handoff retains its one exact package after app ownership ends");
        SyncDownloadedUpdate.CleanupRetained(retainedPath);
        Require(!Directory.Exists(retainedPath), "The next launch can clean a retained download without recursive product deletion");

        var updaterArchive = Archive(installer, updater: true);
        handler.Bytes(Asset("0.3.19", "SyncShark-v0.3.19-win-x64.zip"), updaterArchive);
        var updaterRelease = update with { ArchiveBytes = updaterArchive.Length, ArchiveSha256 = Hash(updaterArchive) };
        using (var updaterDownload = await client.DownloadAsync(updaterRelease, Current))
        {
            Require(File.ReadAllText(Path.Combine(updaterDownload.Directory, "scripts", "SyncSharkUpdater.exe")) ==
                "fixture updater; never execute", "The real downloader accepts the fixed updater member without executing it");
        }
        var retainedUpdater = await client.DownloadAsync(updaterRelease, Current);
        var retainedUpdaterPath = retainedUpdater.Directory;
        retainedUpdater.ReleaseToInstaller(); retainedUpdater.Dispose();
        var foreignMember = Path.Combine(retainedUpdaterPath, "scripts", "OtherUpdater.exe");
        File.WriteAllText(foreignMember, "foreign fixture; preserve on refusal");
        await RejectAsync(() => { SyncDownloadedUpdate.CleanupRetained(retainedUpdaterPath); return Task.CompletedTask; },
            "Cleanup refuses an unknown executable beside the fixed updater");
        Require(File.Exists(foreignMember) && File.Exists(retainedUpdater.Installer) &&
            File.Exists(Path.Combine(retainedUpdaterPath, "scripts", "SyncSharkUpdater.exe")),
            "Refused cleanup preserves both foreign and legitimate package members");
        File.Delete(foreignMember); // Remove only this test's exact injected member.
        SyncDownloadedUpdate.CleanupRetained(retainedUpdaterPath);
        Require(!Directory.Exists(retainedUpdaterPath), "Retained updater packages use the same closed member policy as extraction");
        await RejectAsync(() => { SyncDownloadedUpdate.CleanupRetained(SyncSharkRuntimePaths.StateRoot); return Task.CompletedTask; }, "Retained-download cleanup rejects settings and other product paths");
        await RejectAsync(() => { SyncDownloadedUpdate.RequireDownloadDirectory(SyncSharkRuntimePaths.StateRoot); return Task.CompletedTask; },
            "Crash recovery rejects a non-download folder before inspecting its contents");

        handler.Replace(Asset("0.3.19", "Install-SyncShark.ps1"), () => new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("https://example.org/untrusted") } });
        await RejectAsync(() => client.FindAsync(Current), "A redirect outside GitHub's release delivery hosts is refused");
        handler.Bytes(Asset("0.3.19", "Install-SyncShark.ps1"), installer);
        handler.Json(Api, new[] { Release("0.3.19", installer, archive, draft: true), Release("0.3.20", newer, archive, prerelease: true) });
        Require(await client.FindAsync(Current) is null, "Draft and prerelease builds are not offered on the normal channel");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await client.FindAsync(Current, cancel.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { Require(true, "Cancellation stops an update check without effects"); }
        Require(!Directory.EnumerateFileSystemEntries(SyncSharkRuntimePaths.ReplacementStagingRoot).Any(), "All fixture downloads and failure residues are cleaned");
    }

    private static async Task RejectAsync(Func<Task> action, string label)
    {
        try { await action(); throw new Exception("Unexpected acceptance: " + label); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { Require(true, label); }
    }
    private static void Require(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); }
    private sealed class Handler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new();
        internal bool ArchiveRequested, NoCredentials = true;
        internal void Json(string uri, object value) => Bytes(uri, JsonSerializer.SerializeToUtf8Bytes(value));
        internal void Bytes(string uri, byte[] bytes) => Replace(uri, () => Response(bytes));
        internal void Replace(string uri, Func<HttpResponseMessage> result) => _responses[uri] = result;
        internal static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!.AbsoluteUri;
            ArchiveRequested |= uri.EndsWith(".zip", StringComparison.Ordinal);
            NoCredentials &= request.Headers.Authorization is null && !request.Headers.Contains("Cookie");
            return Task.FromResult(_responses.TryGetValue(uri, out var response) ? response() : new(HttpStatusCode.NotFound));
        }
    }
}
