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
        var invocation = SyncCliParser.Parse(["--cli", "setup", "apply", "--update", "1.2.3", "--yes"]);
        if (invocation.Update != "1.2.3" || !SyncCli.IsStateFreeInvocation(["--cli", "setup", "check"]))
            throw new Exception("Setup CLI routing changed.");
        try { SyncCliParser.Parse(["--cli", "setup", "apply", "--update", "1.2.3"]); }
        catch (SyncCliUsageException) { return 0; }
        throw new Exception("Setup apply accepted missing confirmation.");
    }
}
