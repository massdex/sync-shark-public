using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for DropboxProtocol.</summary>
public static class DropboxProtocolChecks
{
    /// <summary>Verifies the DropboxProtocol contract through the module surface.</summary>
    public static Task RunAsync()
    {
        Check.Require(DropboxProtocol.ListFolder == "files/list_folder" &&
            DropboxProtocol.UploadSessionFinishBatch == "files/upload_session/finish_batch_v2" &&
            DropboxProtocol.ListPageMaximum == 2000, "Provider routes and page limit");

        using (var document = DropboxProtocol.Parse(
            new MemoryStream(Encoding.UTF8.GetBytes("{\"cursor\":\"c1\",\"has_more\":true}"))))
        {
            Check.Require(document.RootElement.GetProperty(DropboxProtocol.Cursor).GetString() == "c1" &&
                document.RootElement.GetProperty(DropboxProtocol.HasMore).GetBoolean(), "Parsed reply body");
        }

        static JsonElement Envelope(long? retryAfter) => retryAfter is { } seconds
            ? JsonSerializer.SerializeToElement(new { error = new { retry_after = seconds } })
            : default;

        Check.Require(DropboxProtocol.ProviderWait(null, default) is null, "No wait supplied");
        Check.Require(DropboxProtocol.ProviderWait(TimeSpan.FromSeconds(5), default) ==
            TimeSpan.FromSeconds(5), "Header wait only");
        Check.Require(DropboxProtocol.ProviderWait(null, Envelope(9)) == TimeSpan.FromSeconds(9), "Body wait only");
        Check.Require(DropboxProtocol.ProviderWait(TimeSpan.FromSeconds(5), Envelope(9)) ==
            TimeSpan.FromSeconds(9), "The larger of the header and the body applies");
        Check.Require(DropboxProtocol.ProviderWait(TimeSpan.FromSeconds(5), Envelope(2)) ==
            TimeSpan.FromSeconds(5), "A smaller body wait does not shorten the header");
        Check.Require(DropboxProtocol.ProviderWait(null, Envelope(0)) == TimeSpan.Zero,
            "A zero body wait is the zero it supplied, not a default");
        Check.Require(DropboxProtocol.ProviderWait(null, Envelope(-3)) is null, "A negative body wait is no wait");
        Check.Require(DropboxProtocol.ProviderWait(TimeSpan.FromSeconds(-1), default) is null,
            "A negative header wait is no wait");
        Check.Require(DropboxProtocol.ProviderWait(null, JsonSerializer.SerializeToElement("9")) is null,
            "A malformed body selects no wait");
        Check.Require(DropboxProtocol.ProviderWait(null, Envelope(long.MaxValue)) is null,
            "A body wait that cannot be a TimeSpan selects no wait");
        var bareError = JsonSerializer.SerializeToElement(new { retry_after = 9 });
        Check.Require(DropboxProtocol.ProviderWait(null, bareError) is null, "A bare retry_after is not an error envelope");
        var malformed = JsonSerializer.SerializeToElement(new { error = "invalid", retry_after = 9 });
        Check.Require(DropboxProtocol.ProviderWait(TimeSpan.FromSeconds(4), malformed) == TimeSpan.FromSeconds(4),
            "A malformed nested error leaves the valid header unchanged");

        return Task.CompletedTask;
    }
}
