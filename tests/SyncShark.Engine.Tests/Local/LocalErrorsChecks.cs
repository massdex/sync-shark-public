using L = Massdex.SyncShark.Engine.Local;
namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks the LocalErrors adapter against observed outcomes.</summary>
public static class LocalErrorsChecks
{
    public static Task RunAsync()
    {
        var locked=new System.ComponentModel.Win32Exception(32); var denied=new UnauthorizedAccessException();
        Check.Require(L.LocalErrors.IsFileFailure(locked) && !L.LocalErrors.IsManagedIoFailure(locked) && L.LocalErrors.IsManagedIoFailure(denied),"Native and managed failure families conflated");
        Check.Require(L.LocalErrors.Cause(new IOException("locked",locked),TextKey.ReadLocalFailed)==TextKey.FailLocalInUse && L.LocalErrors.Cause(new System.ComponentModel.Win32Exception(112),TextKey.ReadLocalFailed)==TextKey.FailLocalNoSpace,"Native cause lost");
        Check.Require(L.LocalErrors.RootReadCause(denied)==TextKey.RootLocalAccessDenied && L.LocalErrors.RootReadCause(new ChangedUnderneathException("root"))==TextKey.RootLocalIdentityChanged && L.LocalErrors.RootReadCause(new ArgumentException()) is null,"Root-read cause lost");
        Check.Require(L.LocalErrors.RootReadCause(new DirectoryNotFoundException())==TextKey.LocalRootMissing && L.LocalErrors.RootReadCause(new System.ComponentModel.Win32Exception(2))==TextKey.LocalRootMissing,"A missing root was not classified as missing");
        return Task.CompletedTask;
    }
}
