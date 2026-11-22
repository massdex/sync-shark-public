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
        var windows = new List<(string Route, long Offset, int Count, bool Closed)>();
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            using var args = System.Text.Json.JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            var chunk = await request.Content!.ReadAsByteArrayAsync(ct);
            received.AddRange(chunk);
            closed = args.RootElement.GetProperty("close").GetBoolean();
            var append = args.RootElement.TryGetProperty("cursor", out var cursor);
            windows.Add((request.RequestUri!.Segments[^1], append ? cursor.GetProperty("offset").GetInt64() : 0, chunk.Length, closed));
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(append ? "{}" : "{\"session_id\":\"receipt\"}") };
        }
        using var bytes = new MemoryStream([42]);
        static Uri Endpoint(string name) => new(new Uri("https://example.invalid/"), name);
        await using var staged = await writer.StageAsync(Send, Endpoint, 2, file.Path, bytes, file, folder, default);
        Check.Require(closed && received.SequenceEqual(new byte[]{42}) && staged.SessionId == "receipt" &&
            !staged.ConsumesSourceOnPublish && staged.ContentHash == file.ContentHash,
            "Folder replacement kept a live source instead of closing a session containing its bytes");
        received.Clear(); windows.Clear();
        using var recovery = new Sequential([1,2,3]);
        using var hash = new ContentHash(); hash.Append([1,2,3]);
        var recovered = file with { Size = 3, ContentHash = hash.Finish() };
        await using var restored = await writer.StageAsync(Send, Endpoint, 2, file.Path, recovery, recovered, null, default);
        Check.Require(windows.SequenceEqual(new[]{("start",0L,2,false),("append_v2",2L,1,true)}) &&
            restored.ContentHash == recovered.ContentHash && received.SequenceEqual(new byte[]{1,2,3}),
            "Nonseekable recovery did not stage all bytes in bounded windows");
        received.Clear(); windows.Clear();
        using var directBytes = new MemoryStream([42]);
        await using var direct = await writer.StageAsync(Send, Endpoint, 2, file.Path, directBytes, file, null, default);
        Check.Require(ReferenceEquals(direct.Content, directBytes) && direct.ConsumesSourceOnPublish && windows.Count == 0,
            "A sole small ordinary upload was sent before publication");
        using var shortRead = new Sequential([1]);
        try
        {
            await using var incomplete = await writer.StageAsync(Send, Endpoint, 2, file.Path, shortRead, recovered, folder, default);
        }
        catch (Exception error) when (error is EndOfStreamException || error is HttpRequestException { InnerException: EndOfStreamException })
        {
            return;
        }
        throw new InvalidDataException("Truncated folder replacement was returned as a closed stage");
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
