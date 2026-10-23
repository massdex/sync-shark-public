using System.Net;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.DropboxCli;

/// <summary>Groups 6, 8, 9, 10, 11: the typed verbs, put, batch, error mapping and spill.</summary>
internal static class DropboxCliVerbChecks
{
    internal static async Task RunAsync(string root)
    {
        await VerbsAsync(Path.Combine(root, "verbs"));
        await PutAsync(Path.Combine(root, "put"));
        await BatchAsync(Path.Combine(root, "batch"));
        await ErrorsAsync(Path.Combine(root, "errors"));
        await SpillAsync(Path.Combine(root, "spill"));
        await ReviewFixesAsync(Path.Combine(root, "review"));
        await RoundThreeAsync(Path.Combine(root, "round3"));
        await V2AccountHookAsync(Path.Combine(root, "v2-account"));
        Console.WriteLine("--dropbox-cli-checks verbs passed.");
    }

    private static async Task VerbsAsync(string root)
    {
        using var h = await DropboxCliHarness.CreateAsync(root);
        var account = h.Binding.ConnectionId;

        h.Wire.Respond("users/get_current_account", HttpStatusCode.OK, "{\"account_id\":\"acc-1\"}");
        var who = await Run(h, "dropbox", "whoami", "--account", account, "--json");
        Require(who.Exit == 0, "whoami exit");
        Require(who.Sent.Single().Route == "/2/users/get_current_account", "whoami route");
        Require(who.Sent.Single().Authorization == "Bearer " + DropboxRouteWire.SecretToken, "whoami bearer");
        Require(who.Sent.Single().PathRoot!.Contains("root-namespace", StringComparison.Ordinal), "whoami path root");
        Require(who.Sent.Single().BodyText == "null", "whoami void body");
        Require(who.Data!.Value.GetProperty("result").GetProperty("account_id").GetString() == "acc-1", "whoami result");
        Require(!who.Out.Contains(DropboxRouteWire.SecretToken) && !who.Err.Contains(DropboxRouteWire.SecretToken),
            "whoami no token");

        h.Wire.Respond("files/list_folder", HttpStatusCode.OK,
            "{\"entries\":[" + FileJson("a.txt") + "],\"cursor\":\"c1\",\"has_more\":true}");
        var ls = await Run(h, "dropbox", "ls", "--account", account, "--path", "/", "--json");
        Require(ls.Exit == 0, "ls exit");
        Require(ls.Sent.Single().Route == "/2/files/list_folder", "ls route");
        Require(ls.Sent.Single().BodyText == "{\"path\":\"\"}", "ls body: root path, no default limit or flags");
        Require(ls.Data!.Value.GetProperty("entries")[0].GetProperty("name").GetString() == "a.txt", "ls entry");
        Require(ls.Data!.Value.GetProperty("hasMore").GetBoolean() && ls.Data!.Value.GetProperty("cursor").GetString() == "c1",
            "ls page");

        h.Wire.Respond("files/list_folder/continue", HttpStatusCode.OK, "{\"entries\":[],\"cursor\":\"c2\",\"has_more\":false}");
        var cont = await Run(h, "dropbox", "ls", "--account", account, "--cursor", "c1", "--json");
        Require(cont.Exit == 0 && cont.Sent.Single().BodyText == "{\"cursor\":\"c1\"}", "ls cursor body");

        h.Wire.Respond("files/get_metadata", HttpStatusCode.OK, FileJson("a.txt"));
        var stat = await Run(h, "dropbox", "stat", "--account", account, "--path", "/a.txt", "--json");
        Require(stat.Exit == 0 && stat.Sent.Single().BodyText == "{\"path\":\"/a.txt\"}", "stat body");
        Require(stat.Data!.Value.GetProperty("tag").GetString() == "file", "stat entry tag");

        var outPath = Path.Combine(h.Root, "got.bin");
        h.Wire.Respond("files/download", HttpStatusCode.OK, bytes: Encoding.UTF8.GetBytes("hello"),
            apiResult: FileJson("a.txt", DropboxContentHash.Compute(Encoding.UTF8.GetBytes("hello"))));
        var get = await Run(h, "dropbox", "get", "--account", account, "--path", "/a.txt", "--out", outPath, "--json");
        Require(get.Exit == 0, "get exit");
        Require(File.ReadAllText(outPath) == "hello", "get wrote the bytes");
        Require(get.Sent.Single().ApiArg == "{\"path\":\"/a.txt\"}", "get api arg");
        Require(get.Data!.Value.GetProperty("bytes").GetInt64() == 5 && get.Data!.Value.GetProperty("verified").GetBoolean(),
            "get receipt");

        h.Wire.Respond("files/create_folder_v2", HttpStatusCode.OK, "{\"metadata\":" + Folder("x") + "}");
        var mkdir = await Run(h, "dropbox", "mkdir", "--account", account, "--path", "/x", "--yes", "--json");
        Require(mkdir.Exit == 0 && mkdir.Sent.Single().BodyText == "{\"path\":\"/x\"}", "mkdir body");
        Require(mkdir.Data!.Value.GetProperty("tag").GetString() == "folder", "mkdir entry");

        h.Wire.Respond("files/delete_v2", HttpStatusCode.OK, "{\"metadata\":" + FileJson("x") + "}");
        var rm = await Run(h, "dropbox", "rm", "--account", account, "--path", "/x", "--yes", "--json");
        Require(rm.Exit == 0 && rm.Sent.Single().BodyText == "{\"path\":\"/x\"}", "rm body");

        h.Wire.Respond("files/move_v2", HttpStatusCode.OK, "{\"metadata\":" + FileJson("b.txt") + "}");
        var mv = await Run(h, "dropbox", "mv", "--account", account, "--path", "/a.txt", "--to", "/b.txt", "--yes", "--json");
        Require(mv.Exit == 0 && mv.Sent.Single().BodyText == "{\"from_path\":\"/a.txt\",\"to_path\":\"/b.txt\"}", "mv body");

        h.Wire.Respond("files/copy_v2", HttpStatusCode.OK, "{\"metadata\":" + FileJson("b.txt") + "}");
        var cp = await Run(h, "dropbox", "cp", "--account", account, "--path", "/a.txt", "--to", "/b.txt", "--yes", "--json");
        Require(cp.Exit == 0 && cp.Sent.Single().BodyText == "{\"from_path\":\"/a.txt\",\"to_path\":\"/b.txt\"}", "cp body");

        h.Wire.Respond("files/search_v2", HttpStatusCode.OK,
            "{\"matches\":[{\"match_type\":\"filename\",\"metadata\":{\"metadata\":" + FileJson("a.txt") + "}}],\"has_more\":false}");
        var search = await Run(h, "dropbox", "search", "--account", account, "--search", "a", "--json");
        Require(search.Exit == 0 && search.Sent.Single().BodyText == "{\"query\":\"a\"}", "search body");
        Require(search.Data!.Value.GetProperty("entries")[0].GetProperty("name").GetString() == "a.txt", "search entry");

        h.Wire.Respond("files/list_revisions", HttpStatusCode.OK,
            "{\"entries\":[" + FileJson("a.txt") + "],\"is_deleted\":true}");
        var revisions = await Run(h, "dropbox", "revisions", "--account", account, "--path", "/a.txt", "--json");
        Require(revisions.Exit == 0 && revisions.Sent.Single().BodyText == "{\"path\":\"/a.txt\",\"mode\":\"path\"}",
            "revisions body: no default limit");
        Require(revisions.Data!.Value.GetProperty("isDeleted").GetBoolean(), "revisions deletion comes from the response");

        h.Wire.Respond("files/list_revisions", HttpStatusCode.OK,
            "{\"entries\":[" + FileJson("b.txt") + "],\"is_deleted\":false}");
        var liveRevisions = await Run(h, "dropbox", "revisions", "--account", account, "--path", "/b.txt", "--json");
        Require(liveRevisions.Exit == 0 && !liveRevisions.Data!.Value.GetProperty("isDeleted").GetBoolean(),
            "a live revision page is not reported deleted");

        h.Wire.Respond("files/restore", HttpStatusCode.OK, FileJson("a.txt"));
        var restore = await Run(h, "dropbox", "restore", "--account", account, "--path", "/a.txt", "--rev", "r0", "--yes", "--json");
        Require(restore.Exit == 0 && restore.Sent.Single().BodyText == "{\"path\":\"/a.txt\",\"rev\":\"r0\"}", "restore body");

        h.Wire.Respond("files/list_folder/get_latest_cursor", HttpStatusCode.OK, "{\"cursor\":\"c1\"}");
        var changes = await Run(h, "dropbox", "changes", "--account", account, "--path", "/", "--json");
        Require(changes.Exit == 0, "changes exit");
        Require(changes.Data!.Value.GetProperty("cursor").GetString() == "c1", "changes cursor");
        Require(changes.Sent.Single().BodyText == "{\"path\":\"\",\"recursive\":false,\"include_deleted\":true}",
            "changes cursor body");

        var thumbPath = Path.Combine(h.Root, "thumb.png");
        h.Wire.Respond("files/get_thumbnail_v2", HttpStatusCode.OK, bytes: new byte[] { 1, 2, 3 },
            apiResult: "{\"name\":\"a.txt\",\"id\":\"id:a\"}");
        var thumb = await Run(h, "dropbox", "thumbnail", "--account", account, "--path", "/a.txt", "--out", thumbPath,
            "--size", "w64h64", "--json");
        Require(thumb.Exit == 0 && File.ReadAllBytes(thumbPath).Length == 3, "thumbnail wrote bytes");
        Require(thumb.Sent.Single().ApiArg ==
            "{\"resource\":{\".tag\":\"path\",\"path\":\"/a.txt\"},\"size\":\"w64h64\"}",
            "thumbnail resource argument");

        h.Wire.Respond("sharing/list_shared_links", HttpStatusCode.OK, "{\"links\":[]}");
        var api = await Run(h, "dropbox", "api", "--account", account, "--route", "sharing/list_shared_links", "--json");
        Require(api.Exit == 0 && api.Sent.Single().BodyText == "{}", "api default body");
        Require(api.Data!.Value.GetProperty("policy").GetString() == "read", "api policy");

        var before = h.Wire.Requests.Count;
        var routes = await Run(h, "dropbox", "routes", "--json");
        Require(routes.Exit == 0 && h.Wire.Requests.Count == before, "routes makes no request");
        Require(routes.Data!.Value.GetProperty("count").GetInt32() > 100, "routes count");
    }

