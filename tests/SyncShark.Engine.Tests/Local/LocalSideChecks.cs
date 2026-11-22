using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks the evidence carried across the local publication and listing boundaries.</summary>
public static class LocalSideChecks
{
    public static Task RunAsync()
    {
        var final = new Entry("file", false, 1, DateTimeOffset.UnixEpoch, "1", "", "", null);
        var close = new JournalPosition(3, 7);
        var wrong = new List<string>();
        if (LocalSide.Publication(final, close).LocalPublished != close ||
            LocalSide.Publication(final, null).LocalPublished is not null)
            wrong.Add("publication did not preserve exactly its held-file proof");
        var root = new DirectoryIdentity(1, 2, null);
        LocalSide.RequireRoot(root, root);
        try { LocalSide.RequireRoot(root, root with { FileId = 3 }); wrong.Add("replacement root allowed to supply coverage"); }
        catch (InventoryIncompleteException) { }
        Check.Require(wrong.Count == 0, string.Join("; ", wrong));
        return Task.CompletedTask;
    }

    // Called by the classifier's check once W08's body is present; never credit a stubbed dependency.
    public static Task LateReadFailureAsync()
    {
        using var reader = new LocalReadStream(new FailingRead(), "broken-item");
        try { reader.ReadByte(); throw new InvalidDataException("read did not report failure"); }
        catch (ItemFailedException e) { Check.Require(e.Path == "broken-item" && e.Cause == TextKey.ReadLocalFailed, "late read lost its item"); }
        return Task.CompletedTask;
    }
    private sealed class FailingRead : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException();
    }
}
