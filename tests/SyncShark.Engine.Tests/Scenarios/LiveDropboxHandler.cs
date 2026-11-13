using System.Net;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Counts the live run's Dropbox routes and injects the two provider faults the cases use; everything else
/// passes straight through to the real service. The counters sit outside the injector so a real attempt and a
/// scripted reply are each observed exactly once.</summary>
internal sealed class LiveDropboxHandler : DelegatingHandler
{
    private readonly FaultInjector faults;

    public LiveDropboxHandler(HttpMessageHandler inner) : this(new FaultInjector(inner))
    {
    }

    private LiveDropboxHandler(FaultInjector faults) : base(new RequestCounters(faults))
    {
        this.faults = faults;
        Counters = (RequestCounters)InnerHandler!;
    }

    /// <summary>Every attempt at the API boundary, including scripted faults the injector returns.</summary>
    public RequestCounters Counters { get; }

    /// <summary>Content downloads the engine made; the scenario's own snapshot is excluded.</summary>
    public int ContentDownloads => (int)Counters.Attempts(DropboxProtocol.Download);

    /// <summary>True while the scenario reads its own tree, so a scripted fault stays for the engine's next call and
    /// those reads stay out of the measured counts.</summary>
    public bool SuspendObservation
    {
        get => Counters.SuspendObservation;
        set
        {
            Counters.SuspendObservation = value;
            faults.Suspend = value;
        }
    }

    public void RefuseUpload(string dropboxPath) => faults.RefuseUpload(dropboxPath);

    public void ClearWriteRefusals() => faults.ClearWriteRefusals();

    public void FailListings(int count) => faults.FailListings(count);

    /// <summary>Returns the scripted provider faults without touching the service; every other request passes to the
    /// real transport.</summary>
    private sealed class FaultInjector(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly object gate = new();
        private readonly HashSet<string> refusedUploads = new(StringComparer.OrdinalIgnoreCase);
        private int listingsToFail;

        public bool Suspend { get; set; }

        public void RefuseUpload(string dropboxPath)
        {
            lock (gate) refusedUploads.Add(dropboxPath);
        }

        public void ClearWriteRefusals()
        {
            lock (gate) refusedUploads.Clear();
        }

        public void FailListings(int count)
        {
            lock (gate) listingsToFail = count;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            bool failListing;
            lock (gate)
            {
                failListing = !Suspend && path == "/2/files/list_folder" && listingsToFail > 0;
                if (failListing) listingsToFail--;
            }
            if (failListing) return Error(HttpStatusCode.BadRequest, "scripted failure");

            if (!Suspend && path == "/2/files/upload" && UploadPath(request) is { } uploaded)
            {
                bool refuse;
                lock (gate) refuse = refusedUploads.Remove(uploaded);
                if (refuse) return Error(HttpStatusCode.Conflict, "path/reason/other");
            }
            if (!Suspend && path == "/2/" + DropboxProtocol.UploadSessionFinishBatch)
                return await FinishBatchAsync(request, cancellationToken).ConfigureAwait(false);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> FinishBatchAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false));
            var entries = document.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            var refused = new bool[entries.Length];
            lock (gate)
            {
                for (int index = 0; index < entries.Length; index++)
                    refused[index] = refusedUploads.Remove(entries[index].GetProperty("commit")
                        .GetProperty("path").GetString()!);
            }
            if (!refused.Any(value => value))
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var forwarded = entries.Where((_, index) => !refused[index]).ToArray();
            var results = new object[entries.Length];
            if (forwarded.Length > 0)
            {
                // Never send refused commits: rewriting their successes would leave unwanted remote files.
                var originalContent = request.Content;
                using var filteredContent = new StringContent(JsonSerializer.Serialize(new { entries = forwarded }),
                    Encoding.UTF8, "application/json");
                HttpResponseMessage response;
                try
                {
                    request.Content = filteredContent;
                    response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    request.Content = originalContent;
                }
                if (!response.IsSuccessStatusCode) return response;
                using (response)
                using (var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false)))
                {
                    var committed = reply.RootElement.GetProperty("entries");
                    if (committed.GetArrayLength() != forwarded.Length) throw new JsonException();
                    int ordinal = 0;
                    for (int index = 0; index < results.Length; index++)
                        if (!refused[index]) results[index] = committed[ordinal++].Clone();
                }
            }
            for (int index = 0; index < results.Length; index++)
                if (refused[index]) results[index] = new Dictionary<string, object>
                {
                    [".tag"] = "failure",
                    ["failure"] = new Dictionary<string, object>
                    {
                        [".tag"] = "path",
                        ["path"] = new Dictionary<string, object> { [".tag"] = "other" }
                    }
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { entries = results }),
                    Encoding.UTF8, "application/json")
            };
        }

        private static string? UploadPath(HttpRequestMessage request) =>
            request.Headers.TryGetValues("Dropbox-API-Arg", out var values)
                ? JsonDocument.Parse(values.Single()).RootElement.GetProperty("path").GetString()
                : null;

        private static HttpResponseMessage Error(HttpStatusCode status, string summary) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["error_summary"] = summary,
                ["error"] = new Dictionary<string, object>
                {
                    [".tag"] = "path",
                    ["reason"] = new Dictionary<string, object> { [".tag"] = "other" }
                }
            }), Encoding.UTF8, "application/json")
        };
    }
}