    private static async Task PutAsync(string root)
    {
        using var h = await DropboxCliHarness.CreateAsync(root);
        var account = h.Binding.ConnectionId;
        var source = Path.Combine(h.Root, "small.txt");
        await File.WriteAllTextAsync(source, "hello");
        var hash = DropboxContentHash.Compute(Encoding.UTF8.GetBytes("hello"));

        h.Wire.Respond("files/upload", HttpStatusCode.OK, "{\"name\":\"a.txt\",\"id\":\"id:a\",\"rev\":\"r1\",\"size\":5,\"content_hash\":\"" + hash + "\"}");
        var put = await Run(h, "dropbox", "put", "--account", account, "--in", source, "--path", "/a.txt", "--yes", "--json");
        Require(put.Exit == 0, "put exit");
        Require(put.Sent.Single().Route == "/2/files/upload", "put route");
        Require(put.Sent.Single().ApiArg!.Contains("\"mode\":{\".tag\":\"add\"}", StringComparison.Ordinal), "put add mode");
        Require(put.Sent.Single().ContentType == "application/octet-stream", "put octet-stream");
        Require(put.Data!.Value.GetProperty("sessionUsed").GetBoolean() == false && put.Data!.Value.GetProperty("verified").GetBoolean(),
            "put receipt");

        h.Wire.Respond("files/upload", HttpStatusCode.OK, "{\"name\":\"a.txt\",\"id\":\"id:a\",\"rev\":\"r1\",\"size\":5,\"content_hash\":\"" + hash + "\"}");
        var overwrite = await Run(h, "dropbox", "put", "--account", account, "--in", source, "--path", "/a.txt",
            "--mode", "overwrite", "--yes", "--json");
        Require(overwrite.Exit == 0 && overwrite.Sent.Single().ApiArg!.Contains("{\".tag\":\"overwrite\"}", StringComparison.Ordinal),
            "put overwrite mode");

        h.Wire.Respond("files/upload", HttpStatusCode.OK, "{\"name\":\"a.txt\",\"id\":\"id:a\",\"rev\":\"r2\",\"size\":5,\"content_hash\":\"" + hash + "\"}");
        var update = await Run(h, "dropbox", "put", "--account", account, "--in", source, "--path", "/a.txt",
            "--mode", "update", "--rev", "r1", "--yes", "--json");
        Require(update.Exit == 0 && update.Sent.Single().ApiArg!.Contains("{\".tag\":\"update\",\"update\":\"r1\"}", StringComparison.Ordinal) &&
            update.Sent.Single().ApiArg!.Contains("\"strict_conflict\":true", StringComparison.Ordinal), "put update mode and rev");

        h.Wire.Respond("files/upload", HttpStatusCode.OK, "{\"name\":\"a.txt\",\"id\":\"id:a\",\"rev\":\"r1\",\"size\":5,\"content_hash\":\"deadbeef\"}");
        var mismatch = await Run(h, "dropbox", "put", "--account", account, "--in", source, "--path", "/a.txt", "--yes", "--json");
        Require(mismatch.Exit == 6 && Error(mismatch) == "verification_failed", "put hash mismatch");

        using var session = await DropboxCliHarness.CreateAsync(Path.Combine(root, "session"), DropboxCliSettings.Default with
        {
            SingleUploadMaxBytes = 8L * 1024 * 1024,
            UploadChunkBytes = 8 * 1024 * 1024
        });
        await SessionPutAsync(session, session.Binding.ConnectionId);
    }

