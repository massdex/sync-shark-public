using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Harness;

/// <summary>Sends a synthetic browser return to the real sign-in listener.</summary>
internal sealed class LoopbackBrowser(bool wrongState = false)
{
    internal Task<string>? Receipt { get; private set; }

    internal void Open(Uri authorization)
    {
        var query = SignInChecks.Query(authorization);
        var redirect = new Uri(query["redirect_uri"]);
        Check.Require(redirect == SignIn.RedirectUri && redirect.IsLoopback, "Unexpected sign-in redirect");
        var state = wrongState ? query["state"] + "-wrong" : query["state"];
        Receipt = SignInChecks.SendAsync(redirect.Port, "GET " + redirect.AbsolutePath + "?code=c&state=" +
            Uri.EscapeDataString(state) + " HTTP/1.1\r\nHost: localhost\r\n\r\n");
    }
}
