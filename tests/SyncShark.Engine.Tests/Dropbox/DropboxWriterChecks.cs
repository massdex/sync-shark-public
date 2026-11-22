namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxWriter.</summary>
public static class DropboxWriterChecks
{
    /// <summary>Verifies the DropboxWriter contract through the module surface.</summary>
    public static Task RunAsync() => throw new NotImplementedException();
    /// <summary>Direct staging retains the source; a closed session owns a complete digest and no source stream.</summary>
    public static Task StagedAsync()
    {
        using var bytes=new MemoryStream(new byte[]{1});var file=Scenarios.ItemChecks.File(Side.Local,"item",1,"id");
        var direct=global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Single(file.Path,bytes,file);var session=global::Massdex.SyncShark.Engine.Dropbox.DropboxStaged.Session(file.Path,"session",1,file,file.ContentHash);
        Check.Require(direct.ConsumesSourceOnPublish && direct.Content==bytes && !session.ConsumesSourceOnPublish && session.Content==null && session.ContentHash==file.ContentHash,"Staging confused a completed session with pending input");return Task.CompletedTask;
    }
}
