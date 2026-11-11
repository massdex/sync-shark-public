using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

/// <summary>Captures the real Save submission without binding folders or registering cloud storage.</summary>
public class FolderStorageConsentHost : DispatchProxy
{
    internal ISyncHost Inner { get; set; } = null!;
    internal string? Method { get; private set; }
    internal SyncDirectoryStorage? DefaultStorage { get; private set; }
    internal bool? FilesOnDemandEnabled { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name is nameof(ISyncPairHost.BindPairAsync) or nameof(ISyncPairHost.BindEndpointPairAsync))
        {
            Method = targetMethod.Name;
            (DefaultStorage, FilesOnDemandEnabled) = args![0] switch
            {
                SyncPairBindingRequest request => (request.DefaultStorage, request.FilesOnDemandEnabled),
                SyncPair pair => (pair.DefaultStorage, pair.FilesOnDemandEnabled),
                _ => throw new InvalidDataException("The Save submission has an unexpected type.")
            };
            return Task.FromException<SyncPairBindingResult>(new IOException("Synthetic binding stopped before effects."));
        }
        try { return targetMethod!.Invoke(Inner, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}
