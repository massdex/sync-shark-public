using Massdex.SyncShark.Engine.Edge;

namespace Massdex.SyncShark.Engine.Tests.Edge;

public static class PipeFrameChecks
{
    public static async Task RunAsync()
    {
        using var data = new MemoryStream();
        byte[] message = [2, 65, 66];
        await PipeFrame.WriteAsync(data, message, default);
        Check.Require(data.ToArray().SequenceEqual(new byte[] {2,2,0,0,0,65,66}), "Frame wire format");
        data.Position = 0;
        Check.Require((await PipeFrame.ReadAsync(data, default)).SequenceEqual(message), "Frame round trip");
        foreach (byte[] invalid in new byte[][] { [2,1,0,16,0], [3,1,0,0,0,255], [1,1,0,0,0,65], [6,0,0,0,0] }) {
            try { await PipeFrame.ReadAsync(new MemoryStream(invalid), default); } catch (InvalidDataException) { continue; }
            throw new Exception("Invalid frame accepted");
        }
    }
}
