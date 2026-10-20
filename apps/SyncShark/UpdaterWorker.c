#include "DiscoveryTrust.h"
#include "EmbeddedSetupHost.h"
#include <shellapi.h>
#include <stdio.h>
#include <wchar.h>

// Separate, argument-free task action. The signed native image admits its fixed
// protected location and split token before loading only its embedded resources.
int WINAPI wWinMain(HINSTANCE instance,HINSTANCE previous,LPWSTR arguments,int show) {
    (void)instance;(void)previous;(void)arguments;(void)show;
    int argc=0;WCHAR **argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    if(!argv)return ERROR_INVALID_PARAMETER;
    LocalFree(argv);if(argc!=1)return ERROR_INVALID_PARAMETER;
    if(!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32) || !SetDllDirectoryW(L""))return ERROR_ACCESS_DENIED;
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    const WCHAR *fixed=L"C:\\Program Files\\Massdex\\SyncShark\\SyncSharkUpdater.exe";
    WCHAR self[4096],actual[4096];DWORD n=GetModuleFileNameW(NULL,self,ARRAYSIZE(self));
    if(!n || n>=ARRAYSIZE(self) || CompareStringOrdinal(self,-1,fixed,-1,TRUE)!=CSTR_EQUAL)return ERROR_ACCESS_DENIED;
    HANDLE high=NULL,medium=NULL;TOKEN_LINKED_TOKEN linked={0};TOKEN_ELEVATION elevated={0};
    HANDLE *held=NULL;DWORD count=0,size=0,session=0,error=ERROR_ACCESS_DENIED;
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&high))goto done;
    if(!GetTokenInformation(high,TokenElevation,&elevated,sizeof(elevated),&size) || !elevated.TokenIsElevated){error=ERROR_ELEVATION_REQUIRED;goto done;}
    if(!GetTokenInformation(high,TokenLinkedToken,&linked,sizeof(linked),&size))goto done;
    if(!DuplicateTokenEx(linked.LinkedToken,TOKEN_QUERY,NULL,SecurityIdentification,TokenImpersonation,&medium))goto done;
    if(!GetTokenInformation(medium,TokenElevation,&elevated,sizeof(elevated),&size) || elevated.TokenIsElevated)goto done;
    if(!ProcessIdToSessionId(GetCurrentProcessId(),&session) || !session)goto done;
    if((error=ssd_hold_protected_file(fixed,medium,&held,&count)))goto done;
    n=GetFinalPathNameByHandleW(held[count-1],actual,ARRAYSIZE(actual),FILE_NAME_NORMALIZED|VOLUME_NAME_DOS);
    if(!n || n>=ARRAYSIZE(actual) || wcsncmp(actual,L"\\\\?\\",4) || CompareStringOrdinal(actual+4,-1,fixed,-1,TRUE)!=CSTR_EQUAL){error=ERROR_ACCESS_DENIED;goto done;}
    if((error=ssd_verify_product_signature(fixed,held[count-1])))goto done;
    char invocation[128];snprintf(invocation,sizeof(invocation),"Invoke-SyncSharkProtectedUpdate -BootstrapProcessId %lu",GetCurrentProcessId());
    // Transaction effects and their recovery own cancellation. Do not kill the
    // privileged child halfway through publication merely to meet a host timeout.
    error=ssd_run_embedded_setup(NULL,invocation,INFINITE,FALSE);
done:
    ssd_release_trusted_app(held,count);
    if(medium)CloseHandle(medium);if(linked.LinkedToken)CloseHandle(linked.LinkedToken);if(high)CloseHandle(high);
    return (int)error;
}
