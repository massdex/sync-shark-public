using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static class PathRuleChecks
{
    internal static void Run()
    {
        static void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Path rule check failed: " + name);
        }
        static Exception Failure(Action action)
        {
            try { action(); }
            catch (Exception error) { return error; }
            throw new InvalidOperationException("Expected path rejection.");
        }

        foreach (var (input, expected) in new[] { ("Parent/Child", "Parent/Child"),
            ("/Parent//Child/", "Parent/Child"), ("Parent\\Child", "Parent/Child"), ("COM0.txt", "COM0.txt"),
            ("COM0", "COM0"), ("LPT0.txt", "LPT0.txt"), ("file", "file"), ("a/b.txt", "a/b.txt"),
            ("a//b", "a/b"), ("/a/", "a"), ("//a", "a"), ("a///b", "a/b"),
            ("Parent//Child/Item.txt", "Parent/Child/Item.txt"),
            ("Parent\\Child\\Item.txt", "Parent/Child/Item.txt"),
            ("é/𝄞.txt", "é/𝄞.txt"), ("..hidden", "..hidden") })
        {
            Check(PathRules.NormalizeRelative(input) == expected, "valid relative path");
            Check(PilotPath.NormalizeRelative(input) == expected, "legacy path forwarding");
        }
        const string canonical = "Parent/Child.txt";
        Check(ReferenceEquals(PathRules.NormalizeRelative(canonical), canonical), "canonical original string");
        foreach (var input in new[] { "", " ", "/", "..", "../Child", "./Child", "Parent/../Child", "a/./b",
            "a//../b", "C:/Child", "C:\\Child", "C:foo", "C:/CON.txt", "a\u0001b", "Parent/bad?name",
            "Parent/name:part", "CON", "con", "CON.txt", "PRN", "AUX", "NUL", "COM1", "COM9", "com1",
            "LPT1", "LPT9", "Lpt9", "lpt9.txt", "a/CON", "a/LPT9.txt", "Child.", "Child ", "...", "a. ",
            "a/x.", "a/x. ", "folder./file.txt", "trailing-space /x" })
        {
            var lower = Failure(() => PathRules.NormalizeRelative(input));
            var legacy = Failure(() => PilotPath.NormalizeRelative(input));
            Check(lower is ArgumentException && lower.GetType() == legacy.GetType() && lower.Message == legacy.Message,
                "exception identity and text");
        }
        var nullLower = Failure(() => PathRules.NormalizeRelative(null!));
        var nullLegacy = Failure(() => PilotPath.NormalizeRelative(null!));
        Check(nullLower is ArgumentNullException && nullLower.GetType() == nullLegacy.GetType() &&
            nullLower.Message == nullLegacy.Message, "null path identity");
        Check(Failure(() => PathRules.NormalizeRelative("Parent/../Child")).Message ==
            new ArgumentException("The relative path contains an unsafe segment.", "value").Message, "unsafe message");
        Check(Failure(() => PathRules.NormalizeRelative("C:/CON.txt")).Message ==
            new ArgumentException("A relative path is required.", "value").Message, "rooted message before segments");
        Check(PathRules.CanonicalComparisonKey("e\u0301.txt") == PathRules.CanonicalComparisonKey("É.TXT"), "Unicode key");
        Check(PathRules.NormalizeDropboxRelative("Parent/name:part") == "Parent/name:part", "provider colon");
        foreach (var input in new[] { "/Child", "Parent//Child", "Parent/../Child", "Parent\\Child", "Child\n" })
            Check(Failure(() => PathRules.NormalizeInventoryPath(input)) is ArgumentException, "metadata rejection");
        Check(PathRules.DropboxNamespaceRootIdentity(" 42 ") == "ns:42" &&
            PathRules.IsDropboxFolderIdentity("ns:42") && !PathRules.IsDropboxFolderIdentity("ns:"), "namespace identity");
        Check(!PathRules.IsPathInside("C:/Parent", "C:/Parent") &&
            !PathRules.IsPathInside("C:/Parent", "C:/ParentOther") &&
            PathRules.IsPathInside("C:/Parent", "C:/Parent/Child"), "contained boundary");
        foreach (var (attributes, tag, expected) in new[] {
            (FileAttributes.Normal, 0u, ForeignObjectKind.None),
            (FileAttributes.ReparsePoint | FileAttributes.Offline, 0xA000000Cu, ForeignObjectKind.Link),
            (FileAttributes.ReparsePoint, 0x9000001Au, ForeignObjectKind.LocalCloud),
            (FileAttributes.Offline, 0u, ForeignObjectKind.OnlineOnlyFile),
            (FileAttributes.Offline | FileAttributes.Directory, 0u, ForeignObjectKind.None) })
            Check(PathRules.ClassifyForeign(attributes, tag) == expected &&
                PilotPath.ClassifyForeign(attributes, tag) == expected, "foreign classification");

        foreach (var (path, expected) in new[] { ("", false), ("Parent/Child", false),
            (".syncshark-case-", true), ("Parent/.SYNCSHARK-CONTROL-X/Child", true),
            (".syncshark-recovery-", false), (".syncshark-recovery-x", true),
            ("Parent/.SYNCSHARK-TRANSFER-X.PARTIAL.SYNCSHARK-ACL", true),
            (".syncshark-transfer-x.backup", true), (".syncshark-transfer-x.txt", false) })
            Check(ArtifactPaths.IsArtifactPath(path) == expected && LocalItems.IsArtifactPath(path) == expected,
                "artifact segment table");
        var full = Path.GetFullPath("C:/Parent/Child.txt");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(full.ToUpperInvariant()))).ToLowerInvariant();
        var stage = Path.Combine(Path.GetDirectoryName(full)!, ".syncshark-transfer-" + hash + ".partial");
        Check(ArtifactPaths.DownloadStagePath(full) == stage && AttendedElevatedAction.DownloadStagePath(full) == stage,
            "stage spelling");

        var pair = new PairDef("p", "Pair A", new("a", ProviderIds.Local, null, "C:/Parent", "a"),
            new("b", ProviderIds.Dropbox, "account", "/Parent", "id:b"), ["Excluded/Child"], [], true);
        Check(PairInclude.For(pair, Side.A, [pair], [])("Excluded") &&
            !PairInclude.ForAction(pair, Side.A, [pair], [])("Excluded"), "inventory versus action ancestor");
        Check(!PairInclude.For(pair, Side.A, [pair], [])("Excluded/Child/File"), "excluded descendant");
        Check(PairInclude.PermanentBoundary(pair, Side.A, "Parent/.syncshark-case-x/Child", false, [pair], []) ==
            "Parent/.syncshark-case-x", "shortest artifact boundary");
        Check(PairInclude.PermanentBoundary(pair, Side.A, "Owned", true, [pair], ["Owned/Child"]) == "Owned/Child",
            "owned ancestor");
        var nested = pair with { PairId = "nested", A = pair.A with { RootId = "nested-a", RootPath = "C:/Parent/Nested" } };
        Check(!PairInclude.For(pair, Side.A, [pair, nested], [])("Nested/Child"), "nested root");
        var rules = SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileExtension, "*.TMP"),
            new(SyncIgnoreKind.FolderName, "Cache"), new(SyncIgnoreKind.FileName, "Thumbs.db")]);
        Check(SyncIgnorePolicy.IsIgnored("CACHE/Child", false, rules) &&
            SyncIgnorePolicy.IsIgnored("Parent/Thumbs.db", false, rules) &&
            SyncIgnorePolicy.IsIgnored("Parent/file.TMP", false, rules) &&
            !SyncIgnorePolicy.IsIgnored("Parent/file.TMP", true, rules), "ignore kind table");
        var mapped = DropboxNameMapping.Fact("Parent/name:part.");
        using (var doc = JsonDocument.Parse(mapped!))
            Check(doc.RootElement.GetProperty("dropboxWindowsPath").GetString() == "Parent/name_part_", "mapping fact");
        var merged = DropboxExportFacts.Merge(mapped, "pdf", ["pdf", "PDF", "txt"], true);
        var item = new ItemState("Parent/name:part.", ItemKind.File, "id:item", null, null, 0,
            DateTimeOffset.UnixEpoch, ItemFlags.None, merged);
        Check(DropboxNameMapping.LocalPath(item) == "Parent/name_part_", "persisted mapping read");
        using (var doc = JsonDocument.Parse(merged!))
            Check(doc.RootElement.GetProperty("dropboxExportAs").GetString() == "pdf" &&
                doc.RootElement.GetProperty("dropboxExportOptions").GetArrayLength() == 2, "export facts");
        Check(DropboxExportFacts.ForItem(item) is not null, "persisted export read");
    }
}
