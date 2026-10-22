namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    public static async Task RunClientModifiedAsync(string root)
    {
        var failures = new List<Exception>();
        foreach (var route in new[] { "batch", "single", "session" })
        foreach (var overrideResponse in new[] { false, true })
        {
            try
            {
                await ClientModifiedCaseAsync(root, route, overrideResponse);
                Console.WriteLine($"clientmod {route} responseOverride={overrideResponse}: PASS");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"clientmod {route} responseOverride={overrideResponse}: FAIL {exception.Message}");
                failures.Add(exception);
            }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
        Console.WriteLine("--v2-clientmod-checks passed (6 upload/persistence/replan cases).");
    }

    private static async Task ClientModifiedCaseAsync(string root, string route, bool overrideResponse)
    {
        using var h = new Harness(uploadThreshold: route == "session" ? 1 : null);
        var bytes = new byte[route == "batch" ? 17 : (8 * 1024 * 1024) + 1];
        var source = Source("mtime.bin", bytes) with
        {
            Mtime = new DateTimeOffset(2020, 2, 3, 10, 5, 6, TimeSpan.FromHours(2)).AddTicks(9_876_543)
        };
        const string requested = "2020-02-03T08:05:06Z";
        if (overrideResponse) h.Wire.UploadModifiedOverride = "2020-02-03T08:05:03Z";
        var expected = DateTimeOffset.Parse(h.Wire.UploadModifiedOverride ?? requested,
            System.Globalization.CultureInfo.InvariantCulture);
        var action = PutAction(source.Path, source);
        using var stream = new MemoryStream(bytes);
        var uploaded = await h.Writer().PutAsync(action, stream, CancellationToken.None);
        var endpoint = route switch
        {
            "batch" => "/2/files/upload_session/finish_batch_v2",
            "single" => "/2/files/upload",
            _ => "/2/files/upload_session/finish"
        };
        Require(h.Wire.Calls.Contains(endpoint), route + ": exercised the intended upload path");
        Require(h.Wire.UploadClientModified.SequenceEqual(new[] { requested }),
            route + ": client_modified is UTC, truncated rather than rounded to whole seconds");
        Require(uploaded.Mtime == expected, route + ": returned timestamp is response truth, not source/request time");

        // Persist through the real commit path, then compare the next fresh remote observation.
        using var store = SqliteStateStore.Open(Path.Combine(root, "mtime-" + route + "-" + overrideResponse),
            V2Settings.Default, TimeProvider.System);
        store.SavePair(new PairDef("p1", "mtime", new RootDef("ra", "Local", null, root, "local"), Root(), [], [], true));
        var gen = store.BeginGeneration(RootId, "snapshot");
        store.CommitResult("p1", gen, action, new ActionResult(action.ActionId, Outcome.Done, source, uploaded, null, null));
        var baseline = store.Baselines("p1").Single();
        Require(baseline.B.Mtime == expected && baseline.A.Mtime == source.Mtime,
            route + ": stored target is provider truth; source retains its subsecond precision");
        var observed = (await h.Writer().ObserveAsync(source.Path, CancellationToken.None))!;
        Require(Content.SameVersion(observed with { Hash = null, VersionId = null },
                baseline.B with { Hash = null, VersionId = null }) == true,
            route + ": even metadata-only same-side checks need no new hash after upload");
        var options = new PlanOptions(V2Settings.Default.TieWindow, false,
            new LabelRule("{name} [{side} {time}]{ext}", "yyyyMMddHHmmss", TimeSpan.Zero, "Local", "Dropbox"), 100);
        var plan = Planner.Decide("p1", gen + 1,
            [new PlanRow(baseline, source, observed, new Evidence(false, false, true, true))], options);
        Require(plan.Actions.Count == 0, route + ": second scan does not re-upload the subsecond source");
    }
}