    private static async Task SessionPutAsync(DropboxCliHarness h, string account)
    {
        var big = Path.Combine(h.Root, "big.bin");
        var bytes = new byte[9 * 1024 * 1024];
        await File.WriteAllBytesAsync(big, bytes);
        var hash = DropboxContentHash.Compute(bytes);
        h.Wire.Respond("files/upload_session/start", HttpStatusCode.OK, "{\"session_id\":\"s1\"}");
        h.Wire.Respond("files/upload_session/append_v2", HttpStatusCode.OK, "{}");
        h.Wire.Respond("files/upload_session/finish", HttpStatusCode.OK,
            "{\"name\":\"big.bin\",\"id\":\"id:big\",\"rev\":\"r1\",\"size\":9437184,\"content_hash\":\"" + hash + "\"}");
        var result = await Run(h, "dropbox", "put", "--account", account, "--in", big, "--path", "/big.bin", "--yes", "--json");
        Require(result.Exit == 0, "session put exit");
        Require(result.Data!.Value.GetProperty("sessionUsed").GetBoolean(), "session put used a session");
        Require(result.Sent[0].Route == "/2/files/upload_session/start", "session start");
        Require(result.Sent[1].Route == "/2/files/upload_session/append_v2" &&
            result.Sent[1].ApiArg!.Contains("\"offset\":8388608", StringComparison.Ordinal), "session append at 8 MiB");
        Require(result.Sent[2].Route == "/2/files/upload_session/finish", "session finish");
        Require(!Directory.EnumerateFiles(Path.Combine(h.Access.ResultFolder, "pending")).Any(),
            "completed upload session resolves its receipt");
        Require(result.Err.Split("Dropbox write receipt:", StringSplitOptions.None).Length == 2,
            "one logical upload creates one receipt across start, append and finish");
        h.Wire.Respond("files/upload_session/start", HttpStatusCode.OK, "{\"session_id\":\"s2\"}");
        h.Wire.Respond("files/upload_session/append_v2", HttpStatusCode.OK, "{}");
        h.Wire.Respond("files/upload_session/finish", HttpStatusCode.InternalServerError, "{\"error_summary\":\"uncertain\"}");
        var uncertain = await Run(h, "dropbox", "put", "--account", account, "--in", big, "--path", "/big.bin", "--yes", "--json");
        Require(uncertain.Exit == 6 && Directory.EnumerateFiles(Path.Combine(h.Access.ResultFolder, "pending"), "*.json").Count() == 1,
            "uncertain session finish retains exactly one logical upload receipt");
    }

