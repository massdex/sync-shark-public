namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for UploadContent.</summary>
public static class UploadContentChecks
{
    /// <summary>Verifies the UploadContent contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var writer = new global::Massdex.SyncShark.Engine.Dropbox.UploadContent();
        var file = Scenarios.ItemChecks.File(Side.Local, "item", 42, "source");
        var folder = Scenarios.ItemChecks.File(Side.Dropbox, "item", 0, "victim", true);
        var received = new List<byte>();
        bool closed = false;
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            using var args = System.Text.Json.JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            received.AddRange(await request.Content!.ReadAsByteArrayAsync(ct));
            closed = args.RootElement.GetProperty("close").GetBoolean();
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"session_id\":\"receipt\"}") };
        }
        using var bytes = new MemoryStream([42]);
        static Uri Endpoint(string name) => new(new Uri("https://example.invalid/"), name);
        await using var staged = await writer.StageAsync(Send, Endpoint, 2, file.Path, bytes, file, folder, default);
        Check.Require(closed && received.SequenceEqual(new byte[]{42}) && staged.SessionId == "receipt" &&
            !staged.ConsumesSourceOnPublish && staged.ContentHash == file.ContentHash,
            "Folder replacement kept a live source instead of closing a session containing its bytes");
        received.Clear();
        using var recovery = new Sequential([1,2,3]);
        using var hash = new ContentHash(); hash.Append([1,2,3]);
        var recovered = file with { Size = 3, ContentHash = hash.Finish() };
        await using var restored = await writer.StageAsync(Send, Endpoint, 2, file.Path, recovery, recovered, null, default);
        Check.Require(closed && restored.ContentHash == recovered.ContentHash && received.SequenceEqual(new byte[]{1,2,3}),
            "Nonseekable recovery did not stage all bytes in bounded windows");
    }
    private sealed class Sequential(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }
    /// <summary>Read and seek only within the selected upload window.</summary>
    public static Task WindowAsync()
    {
        using var bytes=new MemoryStream(new byte[]{0,1,2,3});using var window=new global::Massdex.SyncShark.Engine.Dropbox.UploadSlice(bytes,1,2);
        Check.Require(window.ReadByte()==1 && window.ReadByte()==2 && window.ReadByte()==-1 && window.Seek(0,SeekOrigin.Begin)==0 && window.ReadByte()==1,"Upload window escaped its source range");
        return Task.CompletedTask;
    }
}
