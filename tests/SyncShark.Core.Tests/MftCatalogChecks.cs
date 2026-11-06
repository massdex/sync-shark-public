using System.Buffers.Binary;
using System.Text;
using Massdex.SyncShark;

internal static class MftCatalogChecks
{
    internal static void Run()
    {
        const ulong root = 0x1234000000000042;
        var before = new NtfsUsnCheckpoint(7, 0, 10, 0); var after = before with { NextUsn = 12 };
        var entries = new[] { Entry(root, 5, "Root", true), Entry(33, root, "Documents", true),
            Entry(44, 33, "Ä.txt"), Entry(55, 5, "Unrelated.txt"), Entry(66, root, "Link", true) with { Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint },
            Entry(77, 66, "NeverFollow.txt") };
        var page = Page(100, entries);
        var decoded = NtfsMftCatalog.ParseEnumerationPage(page, 0, out var cursor);
        Check(cursor == 100 && decoded[0].FileId == root, "MFT decoding preserves sequence bits and advances the enumeration cursor");
        var catalogue = new NtfsMftCatalog([root]); catalogue.AddEnumerationPage(page);
        var view = catalogue.Complete(before, after).Single();
        Check(view.Entries.Count == 3 && view.Entries.ContainsKey("Documents/ä.txt"), "Admitted root projection is case-insensitive and keeps Unicode names");
        Check(!view.Entries.ContainsKey("Unrelated.txt"), "Unrelated volume entries never enter the pair view");
        Check(view.RequiresOrdinaryInspection.SequenceEqual(["Link"]) && !view.Entries.ContainsKey("Link/NeverFollow.txt"), "Reparse subtrees are not traversed or silently claimed complete");
        Reject(() => catalogue.Complete(before, after), "Completed catalogue cannot be replayed");
        Reject(() => NtfsMftCatalog.ParseEnumerationPage(page, 100, out _), "Nonadvancing cursor is rejected");
        Reject(() => NtfsMftCatalog.ParseEnumerationPage(page[..^1], 0, out _), "Truncated MFT page is rejected");
        var wrongVersion = page.ToArray(); wrongVersion[12] = 3;
        Reject(() => NtfsMftCatalog.ParseEnumerationPage(wrongVersion, 0, out _), "Unsupported record versions fail closed");
        var badName = Page(100, Entry(44, root, "one.txt")); badName[68] = 0; badName[69] = 0xd8;
        Reject(() => NtfsMftCatalog.ParseEnumerationPage(badName, 0, out _), "Malformed UTF-16 does not become a replacement filename");
        Reject(() => NtfsMftCatalog.ParseEnumerationPage(Page(100, Entry(44, root, "../escape")), 0, out _), "Traversal filenames are rejected");
        var duplicate = new NtfsMftCatalog([root]);
        Reject(() => duplicate.AddEnumerationPage(Page(100, entries[0], entries[0])), "Duplicate identities discard the entire catalogue");
        Reject(() => duplicate.Complete(before, after), "Partial failed catalogue cannot be published");
        var reset = new NtfsMftCatalog([root]); reset.AddEnumerationPage(page);
        Reject(() => reset.Complete(before, after with { JournalId = 8 }), "Journal replacement invalidates MFT enumeration");
        var clash = new NtfsMftCatalog([root]); clash.AddEnumerationPage(Page(100, entries[0], Entry(80, root, "Å"), Entry(81, root, "å")));
        Reject(() => clash.Complete(before, after), "Non-ASCII case aliases cannot produce two competing paths");
        var absentRoot = new NtfsMftCatalog([999]); absentRoot.AddEnumerationPage(page);
        Reject(() => absentRoot.Complete(before, after), "Missing admitted root is not an empty-folder result");
        Reject(() => new NtfsMftCatalog([root, root]), "Duplicate root admissions are rejected");
        var nested = new NtfsMftCatalog([root, 33]); nested.AddEnumerationPage(page);
        Check(nested.Complete(before, after)[1].Entries.ContainsKey("Ä.txt"), "One catalogue supports distinct nested pair views");
        var cancelled = new NtfsMftCatalog([root]); cancelled.AddEnumerationPage(page);
        Reject(() => cancelled.Complete(before, after, new CancellationToken(true)), "Cancellation cannot publish a partial tree");
        Console.WriteLine("MFT parser/catalogue preparation only: no privileged enumeration or product speed claim.");
    }
    private static NtfsMftEntry Entry(ulong id, ulong parent, string name, bool directory = false) =>
        new(id, parent, 5, directory ? FileAttributes.Directory : FileAttributes.Normal, name);
    private static byte[] Page(ulong next, params NtfsMftEntry[] entries)
    {
        using var output = new MemoryStream(); var header = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(header, next); output.Write(header);
        foreach (var entry in entries)
        {
            var name = Encoding.Unicode.GetBytes(entry.Name); var record = new byte[(60 + name.Length + 7) & ~7];
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)record.Length); BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), entry.FileId); BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), entry.ParentFileId);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), entry.Usn); BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), (uint)entry.Attributes);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(56), (ushort)name.Length); BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(58), 60);
            name.CopyTo(record, 60); output.Write(record);
        }
        return output.ToArray();
    }
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException) { Console.WriteLine("PASS " + label); return; }
        throw new Exception("Expected rejection: " + label);
    }
}
