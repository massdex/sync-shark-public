using System.Diagnostics;
using Microsoft.Win32;

namespace Massdex.SyncShark.V2;

/// <summary>Exercises the production registration edge only under an isolated test registry key.</summary>
internal static class AutoSyncStartupChecks
{
    // Measurement repetitions amortize scheduler noise; the figures are evidence, not pass thresholds.
    private const int MeasurementRepetitions = 1000;

    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = AutoSyncProcess.StartupKeyPathForTest;
        var path = @"Software\SyncShark.Tests\Startup-" + Guid.NewGuid().ToString("N");
        AutoSyncProcess.StartupKeyPathForTest = path;
        var root = Path.Combine(Path.GetTempPath(), "startup-" + Guid.NewGuid().ToString("N"));
        var oldExecutable = Path.Combine(root, "previous", "SyncShark.exe");
        var executable = Path.Combine(root, "candidate", "SyncShark.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(oldExecutable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.Copy(Environment.ProcessPath!, oldExecutable);
        File.Copy(Environment.ProcessPath!, executable);
        var messages = new List<string>();
        var writes = new WriteCounter();
        try
        {
            AutoSyncProcess.BeforeStartupWriteForTest = () => writes.Count++;
            CheckPrivateArguments(root, executable);
            CheckAbsenceNeverCreates(path, executable, messages);
            using (var key = Registry.CurrentUser.CreateSubKey(path, writable: true))
            {
                CheckRegistrationBasics(key, oldExecutable, executable, messages, writes);
                CheckOldSupervisorMigration(key, oldExecutable, executable, messages, writes);
                CheckOldGuiMigration(key, oldExecutable, executable, messages, writes);
                CheckBothValues(key, oldExecutable, executable, messages, writes);
                CheckForeignAndMalformed(key, oldExecutable, executable, messages, writes);
                CheckWriteFailurePreservesPredecessor(key, oldExecutable, executable, messages, writes);
                CheckWriteBoundaryChanges(key, oldExecutable, executable, messages, writes);
                CheckEngineStartupInvocation(key, oldExecutable, executable);
            }
            File.Delete(oldExecutable);
            using (var key = Registry.CurrentUser.OpenSubKey(path, writable: true)!)
                CheckMissingTargetRepair(key, executable, messages, writes);
            CheckStoredStartup(root, executable);
            CheckVersionWarning(executable);
            CheckStartupCatalogValues();
            CheckBreakawayWarning();
            Console.WriteLine("PASS startup migration matrix, private arguments, one canonical write, idempotence, foreign/disable protection and read-only facts");
        }
        finally
        {
            AutoSyncProcess.BeforeStartupWriteForTest = null;
            AutoSyncProcess.StartupKeyPathForTest = previous;
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CheckPrivateArguments(string root, string executable)
    {
        Require(AutoSyncProcess.EngineHostArguments == "--core v2 --engine-host" &&
            AutoSyncProcess.EngineSupervisorArguments == "--core v2 --engine-supervisor" &&
            AutoSyncProcess.HostArguments == "--core v2 --background-host" &&
            AutoSyncProcess.LegacySupervisorArguments == "--core v2 --legacy-autosync-supervisor" &&
            AutoSyncProcess.SupervisorArguments == "--core v2 --autosync-supervisor",
            "the private child and compatibility argument spellings stay exact");
        Require(AutoSyncProcess.StartupCommand(executable) ==
            $"{Quoted(Path.GetFullPath(executable))} {AutoSyncProcess.EngineSupervisorArguments}",
            "startup registers the exact canonical Engine supervisor command");
        Require(AutoSyncProcess.IsProductStartupCommand(Quoted(executable) + " " + AutoSyncProcess.EngineSupervisorArguments) &&
            AutoSyncProcess.IsProductStartupCommand(Quoted(executable) + " " + AutoSyncProcess.SupervisorArguments),
            "read-only recognition accepts the exact canonical and old supervisor commands");
        Require(!AutoSyncProcess.IsProductStartupCommand(Quoted(executable) + " " + AutoSyncProcess.EngineHostArguments) &&
            !AutoSyncProcess.IsProductStartupCommand(Quoted(executable) + " " + AutoSyncProcess.LegacySupervisorArguments) &&
            !AutoSyncProcess.IsProductStartupCommand(Quoted(executable) + " " + AutoSyncProcess.HostArguments),
            "private child and compatibility arguments are never adopted as startup registrations");
        // Compile-time legacy API check: StartDetached keeps its (executable, supervisor, warning) shape.
        // The actual child arguments and handle ownership are proven by T318's real-process suite.
        _ = (Func<string, bool, Action<string>?, AutoSyncProcess.ChildProcess>)AutoSyncProcess.StartDetached;
        var rejected = false;
        try { AutoSyncProcess.RegisterStartup(Path.Combine(root, "Renamed.exe"), true); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "the writer rejects names the ownership predicate would reject");
    }

    private static void CheckAbsenceNeverCreates(string path, string executable, List<string> messages)
    {
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(!AutoSyncProcess.ReadStartup(), "absent registration reads disabled");
        Require(!AutoSyncProcess.IsEngineStartupInvocation(executable), "no registration is no startup consent");
        using (var missing = Registry.CurrentUser.OpenSubKey(path))
            Require(missing is null, "read and launch never create a missing key");
        Require(messages.Count == 0, "an absent registration reports no migration");
    }

    private static void CheckRegistrationBasics(RegistryKey key, string oldExecutable, string executable,
        List<string> messages, WriteCounter writes)
    {
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(key.ValueCount == 0 && !AutoSyncProcess.ReadStartup(), "disabled remains disabled on launch");
        AutoSyncProcess.RegisterStartup(executable, false);
        Require(!AutoSyncProcess.ReadStartup() && key.GetValue(AutoSyncProcess.ValueName) is null,
            "disabling an absent registration stays disabled");
        key.SetValue(AutoSyncProcess.LegacyValueName, Legacy(oldExecutable));
        AutoSyncProcess.RegisterStartup(oldExecutable, true);
        Require(AutoSyncProcess.ReadStartup() &&
            (string?)key.GetValue(AutoSyncProcess.ValueName) == AutoSyncProcess.StartupCommand(oldExecutable),
            "enabling registers the canonical supervisor, not the legacy GUI");
        Require(key.GetValue(AutoSyncProcess.LegacyValueName) is null, "successful enable retires the legacy value");
        key.SetValue(AutoSyncProcess.LegacyValueName, Legacy(oldExecutable));
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(key.GetValue(AutoSyncProcess.LegacyValueName) is null,
            "an existing working supervisor can retire a leftover owned legacy launcher");
        Require((string?)key.GetValue(AutoSyncProcess.ValueName) == AutoSyncProcess.StartupCommand(oldExecutable) &&
            messages.Count == 0,
            "launching a candidate or worktree build preserves an existing working canonical registration");
        key.SetValue(AutoSyncProcess.LegacyValueName, "foreign.exe --background");
        AutoSyncProcess.RegisterStartup(executable, true);
        Require((string?)key.GetValue(AutoSyncProcess.LegacyValueName) == "foreign.exe --background",
            "migration preserves a foreign legacy-name value");
        key.DeleteValue(AutoSyncProcess.LegacyValueName, throwOnMissingValue: false);
    }

    private static void CheckOldSupervisorMigration(RegistryKey key, string oldExecutable, string executable,
        List<string> messages, WriteCounter writes)
    {
        key.SetValue(AutoSyncProcess.ValueName, OldSupervisor(executable));
        Require(AutoSyncProcess.ReadStartup(), "the old owned supervisor command reads enabled");
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(writes.Count == 1 && (string?)key.GetValue(AutoSyncProcess.ValueName) == Selected(executable) &&
            messages.Count == 1 && messages[0] == EdgeMessages.Text("startup-repointed"),
            "an old supervisor registration makes one canonical write and reports it once");
        // Wrong-build old target: the target file exists but is not the selected executable.
        key.SetValue(AutoSyncProcess.ValueName, OldSupervisor(oldExecutable));
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(writes.Count == 1 && (string?)key.GetValue(AutoSyncProcess.ValueName) == Selected(executable),
            "an old supervisor command targeting another build is repointed to the selected executable");
        // The verified canonical command is the completion marker: later launches never rewrite it.
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(oldExecutable, messages.Add);
        Require(writes.Count == 0 && (string?)key.GetValue(AutoSyncProcess.ValueName) == Selected(executable) &&
            messages.Count == 0, "a canonical working registration is preserved with no write or diagnostic");
    }

    private static void CheckOldGuiMigration(RegistryKey key, string oldExecutable, string executable,
        List<string> messages, WriteCounter writes)
    {
        key.DeleteValue(AutoSyncProcess.ValueName, throwOnMissingValue: false);
        key.SetValue(AutoSyncProcess.LegacyValueName, Legacy(executable));
        Require(!AutoSyncProcess.ReadStartup() && AutoSyncProcess.IsEngineStartupInvocation(executable),
            "the old owned GUI registration is existing opt-in without reading as the supervisor value");
        Require(Equals(key.GetValue(AutoSyncProcess.LegacyValueName), Legacy(executable)),
            "reading the cutover fact never repairs the registration");
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(writes.Count == 1 && (string?)key.GetValue(AutoSyncProcess.ValueName) == Selected(executable) &&
            key.GetValue(AutoSyncProcess.LegacyValueName) is null && messages.Count == 1,
            "an old owned GUI registration migrates to one canonical supervisor write and retires its value");
        key.DeleteValue(AutoSyncProcess.ValueName, throwOnMissingValue: false);
        key.SetValue(AutoSyncProcess.LegacyValueName, Legacy(oldExecutable));
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(writes.Count == 1 && (string?)key.GetValue(AutoSyncProcess.ValueName) == Selected(executable) &&
            key.GetValue(AutoSyncProcess.LegacyValueName) is null,
            "an old GUI target that is not the selected build is repointed, not preserved");
    }

    private static void CheckBothValues(RegistryKey key, string oldExecutable, string executable,
        List<string> messages, WriteCounter writes)
    {
        key.SetValue(AutoSyncProcess.ValueName, OldSupervisor(oldExecutable));
        key.SetValue(AutoSyncProcess.LegacyValueName, Legacy(oldExecutable));
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(writes.Count == 1 && (string?)key.GetValue(AutoSyncProcess.ValueName) == Selected(executable) &&
            key.GetValue(AutoSyncProcess.LegacyValueName) is null,
            "both startup values migrate with one canonical write and one owned cleanup");
        // Interrupted canonical-write/cleanup: the canonical value is verified and the old owned value remains.
        key.SetValue(AutoSyncProcess.LegacyValueName, Legacy(oldExecutable));
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(writes.Count == 0 && (string?)key.GetValue(AutoSyncProcess.ValueName) == Selected(executable) &&
            key.GetValue(AutoSyncProcess.LegacyValueName) is null && messages.Count == 0,
            "an interrupted migration resumes only the owned cleanup, never a canonical rewrite");
    }

    private static void CheckForeignAndMalformed(RegistryKey key, string oldExecutable, string executable,
        List<string> messages, WriteCounter writes)
    {
        var values = new object[]
        {
            Legacy(oldExecutable), AutoSyncProcess.StartupCommand(oldExecutable) + " --other", "other.exe", 7,
            string.Empty, Quoted(executable) + " " + AutoSyncProcess.EngineHostArguments,
            Quoted(executable) + " " + AutoSyncProcess.LegacySupervisorArguments,
            Quoted(executable) + " " + AutoSyncProcess.HostArguments,
            Quoted(Path.Combine(Path.GetDirectoryName(executable)!, "Renamed.exe")) + " " + AutoSyncProcess.EngineSupervisorArguments
        };
        foreach (var value in values)
        {
            key.SetValue(AutoSyncProcess.ValueName, value);
            key.SetValue(AutoSyncProcess.LegacyValueName, Legacy(oldExecutable));
            Reset(messages, writes);
            AutoSyncProcess.RepairStartup(executable, messages.Add);
            Require(!AutoSyncProcess.ReadStartup() && Equals(key.GetValue(AutoSyncProcess.ValueName), value) &&
                Equals(key.GetValue(AutoSyncProcess.LegacyValueName), Legacy(oldExecutable)) &&
                writes.Count == 0 && messages.Count == 0,
                "a foreign, malformed or non-string value is never adopted, repaired or allowed to retire another value");
            var refused = false;
            try { AutoSyncProcess.RegisterStartup(executable, true); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused && Equals(key.GetValue(AutoSyncProcess.ValueName), value), "explicit enable preserves foreign values");
            Reset(messages, writes);
            var diagnostic = AutoSyncProcess.RegisterStartup(executable, false);
            Require(Equals(key.GetValue(AutoSyncProcess.ValueName), value) &&
                key.GetValue(AutoSyncProcess.LegacyValueName) is null &&
                diagnostic == EdgeMessages.Text("startup-foreign") && writes.Count == 0,
                "disable preserves foreign values without throwing and retires only the owned legacy value");
        }
        key.DeleteValue(AutoSyncProcess.ValueName, throwOnMissingValue: false);
    }

    private static void CheckWriteFailurePreservesPredecessor(RegistryKey key, string oldExecutable, string executable,
        List<string> messages, WriteCounter writes)
    {
        const string injectedFailure = "Injected registry write failure";
        var hook = AutoSyncProcess.BeforeStartupWriteForTest;
        try
        {
            SetupOldSupervisor(key, oldExecutable);
            AutoSyncProcess.BeforeStartupWriteForTest = () => throw new IOException(injectedFailure);
            Reset(messages, writes);
            AutoSyncProcess.RepairStartup(executable, messages.Add);
            Require(Equals(key.GetValue(AutoSyncProcess.ValueName), OldSupervisor(oldExecutable)) &&
                Equals(key.GetValue(AutoSyncProcess.LegacyValueName), Legacy(oldExecutable)) &&
                messages.Count == 1 && messages[0] == EdgeMessages.Text("startup-repair-failed", injectedFailure) &&
                messages[0].Contains("Retry with autosync install-startup --yes", StringComparison.Ordinal),
                "a failed automatic write keeps the predecessor and reports an actionable retry");
            var refused = false;
            try { AutoSyncProcess.RegisterStartup(executable, true); }
            catch (IOException) { refused = true; }
            Require(refused && Equals(key.GetValue(AutoSyncProcess.ValueName), OldSupervisor(oldExecutable)),
                "a failed explicit registration keeps the predecessor");
        }
        finally { AutoSyncProcess.BeforeStartupWriteForTest = hook; }
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(writes.Count == 1 && (string?)key.GetValue(AutoSyncProcess.ValueName) == Selected(executable) &&
            key.GetValue(AutoSyncProcess.LegacyValueName) is null && messages.Count == 1,
            "the same launch migrates normally after the transient failure");
    }

    private static void CheckWriteBoundaryChanges(RegistryKey key, string oldExecutable, string executable,
        List<string> messages, WriteCounter writes)
    {
        var hook = AutoSyncProcess.BeforeStartupWriteForTest;
        try
        {
            // A serialized competing automatic owner completes the migration before this one commits.
            SetupOldSupervisor(key, oldExecutable);
            Reset(messages, writes);
            AutoSyncProcess.BeforeStartupWriteForTest = () =>
            {
                AutoSyncProcess.BeforeStartupWriteForTest = hook;
                AutoSyncProcess.RepairStartup(executable, messages.Add);
            };
            AutoSyncProcess.RepairStartup(executable, messages.Add);
            Require(writes.Count == 1 && (string?)key.GetValue(AutoSyncProcess.ValueName) == Selected(executable) &&
                messages.Count == 1,
                "a serialized competing owner produces exactly one canonical write and diagnostic");
            // An explicit disable before commit is preserved, not overwritten by the automatic owner.
            SetupOldSupervisor(key, oldExecutable);
            Reset(messages, writes);
            AutoSyncProcess.BeforeStartupWriteForTest = () =>
            {
                AutoSyncProcess.BeforeStartupWriteForTest = hook;
                AutoSyncProcess.RegisterStartup(executable, false);
            };
            AutoSyncProcess.RepairStartup(executable, messages.Add);
            Require(!AutoSyncProcess.ReadStartup() && key.GetValue(AutoSyncProcess.ValueName) is null &&
                key.GetValue(AutoSyncProcess.LegacyValueName) is null && writes.Count == 0 && messages.Count == 0,
                "an explicit disable before the commit is preserved and the automatic owner writes nothing");
            // A foreign value before commit is preserved for the same reason.
            SetupOldSupervisor(key, oldExecutable);
            Reset(messages, writes);
            AutoSyncProcess.BeforeStartupWriteForTest = () =>
            {
                AutoSyncProcess.BeforeStartupWriteForTest = hook;
                key.SetValue(AutoSyncProcess.ValueName, "other.exe");
            };
            AutoSyncProcess.RepairStartup(executable, messages.Add);
            Require(Equals(key.GetValue(AutoSyncProcess.ValueName), "other.exe") && writes.Count == 0 && messages.Count == 0,
                "a foreign value before the commit is preserved and the automatic owner writes nothing");
        }
        finally { AutoSyncProcess.BeforeStartupWriteForTest = hook; }
    }

    private static void CheckEngineStartupInvocation(RegistryKey key, string oldExecutable, string executable)
    {
        key.DeleteValue(AutoSyncProcess.ValueName, throwOnMissingValue: false);
        key.DeleteValue(AutoSyncProcess.LegacyValueName, throwOnMissingValue: false);
        Require(!AutoSyncProcess.IsEngineStartupInvocation(executable), "absent registration is no startup consent");
        key.SetValue(AutoSyncProcess.LegacyValueName, Legacy(executable));
        Require(AutoSyncProcess.IsEngineStartupInvocation(executable) &&
            Equals(key.GetValue(AutoSyncProcess.LegacyValueName), Legacy(executable)) &&
            key.GetValue(AutoSyncProcess.ValueName) is null,
            "the owned GUI value targets the cutover without repairing or writing canonical");
        key.SetValue(AutoSyncProcess.ValueName, AutoSyncProcess.StartupCommand(executable));
        Require(AutoSyncProcess.IsEngineStartupInvocation(executable) && AutoSyncProcess.ReadStartup() &&
            Equals(key.GetValue(AutoSyncProcess.LegacyValueName), Legacy(executable)),
            "a canonical replacement targeting this executable is recognized while reads never clean the owned value");
        key.SetValue(AutoSyncProcess.LegacyValueName, Legacy(oldExecutable));
        key.SetValue(AutoSyncProcess.ValueName, AutoSyncProcess.StartupCommand(oldExecutable));
        Require(!AutoSyncProcess.IsEngineStartupInvocation(executable), "values targeting another executable are not consent");
        key.SetValue(AutoSyncProcess.LegacyValueName, "foreign.exe --background");
        key.SetValue(AutoSyncProcess.ValueName, OldSupervisor(executable));
        Require(!AutoSyncProcess.IsEngineStartupInvocation(executable), "old or foreign startup values are not the cutover fact");
        key.DeleteValue(AutoSyncProcess.ValueName, throwOnMissingValue: false);
        key.DeleteValue(AutoSyncProcess.LegacyValueName, throwOnMissingValue: false);
    }

    private static void CheckMissingTargetRepair(RegistryKey key, string executable, List<string> messages,
        WriteCounter writes)
    {
        key.SetValue(AutoSyncProcess.ValueName, AutoSyncProcess.StartupCommand(
            Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N"), "SyncShark.exe")));
        key.DeleteValue(AutoSyncProcess.LegacyValueName, throwOnMissingValue: false);
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(writes.Count == 1 && (string?)key.GetValue(AutoSyncProcess.ValueName) == Selected(executable) &&
            messages.Count == 1 && messages[0] == EdgeMessages.Text("startup-repointed"),
            "only a missing target is repaired using the shared selection");
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(writes.Count == 0 && messages.Count == 0, "an unchanged launch does not repeat the repair write or diagnostic");
        AutoSyncProcess.RegisterStartup(executable, false);
        Require(!AutoSyncProcess.ReadStartup() && key.GetValue(AutoSyncProcess.ValueName) is null,
            "disabling removes the registration");
        Reset(messages, writes);
        AutoSyncProcess.RepairStartup(executable, messages.Add);
        Require(!AutoSyncProcess.ReadStartup() && writes.Count == 0, "a later launch cannot re-enable startup");
    }

    private static void CheckStoredStartup(string root, string executable)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new SyncAutomationSettings(false, 5, false, true));
        Require(!json.Contains("StartWithWindows", StringComparison.OrdinalIgnoreCase), "the unread startup flag is not persisted");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        // Write the old on-disk shape through the same envelope/checksum owner as production.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            "{\"Enabled\":true,\"IntervalMinutes\":7,\"RunOnStartup\":false,\"StartWithWindows\":true}");
        new DualSlotJsonStore<System.Text.Json.JsonElement>(instance.StateAuthority, SyncSharkRuntimePaths.AutomationStateName)
            .SaveAsync(legacy).GetAwaiter().GetResult();
        var store = new SyncAutomationStore(instance.StateAuthority);
        var loaded = store.Load();
        Require(loaded.Enabled && loaded.IntervalMinutes == 7 && !loaded.StartWithWindows && !AutoSyncProcess.ReadStartup(),
            "old stored startup true loads but cannot enable absent registry startup");
        AutoSyncProcess.RegisterStartup(executable, true);
        Require(!store.Load().StartWithWindows && AutoSyncProcess.ReadStartup(),
            "registry remains the startup authority independently of the old stored flag");
        AutoSyncProcess.RegisterStartup(executable, false);
    }

    // AUDIT-D1-rev5 section A: the seven startup values live in the shared catalog with exact wording.
    private static void CheckStartupCatalogValues()
    {
        Require(EdgeMessages.Text("startup-owner-notice") ==
            "Another process owns Sync Shark's state; this launch did not become ready. For an Engine owner, " +
            "check engine host status and, if you choose to stop it, use engine host stop --yes. For a legacy owner, " +
            "check autosync status and use autosync stop. Retry after the owner releases the state. Automatic " +
            "relaunch will retry while supervision is running.",
            "the owner notice names both owner generations, the Engine --yes stop and the legacy stop route");
        Require(EdgeMessages.Text("startup-verification-failed") ==
            "The Windows startup entry could not be verified after writing.",
            "a failed verification reports its exact diagnostic");
        Require(EdgeMessages.Text("startup-removed") == "Removed Sync Shark logon startup; running processes are unchanged.",
            "removal states that running processes are untouched");
        Require(EdgeMessages.Text("startup-running-build") ==
            "Registering the running build. Keep this executable in place, or rerun autosync install-startup --yes after installing an update.",
            "registration of the running build carries the --yes guidance");
        Require(EdgeMessages.Text("startup-registered") ==
            "Registered per-user Engine logon startup with crash supervision.",
            "registration names the Engine destination and its supervision");
        Require(EdgeMessages.Text("startup-repointed") ==
            "Updated Engine logon startup using the shared executable selection for an owned legacy registration or a missing target. Working canonical Engine targets are preserved.",
            "repointing states the shared selection and canonical preservation");
        Require(EdgeMessages.Text("startup-repair-failed", "detail-fixture") ==
            "Engine logon startup could not be updated: detail-fixture Retry with autosync install-startup --yes; automatic syncing can continue.",
            "the repair diagnostic fills its detail and ends with the --yes retry guidance");
        var refused = false;
        try { EdgeMessages.Text("startup-owner-notice-missing"); }
        catch (InvalidDataException) { refused = true; }
        Require(refused, "an absent startup key fails loudly instead of substituting different wording");
    }

    // AUDIT-D1-rev5 section B: one shared warning on the error-5 fallback, built before the sink check.
    private static void CheckBreakawayWarning()
    {
        const string warning = "This launcher prohibits job breakaway. Starting without a console; its job may " +
            "still stop Sync Shark's background host or supervisor when the launcher closes. Per-user logon " +
            "startup avoids this launcher's job.";
        // AUDIT-D1-rev5 section P retains the pre-change literal as the test-only comparison baseline.
        const string previousWarning = "This launcher prohibits job breakaway. Starting without a console; its job " +
            "may still stop AutoSync when the launcher closes. Per-user logon startup avoids this launcher's job.";
        Require(EdgeMessages.Text("startup-job-breakaway") == warning, "the breakaway warning is the exact shared text");
        var builds = 0;
        string Build() { builds++; return EdgeMessages.Text("startup-job-breakaway"); }
        ProgressGuard.Report((Action<string>?)null, Build());
        Require(builds == 1, "the warning text is built even when no sink is attached");
        string? delivered = null;
        ProgressGuard.Report(text => delivered = text, Build());
        Require(delivered == warning && builds == 2, "an attached sink receives the warning unchanged");
        // Cold is one direct parse of the embedded catalog: this suite is already warm, so it is the Load
        // component, not the process's first catalog use.
        using var catalogStream = EmbeddedCatalog();
        var coldBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var coldStart = Stopwatch.GetTimestamp();
        var cold = MessageCatalog.Load(catalogStream);
        var coldMicroseconds = Stopwatch.GetElapsedTime(coldStart).TotalMicroseconds;
        var coldBytes = GC.GetAllocatedBytesForCurrentThread() - coldBytesBefore;
        Require(cold.Message("startup-job-breakaway") == warning, "a fresh catalog load resolves the same warning");
        // Warm: repeated shared lookups against the retained literal, same process and profile.
        var catalogBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var catalogStart = Stopwatch.GetTimestamp();
        var catalogTotal = 0;
        for (var index = 0; index < MeasurementRepetitions; index++)
            catalogTotal += EdgeMessages.Text("startup-job-breakaway").Length;
        var catalogMicroseconds = Stopwatch.GetElapsedTime(catalogStart).TotalMicroseconds;
        var catalogBytes = GC.GetAllocatedBytesForCurrentThread() - catalogBytesBefore;
        var literalStart = Stopwatch.GetTimestamp();
        var literalTotal = 0;
        for (var index = 0; index < MeasurementRepetitions; index++)
            literalTotal += previousWarning.Length;
        var literalMicroseconds = Stopwatch.GetElapsedTime(literalStart).TotalMicroseconds;
        Require(catalogTotal == warning.Length * MeasurementRepetitions &&
            literalTotal == previousWarning.Length * MeasurementRepetitions,
            "the warm cost probes execute every repetition");
        Console.WriteLine(
            $"WARNING-MEASURE coldLoadUs={coldMicroseconds:F1} coldLoadBytes={coldBytes} " +
            $"warmCatalogUs={catalogMicroseconds:F1} warmCatalogBytes={catalogBytes} " +
            $"warmLiteralUs={literalMicroseconds:F1} repetitions={MeasurementRepetitions} profile=debug single-thread");
    }

    private static Stream EmbeddedCatalog() =>
        typeof(MessageCatalog).Assembly.GetManifestResourceStream("SyncShark.EdgeMessages.json")
        ?? throw new InvalidOperationException("The embedded edge message catalog is missing.");

    private static void CheckVersionWarning(string executable)
    {
        Require(SyncSharkRuntimePaths.HasTestProductRoot, "version warning uses the CLI suite isolated product root");
        var installed = SyncSharkRuntimePaths.InstalledExecutablePath;
        Require(!File.Exists(installed), "version fixture does not replace an existing executable");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "fixture without version metadata");
        try
        {
            var messages = new List<string>();
            AutoSyncProcess.SelectStartupExecutable(executable, warning: messages.Add);
            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).ProductVersion;
            var installedVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(installed).ProductVersion;
            Require(!string.Equals(version, installedVersion, StringComparison.Ordinal) &&
                messages.Contains(EdgeMessages.Text("startup-versions-differ", $"{executable} ({version}) / {installed} ({installedVersion})")),
                "version warning includes both executable paths and versions");
        }
        finally { File.Delete(installed); }
    }

    private static void SetupOldSupervisor(RegistryKey key, string oldExecutable)
    {
        key.SetValue(AutoSyncProcess.ValueName, OldSupervisor(oldExecutable));
        key.SetValue(AutoSyncProcess.LegacyValueName, Legacy(oldExecutable));
    }

    private static void Reset(List<string> messages, WriteCounter writes)
    {
        messages.Clear();
        writes.Count = 0;
    }

    private static string Quoted(string executable) => $"\"{executable}\"";

    private static string Legacy(string executable) => $"{Quoted(executable)} {AutoSyncProcess.LegacyArguments}";

    private static string OldSupervisor(string executable) => $"{Quoted(executable)} {AutoSyncProcess.SupervisorArguments}";

    private static string Selected(string executable) =>
        AutoSyncProcess.StartupCommand(AutoSyncProcess.SelectStartupExecutable(executable));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class WriteCounter
    {
        internal int Count;
    }
}
