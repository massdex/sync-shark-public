using System.Diagnostics;
using System.IO;
using Windows.Storage.Provider;

namespace Massdex.SyncShark;

internal static class ExplorerOwnership
{
    // This is a shell-registration handover, never a forced CFAPI-provider removal.
    // Keep a held root and a continuously armed change detector from inspection through effect.
    internal sealed class Inspection : IDisposable
    {
        internal readonly ExplorerIntegration.CompetingRoot Target;
        internal readonly StorageProviderSyncRootInfo Registration;
        private readonly PilotDirectoryLease _root;
        private readonly FileSystemWatcher _watcher;
        private int _changed;
        private bool _inspected;
        private readonly string _registrationFingerprint;
        internal int Files { get; private set; }
        internal int Folders { get; private set; }
        internal bool Removed { get; private set; }
        internal bool RemovalUncertain { get; private set; }

        internal Inspection(ExplorerIntegration.CompetingRoot target)
        {
            if (target.Provider != "Dropbox" || !target.HasRestorableShellMetadata)
                throw new InvalidOperationException("This registration cannot be safely handed over here. Use the original provider to disconnect or move its folder.");
            Target = target;
            RequireProviderStopped();
            RequireNoNativeProvider(target.Path);
            Registration = StorageProviderSyncRootManager.GetCurrentSyncRoots().SingleOrDefault(item => item.Id == target.Id)
                ?? throw new IOException("The registration changed. Refresh the pair status first.");
            if (!SamePath(Registration.Path.Path, target.Path)) throw new IOException("The registered folder changed.");
            _registrationFingerprint = Fingerprint(Registration);
            _root = PilotDirectoryLease.Open(target.Path);
            try
            {
                _watcher = new FileSystemWatcher(_root.Path) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Security };
                _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Deleted += Changed;
                _watcher.Renamed += (_, _) => Interlocked.Exchange(ref _changed, 1);
                _watcher.Error += (_, _) => Interlocked.Exchange(ref _changed, 1);
                _watcher.EnableRaisingEvents = true;
            }
            catch { _root.Dispose(); throw; }
        }

        private void Changed(object sender, FileSystemEventArgs e) => Interlocked.Exchange(ref _changed, 1);
        internal void Inspect(CancellationToken token)
        {
            if (_inspected) throw new InvalidOperationException("Start a fresh inspection before another handover.");
            var folders = new Stack<string>(); folders.Push(_root.Path);
            var watch = Stopwatch.StartNew();
            while (folders.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (watch.Elapsed > TimeSpan.FromMinutes(2)) throw new IOException("Inspection reached its time limit. Nothing was unregistered.");
                // A queued directory may have been replaced after its parent was enumerated.
                // Hold it against rename/deletion and reject every reparse before traversing it.
                using var directory = PilotDirectoryLease.Open(folders.Pop());
                if (directory.Identity.VolumeSerialNumber != _root.Identity.VolumeSerialNumber ||
                    PilotPath.IsUnsafeAttributes(File.GetAttributes(directory.Path)))
                    throw new IOException("The inspected directory changed or crossed a filesystem boundary.");
                VerifyUnchanged();
                foreach (var child in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (Files + Folders >= 500_000) throw new IOException("Inspection reached its safe item limit. Nothing was unregistered.");
                    var attributes = File.GetAttributes(child);
                    // No content is opened. Legacy placeholders, offline files, junctions and any
                    // unreadable subtree all require the original provider's supported preparation.
                    if (PilotPath.IsUnsafeAttributes(attributes))
                        throw new IOException("The parent folder still contains a cloud placeholder or link: " + Path.GetRelativePath(_root.Path, child) +
                            ". Make it fully local or move the sync pair outside this root using Dropbox first.");
                    if ((attributes & FileAttributes.Directory) != 0) { Folders++; folders.Push(child); }
                    else Files++;
                }
                directory.VerifyBinding();
            }
            VerifyUnchanged();
            _inspected = true;
        }

        internal void Remove()
        {
            if (!_inspected || Removed || RemovalUncertain) throw new InvalidOperationException("A fresh completed inspection is required before removal.");
            VerifyUnchanged();
            var current = StorageProviderSyncRootManager.GetCurrentSyncRoots().SingleOrDefault(item => item.Id == Target.Id);
            if (current is null || Fingerprint(current) != _registrationFingerprint)
                throw new IOException("Dropbox's registration changed after inspection. Nothing was unregistered.");
            // A failing native call may already have changed the shell. Never replay it blindly.
            RemovalUncertain = true;
            StorageProviderSyncRootManager.Unregister(Target.Id);
            Removed = true;
            _root.VerifyBinding(); RequireNoNativeProvider(Target.Path);
            if (ExplorerIntegration.CompetingRoots(Target.Path).Any(item => item.Id == Target.Id))
                throw new IOException("Windows has not confirmed removal. Refresh the pair status; no files were removed.");
            RemovalUncertain = false;
        }

        internal void Restore()
        {
            if (RemovalUncertain) throw new InvalidOperationException("Windows did not confirm the previous change. Refresh ownership status and use Dropbox to restore its registration; no automatic retry was made.");
            if (!Removed) return;
            RequireProviderStopped(); _root.VerifyBinding(); RequireNoNativeProvider(Target.Path);
            if (StorageProviderSyncRootManager.GetCurrentSyncRoots().Any(item => SamePath(item.Path.Path, Target.Path) ||
                    item.Path.Path.StartsWith(Target.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A new registration now uses this folder. It was not overwritten.");
            StorageProviderSyncRootManager.Register(Registration);
            if (!ExplorerIntegration.CompetingRoots(Target.Path).Any(item => item.Id == Target.Id && SamePath(item.Path, Target.Path)))
                throw new IOException("Windows has not confirmed restoration. Reopen Dropbox to restore its own registration.");
            Removed = false;
        }

        private static string Fingerprint(StorageProviderSyncRootInfo value)
        {
            byte[] context = [];
            if (value.Context is not null) Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(value.Context, out context);
            return System.Text.Json.JsonSerializer.Serialize(new {
                value.Id, Path = value.Path.Path, value.Version, value.DisplayNameResource, value.IconResource,
                value.AllowPinning, value.ShowSiblingsAsGroup, value.HydrationPolicy, value.HydrationPolicyModifier,
                value.PopulationPolicy, value.InSyncPolicy, value.HardlinkPolicy,
                Context = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(context)),
                Properties = value.StorageProviderItemPropertyDefinitions.Select(item => new { item.Id, item.DisplayNameResource }).ToArray()
            });
        }

        private void VerifyUnchanged()
        {
            _root.VerifyBinding(); RequireProviderStopped(); RequireNoNativeProvider(Target.Path);
            if (Volatile.Read(ref _changed) != 0) throw new IOException("The folder changed during inspection. Inspect it again before handing it over.");
        }
        private static void RequireNoNativeProvider(string path)
        {
            if (WindowsCloudFilesProvider.ReadRegisteredSyncRootFileId(path) is not null)
                throw new InvalidOperationException("Dropbox still owns a native cloud-files root. Disconnect or move this folder using Dropbox first; Sync Shark will not force-remove a provider that may hold online-only files.");
        }
        private static void RequireProviderStopped()
        {
            var processes = Process.GetProcessesByName("Dropbox");
            try { if (processes.Length > 0) throw new InvalidOperationException("Quit the Dropbox desktop app, then inspect again. Pausing sync is not enough."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        public void Dispose() { _watcher.Dispose(); _root.Dispose(); }
    }
}
