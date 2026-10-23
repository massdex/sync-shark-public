using System.Text.Json;
using Massdex.SyncShark;

internal static class ReleaseRecoveryAdmissionChecks
{
    private const string Revision = "1234567890abcdef1234567890abcdef12345678";
    private const string Version = "0.3.55";
    private static readonly string[] InstallSteps = ["stage-package", "publish-runtime", "backup-state",
        "create-next-link", "retain-predecessor", "publish-successor", "register-explorer-package", "verify-complete"];

    internal static void Run(string root)
    {
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("The isolated recovery input must be new.");
        Directory.CreateDirectory(root);
        var transaction = Path.Combine(root, "replacement-staging", "transactions", Version + "-" + Revision[..7]);
        var install = Path.Combine(transaction, "install-journal.json");
        var rollback = Path.Combine(transaction, "rollback-journal.json");
        var state = Path.Combine(root, "state", "owner-marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        File.WriteAllText(state, "untouched state");
        void Check(Action action, string name, bool rejected = false)
        {
            Exception? observed = null;
            try { action(); }
            catch (PilotReleaseRecoveryRequiredException error) { observed = error; }
            if ((observed is not null) != rejected) throw new InvalidDataException("Wrong release admission: " + name);
            if (File.ReadAllText(state) != "untouched state") throw new InvalidDataException("Admission modified saved state.");
            Console.WriteLine("PASS " + name + (rejected ? " (refused without state writes)" : " (admitted)"));
        }
        void Admit() => PilotReleaseRecovery.RequireBuildReady(root, Version + "+" + Revision, Revision);
        void Write(string path, string operation = "install", string status = "complete", string? pending = null,
            string[]? completed = null, string[]? allowed = null, string? productRoot = null, string? revision = null)
            => File.WriteAllText(path, JsonSerializer.Serialize(new { schema = 1, operation, version = Version,
                commit = revision ?? Revision, product_root = productRoot ?? root, status, pending,
                completed = completed ?? InstallSteps, allowed_steps = allowed ?? InstallSteps }));

        Check(Admit, "No current release transaction is admitted");
        Directory.CreateDirectory(transaction);
        Check(Admit, "An empty current transaction has not started a state replacement");
        Write(install);
        Check(Admit, "The exact complete current install is admitted");
        var historical = Path.Combine(root, "replacement-staging", "transactions", "0.3.19-e99ad40");
        Directory.CreateDirectory(historical);
        File.WriteAllText(Path.Combine(historical, "install-journal.json"), "{\"status\":\"active\"}");
        Check(Admit, "An unrelated retained historical active journal is not selected");
        Write(install, status: "active", pending: "register-explorer-package", completed: InstallSteps[..6]);
        Check(Admit, "An unfinished current install is refused", true);
        Write(install);
        Write(rollback, operation: "rollback", status: "active", pending: "restore-state", completed: []);
        Check(Admit, "An unfinished current rollback is refused", true);
        Write(rollback, operation: "rollback");
        Check(Admit, "A rolled-back successor cannot reopen predecessor state", true);
        File.Delete(rollback);
        var temporary = install + ".new";
        File.WriteAllText(temporary, "retained interrupted bytes");
        Check(Admit, "An interrupted atomic journal write is refused", true);
        if (File.ReadAllText(temporary) != "retained interrupted bytes") throw new InvalidDataException("Admission removed recovery evidence.");
        Console.WriteLine("PASS Interrupted journal evidence remains byte-exact");
        File.Delete(temporary);
        Write(install, productRoot: Path.Combine(root, "other"));
        Check(Admit, "Contradictory product root is refused", true);
        Write(install, revision: new string('f', 40));
        Check(Admit, "Contradictory source revision is refused", true);
        Write(install, completed: InstallSteps[..7]);
        Check(Admit, "A falsely complete install missing its final phase is refused", true);
        Write(install, allowed: ["verify-complete"], completed: ["verify-complete"]);
        Check(Admit, "A fabricated shortened complete phase list is refused", true);
        Write(install, pending: "verify-complete");
        Check(Admit, "A complete install with a pending phase is refused", true);
        File.WriteAllText(install, "{ incomplete");
        Check(Admit, "A malformed retained journal is refused", true);
        File.WriteAllText(install, new string('x', 65537));
        Check(Admit, "An oversized retained journal is refused", true);
        Write(install);
        File.WriteAllText(install, File.ReadAllText(install).Replace("\"schema\":1", "\"schema\":2147483648", StringComparison.Ordinal));
        Check(Admit, "A numeric schema outside Int32 is a recovery refusal, not an unhandled parser exception", true);
        File.WriteAllText(install, "[]");
        Check(Admit, "A non-object journal is a recovery refusal", true);
        Write(install);
        using (File.Open(install, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            Check(Admit, "A concurrently writable release journal is refused", true);
        Check(() => PilotReleaseRecovery.RequireBuildReady(root, Version + "+" + new string('f', 40), Revision),
            "Contradictory runtime identity is refused", true);
        Check(() => PilotReleaseRecovery.RequireBuildReady(root, Version, "not-a-revision"),
            "Malformed bound runtime identity is not mistaken for a development build", true);
        Write(install, status: "active", completed: [], pending: "stage-package");
        Check(() => PilotReleaseRecovery.RequireBuildReady(root, Version, "UNBOUND"),
            "An explicitly unbound isolated development runtime does not claim an installed release");
        if (BuildIdentity.SourceRevision != "UNBOUND")
        {
            BuildIdentity.RequireExactRevision(BuildIdentity.SourceRevision);
            if (BuildIdentity.InformationalVersion.Split('+', 2)[0] != Version)
                throw new InvalidDataException("The public-entry fixture requires the declared release version.");
            var currentTransaction = Path.Combine(root, "replacement-staging", "transactions",
                Version + "-" + BuildIdentity.SourceRevision[..7]);
            Directory.CreateDirectory(currentTransaction);
            var currentInstall = Path.Combine(currentTransaction, "install-journal.json");
            var lockPath = Path.Combine(root, "state", SyncSharkRuntimePaths.InstanceLockFileName);
            Write(currentInstall, revision: BuildIdentity.SourceRevision);
            Check(() => { using var owner = PilotInstanceLock.Acquire(lockPath); },
                "Bound ordinary startup admits the exact complete current transaction");
            var retainedLock = File.ReadAllBytes(lockPath);
            Write(currentInstall, status: "active", pending: "register-explorer-package",
                completed: InstallSteps[..6], revision: BuildIdentity.SourceRevision);
            Check(() => { using var owner = PilotInstanceLock.Acquire(lockPath); },
                "Bound ordinary startup refuses an active current transaction", true);
            if (!File.ReadAllBytes(lockPath).SequenceEqual(retainedLock))
                throw new InvalidDataException("Refused startup modified the retained owner lock.");
            Console.WriteLine("PASS Refused bound startup leaves retained owner-lock bytes unchanged");
            var cliRejected = false;
            try
            {
                var runtime = SyncCliRuntime.Acquire(lockPath);
                runtime.DisposeAsync().GetAwaiter().GetResult();
            }
            catch (SyncCliOperationException error) when (error.InnerException is PilotReleaseRecoveryRequiredException)
            { cliRejected = true; }
            if (!cliRejected || File.ReadAllText(state) != "untouched state" ||
                !File.ReadAllBytes(lockPath).SequenceEqual(retainedLock))
                throw new InvalidDataException("The direct CLI did not preserve and expose the exact recovery refusal.");
            Console.WriteLine("PASS Bound direct CLI exposes recovery refusal, not busy, without state writes");
            Check(() => { using var owner = PilotInstanceLock.AcquireForMaintenance(lockPath); },
                "Bound explicit maintenance remains the existing recovery route");
            if (!File.ReadAllBytes(lockPath).SequenceEqual(retainedLock))
                throw new InvalidDataException("Maintenance changed the retained owner-lock preimage.");
            Write(currentInstall, revision: BuildIdentity.SourceRevision);
            Check(() => { using var owner = PilotInstanceLock.Acquire(lockPath); },
                "Bound ordinary startup resumes after the exact complete boundary is restored");
        }
        Directory.Delete(root, true);
        Console.WriteLine("PASS Isolated recovery fixture cleaned; no product journal, installer, provider or saved state was changed");
    }
}
