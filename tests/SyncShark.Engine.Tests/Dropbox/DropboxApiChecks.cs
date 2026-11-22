using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxApi.</summary>
public static class DropboxApiChecks
{
    /// <summary>Verifies the single send owner's catalog guards before any request is built.</summary>
    public static async Task RunAsync()
    {
        var settings = SettingsParser.Load();
        var wire = new Wire();
        using var http = new HttpClient(wire);
        var api = new DropboxApi(http, TimeProvider.System, settings, _ => { });

        // A wrong style is rejected before any request; a rejected call never reaches the transport.
        await ExpectArgumentException(() => api.DownloadAsync("files/delete_v2", "{}", CancellationToken.None));
        await ExpectArgumentException(() => api.CallJsonAsync("files/download", "{}", CancellationToken.None));

        // The prepared path consults the catalog: content on a non-upload route, or none on an upload route, is refused.
        using (var withContent = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint("files/delete_v2"))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        })
        {
            await ExpectArgumentException(() => api.SendAsync(withContent, CancellationToken.None));
        }
        using (var missingContent = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint("files/upload")))
        {
            await ExpectArgumentException(() => api.SendAsync(missingContent, CancellationToken.None));
        }

        Check.Require(wire.Paths.Count == 0, "a rejected call reached the transport");
    }

    /// <summary>INTEGRATION-CHECK (needs W10's real RateLimit, DropboxProtocol and DropboxErrors; R29). Passes at F1,
    /// returns NOT IMPLEMENTED in the lane. Wired into the F1 harness when the registry adds it.</summary>
    public static async Task SendPathAsync()
    {
        var settings = SettingsParser.Load();

        // Clock: exact signed offset and full-RTT-plus-one-second uncertainty from a successful dated reply.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var rtt = TimeSpan.FromSeconds(5);
        var serverDate = DateTimeOffset.UnixEpoch.AddSeconds(100);
        var clockWire = new Wire
        {
            Clock = clock,
            Advance = rtt,
            Date = serverDate,
            Body = "{\"used\":1,\"allocation\":{\".tag\":\"individual\",\"allocated\":2}}"
        };
        using (var clockHttp = new HttpClient(clockWire))
        {
            var clockApi = new DropboxApi(clockHttp, clock, settings, _ => { },
                _ => new ValueTask<string?>("token"), "root-id");
            var (used, allocated) = await clockApi.SpaceUsageAsync(CancellationToken.None);
            Check.Require(used == 1 && allocated == 2, "individual space usage decode");
            Check.Require(clockWire.Bodies.Count == 1 && clockWire.Bodies[0] == "null", "void wire body is not JSON null");
            Check.Require(clockApi.ClockOffset is { } sample, "clock sample not recorded");
            Check.Require(sample.Offset == serverDate - clock.GetUtcNow(), "clock offset wrong");
            Check.Require(sample.Uncertainty == rtt + TimeSpan.FromSeconds(1), "clock uncertainty wrong");
        }

        // The prepared path uses the same binding: each account's own token, and the same verified root.
        var preparedWire = new Wire { Body = "{}", Date = serverDate };
        using (var preparedHttp = new HttpClient(preparedWire))
        {
            var preparedApi = new DropboxApi(preparedHttp, TimeProvider.System, settings, _ => { },
                _ => new ValueTask<string?>("token-b"), "shared-root");
            using var request = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint("files/delete_v2"))
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            using var response = await preparedApi.SendAsync(request, CancellationToken.None);
            Check.Require(preparedWire.Authorizations.Count == 1 &&
                preparedWire.Authorizations[0] == "Bearer token-b", "prepared path missing its token");
            Check.Require(preparedWire.PathRoots.Count == 1 &&
                preparedWire.PathRoots[0].Contains("shared-root", StringComparison.Ordinal), "prepared path missing its root");
        }

        // A refused prepared upload must leave the caller-owned source open (no request disposal closes it).
        var caller = new TrackingStream(new byte[] { 1, 2, 3 });
        var refusalWire = new Wire { Status = 409, Body = "{\"error_summary\":\"path/not_found\"}" };
        using (var refusalHttp = new HttpClient(refusalWire))
        {
            var refusalApi = new DropboxApi(refusalHttp, TimeProvider.System, settings, _ => { },
                _ => new ValueTask<string?>("token"), "root-id");
            var upload = new HttpRequestMessage(HttpMethod.Post, Routes.Endpoint("files/upload"))
            {
                Content = new StreamContent(caller)
            };
            upload.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            try
            {
                using var refusedResponse = await refusalApi.SendAsync(upload, CancellationToken.None);
                throw new InvalidDataException("refusal accepted");
            }
            catch (HttpRequestException)
            {
            }
            Check.Require(!caller.Disposed, "a refused send closed the caller-owned upload source");
        }
    }

    private static async Task ExpectArgumentException(Func<Task> action)
    {
        try
        {
            await action();
            throw new InvalidDataException("a rejected call was accepted");
        }
        catch (ArgumentException)
        {
        }
    }

    private sealed class Wire : HttpMessageHandler
    {
        internal int Status { get; set; } = 200;
        internal string Body { get; set; } = "{}";
        internal DateTimeOffset? Date { get; set; }
        internal TimeSpan Advance { get; set; }
        internal FakeTimeProvider? Clock { get; set; }
        internal List<string> Paths { get; } = [];
        internal List<string> Bodies { get; } = [];
        internal List<string> Authorizations { get; } = [];
        internal List<string> PathRoots { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            var current = request.Content;
            Bodies.Add(current is null ? string.Empty : await current.ReadAsStringAsync(cancellationToken));
            if (request.Headers.Authorization is { } authorization)
                Authorizations.Add(authorization.Scheme + " " + (authorization.Parameter ?? string.Empty));
            if (request.Headers.TryGetValues("Dropbox-API-Path-Root", out var roots)) PathRoots.AddRange(roots);
            Clock?.Advance(Advance);
            var response = new HttpResponseMessage((HttpStatusCode)Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            };
            if (Date is { } date) response.Headers.Date = date;
            return response;
        }
    }

    private sealed class TrackingStream : MemoryStream
    {
        internal bool Disposed { get; private set; }

        internal TrackingStream(byte[] bytes) : base(bytes, writable: false)
        {
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }
}
