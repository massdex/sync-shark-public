#include "DiscoveryTrust.h"
#include "EmbeddedSetupHost.h"
#include <shlobj.h>
#include <sddl.h>
#include <wincrypt.h>
#include <wchar.h>
#include <stdio.h>
#include <string.h>

static DWORD setup_caller(DWORD pid,HANDLE medium,DWORD session,const WCHAR *app,HANDLE *held) {
    HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,pid),token=NULL;
    if(!process)return GetLastError();DWORD error=ERROR_ACCESS_DENIED,actualSession,size;TOKEN_STATISTICS actual,expected;TOKEN_ELEVATION elevation;
    if(ProcessIdToSessionId(pid,&actualSession) && actualSession==session && OpenProcessToken(process,TOKEN_QUERY,&token) &&
        GetTokenInformation(token,TokenStatistics,&actual,sizeof(actual),&size) && GetTokenInformation(medium,TokenStatistics,&expected,sizeof(expected),&size) &&
        !memcmp(&actual.AuthenticationId,&expected.AuthenticationId,sizeof(LUID)) &&
        GetTokenInformation(token,TokenElevation,&elevation,sizeof(elevation),&size) && !elevation.TokenIsElevated)error=0;
    (void)app;if(token)CloseHandle(token);if(error)CloseHandle(process);else *held=process;return error;
}
DWORD ssd_setup_run(const WCHAR *mode,DWORD pid,HANDLE medium,DWORD session) {
    // Fixed setup verbs and a positive decimal PID only. Runtime placement
    // authenticates its retained signed release contract inside the embedded code.
    // No arbitrary script, input path or process command is accepted by the EXE.
    BOOL runtime=!wcscmp(mode,L"RuntimeInstall") || !wcscmp(mode,L"RuntimeRemove") || !wcscmp(mode,L"RuntimeRollback");
    DWORD error=ERROR_ACCESS_DENIED;WCHAR self[4096],actual[4096],expected[4096];PWSTR local=NULL;
    HANDLE selfFile=INVALID_HANDLE_VALUE,*appHeld=NULL,parent=NULL;
    DWORD appCount=0;
    SsdTrustManifest *binding=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(*binding));
    if(!binding)return ERROR_NOT_ENOUGH_MEMORY;
    DWORD n=GetModuleFileNameW(NULL,self,ARRAYSIZE(self));if(!n || n>=ARRAYSIZE(self))goto done;
    selfFile=CreateFileW(self,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    BY_HANDLE_FILE_INFORMATION info;
    if(selfFile==INVALID_HANDLE_VALUE || !GetFileInformationByHandle(selfFile,&info) || info.nNumberOfLinks!=1 ||
        (info.dwFileAttributes&(FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_OFFLINE|FILE_ATTRIBUTE_DIRECTORY)))goto done;
    n=GetFinalPathNameByHandleW(selfFile,actual,ARRAYSIZE(actual),FILE_NAME_NORMALIZED|VOLUME_NAME_DOS);
    if(!n || n>=ARRAYSIZE(actual) || wcsncmp(actual,L"\\\\?\\",4) || wcslen(actual+4)>=ARRAYSIZE(binding->app_directory))goto done;
    wcscpy(binding->app_directory,actual+4);WCHAR *end=wcsrchr(binding->app_directory,L'\\');
    if(!end || _wcsicmp(end,L"\\SyncSharkDiscovery.exe"))goto done;*end=0;
    WCHAR *version=wcsrchr(binding->app_directory,L'\\');if(!version || !version[1])goto done;version++;
    unsigned dots=0;BOOL digit=FALSE;
    for(WCHAR *at=version;*at;at++){if(*at==L'.'){if(!digit || ++dots>2)goto done;digit=FALSE;}else if(*at>=L'0'&&*at<=L'9')digit=TRUE;else goto done;}
    if(dots!=2 || !digit || FAILED(SHGetKnownFolderPath(&FOLDERID_LocalAppData,KF_FLAG_DEFAULT,NULL,&local)) || wcslen(local)+wcslen(version)+40>=ARRAYSIZE(expected))goto done;
    if(runtime)swprintf(expected,ARRAYSIZE(expected),L"%ls\\Massdex\\SyncShark\\versions\\%ls",local,version);
    else swprintf(expected,ARRAYSIZE(expected),L"C:\\Program Files\\Massdex\\SyncShark\\versions\\%ls",version);
    if(CompareStringOrdinal(expected,-1,binding->app_directory,-1,TRUE)!=CSTR_EQUAL ||
        (error=ssd_verify_product_signature(actual+4,selfFile)) || (error=setup_caller(pid,medium,session,binding->app_directory,&parent)))goto done;
    const WCHAR *members[]={L"SyncShark.exe",L"SyncShark.dll",L"SyncShark.Core.dll",L"SyncShark.deps.json",L"SyncShark.runtimeconfig.json",L"SyncSharkDiscovery.exe"};
    binding->count=ARRAYSIZE(members);
    for(DWORD i=0;i<binding->count;i++) {
        wcscpy(binding->members[i].path,members[i]);swprintf(expected,ARRAYSIZE(expected),L"%ls\\%ls",binding->app_directory,members[i]);
        HANDLE file=CreateFileW(expected,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,NULL);
        if(file==INVALID_HANDLE_VALUE){error=GetLastError();goto done;}
        error=ssd_hash_file(file,binding->members[i].sha256);CloseHandle(file);if(error)goto done;
    }
    if(!runtime && (error=ssd_hold_trusted_app(binding,pid,&appHeld,&appCount)))goto done;
    char invocation[256];snprintf(invocation,sizeof(invocation),"%s -Mode %ls -ParentProcessId %lu -AppDirectory $app",
        runtime?"Invoke-SyncSharkProtectedRuntimeSetup":"Invoke-SyncSharkDiscoverySetup",mode,pid);
    error=ssd_run_embedded_setup(binding->app_directory,invocation,180000,TRUE);
done:;
    HANDLE handles[]={selfFile,parent};
    for(DWORD i=0;i<ARRAYSIZE(handles);i++)if(handles[i] && handles[i]!=INVALID_HANDLE_VALUE)CloseHandle(handles[i]);
    if(local)CoTaskMemFree(local);ssd_release_trusted_app(appHeld,appCount);SecureZeroMemory(binding,sizeof(*binding));HeapFree(GetProcessHeap(),0,binding);return error;
}
