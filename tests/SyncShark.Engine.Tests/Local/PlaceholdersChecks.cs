using L = Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks the Placeholders adapter against observed outcomes.</summary>
public static class PlaceholdersChecks
{
    public static Task RunAsync()
    {
        const FileAttributes reparse=FileAttributes.ReparsePoint, recall=(FileAttributes)0x00400000;
        Check.Require(L.Placeholders.IsOnlineOnly(reparse|recall,0x9000301a) && L.Placeholders.IsOnlineOnly(recall,0),"Recall-only files were treated as resident");
        Check.Require(!L.Placeholders.IsOnlineOnly(reparse,0x9000001a) && !L.Placeholders.IsOnlineOnly(reparse|recall,0xa000000c) && !L.Placeholders.IsOnlineOnly(FileAttributes.Directory|recall,0),"Resident, linked or directory entries were treated as online-only files");
        return Task.CompletedTask;
    }
}
