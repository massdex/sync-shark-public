using System.Text.Json.Nodes;
using Massdex.SyncShark;

internal static class SetupUpdateChecks
{
    internal static int Run()
    {
        const string good = """
            {"draft":false,"prerelease":false,"tag_name":"v1.2.3","assets":[{"name":"SyncShark-Setup-v1.2.3.exe","size":42,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","browser_download_url":"https://github.com/massdex/sync-shark-releases/releases/download/v1.2.3/SyncShark-Setup-v1.2.3.exe"}]}
            """;
        if (SetupUpdates.Parse(good).Version != "1.2.3") throw new Exception("Stable setup was not parsed.");
        foreach (var mutation in new Action<JsonObject>[]
        {
            root => root["draft"] = true,
            root => root["prerelease"] = true,
            root => root["tag_name"] = "v1.2.3-rc1",
            root => root["assets"]![0]!.AsObject().Remove("digest"),
            root => root["assets"]!.AsArray().Add(root["assets"]![0]!.DeepClone()),
            root => root["assets"]![0]!["browser_download_url"] = "https://example.invalid/setup.exe",
            root => root["assets"]![0]!["name"] = "SyncShark-Setup-v1.2.3-extra.exe"
        })
        {
            var root = JsonNode.Parse(good)!.AsObject();
            mutation(root);
            try { SetupUpdates.Parse(root.ToJsonString()); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Unsafe release accepted.");
        }
        foreach (var (key, argument, expected) in new[]
        {
            ("cli.versionMatch", "", "setup apply requires --update matching the latest stable version."),
            ("cli.newer", "", "The selected setup must be newer than this application."),
            ("cli.latest", "1.2.3", "Latest stable setup: 1.2.3"),
            ("cli.started", "setup.exe", "Setup started: setup.exe")
        })
            if (SetupMessages.Text(key, argument) != expected)
                throw new Exception("Setup CLI message changed: " + key);
        var invocation = SyncCliParser.Parse(["--cli", "setup", "apply", "--update", "1.2.3", "--yes"]);
        if (invocation.Update != "1.2.3" || !SyncCli.IsStateFreeInvocation(["--cli", "setup", "check"]))
            throw new Exception("Setup CLI routing changed.");
        var named = false;
        try { SyncCliParser.Parse(["--cli", "setup", "apply", "--yes"]); }
        catch (SyncCliUsageException error)
        {
            if (error.Message != "setup apply requires --update <exact-version>, for example 0.3.35.") throw;
            named = true;
        }
        if (!named) throw new Exception("Setup apply accepted a missing version.");
        var temporary = Directory.GetDirectories(Path.GetTempPath(), "SyncShark-Update-*");
        var downloaded = false;
        try
        {
            SetupUpdates.ApplyAsync(new SetupRelease("9.9.9",
                new Uri("https://127.0.0.1:1/SyncShark-Setup-v9.9.9.exe"), 1, new string('0', 64)),
                CancellationToken.None).GetAwaiter().GetResult();
            downloaded = true;
        }
        catch (Exception) { }
        if (downloaded) throw new Exception("An unreachable setup download was accepted.");
        if (Directory.GetDirectories(Path.GetTempPath(), "SyncShark-Update-*").Except(temporary).Any())
            throw new Exception("A failed setup download retained its temporary folder.");
        var retained = Path.Combine(Path.GetTempPath(), "SyncShark-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(retained);
        try
        {
            using (var held = new FileStream(Path.Combine(retained, "locked.exe"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var diagnostics = new StringWriter();
                SetupDownloadCleanup.Delete(retained, diagnostics);
                if (!diagnostics.ToString().Contains(retained) || !File.ReadAllText(SetupDownloadCleanup.LogPath).Contains(retained))
                    throw new Exception("Immediate cleanup failure was not reported in output and log.");
            }
            SetupDownloadCleanup.Delete(retained, new StringWriter());
            if (Directory.Exists(retained)) throw new Exception("Cleanup did not remove the unlocked download.");
        }
        finally { if (Directory.Exists(retained)) Directory.Delete(retained, true); }
        SetupCleanupLaunchChecks.Run();
        try { SyncCliParser.Parse(["--cli", "setup", "apply", "--update", "1.2.3"]); }
        catch (SyncCliUsageException) { return 0; }
        throw new Exception("Setup apply accepted missing confirmation.");
    }
}
