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
