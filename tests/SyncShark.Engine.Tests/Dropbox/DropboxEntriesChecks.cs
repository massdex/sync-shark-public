using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Metadata keeps the provider's relative spelling, mount flag and deletion evidence.</summary>
public static class DropboxEntriesChecks
{
    internal static readonly PairConfig Pair = new("pair", "Pair A", "", "/Parent", "connection", [], [], FirstRunPolicy.NewestWins, true, null);

    public static Task RunAsync()
    {
        using var document = JsonDocument.Parse("""[{".tag":"folder","id":"one","sharing_info":{"shared_folder_id":"mount"},"path_display":"/parent/Child"},{".tag":"deleted","path_display":"/Parent/Child"},{".tag":"deleted","path_display":"/ParentOther/Child"}]""");
        var decode = new DropboxEntries();
        Check.Require(decode.Decode(document.RootElement[0], Pair).Entry is { Path: "Child", SharedRoot: true, Id: "one" }, "Returned folder metadata");
        Check.Require(decode.Decode(document.RootElement[1], Pair) == new Listed("Child", null), "Path-only deletion");
        try { decode.Decode(document.RootElement[2], Pair); }
        catch (JsonException) { return Task.CompletedTask; }
        throw new InvalidDataException("Sibling root accepted");
    }
}
