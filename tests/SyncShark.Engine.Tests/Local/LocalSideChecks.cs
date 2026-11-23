namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Contract checks for LocalSide.</summary>
public static class LocalSideChecks
{
    public static Task ArmedOfflineStageAsync() => throw new NotImplementedException();
    public static Task WriterPreconditionsAsync() => throw new NotImplementedException();

    public static Task LongPathRecoveryAsync() => throw new NotImplementedException();
    public static Task ReceiptWalkAsync() => throw new NotImplementedException();
    public static Task SelectedReceiptAsync() => throw new NotImplementedException();
    public static Task ReplacedInventoryRootAsync() => throw new NotImplementedException();
    public static Task SettledPrefixAsync() => throw new NotImplementedException();

    /// <summary>Check late read failure through the module interface.</summary>
    public static Task LateReadFailureAsync() => throw new NotImplementedException();
    /// <summary>Check excluded first alias through the module interface.</summary>
    public static Task ExcludedFirstAliasAsync() => throw new NotImplementedException();
    /// <summary>Check published file record through the module interface.</summary>
    public static Task PublishedFileRecordAsync() => throw new NotImplementedException();
    /// <summary>Check bound inventory through the module interface.</summary>
    public static Task BoundInventoryAsync() => throw new NotImplementedException();
    /// <summary>Compare parent handles retained during publication at different directory depths.</summary>
    public static Task ParentPinCountAsync() => throw new NotImplementedException();
    /// <summary>Verifies the LocalSide contract through the module surface.</summary>
    public static Task RunAsync() => throw new NotImplementedException();
}
