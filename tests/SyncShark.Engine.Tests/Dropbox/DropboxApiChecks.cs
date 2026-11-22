using System.Net;
using System.Text;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxApi.</summary>
public static class DropboxApiChecks
{
    /// <summary>Verifies route selection, storage usage, the server clock offset and the authenticated binding.</summary>
    public static async Task RunAsync()
    {
        var settings = SettingsParser.Load();

        var wire = new Wire();
        using var http = new HttpClient(wire);
        var api = new DropboxApi(http, TimeProvider.System, settings, _ => { });
        Check.Require(api.ClockOffset is null, "a clock offset was claimed before any reply");

        var (used, allocated) = await api.SpaceUsageAsync(CancellationToken.None);
        Check.Require(used == 123 && allocated == 456, "storage usage was not read from the reply");
        Check.Require(wire.Paths.Count == 1 &&
            wire.Paths[0].EndsWith("/users/get_space_usage", StringComparison.Ordinal), "wrong storage-usage route");
        Check.Require(wire.Methods[0] == HttpMethod.Post && wire.Bodies[0] == "{}",
            "storage usage was not the void route call");
        Check.Require(api.ClockOffset is { } recorded && recorded.Uncertainty >= TimeSpan.Zero,
            "the server clock offset was not recorded");

        // The verified binding presents the refreshing token and namespace on ordinary routes only.
        var bound = new Wire();
        using var boundHttp = new HttpClient(bound);
        var boundApi = new DropboxApi(boundHttp, TimeProvider.System, settings, _ => { },
            _ => new ValueTask<string?>("token"), "namespace-id");
        (await boundApi.CallJsonAsync("files/get_metadata", "{\"path\":\"/x\"}", CancellationToken.None)).Dispose();
        Check.Require(bound.Authorizations.Count == 1 && bound.Authorizations[0] == "Bearer token",
            "the access token was not presented");
        Check.Require(bound.PathRoots.Count == 1 && bound.PathRoots[0].Contains("namespace-id", StringComparison.Ordinal),
            "the namespace binding was not sent");

        var account = new Wire();
        using var accountHttp = new HttpClient(account);
        var accountApi = new DropboxApi(accountHttp, TimeProvider.System, settings, _ => { },
            _ => new ValueTask<string?>("token"), "namespace-id");
        (await accountApi.CallJsonAsync("users/get_current_account", "{}", CancellationToken.None)).Dispose();
        Check.Require(account.PathRoots.Count == 0, "the account read carried a path-root header");

        try
        {
            await api.CallJsonAsync("files/does_not_exist", "{}", CancellationToken.None);
            throw new InvalidDataException("unknown route accepted");
        }
        catch (ArgumentException)
        {
        }
    }

    private sealed class Wire : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal List<string> Bodies { get; } = [];
        internal List<HttpMethod> Methods { get; } = [];
        internal List<string> Authorizations { get; } = [];
        internal List<string> PathRoots { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Methods.Add(request.Method);
            var current = request.Content;
            Bodies.Add(current is null ? string.Empty : await current.ReadAsStringAsync(cancellationToken));
            if (request.Headers.Authorization is { } authorization)
                Authorizations.Add(authorization.Scheme + " " + (authorization.Parameter ?? string.Empty));
            if (request.Headers.TryGetValues("Dropbox-API-Path-Root", out var roots)) PathRoots.AddRange(roots);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"used\":123,\"allocation\":{\"allocated\":456},\"metadata\":{\".tag\":\"folder\"}}",
                    Encoding.UTF8, "application/json")
            };
            response.Headers.Date = DateTimeOffset.UnixEpoch;
            return response;
        }
    }
}
