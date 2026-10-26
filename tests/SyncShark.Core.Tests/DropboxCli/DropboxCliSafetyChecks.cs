namespace Massdex.SyncShark.DropboxCli;

/// <summary>Group 7: --yes, scope and denial refusals happen before any request.</summary>
internal static class DropboxCliSafetyChecks
{
    internal static async Task RunAsync(string root)
    {
        using var h = await DropboxCliHarness.CreateAsync(root);
        var account = h.Binding.ConnectionId;

        foreach (var args in new[]
        {
            new[] { "dropbox", "put", "--account", account, "--in", Path.Combine(root, "a.txt"), "--path", "/a.txt" },
            new[] { "dropbox", "mkdir", "--account", account, "--path", "/x" },
            new[] { "dropbox", "rm", "--account", account, "--path", "/x" },
            new[] { "dropbox", "mv", "--account", account, "--path", "/a.txt", "--to", "/b.txt" },
            new[] { "dropbox", "cp", "--account", account, "--path", "/a.txt", "--to", "/b.txt" },
            new[] { "dropbox", "restore", "--account", account, "--path", "/a.txt", "--rev", "r0" }
        })
        {
            var refused = await h.RunAsync(args);
            Require(refused.Exit == 2, string.Join(' ', args) + " needs --yes");
            Require(h.Wire.Requests.Count == 0, string.Join(' ', args) + " sent no request");
        }

        foreach (var args in new[]
        {
            new[] { "dropbox", "put", "--account", account, "--in", Path.Combine(root, "a.txt"), "--path", "/a.txt",
                "--rev", "r1", "--yes" },
            new[] { "dropbox", "put", "--account", account, "--in", Path.Combine(root, "a.txt"), "--path", "/a.txt",
                "--mode", "update", "--yes" }
        })
        {
            var refused = await h.RunAsync(args);
            Require(refused.Exit == 2, string.Join(' ', args) + " is a usage error");
            Require(h.Wire.Requests.Count == 0, string.Join(' ', args) + " sent no request");
        }

        var apiWrite = await h.RunAsync("dropbox", "api", "--account", account, "--route", "files/delete_v2", "--json");
        Require(apiWrite.Exit == 2 && h.Wire.Requests.Count == 0, "api write without --yes is refused at parse time");

        var denied = await h.RunAsync("dropbox", "api", "--account", account, "--route", "auth/token/revoke", "--yes", "--json");
        Require(denied.Exit == 5 && h.Wire.Requests.Count == 0, "denied route is refused");
        Require(Contains(denied.Out, "denied"), "denied route error code");

        var scope = await h.RunAsync("dropbox", "api", "--account", account, "--route", "files/permanently_delete", "--yes", "--json");
        Require(scope.Exit == 5 && h.Wire.Requests.Count == 0, "permanent delete needs an ungranted scope");
        Require(Contains(scope.Out, "missing_scope"), "missing_scope error code");

        var team = await h.RunAsync("dropbox", "api", "--account", account, "--route", "team/get_info", "--yes", "--json");
        Require(team.Exit == 2 && h.Wire.Requests.Count == 0, "a team route is not in the user-token table");

        var existing = Path.Combine(h.Root, "present.bin");
        await File.WriteAllTextAsync(existing, "keep-me");
        var replace = await h.RunAsync("dropbox", "get", "--account", account, "--path", "/a.txt", "--out", existing, "--json");
        Require(replace.Exit == 2 && h.Wire.Requests.Count == 0, "get onto an existing --out needs --yes");
        Require(await File.ReadAllTextAsync(existing) == "keep-me", "get left the existing file unchanged");

        var accountRefusal = await h.RunAsync("dropbox", "ls", "--account", "connection-fixture-1",
            "--path", "/", "--json");
        Require(accountRefusal.Exit == 4, "an unknown account is a missing target");

        foreach (var text in new[] { denied.Out, denied.Err, scope.Out, scope.Err, apiWrite.Out, apiWrite.Err })
            Require(!text.Contains(DropboxRouteWire.SecretToken, StringComparison.Ordinal), "the token never reaches the console");

        Console.WriteLine("--dropbox-cli-checks safety passed.");
    }

    private static bool Contains(string json, string code)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString() == code;
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
