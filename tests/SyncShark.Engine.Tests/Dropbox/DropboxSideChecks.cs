using System.Net;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Exercises the configured side, not just the upload helper.</summary>
public static class DropboxSideChecks
{
    /// <summary>A one-byte replacement must already be in a closed session before the folder can be removed.</summary>
    public static async Task RunAsync()
    {
        var wire = new SessionWire();
        using var http = new HttpClient(wire);
        var api = new DropboxApi(http, TimeProvider.System, ItemChecks.Settings, _ => { });
        using var store = new InMemoryStateStore();
        var pair = ItemChecks.Pair();
        ISide side = new DropboxSide(pair, () => new PathScope(pair, [pair], DriveType.Fixed), api, new Dispatcher(api, store));
        var file = ItemChecks.File(Side.Local, "item", 42, "source");
        var folder = ItemChecks.File(Side.Dropbox, file.Path, 0, "folder", true);
        using var input = new MemoryStream([42]);
        await using var result = await side.StageAsync(file.Path, input, file, folder, default);
        Check.Require(result is DropboxStaged { SessionId: "session", Content: null } stage &&
            !stage.ConsumesSourceOnPublish && stage.ContentHash == file.ContentHash && wire.ClosedWithSource,
            "The selected folder could be removed before its replacement bytes were in a closed session");
    }

    private sealed class SessionWire : HttpMessageHandler
    {
        public bool ClosedWithSource { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check.Require(request.RequestUri!.AbsolutePath.EndsWith("/files/upload_session/start", StringComparison.Ordinal),
                "Staging published content or touched the selected folder");
            using var args = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
            ClosedWithSource = args.RootElement.GetProperty("close").GetBoolean() &&
                (await request.Content!.ReadAsByteArrayAsync(ct)).SequenceEqual(new byte[] { 42 });
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"session_id\":\"session\"}") };
        }
    }
}