    private static async Task BatchAsync(string root)
    {
        var settings = DropboxCliSettings.Default with
        {
            BatchPollInterval = TimeSpan.Zero,
            BatchPollMaxWait = TimeSpan.FromSeconds(30)
        };
        using (var h = await DropboxCliHarness.CreateAsync(Path.Combine(root, "complete"), settings))
        {
            var account = h.Binding.ConnectionId;
            h.Wire.Respond("files/delete_batch", HttpStatusCode.OK, "{\"async_job_id\":\"j1\"}");
            h.Wire.Respond("files/delete_batch/check", HttpStatusCode.OK, "{\".tag\":\"in_progress\"}");
            h.Wire.Respond("files/delete_batch/check", HttpStatusCode.OK,
                "{\".tag\":\"complete\",\"entries\":[{\"success\":{\"metadata\":" + FileJson("x") + "}}]}");
            var batch = await Run(h, "dropbox", "rm", "--account", account, "--batch", "[{\"path\":\"/x\"}]", "--yes", "--json");
            Require(batch.Exit == 0, "batch exit");
            Require(!Directory.EnumerateFiles(Path.Combine(h.Access.ResultFolder, "pending")).Any(),
                "completed batch resolves its durable receipt");
            Require(batch.Data!.Value.GetProperty("complete").GetBoolean(), "batch complete");
            Require(batch.Data!.Value.GetProperty("entries")[0].GetProperty("ok").GetBoolean(), "batch entry ok");
            Require(batch.Data!.Value.GetProperty("entries")[0].GetProperty("entry").GetProperty("name").GetString() == "x",
                "batch entry reads the success variant's metadata");
        }
        using (var h = await DropboxCliHarness.CreateAsync(Path.Combine(root, "poll-failure"), settings))
        {
            h.Wire.Respond("files/delete_batch", HttpStatusCode.OK, "{\"async_job_id\":\"retained-job\"}");
            h.Wire.Respond("files/delete_batch/check", HttpStatusCode.BadRequest, "{\"error_summary\":\"poll unavailable\"}");
            var batch = await Run(h, "dropbox", "rm", "--account", h.Binding.ConnectionId,
                "--batch", "[{\"path\":\"/x\"}]", "--yes", "--json");
            Require(batch.Exit == 6 && batch.Data!.Value.GetProperty("jobId").GetString() == "retained-job",
                "poll failure retains the provider handle in the failure envelope");
            var file = batch.Data!.Value.GetProperty("receiptPath").GetString()!;
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Require(receipt.RootElement.GetProperty("pollRoute").GetString() == "files/delete_batch/check" &&
                receipt.RootElement.GetProperty("status").GetString() == "job_pending",
                "failed polling preserves a durable resumable job rather than retrying its write");
        }
        using (var h = await DropboxCliHarness.CreateAsync(Path.Combine(root, "poll-cancel"), settings))
        {
            var invocation = SyncCliRegistry.Parse(["--cli", "dropbox", "rm", "--account", h.Binding.ConnectionId,
                "--batch", "[{\"path\":\"/x\"}]", "--yes"]);
            var verb = DropboxCliCatalog.Verb("dropbox rm")!;
            var arguments = new DropboxCliArguments(invocation, verb, settings);
            var account = await DropboxCliRouteRunner.OpenAsync(h.Access, arguments, CancellationToken.None);
            var runner = new DropboxCliRouteRunner(account, arguments, settings, _ => { }, h.Access.ResultFolder);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try
            {
                await runner.PollBatchAsync(verb.FirstCall(arguments.Present)!.Poll!, "cancel-job", canceled.Token);
                throw new InvalidOperationException("Canceled poll unexpectedly completed");
            }
            catch (SyncCliException error) when (error.ResultData is DropboxCliWriteReceipt receipt)
            {
                Require(receipt.JobId == "cancel-job" && File.Exists(receipt.ReceiptPath),
                    "canceled polling persists and reports its handle independently of the canceled token");
                Require(h.Wire.Requests.Count == 0, "canceled polling never resubmits the original effect");
            }
        }
        var incomplete = DropboxCliSettings.Default with { BatchPollMaxWait = TimeSpan.Zero };
        using (var h = await DropboxCliHarness.CreateAsync(Path.Combine(root, "wait"), incomplete))
        {
            var account = h.Binding.ConnectionId;
            h.Wire.Respond("files/delete_batch", HttpStatusCode.OK, "{\"async_job_id\":\"j2\"}");
            var batch = await Run(h, "dropbox", "rm", "--account", account, "--batch", "[{\"path\":\"/x\"}]", "--yes", "--json");
            Require(batch.Exit == 0 && !batch.Data!.Value.GetProperty("complete").GetBoolean(), "batch still running");
            Require(batch.Data!.Value.GetProperty("jobId").GetString() == "j2", "batch job id retained");
        }
    }

