namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Contract checks for UsnJournal.</summary>
public static class UsnJournalChecks
{
    /// <summary>Verifies the UsnJournal contract through the module surface.</summary>
    public static Task RunAsync()
    {
        var dir=Directory.CreateTempSubdirectory();
        try
        {
            var file=Path.Combine(dir.FullName,"first");var other=Path.Combine(dir.FullName,"second");File.WriteAllBytes(file,[1]);
            using var held=File.OpenHandle(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var journal=new global::Massdex.SyncShark.Engine.Local.UsnJournal();var before=journal.Published(held);File.WriteAllBytes(other,[2]);
            var tail=journal.Current(Path.GetPathRoot(file)!);var after=journal.Published(held);
            Check.Require(before!=null && before==after && after!.JournalId==tail.JournalId && after.NextUsn<tail.NextUsn,"Unrelated volume activity was mistaken for the selected file’s publication");
            return Task.CompletedTask;
        }
        finally { dir.Delete(true); }
    }
}
