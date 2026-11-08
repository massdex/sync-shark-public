using System.Runtime.InteropServices;
using Massdex.SyncShark.Engine.Local;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for LocalStaged (`--engine-checks local`), written by LocalStaged's writer.</summary>
public static class LocalStagedChecks
{
    public static void Run()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var source = new Entry(nameof(LocalStagedChecks), false, default, DateTimeOffset.UnixEpoch,
                "", "", "", null);
            var target = Path.Combine(folder.FullName, source.Path);
            File.WriteAllText(target, nameof(LocalStagedChecks));
            Check.Require(typeof(LocalStaged).GetProperty(nameof(LocalStaged.State))?.SetMethod
                is not { IsPublic: true }, nameof(LocalStaged.State));
            var states = new[] { LocalStageState.Staged, LocalStageState.PendingOwned, LocalStageState.Published };
            foreach (var state in states)
            {
                foreach (var durablyOwned in new[] { true, false })
                {
                    foreach (var exists in new[] { true, false })
                        CheckDisposal(folder.FullName, source, state, durablyOwned, exists);
                }
            }
            CheckDeleteFailure(folder.FullName, source);
            CheckReplacementSurvives(folder.FullName, source);
            Check.Require(File.ReadAllText(target) == nameof(LocalStagedChecks), nameof(LocalStaged.Path));
        }
        finally
        {
            folder.Delete(true);
        }
    }

    private static void CheckDisposal(string folder, Entry source, LocalStageState state, bool durablyOwned,
        bool exists)
    {
        var stagePath = Path.Combine(folder, Path.GetRandomFileName());
        StageProof proof;
        if (exists)
        {
            File.WriteAllText(stagePath, nameof(LocalStagedChecks));
            proof = ProofFor(stagePath);
        }
        else
        {
            proof = new StageProof(stagePath, "volume", "file", 0, "hash");
        }
        var active = new HashSet<string> { stagePath };
        int releases = default;
        var staged = new LocalStaged(source.Path, source, proof, () => durablyOwned, path =>
        {
            Check.Require(path == stagePath && active.Remove(path), nameof(LocalStaged.StagePath));
            releases++;
        });
        IStaged owned = staged;
        IRecoveryStage recovery = staged;
        Check.Require(ReferenceEquals(recovery.Proof, proof) && staged.StagePath == proof.Path,
            nameof(LocalStaged.Proof));
        Check.Require(!owned.ConsumesSourceOnPublish, nameof(IStaged.ConsumesSourceOnPublish));
        Check.Require(owned.Path == source.Path && ReferenceEquals(staged.Source, source),
            nameof(LocalStaged.Source));
        Check.Require(staged.State == LocalStageState.Staged, nameof(LocalStageState.Staged));
        if (state != LocalStageState.Staged) staged.State = state;

        Check.Require(owned.DisposeAsync().IsCompletedSuccessfully, nameof(LocalStaged.DisposeAsync));
        Check.Require(releases == 1 && active.Count == 0, nameof(LocalSide.ActiveStages));
        var retained = exists && !(state == LocalStageState.Staged && !durablyOwned);
        Check.Require(File.Exists(stagePath) == retained, state.ToString());
        if (retained)
            Check.Require(File.ReadAllText(stagePath) == nameof(LocalStagedChecks), state.ToString());
    }

    private static void CheckDeleteFailure(string folder, Entry source)
    {
        var stagePath = Path.Combine(folder, Path.GetRandomFileName());
        File.WriteAllText(stagePath, nameof(LocalStagedChecks));
        var proof = new StageProof(stagePath, "volume", "file", nameof(LocalStagedChecks).Length, "hash");
        var active = new HashSet<string> { stagePath };
        int releases = default;
        var staged = new LocalStaged(source.Path, source, proof, () => false, path =>
        {
            Check.Require(path == stagePath && active.Remove(path), nameof(LocalStaged.StagePath));
            releases++;
        });
        using var held = File.Open(stagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Check.Require(staged.DisposeAsync().IsCompletedSuccessfully,
            "A locked stage must not replace the primary error during disposal");
        Check.Require(releases == 1 && active.Count == 0, nameof(LocalSide.ActiveStages));
        Check.Require(File.Exists(stagePath), nameof(LocalStageState.Staged));
    }

    // RT-02: another process replaces the stage while publication is delayed; disposal must bind the proof's
    // identity and leave the replacement occupant in place.
    private static void CheckReplacementSurvives(string folder, Entry source)
    {
        var stagePath = Path.Combine(folder, Path.GetRandomFileName());
        File.WriteAllText(stagePath, nameof(LocalStagedChecks));
        var proof = ProofFor(stagePath);
        var active = new HashSet<string> { stagePath };
        var staged = new LocalStaged(source.Path, source, proof, () => false, path => active.Remove(path));
        File.Delete(stagePath);
        File.WriteAllText(stagePath, "replacement");
        Check.Require(staged.DisposeAsync().IsCompletedSuccessfully, nameof(LocalStaged.DisposeAsync));
        Check.Require(active.Count == 0, nameof(LocalSide.ActiveStages));
        Check.Require(File.Exists(stagePath) && File.ReadAllText(stagePath) == "replacement",
            "A replacement occupant at the stage path survives disposal");
    }

    // The same proof the writer builds: the created file's native identity, not the name.
    private static StageProof ProofFor(string stagePath)
    {
        using var handle = CreateFileW(stagePath, 0, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information))
            throw new InvalidOperationException("The fixture stage identity could not be read.");
        var identity = new PilotDirectoryIdentity(information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        return LocalSide.ProofOf(identity, stagePath, new FileInfo(stagePath).Length, "hash");
    }

    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