    private static async Task ErrorsAsync(string root)
    {
        using var h = await DropboxCliHarness.CreateAsync(root);
        var account = h.Binding.ConnectionId;

        h.Wire.Respond("files/get_metadata", HttpStatusCode.Conflict,
            "{\"error_summary\":\"path/not_found/..\",\"error\":{\".tag\":\"path\"}}");
        var missing = await Run(h, "dropbox", "stat", "--account", account, "--path", "/missing", "--json");
        Require(missing.Exit == 4 && Error(missing) == "missing_target", "409 not_found maps to 4");

        h.Wire.Respond("files/create_folder_v2", HttpStatusCode.Conflict,
            "{\"error_summary\":\"path/conflict/folder/..\",\"error\":{\".tag\":\"path\"}}");
        var conflict = await Run(h, "dropbox", "mkdir", "--account", account, "--path", "/x", "--yes", "--json");
        Require(conflict.Exit == 5 && Error(conflict) == "conflict", "409 conflict maps to 5");
        Require(!Directory.EnumerateFiles(Path.Combine(h.Access.ResultFolder, "pending")).Any(),
            "definite provider rejection does not leave an uncertain receipt");
        h.Wire.Respond("files/create_folder_v2", HttpStatusCode.OK, "{\"metadata\":{\"name\":\"api\"}}");
        var apiSuccess = await Run(h, "dropbox", "api", "--account", account, "--route", "files/create_folder_v2",
            "--body", "{\"path\":\"/api\"}", "--yes", "--json");
        Require(apiSuccess.Exit == 0 && !Directory.EnumerateFiles(Path.Combine(h.Access.ResultFolder, "pending")).Any(),
            "generic API successful write resolves its receipt");
        h.Wire.Respond("files/create_folder_v2", HttpStatusCode.InternalServerError, "{\"error_summary\":\"uncertain\"}");
        var apiUncertain = await Run(h, "dropbox", "api", "--account", account, "--route", "files/create_folder_v2",
            "--body", "{\"path\":\"/api\"}", "--yes", "--json");
        Require(apiUncertain.Exit == 6 && File.Exists(apiUncertain.Data!.Value.GetProperty("receiptPath").GetString()),
            "generic API uncertain write retains a durable receipt");

        h.Wire.Respond("users/get_current_account", HttpStatusCode.Unauthorized,
            "{\"error_summary\":\"invalid_access_token/..\",\"error\":{\".tag\":\"other\"}}");
        var unauthorized = await Run(h, "dropbox", "whoami", "--account", account, "--json");
        Require(unauthorized.Exit == 5 && Error(unauthorized) == "forbidden", "401 maps to 5");

        h.Wire.Respond("files/delete_v2", HttpStatusCode.InternalServerError, "{\"error_summary\":\"internal_error\"}");
        var uncertain = await Run(h, "dropbox", "rm", "--account", account, "--path", "/x", "--yes", "--json");
        Require(uncertain.Exit == 6 && Error(uncertain) == "effect_uncertain", "write 500 is uncertain");
        Require(uncertain.Data!.Value.GetProperty("effectUncertain").GetBoolean(), "uncertain flag");
        var receiptPath = uncertain.Data.Value.GetProperty("receiptPath").GetString()!;
        Require(File.Exists(receiptPath), "uncertain effect has a durable receipt");
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
        Require(receipt.RootElement.GetProperty("status").GetString() == "effect_unverified" &&
            receipt.RootElement.GetProperty("route").GetString() == "files/delete_v2",
            "durable receipt preserves exact route without claiming completion");
    }

