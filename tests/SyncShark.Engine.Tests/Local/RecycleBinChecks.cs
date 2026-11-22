using Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

public static class RecycleBinChecks
{
    public static async Task RunAsync()
    {
        Check.Require(RecycleBin.BinName("first.txt",false,1)=="first.txt" && RecycleBin.BinName("first.txt",false,2)=="first (2).txt" &&
            RecycleBin.BinName("group.a",true,3)=="group.a (3)","bin collisions preserve both items");
        var untouched = new Entry("file",false,0,DateTimeOffset.UnixEpoch,"1","","",null);
        try { await new RecycleBin().RemoveAsync("invalid\0root",DriveType.Removable,untouched,null,default);
            throw new InvalidDataException("removal accepted no exact victim"); }
        catch (ChangedUnderneathException) { }
        // Non-removable drives must not touch even a similarly named directory.
        await new RecycleBin().TrimAsync("invalid\0root",DriveType.Fixed,DateTimeOffset.UnixEpoch,default);
    }
}
