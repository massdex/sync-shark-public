using System.Reflection;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Metadata keeps the provider's relative spelling, mount flag and deletion evidence.</summary>
public static class DropboxEntriesChecks
{
    internal static readonly PairConfig Pair = new("pair", "Pair A", "", "/Parent", "connection", [], [], FirstRunPolicy.NewestWins, true, null);

    public static Task RunAsync()
    {
        // Parser nesting levels are derived from the reply, never hand-numbered constants.
        var depths = typeof(DropboxEntries).Assembly.GetTypes().Where(t => t.Namespace == typeof(DropboxEntries).Namespace)
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => f.IsLiteral && f.Name.EndsWith("Depth", StringComparison.Ordinal)).Select(f => f.DeclaringType!.Name + "." + f.Name).ToArray();
        Check.Require(depths.Length == 0, "Hand-numbered Dropbox parser depth: " + string.Join(", ", depths));
        using var document = JsonDocument.Parse("""[{".tag":"folder","id":"one","sharing_info":{"shared_folder_id":"mount"},"path_display":"/parent/Child"},{".tag":"deleted","path_display":"/Parent/Child"},{".tag":"deleted","path_display":"/ParentOther/Child"}]""");
        var decode = new DropboxEntries();
        Check.Require(decode.Decode(document.RootElement[0], Pair).Entry is { Path: "Child", SharedRoot: true, Id: "one" }, "Returned folder metadata");
        Check.Require(decode.Decode(document.RootElement[1], Pair) == new Listed("Child", null), "Path-only deletion");
        try { decode.Decode(document.RootElement[2], Pair); }
        catch (InventoryIncompleteException) { return Task.CompletedTask; }
        throw new InvalidDataException("Sibling root accepted");
    }
}