    private static async Task SpillAsync(string root)
    {
        using var h = await DropboxCliHarness.CreateAsync(root);
        var big = "{\"blob\":\"" + new string('x', 200_000) + "\"}";
        h.Wire.Respond("users/get_current_account", HttpStatusCode.OK, big);
        var spilling = await Run(h, "dropbox", "whoami", "--account", h.Binding.ConnectionId, "--json");
        Require(spilling.Exit == 0, "spill exit");
        Require(spilling.Data!.Value.GetProperty("spilled").GetBoolean(), "spill flag");
        var file = spilling.Data!.Value.GetProperty("resultFile").GetString()!;
        Require(File.Exists(file) && file.StartsWith(h.Access.ResultFolder, StringComparison.OrdinalIgnoreCase),
            "spill wrote the result file");
    }

    private static async Task ReviewFixesAsync(string root)
    {
        using var h = await DropboxCliHarness.CreateAsync(root);
        var account = h.Binding.ConnectionId;

        // Fix 6: the human output of a single result renders the result, not an empty object.
        h.Wire.Respond("users/get_current_account", HttpStatusCode.OK, "{\"account_id\":\"acc-1\"}");
        var human = await Run(h, "dropbox", "whoami", "--account", account);
        Require(human.Exit == 0 && human.Out.Contains("acc-1", StringComparison.Ordinal),
            "human output of a single result shows the result");

        // Fix 4: verified is true only when Dropbox returned a matching content_hash.
        var source = Path.Combine(h.Root, "small.txt");
        await File.WriteAllTextAsync(source, "hello");
        h.Wire.Respond("files/upload", HttpStatusCode.OK, "{\"name\":\"a.txt\",\"id\":\"id:a\",\"rev\":\"r1\",\"size\":5}");
        var unverified = await Run(h, "dropbox", "put", "--account", account, "--in", source, "--path", "/a.txt",
            "--yes", "--json");
        Require(unverified.Exit == 0 && !unverified.Data!.Value.GetProperty("verified").GetBoolean(),
            "an upload with no returned hash is not verified");

        // Fixes 1 and 10: a failed download never truncates the destination and leaves no temporary file.
        var destination = Path.Combine(h.Root, "kept.bin");
        await File.WriteAllTextAsync(destination, "original");
        h.Wire.Respond("files/download", HttpStatusCode.OK, bytes: Encoding.UTF8.GetBytes("new"),
            apiResult: FileJson("a.txt", "not-the-real-hash"));
        var mismatch = await Run(h, "dropbox", "get", "--account", account, "--path", "/a.txt", "--out", destination,
            "--yes", "--json");
        Require(mismatch.Exit == 6, "a download hash mismatch fails");
        Require(await File.ReadAllTextAsync(destination) == "original",
            "the refused download left the destination unchanged");
        Require(!Directory.EnumerateFiles(h.Root, "*.partial").Any(), "no temporary file survives a failed download");

        // Fix 1: an approved download replaces the destination atomically.
        h.Wire.Respond("files/download", HttpStatusCode.OK, bytes: Encoding.UTF8.GetBytes("new"),
            apiResult: FileJson("a.txt", DropboxContentHash.Compute(Encoding.UTF8.GetBytes("new"))));
        var replaced = await Run(h, "dropbox", "get", "--account", account, "--path", "/a.txt", "--out", destination,
            "--yes", "--json");
        Require(replaced.Exit == 0 && await File.ReadAllTextAsync(destination) == "new",
            "an approved download replaces the destination");
        Require(!Directory.EnumerateFiles(h.Root, "*.partial").Any(), "no temporary file survives a successful download");

        // Fix 2: the rpc output path is validated before the route is called.
        var missingFolder = Path.Combine(h.Root, "absent", "result.json");
        h.Wire.Respond("users/get_space_usage", HttpStatusCode.OK, "{\"used\":1}");
        var before = h.Wire.Requests.Count;
        var refused = await Run(h, "dropbox", "api", "--account", account, "--route", "users/get_space_usage",
            "--out", missingFolder, "--json");
        Require(refused.Exit == 2 && h.Wire.Requests.Count == before, "api validates --out before calling the route");

        // Fix 5: the v2 runtime serves the Dropbox group through the shared registry dispatch path.
        var store = new Massdex.SyncShark.V2.InMemoryStateStore();
        var v2 = new Massdex.SyncShark.V2.V2CliRuntime(null, store,
            new Massdex.SyncShark.V2.SyncRun(store, new Massdex.SyncShark.V2.Executor(store, Massdex.SyncShark.V2.V2Settings.Default,
                    Massdex.SyncShark.V2.ExecutorChecks.Admission()),
                new Massdex.SyncShark.V2.FakeEndpointProvider(), Massdex.SyncShark.V2.V2Settings.Default,
                TimeProvider.System),
            new Massdex.SyncShark.V2.EndpointRootProbe(new Massdex.SyncShark.V2.FakeEndpointProvider()), "C:\\root");
        var routes = await SyncCliRegistry.DispatchAsync(v2,
            SyncCliRegistry.Parse(["--cli", "dropbox", "routes", "--json"]), TextWriter.Null, CancellationToken.None);
        Require(routes is DropboxCliRoutesData { Count: > 100 }, "the v2 runtime serves the Dropbox group");

        // Round 3 fix 3: without the retained product state the v2 hook refuses with the catalog's text.
        try
        {
            await SyncCliRegistry.DispatchAsync(v2, SyncCliRegistry.Parse(
                ["--cli", "dropbox", "whoami", "--account", account, "--json"]), TextWriter.Null, CancellationToken.None);
            Require(false, "the v2 hook without product state refuses");
        }
        catch (SyncCliUnsupportedException error)
        {
            Require(error.Message == DropboxCliCatalog.Message(DropboxCliMessage.StateUnavailable),
                "the v2 hook's refusal text comes from dropbox-cli.json");
        }
        await v2.DisposeAsync();
    }

