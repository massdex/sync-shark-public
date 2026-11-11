using Massdex.SyncShark;

/// <summary>Checks for AtomicFile (`--atomic-file-checks`), written by AtomicFile's writer. The retry waits are
/// driven by a test seam, so the policy is asserted without sleeping; the holds are real Windows handles.</summary>
internal static class AtomicFileChecks
{
    // Four waits follow the first attempt, matching the shipped defaults in engine-settings.json.
    private static readonly AtomicReplacePolicy Policy = new(
        [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(400)]);

    // The two Windows reports a brief external hold produces.
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int AccessDenied = unchecked((int)0x80070005);

    internal static void Run()
    {
        var root = Directory.CreateTempSubdirectory("ss135-");
        try
        {
            var source = Path.Combine(root.FullName, "source");
            var destination = Path.Combine(root.FullName, "destination");

            // A destination held by another handle, released after the first failure, is published on retry.
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");
            var retries = 0;
            using (var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                AtomicFile.Move(source, destination, overwrite: true, Policy, default, delay =>
                {
                    retries++;
                    Require(delay == Policy.Delays[0], "the first wait is the policy's first delay");
                    held.Dispose();
                });
            }
            Require(retries == 1, "one transient hold was retried");
            Require(File.ReadAllText(destination) == "new", "the retry published the completed file");
            Require(!File.Exists(source), "a published move leaves no source behind");

            // A hold that never clears is bounded, and the original failure reaches the caller unchanged.
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");
            var failures = 0;
            Exception? thrown = null;
            using (var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var direct = Capture(() => File.Move(source, destination, overwrite: true));
                try
                {
                    AtomicFile.Move(source, destination, overwrite: true, Policy, default, _ => failures++);
                }
                catch (Exception error)
                {
                    thrown = error;
                }
                Require(Held(direct), "a plain single move fails while the destination is held");
                Require(failures == Policy.Delays.Count, "the retry bound is the policy's wait count");
                Require(thrown is not null && direct is not null && thrown.GetType() == direct.GetType()
                    && thrown.HResult == direct.HResult, "the bound rethrows the original failure unchanged");
            }
            Require(File.ReadAllText(destination) == "old", "a failed publication leaves the old complete file");
            Require(File.ReadAllText(source) == "new", "a failed publication leaves the source in place");

            // A pre-cancelled caller publishes nothing: no attempt is made and both files stay as they were.
            var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var attempted = false;
            var stopped = false;
            try
            {
                AtomicFile.Move(source, destination, overwrite: true, Policy, canceled.Token, _ => attempted = true);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
            }
            Require(stopped && !attempted, "a pre-cancelled caller never attempts the move");
            Require(File.ReadAllText(destination) == "old" && File.ReadAllText(source) == "new",
                "a pre-cancelled caller leaves both files unchanged");

            // A non-transient failure is never retried. A missing destination folder gives the
            // deterministic DirectoryNotFoundException on every supported Windows runtime.
            File.WriteAllText(source, "new");
            var absent = Path.Combine(root.FullName, "missing", "destination");
            var retried = false;
            var refused = false;
            try
            {
                AtomicFile.Move(source, absent, overwrite: false, Policy, default, _ => retried = true);
            }
            catch (DirectoryNotFoundException)
            {
                refused = true;
            }
            Require(refused && !retried, "a directory-not-found failure is not retried");

            // A create-only publish onto an occupied name is not a transient hold: the existing file wins.
            var occupied = Path.Combine(root.FullName, "occupied");
            File.WriteAllText(source, "new");
            File.WriteAllText(occupied, "existing");
            var occupiedRetried = false;
            var occupiedRefused = false;
            try
            {
                AtomicFile.Move(source, occupied, overwrite: false, Policy, default, _ => occupiedRetried = true);
            }
            catch (IOException)
            {
                occupiedRefused = true;
            }
            Require(occupiedRefused && !occupiedRetried, "an occupied create-only destination is not retried");
            Require(File.ReadAllText(occupied) == "existing", "the occupied destination keeps its file");

            // A permanent access denial is bounded like a hold: the same failure, the same bound, no publication.
            var denied = Path.Combine(root.FullName, "denied");
            File.WriteAllText(source, "new");
            File.WriteAllText(denied, "read-only");
            File.SetAttributes(denied, FileAttributes.ReadOnly);
            try
            {
                var denialRetries = 0;
                Exception? denial = null;
                try
                {
                    AtomicFile.Move(source, denied, overwrite: true, Policy, default, _ => denialRetries++);
                }
                catch (UnauthorizedAccessException error)
                {
                    denial = error;
                }
                Require(denial is { HResult: AccessDenied } && denialRetries == Policy.Delays.Count,
                    "an access denial is retried only to the same bound");
                Require(File.ReadAllText(denied) == "read-only", "a denied publication keeps the destination");
                Require(File.ReadAllText(source) == "new", "a denied publication keeps the source");
            }
            finally
            {
                File.SetAttributes(denied, FileAttributes.Normal);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>The two failures a brief external hold produces: access denied, or a sharing violation.</summary>
    private static bool Held(Exception? failure) =>
        failure is UnauthorizedAccessException ||
        failure is IOException { HResult: SharingViolation or AccessDenied };

    private static Exception? Capture(Action move)
    {
        try
        {
            move();
        }
        catch (Exception error)
        {
            return error;
        }
        return null;
    }

    private static void Require(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
