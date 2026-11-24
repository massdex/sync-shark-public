using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for Routes.</summary>
public static class RoutesChecks
{
    /// <summary>Verifies the route catalog limits, per-route facts and the ASCII argument header.</summary>
    public static async Task RunAsync(CheckArguments input)
    {
        Check.Require(Routes.MaxEntries("files/delete_batch") == 1000, "delete batch limit");
        Check.Require(Routes.MaxEntries("files/create_folder_batch") == 10000, "create folder batch limit");
        Check.Require(Routes.MaxEntries("files/move_batch_v2") == 1000, "move batch limit");
        Check.Require(Routes.MaxEntries("files/upload_session/finish_batch_v2") == 1000, "finish batch limit");
        Check.Require(Routes.ContentRequestBytes == 150L * 1024 * 1024, "content request bytes");
        Check.Require(Routes.RelocationFiles == 10000, "relocation files");
        // Update-DropboxRoutes.ps1 writes this catalog; it must carry the loopback head with its two placeholders.
        Check.Require(Routes.LoopbackHttpVersion.Length > 0 && Routes.LoopbackResponseHead.StartsWith("{LoopbackHttpVersion} ", StringComparison.Ordinal) &&
            Routes.LoopbackResponseHead.Contains("{Length}", StringComparison.Ordinal), "route catalog lost its loopback head");
        await RegeneratedLoopbackAsync(input.Root);
        Check.Require(Routes.Endpoint("users/get_space_usage").AbsoluteUri ==
            "https://api.dropboxapi.com/2/users/get_space_usage", "space usage endpoint");
        Check.Require(Routes.Endpoint("files/download").AbsoluteUri ==
            "https://content.dropboxapi.com/2/files/download", "content host endpoint");
        foreach (var (path, url) in new[]
        {
            ("oauth2/authorize", "https://www.dropbox.com/oauth2/authorize"),
            ("oauth2/token", "https://api.dropboxapi.com/oauth2/token")
        })
        {
            var endpoint = Routes.Endpoint(path);
            Check.Require(endpoint.AbsoluteUri == url && Routes.NameOf(endpoint) is null,
                "OAuth endpoint is incorrect or was classified as a catalog route");
        }
        Check.Require(Routes.StyleOf("files/upload") == Routes.Style.Upload, "upload style");
        Check.Require(Routes.StyleOf("files/download") == Routes.Style.Download, "download style");
        Check.Require(Routes.StyleOf("files/list_folder") == Routes.Style.Rpc, "rpc style");
        Check.Require(Routes.PolicyOf("files/list_folder") == Routes.Policy.Read, "read policy");
        Check.Require(Routes.PolicyOf("files/delete_v2") == Routes.Policy.Write, "write policy");
        Check.Require(Routes.SpecialOf("files/list_folder/longpoll") == Routes.Special.Longpoll, "longpoll special");
        Check.Require(Routes.SpecialOf("files/list_folder/continue") == Routes.Special.Continue, "continue special");
        Check.Require(Routes.SpecialOf("files/list_folder") is null, "plain route special");
        Check.Require(!Routes.IsNoAuth("users/get_current_account"), "account route uses a token");
        Check.Require(Routes.VoidArg("users/get_space_usage"), "space usage takes the void argument");

        var json = "{\"path\":\"/café\"}";
        var header = Routes.ArgHeader(json);
        Check.Require(!header.Contains('é') && header.Contains("\\u00E9", StringComparison.OrdinalIgnoreCase),
            "argument header ASCII escape");
        Check.Require(Routes.ArgHeader("{\"close\":true}") == "{\"close\":true}", "already-ASCII argument preserved");

        // The style guard rejects a call before any request is built; a matching style passes.
        try
        {
            Routes.RequireStyle("files/download", Routes.Style.Rpc);
            throw new InvalidDataException("wrong style accepted");
        }
        catch (ArgumentException)
        {
        }
        Routes.RequireStyle("files/download", Routes.Style.Download);

        // Endpoint reverse lookup drives the prepared (pre-built request) send path.
        Check.Require(Routes.NameOf(Routes.Endpoint("files/download")) == "files/download", "endpoint reverse lookup");
        Check.Require(Routes.NameOf(new Uri("https://example.invalid/none")) is null, "unknown endpoint named");

        // ASCII round trip: the header is pure ASCII, escapes a non-BMP code point, and parses back to the same value.
        var tricky = "{\"path\":\"/a\\\"b/😀\"}";
        var trickyHeader = Routes.ArgHeader(tricky);
        Check.Require(trickyHeader.All(character => character <= '~'), "non-ASCII character left in header");
        Check.Require(trickyHeader.Contains("\\uD83D", StringComparison.OrdinalIgnoreCase), "non-BMP code point not escaped");
        using var originalArg = JsonDocument.Parse(tricky);
        using var roundTrippedArg = JsonDocument.Parse(trickyHeader);
        Check.Require(roundTrippedArg.RootElement.GetProperty("path").GetString() ==
            originalArg.RootElement.GetProperty("path").GetString(), "argument header round trip changed the value");

        try
        {
            Routes.Endpoint("files/does_not_exist");
            throw new InvalidDataException("unknown route accepted");
        }
        catch (ArgumentException)
        {
        }

        try
        {
            Routes.MaxEntries("files/get_metadata");
            throw new InvalidDataException("route without a limit accepted");
        }
        catch (ArgumentException)
        {
        }
    }

    // Runs the generator offline on the synthetic spec in RouteSpec/ (no provider fetch) into a temporary file and
    // requires that its output carries the input catalog's loopback object unchanged. root is the checked-out source root.
    private static async Task RegeneratedLoopbackAsync(string root)
    {
        var output = Path.Combine(Path.GetTempPath(), "syncshark-routes-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "tools", "Update-DropboxRoutes.ps1"),
                         "-SpecCommit", "fixture", "-SpecDir", Path.Combine(root, "tests", "SyncShark.Engine.Tests", "Dropbox", "RouteSpec"), "-OutputPath", output })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdout;
            Check.Require(process.ExitCode == 0, "route generator failed offline: " + await stderr);
            var input = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "src", "SyncShark.Engine", "Dropbox", "dropbox-routes.json")))!["loopback"];
            var generated = JsonNode.Parse(File.ReadAllText(output))!["loopback"];
            Check.Require(input is not null && JsonNode.DeepEquals(input, generated), "route generator dropped or changed the loopback object");
        }
        finally
        {
            File.Delete(output);
        }
    }
}
