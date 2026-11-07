using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class CloudFilesLifetimeChecks
{
    internal static void Run()
    {
        var pair = new PairDef("stable-pair", "test", new("local", "local", null, @"F:\offline-fixture", "id:local:00000007:000000000000002a"),
            new("remote", "dropbox", "account", "/test", "id:root"), [], [], true);
        var retained = new FakeConnection { Retained = true };
        using (var life = new CloudFilesLifetime((_, _, _) => retained, _ => true))
        {
            life.Open(pair, false, false, SyncDirectoryStorage.AlwaysAvailable, default);
            life.Open(pair, false, false, SyncDirectoryStorage.AlwaysAvailable, default);
            Require(retained.Connects == 1, "retained reconnect exactly once");
            try { life.Open(pair with { B = pair.B with { AccountId = "wrong" } }, false, false,
                SyncDirectoryStorage.AlwaysAvailable, default); throw new Exception("binding accepted"); }
            catch (InvalidOperationException) { }
        }
        Require(retained.Disconnects == 1 && retained.Removals == 0 && retained.Rollbacks == 0, "ordinary dispose disconnects only");
        foreach (var wasRetained in new[] { false, true })
        foreach (var dependent in new[] { false, true })
        {
            var failed = new FakeConnection { Retained = wasRetained, Fail = true, Dependent = dependent };
            using var life = new CloudFilesLifetime((_, _, _) => failed, _ => wasRetained);
            try { life.Open(pair, true, false, SyncDirectoryStorage.AlwaysAvailable, default); throw new Exception("connect failure hidden"); }
            catch (IOException) { }
            Require(failed.Disconnects == 1 && failed.Removals == (!wasRetained && !dependent ? 1 : 0), "rollback only newly created empty registration");
        }
        var healthy = new FakeConnection { Retained = true };
        using (var startup = new CloudFilesLifetime((_, _, _) => healthy,
                   p => p.PairId == "broken" ? throw new IOException("unreadable registration") : p.PairId != "incomplete"))
        {
            var reported = new List<string>();
            var failures = startup.Reconnect(new[] { pair with { PairId = "broken" },
                pair with { PairId = "incomplete", B = pair.B with { AccountId = null } },
                pair with { PairId = "registered-incomplete", B = pair.B with { AccountId = null } }, pair }, default,
                (p, _) => reported.Add(p.PairId));
            Require(failures.Count == 2 && reported.Count == 2 && healthy.Connects == 1,
                "startup reports bad registered pairs, skips unregistered incomplete pair and connects healthy pair");
        }
        var rollbackFailure = new FakeConnection { Fail = true, RollbackFails = true };
        using (var rollback = new CloudFilesLifetime((_, _, _) => rollbackFailure, _ => false))
        {
            try { rollback.Open(pair, true, false, SyncDirectoryStorage.AlwaysAvailable, default); throw new Exception("failure hidden"); }
            catch (AggregateException errors) { Require(errors.InnerExceptions.Count == 2, "both connection and rollback errors retained"); }
            Require(rollbackFailure.Disconnects == 1, "rollback refusal still disconnects");
        }
        using (var preview = new CloudFilesLifetime((_, _, _) => throw new Exception("preview created provider"),
                   _ => throw new Exception("preview probed registration")))
            preview.Open(pair, true, true, SyncDirectoryStorage.AlwaysAvailable, default);
        using (var cancelled = new CloudFilesLifetime((_, _, _) => throw new Exception("cancelled created provider"), _ => true))
        {
            try { cancelled.Open(pair, true, false, SyncDirectoryStorage.AlwaysAvailable, new CancellationToken(true));
                throw new Exception("cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        Console.WriteLine("PASS Cloud Files offline lifetime: retained reconnect, exact binding, rollback, dependent pointers, disconnect-only dispose, preview and cancellation; no native calls.");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class FakeConnection : ICloudFilesConnection
    {
        internal bool Retained, Fail, Dependent, RollbackFails;
        internal int Connects, Disconnects, Removals, Rollbacks;
        public void RegisterAndConnect() { Connects++; if (Fail) throw new IOException("connect failed"); }
        public void RollBackNewRegistration() { Rollbacks++; if (RollbackFails) throw new IOException("rollback refused"); if (!Retained && !Dependent) Removals++; }
        public void Dispose() => Disconnects++;
    }
}
