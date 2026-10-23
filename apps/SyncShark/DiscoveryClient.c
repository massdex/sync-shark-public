#include "DiscoveryTrust.h"
#include <sddl.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

typedef struct {
    HANDLE pipe,process,token,server_token,manifest_file;
    HANDLE *worker,*manifest,*app;DWORD worker_count,manifest_count,app_count;
    SsdTrustManifest identity;
} ClientLease;
static const WCHAR helper[]=L"C:\\Program Files\\Massdex\\SyncShark\\SyncSharkDiscovery.exe";
static const WCHAR metadata[]=L"C:\\Program Files\\Massdex\\SyncShark\\discovery-manifest.json";
void WINAPI SyncSharkCloseDiscoveryPipe(void *value) {
    ClientLease *lease=value;if(!lease)return;
    if(lease->pipe && lease->pipe!=INVALID_HANDLE_VALUE){CancelIoEx(lease->pipe,NULL);CloseHandle(lease->pipe);}
    HANDLE handles[]={lease->process,lease->token,lease->server_token,lease->manifest_file};
    for(DWORD i=0;i<ARRAYSIZE(handles);i++)if(handles[i] && handles[i]!=INVALID_HANDLE_VALUE)CloseHandle(handles[i]);
    ssd_release_trusted_app(lease->worker,lease->worker_count);ssd_release_trusted_app(lease->manifest,lease->manifest_count);
    ssd_release_trusted_app(lease->app,lease->app_count);SecureZeroMemory(lease,sizeof(*lease));HeapFree(GetProcessHeap(),0,lease);
}
static DWORD user_sid(HANDLE token,WCHAR *text,DWORD capacity) {
    BYTE bytes[1024];DWORD count;WCHAR *sid=NULL;
    if(!GetTokenInformation(token,TokenUser,bytes,sizeof(bytes),&count) || !ConvertSidToStringSidW(((TOKEN_USER*)bytes)->User.Sid,&sid))return GetLastError();
    DWORD error=wcslen(sid)>=capacity?ERROR_BUFFER_OVERFLOW:0;if(!error)wcscpy(text,sid);LocalFree(sid);return error;
}
static DWORD connect_verified(ClientLease *lease) {
    HANDLE primary=NULL;DWORD size,error=0;TOKEN_ELEVATION elevated;
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY|TOKEN_DUPLICATE,&primary))return GetLastError();
    BOOL copied=DuplicateTokenEx(primary,TOKEN_QUERY|TOKEN_IMPERSONATE,NULL,SecurityImpersonation,TokenImpersonation,&lease->token);
    error=copied?0:GetLastError();CloseHandle(primary);if(error)return error;
    if(!GetTokenInformation(lease->token,TokenElevation,&elevated,sizeof(elevated),&size) || elevated.TokenIsElevated)return ERROR_ACCESS_DENIED;
    WCHAR user[192],server_user[192],name[512],image[4096];DWORD session,server_session;
    if((error=user_sid(lease->token,user,ARRAYSIZE(user))))return error;
    if(!ProcessIdToSessionId(GetCurrentProcessId(),&session) || !session)return ERROR_NO_SUCH_LOGON_SESSION;
    if(swprintf(name,ARRAYSIZE(name),L"\\\\.\\pipe\\SyncSharkDiscovery-%ls-%lu",user,session)<0)return ERROR_INVALID_NAME;
    lease->pipe=CreateFileW(name,0x0012019b,0,NULL,OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED|SECURITY_SQOS_PRESENT|SECURITY_IMPERSONATION|SECURITY_EFFECTIVE_ONLY,NULL);
    if(lease->pipe==INVALID_HANDLE_VALUE)return GetLastError();
    ULONG pid;if(!GetNamedPipeServerProcessId(lease->pipe,&pid) || !GetNamedPipeServerSessionId(lease->pipe,&server_session))return GetLastError();
    if(session!=server_session)return ERROR_ACCESS_DENIED;
    lease->process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,pid);if(!lease->process)return GetLastError();
    if(!OpenProcessToken(lease->process,TOKEN_QUERY,&lease->server_token))return GetLastError();
    if(!GetTokenInformation(lease->server_token,TokenElevation,&elevated,sizeof(elevated),&size) || !elevated.TokenIsElevated)return ERROR_ACCESS_DENIED;
    if((error=user_sid(lease->server_token,server_user,ARRAYSIZE(server_user))))return error;
    if(wcscmp(user,server_user))return ERROR_ACCESS_DENIED;
    size=ARRAYSIZE(image);if(!QueryFullProcessImageNameW(lease->process,0,image,&size))return GetLastError();
    if(CompareStringOrdinal(image,-1,helper,-1,TRUE)!=CSTR_EQUAL)return ERROR_ACCESS_DENIED;
    if((error=ssd_hold_protected_file(helper,lease->token,&lease->worker,&lease->worker_count)) ||
        (error=ssd_hold_protected_file(metadata,lease->token,&lease->manifest,&lease->manifest_count)) ||
        (error=ssd_load_trust_manifest(metadata,&lease->identity,&lease->manifest_file)))return error;
    if(wcscmp(user,lease->identity.user_sid))return ERROR_ACCESS_DENIED;
    HANDLE file=lease->worker[lease->worker_count-1];BYTE digest[32];
    if((error=ssd_hash_file(file,digest)))return error;
    if(memcmp(digest,lease->identity.worker_sha256,32))return ERROR_INVALID_IMAGE_HASH;
    if((error=ssd_verify_product_signature(helper,file)))return error;
    if((error=ssd_hold_signed_caller(GetCurrentProcessId(),&lease->app,&lease->app_count)))return error;
    return WaitForSingleObject(lease->process,0)==WAIT_TIMEOUT?ERROR_SUCCESS:ERROR_PROCESS_ABORTED;
}
DWORD WINAPI SyncSharkOpenDiscoveryPipe(HANDLE *pipe,void **context) {
    if(!pipe || !context)return ERROR_INVALID_PARAMETER;*pipe=INVALID_HANDLE_VALUE;*context=NULL;
    ClientLease *lease=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(*lease));if(!lease)return ERROR_NOT_ENOUGH_MEMORY;
    DWORD error=connect_verified(lease);
    if(error)SyncSharkCloseDiscoveryPipe(lease);else {*pipe=lease->pipe;*context=lease;}return error;
}
DWORD WINAPI SyncSharkPrepareDiscovery(void **context) {
    if(!context)return ERROR_INVALID_PARAMETER;*context=NULL;
    ClientLease *lease=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(*lease));if(!lease)return ERROR_NOT_ENOUGH_MEMORY;
    HANDLE primary=NULL;DWORD error=0,size;TOKEN_ELEVATION elevation;
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY|TOKEN_DUPLICATE,&primary)){error=GetLastError();goto done;}
    BOOL copied=DuplicateTokenEx(primary,TOKEN_QUERY|TOKEN_IMPERSONATE,NULL,SecurityImpersonation,TokenImpersonation,&lease->token);
    error=copied?0:GetLastError();CloseHandle(primary);if(error)goto done;
    if(!GetTokenInformation(lease->token,TokenElevation,&elevation,sizeof(elevation),&size) || elevation.TokenIsElevated){error=ERROR_ACCESS_DENIED;goto done;}
    if((error=ssd_hold_protected_file(helper,lease->token,&lease->worker,&lease->worker_count)) ||
        (error=ssd_hold_protected_file(metadata,lease->token,&lease->manifest,&lease->manifest_count)) ||
        (error=ssd_load_trust_manifest(metadata,&lease->identity,&lease->manifest_file)))goto done;
    WCHAR user[192];if((error=user_sid(lease->token,user,ARRAYSIZE(user))))goto done;
    if(wcscmp(user,lease->identity.user_sid)){error=ERROR_ACCESS_DENIED;goto done;}
    HANDLE file=lease->worker[lease->worker_count-1];BYTE digest[32];if((error=ssd_hash_file(file,digest)))goto done;
    if(memcmp(digest,lease->identity.worker_sha256,32)){error=ERROR_INVALID_IMAGE_HASH;goto done;}
    error=ssd_verify_product_signature(helper,file);
done:
    if(error)SyncSharkCloseDiscoveryPipe(lease);else *context=lease;return error;
}
