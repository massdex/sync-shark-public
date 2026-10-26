using System.Net;
using System.Text.Json;

namespace Massdex.SyncShark.DropboxCli;

/// <summary>Regression checks for the standards-audit fixes to the Dropbox CLI.</summary>
internal static class DropboxCliAuditFixChecks
{
    internal static async Task RunAsync(string root)
    {
        await BatchStatusAsync(Path.Combine(root, "batch-status"));
        await DownloadVerificationAsync(Path.Combine(root, "download-verification"));
        await ApiDownloadAsync(Path.Combine(root, "api-download"));
        CatalogRouteKeys();
        await RouteVocabularyAsync(Path.Combine(root, "route-vocabulary"));
        HandlerValidation();
        await CleanupWarningsAsync(Path.Combine(root, "cleanup-warnings"));
        await JsonFileOptionsAsync(Path.Combine(root, "json-file-options"));
        Console.WriteLine("--dropbox-cli-checks audit fixes passed.");
    }

    // C3: a file-backed --body/--batch is read off the command thread and behaves as an inline value.
    private static async Task JsonFileOptionsAsync(string root)
    {
        var settings = DropboxCliSettings.Default with
        {
            BatchPollInterval = TimeSpan.Zero,
            BatchPollMaxWait = TimeSpan.FromSeconds(30)
        };
        using var h = await DropboxCliHarness.CreateAsync(root, settings);
        var account = h.Binding.ConnectionId;

        var body = Path.Combine(h.Root, "body.json");
        await File.WriteAllTextAsync(body, "{\"path\":\"/a.txt\"}");
        h.Wire.Respond("files/get_metadata", HttpStatusCode.OK, "{\".tag\":\"file\",\"name\":\"a.txt\"}");
        var viaBody = await Run(h, "dropbox", "api", "--account", account, "--route", "files/get_metadata",
            "--body", "@" + body, "--json");
        Require(viaBody.Exit == 0 && viaBody.Sent.Single().BodyText == "{\"path\":\"/a.txt\"}",
            "an @file body is read and sent verbatim");

        var batch = Path.Combine(h.Root, "batch.json");
        await File.WriteAllTextAsync(batch, "[{\"path\":\"/x\"}]");
        h.Wire.Respond("files/delete_batch", HttpStatusCode.OK, "{\"async_job_id\":\"j1\"}");
        h.Wire.Respond("files/delete_batch/check", HttpStatusCode.OK, "{\".tag\":\"complete\",\"entries\":[]}");
        var viaBatch = await Run(h, "dropbox", "rm", "--account", account, "--batch", "@" + batch, "--yes", "--json");
        Require(viaBatch.Exit == 0 && viaBatch.Sent[0].BodyText == "{\"entries\":[{\"path\":\"/x\"}]}",
            "an @file batch is read and sent verbatim");

        var missing = await Run(h, "dropbox", "api", "--account", account, "--route", "files/get_metadata",
            "--body", "@" + Path.Combine(h.Root, "absent.json"), "--json");
        Require(missing.Exit == 2 && missing.Sent.Length == 0, "a missing @file body is refused without a request");
    }