    private static async Task RoundThreeAsync(string root)
    {
        using var h = await DropboxCliHarness.CreateAsync(root);
        var account = h.Binding.ConnectionId;

        // Fix 1: a destination that appears after the preflight is refused by the move itself, not a prior check.
        var raced = Path.Combine(h.Root, "raced.bin");
        var temporary = DropboxCliLocalFiles.CreateTemporary(raced);
        temporary.Write("downloaded"u8);
        await File.WriteAllTextAsync(raced, "appeared");
        var refused = false;
        try { DropboxCliLocalFiles.CommitOutput(raced, temporary, yes: false); }
        catch (SyncCliUsageException) { refused = true; }
        Require(refused, "a destination that appeared before the move is refused without --yes");
        Require(await File.ReadAllTextAsync(raced) == "appeared", "the refused move left the destination unchanged");
        Require(!Directory.EnumerateFiles(h.Root, "*.partial").Any(), "the refused move deleted the temporary");

        var approved = DropboxCliLocalFiles.CreateTemporary(raced);
        approved.Write("downloaded"u8);
        DropboxCliLocalFiles.CommitOutput(raced, approved, yes: true);
        Require(await File.ReadAllTextAsync(raced) == "downloaded", "with --yes the move replaces the destination");
        Require(!Directory.EnumerateFiles(h.Root, "*.partial").Any(), "the approved move leaves no temporary");

        // Fix 2: an --out that is an existing folder is refused before any request, even with --yes.
        var folder = Path.Combine(h.Root, "a-folder");
        Directory.CreateDirectory(folder);
        foreach (var args in new[]
        {
            new[] { "dropbox", "get", "--account", account, "--path", "/a.txt", "--out", folder, "--yes", "--json" },
            new[] { "dropbox", "thumbnail", "--account", account, "--path", "/a.txt", "--out", folder, "--yes",
                "--json" },
            new[] { "dropbox", "api", "--account", account, "--route", "users/get_space_usage", "--out", folder,
                "--yes", "--json" },
            new[] { "dropbox", "api", "--account", account, "--route", "files/download", "--body",
                "{\"path\":\"/a.txt\"}", "--out", folder, "--yes", "--json" }
        })
        {
            var result = await Run(h, args);
            Require(result.Exit == 2 && result.Sent.Length == 0, string.Join(' ', args) + " refuses a folder --out");
            Require(Directory.Exists(folder), string.Join(' ', args) + " left the folder in place");
        }

        // Fix 3: the human view's presentation strings come from dropbox-cli.json.
        h.Wire.Respond("files/list_folder", HttpStatusCode.OK,
            "{\"entries\":[" + FileJson("a.txt") + "],\"cursor\":\"c1\",\"has_more\":true}");
        var page = await Run(h, "dropbox", "ls", "--account", account, "--path", "/");
        Require(page.Exit == 0 && page.Out.Contains(DropboxCliCatalog.View(DropboxCliView.PageFooter,
            ("cursor", "c1"), ("hasMore", true.ToString())), StringComparison.Ordinal), "page footer from the catalog");
        Require(page.Out.Contains(DropboxCliCatalog.View(DropboxCliView.EntryLine, ("tag", "file"), ("size", "5"),
            ("modified", "2026-10-02 00:00:00"), ("path", "/a.txt")), StringComparison.Ordinal),
            "entry line from the catalog");
        var table = await Run(h, "dropbox", "routes", "--search", "files/upload");
        Require(table.Exit == 0 && table.Out.Contains(" route(s) at ", StringComparison.Ordinal) &&
            table.Out.Contains("files/upload  upload  content  write", StringComparison.Ordinal),
            "the routes table renders from the catalog templates");
    }

