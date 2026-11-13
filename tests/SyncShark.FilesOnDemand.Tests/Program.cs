// This host exercises the native Files On-Demand surface: expose placeholder attributes before any
// directory or file I/O. Non-Windows hosts make no native call.
Massdex.SyncShark.WindowsPlaceholderCompatibility.Expose();

if (args is ["native", var cleanupRoot] && cleanupRoot.StartsWith("cleanup:", StringComparison.Ordinal))
    await FilesOnDemandNativeChecks.CleanupAsync(cleanupRoot[8..]);
else if (args is ["native", var nativeRoot])
    await FilesOnDemandNativeChecks.RunAsync(nativeRoot);
else if (args is ["storage", var storageRoot])
{
    await FilesOnDemandStorageChecks.RunAsync(storageRoot);
    CloudFilesLifetimeChecks.Run();
}
else
    throw new ArgumentException("Choose native or storage and a fresh disposable directory.");