    // C2: a failed cleanup is carried in the command's result and reported on the progress channel.
    private static async Task CleanupWarningsAsync(string root)
    {
        using var h = await DropboxCliHarness.CreateAsync(Path.Combine(root, "harness"));
        var folder = h.Access.ResultFolder;
        Directory.CreateDirectory(folder);
        var stale = Path.Combine(folder, "stale-result.json");
        await File.WriteAllTextAsync(stale, "{}");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(30));
        File.SetAttributes(stale, FileAttributes.ReadOnly);
        try
        {
            // The spill prune cannot remove the read-only stale file, so the warning must reach the caller.
            h.Wire.Respond("users/get_current_account", HttpStatusCode.OK,
                "{\"blob\":\"" + new string('x', 200_000) + "\"}");
            var spilled = await Run(h, "dropbox", "whoami", "--account", h.Binding.ConnectionId, "--json");
            Require(spilled.Exit == 0 && spilled.Data!.Value.GetProperty("spilled").GetBoolean(),
                "an oversized result spills");
            var warnings = spilled.Data!.Value.GetProperty("warnings");
            Require(warnings.GetArrayLength() == 1 &&
                warnings[0].GetString()!.Contains("stale-result.json", StringComparison.OrdinalIgnoreCase),
                "the cleanup warning is carried in the command result");
            Require(spilled.Err.Contains("stale-result.json", StringComparison.OrdinalIgnoreCase),
                "the cleanup warning is on the progress channel");
        }
        finally
        {
            File.SetAttributes(stale, FileAttributes.Normal);
            File.Delete(stale);
        }
    }

    // C1: the catalog accepts only an exact declared handler name.
    private static void HandlerValidation()
    {
        foreach (var known in Enum.GetValues<DropboxCliHandler>())
            Require(DropboxCliCatalog.ParseHandler(known.ToString()) == known, "handler " + known + " parses");
        foreach (var bad in new[] { "Bogus", "", "0", "3", "-1", "99", "Route, Api" })
            Require(Throws(() => DropboxCliCatalog.ParseHandler(bad)), "an unknown handler '" + bad + "' is refused");
        foreach (var verb in Enum.GetValues<DropboxCliVerb>())
            Require(Enum.IsDefined(DropboxCliCatalog.Verb("dropbox " + verb.ToString().ToLowerInvariant())!.Handler),
                "verb " + verb + " carries a runnable handler");
    }

    // B5: api and routes share one style/policy mapping, so both read the same value for one route.
    private static async Task RouteVocabularyAsync(string root)
    {
        using var h = await DropboxCliHarness.CreateAsync(root);
        var account = h.Binding.ConnectionId;
        h.Wire.Respond("sharing/list_shared_links", HttpStatusCode.OK, "{\"links\":[]}");
        var api = await Run(h, "dropbox", "api", "--account", account, "--route", "sharing/list_shared_links",
            "--json");
        Require(api.Exit == 0, "api call for the vocabulary check");
        var listed = await Run(h, "dropbox", "routes", "--search", "sharing/list_shared_links", "--json");
        Require(listed.Exit == 0, "routes listing for the vocabulary check");
        var row = listed.Data!.Value.GetProperty("routes")[0];
        Require(api.Data!.Value.GetProperty("style").GetString() == row.GetProperty("style").GetString(),
            "api and routes agree on the route style");
        Require(api.Data!.Value.GetProperty("policy").GetString() == row.GetProperty("policy").GetString(),
            "api and routes agree on the route policy");
    }

    // B4: put and changes resolve their route names from the catalog's keyed calls, not from code literals.
    private static void CatalogRouteKeys()
    {
        var put = DropboxCliCatalog.Verb("dropbox put")!;
        Require(put.Call(DropboxCliCall.Upload).Route.Name == "files/upload", "put resolves the upload route");
        Require(put.Call(DropboxCliCall.SessionStart).Route.Name == "files/upload_session/start",
            "put resolves the session-start route");
        Require(put.Call(DropboxCliCall.SessionAppend).Route.Name == "files/upload_session/append_v2",
            "put resolves the session-append route");
        Require(put.Call(DropboxCliCall.SessionFinish).Route.Name == "files/upload_session/finish",
            "put resolves the session-finish route");
        var changes = DropboxCliCatalog.Verb("dropbox changes")!;
        Require(changes.Call(DropboxCliCall.LatestCursor).Route.Name == "files/list_folder/get_latest_cursor",
            "changes resolves the latest-cursor route");
        Require(changes.Call(DropboxCliCall.Longpoll).Route.Name == "files/list_folder/longpoll",
            "changes resolves the longpoll route");
        Require(changes.Call(DropboxCliCall.Continue).Route.Name == "files/list_folder/continue",
            "changes resolves the continue route");
        Require(Throws(() => DropboxCliCatalog.Verb("dropbox ls")!.Call(DropboxCliCall.Upload)),
            "a verb without the keyed call refuses the lookup instead of falling back");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    // A3: a missing content_hash skips verification, so verified is false and the reason is reported.
    private static async Task DownloadVerificationAsync(string root)
    {
        using var h = await DropboxCliHarness.CreateAsync(root);
        var account = h.Binding.ConnectionId;
        var destination = Path.Combine(h.Root, "got.bin");
        h.Wire.Respond("files/download", HttpStatusCode.OK, bytes: System.Text.Encoding.UTF8.GetBytes("hello"),
            apiResult: "{\"name\":\"a.txt\",\"path_display\":\"/a.txt\",\"path_lower\":\"/a.txt\",\"id\":\"id:a\"," +
                "\"rev\":\"r1\",\"size\":5}");
        var noHash = await Run(h, "dropbox", "get", "--account", account, "--path", "/a.txt", "--out", destination,
            "--json");
        Require(noHash.Exit == 0, "a download with no content_hash is written");
        Require(!noHash.Data!.Value.GetProperty("verified").GetBoolean(),
            "a download with no content_hash is reported unverified");
        Require(File.ReadAllText(destination) == "hello", "the unverified download still wrote the bytes");
        Require(noHash.Err.Contains("content_hash", StringComparison.Ordinal),
            "the unverified download's reason is reported");
    }

    // A1: polling completes only on an explicit complete; a failed job and an unknown tag are errors.
    private static async Task BatchStatusAsync(string root)
    {
        var settings = DropboxCliSettings.Default with
        {
            BatchPollInterval = TimeSpan.Zero,
            BatchPollMaxWait = TimeSpan.FromSeconds(30)
        };

        using (var h = await DropboxCliHarness.CreateAsync(Path.Combine(root, "failed"), settings))
        {
            var account = h.Binding.ConnectionId;
            h.Wire.Respond("files/delete_batch", HttpStatusCode.OK, "{\"async_job_id\":\"jf\"}");
            h.Wire.Respond("files/delete_batch/check", HttpStatusCode.OK,
                "{\".tag\":\"failed\",\"failed\":{\"reason\":\"too_many_write_operations\"}}");
            var failed = await Run(h, "dropbox", "rm", "--account", account, "--batch", "[{\"path\":\"/x\"}]",
                "--yes", "--json");
            Require(failed.Exit == 6 && Error(failed) == "batch_failed",
                "a failed batch job is an error, not a completed batch");
        }

        using (var h = await DropboxCliHarness.CreateAsync(Path.Combine(root, "unknown"), settings))
        {
            var account = h.Binding.ConnectionId;
            h.Wire.Respond("files/delete_batch", HttpStatusCode.OK, "{\"async_job_id\":\"ju\"}");
            h.Wire.Respond("files/delete_batch/check", HttpStatusCode.OK, "{\".tag\":\"something_else\"}");
            var unknown = await Run(h, "dropbox", "rm", "--account", account, "--batch", "[{\"path\":\"/x\"}]",
                "--yes", "--json");
            Require(unknown.Exit == 6 && Error(unknown) == "batch_failed",
                "an unrecognised batch status is refused");
        }
    }

    // B3: the api download path runs the same operation as get, so it verifies the returned content hash.
    private static async Task ApiDownloadAsync(string root)
    {
        using var h = await DropboxCliHarness.CreateAsync(root);
        var account = h.Binding.ConnectionId;
        var destination = Path.Combine(h.Root, "api-got.bin");
        var bytes = System.Text.Encoding.UTF8.GetBytes("hello");
        h.Wire.Respond("files/download", HttpStatusCode.OK, bytes: bytes,
            apiResult: "{\"name\":\"a.txt\",\"id\":\"id:a\",\"content_hash\":\"deadbeef\"}");
        var mismatch = await Run(h, "dropbox", "api", "--account", account, "--route", "files/download",
            "--body", "{\"path\":\"/a.txt\"}", "--out", destination, "--json");
        Require(mismatch.Exit == 6 && Error(mismatch) == "verification_failed",
            "an api download with a mismatched content_hash fails verification");

        h.Wire.Respond("files/download", HttpStatusCode.OK, bytes: bytes,
            apiResult: "{\"name\":\"a.txt\",\"id\":\"id:a\",\"content_hash\":\"" +
                DropboxContentHash.Compute(bytes) + "\"}");
        var ok = await Run(h, "dropbox", "api", "--account", account, "--route", "files/download",
            "--body", "{\"path\":\"/a.txt\"}", "--out", destination, "--json");
        Require(ok.Exit == 0 && File.ReadAllText(destination) == "hello",
            "an api download with a matching content_hash succeeds");
    }

    private static async Task<(int Exit, string Out, string Err, JsonElement? Data,
        DropboxRouteWire.Request[] Sent)> Run(DropboxCliHarness harness, params string[] args)
    {
        var before = harness.Wire.Requests.Count;
        var result = await harness.RunAsync(args);
        return (result.Exit, result.Out, result.Err, result.Data,
            harness.Wire.Requests.Skip(before).ToArray());
    }

    private static string Error((int Exit, string Out, string Err, JsonElement? Data,
        DropboxRouteWire.Request[] Sent) result)
    {
        using var document = JsonDocument.Parse(result.Out);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
