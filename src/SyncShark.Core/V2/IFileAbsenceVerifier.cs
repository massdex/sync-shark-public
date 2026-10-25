namespace Massdex.SyncShark.V2;

internal interface IFileAbsenceVerifier : IDisposable
{
    FileAbsence Check(ItemState item);
}