    private static async Task V2AccountHookAsync(string root)
    {
        // Fix 4: an account verb through the v2 runtime's own hook: the retained connection store under the
        // product lock, the protected-credential seam, the real refreshing token source and the HTTP wire.
        Directory.CreateDirectory(root);
        using var wire = new DropboxRouteWire();
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id",
            "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        var store = new Massdex.SyncShark.V2.InMemoryStateStore();
        var v2 = new Massdex.SyncShark.V2.V2CliRuntime(instance, store,
            new Massdex.SyncShark.V2.SyncRun(store, new Massdex.SyncShark.V2.Executor(store, Massdex.SyncShark.V2.V2Settings.Default,
                    Massdex.SyncShark.V2.ExecutorChecks.Admission()),
                new Massdex.SyncShark.V2.FakeEndpointProvider(), Massdex.SyncShark.V2.V2Settings.Default,
                TimeProvider.System),
            new Massdex.SyncShark.V2.EndpointRootProbe(new Massdex.SyncShark.V2.FakeEndpointProvider()), root)
        {
            DropboxHandlerFactory = () => new NonDisposingHandler(wire),
            DropboxCredentialsFactory = () => new DropboxCliHarness.CredentialStore()
        };
        try
        {
            const string identity = "{\"account_id\":\"account-id\",\"root_info\":{\".tag\":\"user\"," +
                "\"root_namespace_id\":\"root-namespace\",\"home_namespace_id\":\"home-namespace\"}}";
            wire.Respond("/oauth2/token", HttpStatusCode.OK,
                "{\"access_token\":\"" + DropboxRouteWire.SecretToken + "\",\"expires_in\":14400," +
                "\"account_id\":\"account-id\"}");
            wire.Respond("users/get_current_account", HttpStatusCode.OK, identity);
            wire.Respond("users/get_current_account", HttpStatusCode.OK, identity);
            var who = await SyncCliRegistry.DispatchAsync(v2, SyncCliRegistry.Parse(
                    ["--cli", "dropbox", "whoami", "--account", binding.ConnectionId, "--json"]),
                TextWriter.Null, CancellationToken.None);
            Require(who is DropboxCliRawData raw &&
                raw.Result.GetProperty("account_id").GetString() == "account-id", "v2 whoami returns the account");
            Require(wire.Requests.Count == 3 && wire.Requests[0].Route == "/oauth2/token",
                "v2 hook refreshed the token through the credential seam");
            var call = wire.Requests[2];
            Require(call.Route == "/2/users/get_current_account" &&
                call.Authorization == "Bearer " + DropboxRouteWire.SecretToken &&
                call.PathRoot!.Contains("root-namespace", StringComparison.Ordinal),
                "v2 whoami is authenticated and rooted at the retained account");

            var unknown = await SyncCliRegistry.DispatchAsync(v2, SyncCliRegistry.Parse(
                    ["--cli", "dropbox", "ls", "--account", "connection-fixture-1", "--path", "/",
                        "--json"]), TextWriter.Null, CancellationToken.None)
                .ContinueWith(task => task.Exception?.GetBaseException());
            Require(unknown is SyncCliTargetException && wire.Requests.Count == 3,
                "v2 hook reports an unknown account as a missing target without a request");
        }
        finally
        {
            await v2.DisposeAsync();
        }
    }

    /// <summary>Lets the runtime own and dispose its HttpClient while the check keeps reading the wire.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) { }
    }

    private static async Task<(int Exit, string Out, string Err, JsonElement? Data, DropboxRouteWire.Request[] Sent)>
        Run(DropboxCliHarness harness, params string[] args)
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

    private static string FileJson(string name, string contentHash = "hash-a") =>
        "{\".tag\":\"file\",\"name\":\"" + name + "\",\"path_display\":\"/" + name + "\",\"path_lower\":\"/" + name +
        "\",\"id\":\"id:a\",\"rev\":\"r1\",\"size\":5,\"content_hash\":\"" + contentHash + "\"," +
        "\"client_modified\":\"2026-10-01T00:00:00Z\",\"server_modified\":\"2026-10-02T00:00:00Z\"}";

    private static string Folder(string name) =>
        "{\".tag\":\"folder\",\"name\":\"" + name + "\",\"path_display\":\"/" + name + "\",\"path_lower\":\"/" +
        name + "\",\"id\":\"id:f\"}";

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
