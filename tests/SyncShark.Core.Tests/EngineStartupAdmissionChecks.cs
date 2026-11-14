using System.Security;
using Massdex.SyncShark;

internal static class EngineStartupAdmissionChecks
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string OtherSid = "S-1-5-21-1-2-3-1002";

    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "startup-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            IdentityAndRegistrations(root);
            Metadata(root);
            RefusalLogging(root);
            var observations = new List<string>();
            var registrationReads = 0;
            EngineStartupAdmission Acquire() => EngineStartupAdmission.Require(root, () => Sid,
                (folder, pattern) => { observations.Add(Path.GetRelativePath(root, folder) + "/" + pattern); return null; },
                _ => { registrationReads++; return []; });
            var admission = Acquire();
            admission.RequireRoot(root);
            admission.RequireRoot(root + Path.DirectorySeparatorChar);
            Require(observations.SequenceEqual(new[] { "v2/state.db*", "vNext\\state/syncshark.db*" }) &&
                registrationReads == 1, "one admission checks exactly two metadata locations and one registry inventory");
            Refuses(() => admission.RequireRoot(Path.Combine(root, "different")), "startup-inspection-failed");
            _ = Acquire();
            Require(registrationReads == 2 && observations.Count == 4, "a later lifetime inspects again; no cached clearance");
            foreach (var failure in new Exception[] { new IOException(), new UnauthorizedAccessException(), new SecurityException() })
            {
                Refuses(() => EngineStartupAdmission.Require(root, () => Sid, (_, _) => throw failure, _ => []),
                    "startup-inspection-failed");
                Refuses(() => EngineStartupAdmission.Require(root, () => Sid, (_, _) => null, _ => throw failure),
                    "startup-inspection-failed");
            }
            foreach (var sid in new string?[] { null, "", "not-a-sid" })
                Refuses(() => EngineStartupAdmission.Require(root, () => sid, (_, _) => null,
                    _ => throw new InvalidOperationException("unknown SID reached registry")), "startup-user-unavailable");
            Refuses(() => EngineStartupAdmission.Require(root, () => Sid, (_, _) => null,
                _ => [new(root, SyncRootRegistrationIdentity.ProviderId, "fixture")]), "startup-owned-registration");
            Console.WriteLine("PASS startup admission: metadata-only footprint, exact SID ownership, uncertainty refusal and lifetime renewal");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void IdentityAndRegistrations(string root)
    {
        var owned = SyncRootRegistrationIdentity.Create(Sid, "pair-a");
        var other = SyncRootRegistrationIdentity.Create(OtherSid, "pair-b");
        Require(owned == "Massdex.SyncShark!" + Sid + "!pair-a" &&
            SyncRootRegistrationIdentity.IsForUser(owned, Sid), "creation and exact owned identity share one rule");
        Require(!SyncRootRegistrationIdentity.IsProductRegistration("Massdex.SyncSharkOther!" + Sid + "!pair-a") &&
            !SyncRootRegistrationIdentity.IsProductRegistration("massdex.syncshark!" + Sid + "!pair-a"),
            "prefix lookalikes and case variants are foreign, not ownership evidence");
        Require(StorageInventoryWindows.RegistrationProvider(SyncRootRegistrationIdentity.ProviderId) ==
            SyncRootRegistrationIdentity.ProviderId && StorageInventoryWindows.RegistrationProvider(owned) == "Sync Shark" &&
            StorageInventoryWindows.RegistrationProvider("Foreign!root") == "Foreign" &&
            StorageInventoryWindows.RegistrationProvider("DROPBOX!root") == "Dropbox",
            "general inventory retains raw display for bare malformed provider IDs and existing provider labels");
        var mappings = new List<string>();
        var found = StorageInventoryWindows.ReadOwnedRegistrationsForUser(Sid,
            [other, "Foreign!" + Sid + "!pair-a", owned], id => { mappings.Add(id); return root; });
        Require(mappings.SequenceEqual(new[] { owned }) && found.Count == 1 && found[0].RegistrationId == owned,
            "other-user and foreign IDs are skipped before mapping access");
        Require(StorageInventoryWindows.ReadOwnedRegistrationsForUser(Sid, null,
            _ => throw new InvalidOperationException()).Count == 0, "a successfully observed missing manager is empty");
        foreach (var mapping in new object?[] { null, 42, "relative", "" })
            Refuses(() => StorageInventoryWindows.ReadOwnedRegistrationsForUser(Sid, [owned], _ => mapping),
                "startup-registration-invalid");
        Refuses(() => StorageInventoryWindows.ReadOwnedRegistrationsForUser(Sid, [owned],
            _ => throw new UnauthorizedAccessException()), "startup-registration-invalid");
        foreach (var id in new[] { SyncRootRegistrationIdentity.ProviderId, "Massdex.SyncShark!!pair-a",
            "Massdex.SyncShark!unknown!pair-a", "Massdex.SyncShark!" + Sid + "!", owned + "!extra" })
            Refuses(() => StorageInventoryWindows.ReadOwnedRegistrationsForUser(Sid, [id],
                _ => throw new InvalidOperationException("malformed ID reached mapping")), "startup-registration-invalid");
        foreach (var account in new[] { "", "a!b", "a/b", new string('a', 174) })
        {
            try { _ = SyncRootRegistrationIdentity.Create(Sid, account); throw new Exception("invalid account admitted"); }
            catch (InvalidDataException) { }
        }
    }

    private static void Metadata(string root)
    {
        EngineStartupAdmission Acquire() => EngineStartupAdmission.Require(root, () => Sid,
            EngineStartupAdmission.FindArtifact, _ => []);
        _ = Acquire();
        foreach (var (folder, file) in new[]
        {
            ("v2", "state.db"), ("v2", "state.db-wal"), ("v2", "state.db-shm"), ("v2", "STATE.DB-backup"),
            (Path.Combine("vNext", "state"), "syncshark.db"), (Path.Combine("vNext", "state"), "syncshark.db-wal"),
            (Path.Combine("vNext", "state"), "syncshark.db-shm"), (Path.Combine("vNext", "state"), "syncshark.db-extra")
        })
        {
            var directory = Path.Combine(root, folder);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, file);
            using (var locked = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                Refuses(() => Acquire(), "startup-legacy-store");
            File.Delete(path);
            _ = Acquire();
        }
        var matchingDirectory = Path.Combine(root, "v2", "state.db-directory");
        Directory.CreateDirectory(matchingDirectory);
        Refuses(() => Acquire(), "startup-legacy-store");
        Directory.Delete(matchingDirectory);
        // Only top-level metadata is relevant; endpoint-like descendants must not be walked.
        var nested = Path.Combine(root, "v2", "child");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "state.db"), "fixture");
        _ = Acquire();
        Directory.Delete(Path.Combine(root, "vNext"), recursive: true);
        File.WriteAllText(Path.Combine(root, "vNext"), "not a directory");
        Refuses(() => Acquire(), "startup-inspection-failed");
        File.Delete(Path.Combine(root, "vNext"));
        _ = Acquire();
    }

    private static void RefusalLogging(string root)
    {
        var refusal = new EngineStartupRefusedException("startup-legacy-store", "metadata\nlocation",
            new IOException("inner detail must not be logged"));
        Massdex.SyncShark.V2.AutoSyncLog.StartupRefused(root, refusal);
        var lines = File.ReadAllLines(Massdex.SyncShark.V2.AutoSyncLog.FilePath(root));
        Require(lines.Length == 1 && lines[0].Contains("reason=startup-legacy-store location=metadata location") &&
            !lines[0].Contains("inner detail"), "typed refusal logs only its sanitized reason and location");
        Massdex.SyncShark.V2.AutoSyncLog.StartupRefused(root, new EngineStartupRefusedException(
            "startup-inspection-failed", new string('x', Massdex.SyncShark.V2.AutoSyncLog.LongestLine + 1)));
        Require(File.ReadAllLines(Massdex.SyncShark.V2.AutoSyncLog.FilePath(root))
            .All(line => line[(line.IndexOf(' ') + 1)..].Length <= Massdex.SyncShark.V2.AutoSyncLog.LongestLine),
            "typed refusal reuses the lifecycle sink's line bound");
    }

    private static void Refuses(Action action, string reason)
    {
        try { action(); }
        catch (EngineStartupRefusedException error)
        {
            Require(error.ReasonKey == reason && error.ExitCode == SyncCliExitCode.NeedsReview &&
                !string.IsNullOrWhiteSpace(error.Location), "typed refusal preserves reason, location and public exit");
            return;
        }
        throw new InvalidOperationException("Expected startup refusal: " + reason);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
