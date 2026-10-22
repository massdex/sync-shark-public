#include "DiscoveryProtocol.h"
#include "DiscoveryTrust.h"
#include <sddl.h>
#include <shellapi.h>
#include <stdio.h>
#include <wchar.h>
#include <strsafe.h>

static const WCHAR worker_path[]=L"C:\\Program Files\\Massdex\\SyncShark\\SyncSharkDiscovery.exe";
static const WCHAR manifest_path[]=L"C:\\Program Files\\Massdex\\SyncShark\\discovery-manifest.json";
static const WCHAR setup_journal[]=L"C:\\Program Files\\Massdex\\SyncShark\\discovery-setup.json";
DWORD ssd_setup_run(const WCHAR *mode,DWORD pid,HANDLE medium,DWORD session);
static BOOL setup_incomplete(void) {
    if(GetFileAttributesW(setup_journal)!=INVALID_FILE_ATTRIBUTES)return TRUE;
    return GetLastError()!=ERROR_FILE_NOT_FOUND;
}
typedef struct {
    HANDLE pipe,cancel,done,watcher,client_token,medium,high,manifest_file,volume;
    HANDLE *protected_worker,*protected_manifest,*app;
    DWORD worker_handles,manifest_handles,app_handles,session;
    ULONGLONG deadline,last_sent,progress_sequence;
    volatile LONG64 last_progress;
    /* First observed stop trigger, not necessarily the underlying cause. Written by the watcher and by a failed pipe
       write, read once when the stop is mapped to the fixed numeric diagnostic. */
    volatile LONG cancel_origin;
    DWORD last_stage;
    SsdRequest request;SsdAdmission *admission;SsdCatalog *catalog;
    SsdTrustManifest manifest;WCHAR user[192],logon[192];DWORD emitted;
} Worker;
static DWORD io(HANDLE pipe,BOOL write,BYTE *bytes,DWORD count,ULONGLONG deadline) {
    while(count) {
        OVERLAPPED op={0};op.hEvent=CreateEventW(NULL,TRUE,FALSE,NULL);if(!op.hEvent)return GetLastError();
        DWORD transferred=0;BOOL ok=write?WriteFile(pipe,bytes,count,&transferred,&op):ReadFile(pipe,bytes,count,&transferred,&op);
        DWORD error=ok?0:GetLastError();
        if(error==ERROR_IO_PENDING) {
            ULONGLONG now=GetTickCount64();DWORD timeout=now>=deadline?0:(DWORD)min(deadline-now,120000);
            DWORD waited=WaitForSingleObject(op.hEvent,timeout);
            if(waited==WAIT_OBJECT_0)error=GetOverlappedResult(pipe,&op,&transferred,FALSE)?0:GetLastError();
            else {error=waited==WAIT_TIMEOUT?ERROR_TIMEOUT:GetLastError();CancelIoEx(pipe,&op);DWORD ignored;GetOverlappedResult(pipe,&op,&ignored,TRUE);}
        }
        CloseHandle(op.hEvent);if(error)return error;if(!transferred)return ERROR_BROKEN_PIPE;
        bytes+=transferred;count-=transferred;
    }
    return ERROR_SUCCESS;
}
static DWORD token_sid(HANDLE token,TOKEN_INFORMATION_CLASS kind,WCHAR *out,DWORD capacity) {
    BYTE bytes[65536];DWORD size;if(!GetTokenInformation(token,kind,bytes,sizeof(bytes),&size))return GetLastError();
    PSID sid=NULL;
    if(kind==TokenUser)sid=((TOKEN_USER*)bytes)->User.Sid;
    else {TOKEN_GROUPS *groups=(TOKEN_GROUPS*)bytes;for(DWORD i=0;i<groups->GroupCount;i++)if((groups->Groups[i].Attributes&SE_GROUP_LOGON_ID)==SE_GROUP_LOGON_ID)sid=groups->Groups[i].Sid;}
    WCHAR *text=NULL;if(!sid || !ConvertSidToStringSidW(sid,&text))return ERROR_NO_SUCH_LOGON_SESSION;
    DWORD error=wcslen(text)>=capacity?ERROR_BUFFER_OVERFLOW:0;if(!error)wcscpy(out,text);LocalFree(text);return error;
}
static DWORD ordinary_linked_token(Worker *w) {
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&w->high))return GetLastError();
    TOKEN_ELEVATION elevation;DWORD size;
    if(!GetTokenInformation(w->high,TokenElevation,&elevation,sizeof(elevation),&size) || !elevation.TokenIsElevated)return ERROR_ELEVATION_REQUIRED;
    TOKEN_LINKED_TOKEN linked={0};
    if(!GetTokenInformation(w->high,TokenLinkedToken,&linked,sizeof(linked),&size))return GetLastError();
    // A split token's linked handle can be identification-only. This reference
    // is used solely for identity and AccessCheck, never filesystem impersonation.
    // Requesting SecurityImpersonation here fails with 1346 on the real UAC path.
    BOOL ok=DuplicateTokenEx(linked.LinkedToken,TOKEN_QUERY,NULL,SecurityIdentification,TokenImpersonation,&w->medium);
    DWORD error=ok?0:GetLastError();CloseHandle(linked.LinkedToken);if(error)return error;
    if(!GetTokenInformation(w->medium,TokenElevation,&elevation,sizeof(elevation),&size) || elevation.TokenIsElevated)return ERROR_ACCESS_DENIED;
    if(!ProcessIdToSessionId(GetCurrentProcessId(),&w->session) || !w->session)return ERROR_NO_SUCH_LOGON_SESSION;
    if((error=token_sid(w->medium,TokenUser,w->user,ARRAYSIZE(w->user))))return error;
    return token_sid(w->medium,TokenGroups,w->logon,ARRAYSIZE(w->logon));
}
static DWORD authenticate(Worker *w) {
    ULONG pid,session;if(!GetNamedPipeClientProcessId(w->pipe,&pid) || !GetNamedPipeClientSessionId(w->pipe,&session))return GetLastError();
    if(session!=w->session)return ERROR_ACCESS_DENIED;
    if(!ImpersonateNamedPipeClient(w->pipe))return GetLastError();
    BOOL captured=OpenThreadToken(GetCurrentThread(),TOKEN_QUERY|TOKEN_IMPERSONATE,TRUE,&w->client_token);
    DWORD error=captured?0:GetLastError();if(!RevertToSelf())TerminateProcess(GetCurrentProcess(),ERROR_CANNOT_IMPERSONATE);
    if(error)return error;
    WCHAR user[192],logon[192];
    if((error=token_sid(w->client_token,TokenUser,user,ARRAYSIZE(user))) || (error=token_sid(w->client_token,TokenGroups,logon,ARRAYSIZE(logon))))return error;
    if(wcscmp(user,w->user) || wcscmp(logon,w->logon))return ERROR_ACCESS_DENIED;
    TOKEN_STATISTICS actual,expected;DWORD size;
    if(!GetTokenInformation(w->client_token,TokenStatistics,&actual,sizeof(actual),&size) ||
        !GetTokenInformation(w->medium,TokenStatistics,&expected,sizeof(expected),&size) ||
        memcmp(&actual.AuthenticationId,&expected.AuthenticationId,sizeof(LUID)))return ERROR_ACCESS_DENIED;
    return ssd_hold_signed_caller(pid,&w->app,&w->app_handles);
}
static DWORD connect_client(Worker *w) {
    WCHAR name[512],security[768];
    if(swprintf(name,ARRAYSIZE(name),L"\\\\.\\pipe\\SyncSharkDiscovery-%ls-%lu",w->user,w->session)<0)return ERROR_INVALID_NAME;
    /* BA ownership is assignable by the elevated split token. The medium token's
       Administrators group is deny-only, so it gains no implicit owner rights. */
    if(swprintf(security,ARRAYSIZE(security),L"O:BAG:BAD:P(A;;GA;;;SY)(A;;0x0012019b;;;%ls)S:(ML;;NW;;;ME)",w->logon)<0)return ERROR_INVALID_SECURITY_DESCR;
    PSECURITY_DESCRIPTOR descriptor=NULL;
    if(!ConvertStringSecurityDescriptorToSecurityDescriptorW(security,SDDL_REVISION_1,&descriptor,NULL))return GetLastError();
    SECURITY_ATTRIBUTES attributes={sizeof(attributes),descriptor,FALSE};
    w->pipe=CreateNamedPipeW(name,PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED|FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,65536,65536,0,&attributes);
    DWORD error=w->pipe==INVALID_HANDLE_VALUE?GetLastError():0;LocalFree(descriptor);if(error)return error;
    OVERLAPPED op={0};op.hEvent=CreateEventW(NULL,TRUE,FALSE,NULL);if(!op.hEvent)return GetLastError();
    BOOL connected=ConnectNamedPipe(w->pipe,&op);error=connected?0:GetLastError();
    if(error==ERROR_PIPE_CONNECTED)error=0;
    else if(error==ERROR_IO_PENDING) {
        DWORD wait=WaitForSingleObject(op.hEvent,15000),bytes;
        if(wait==WAIT_OBJECT_0)error=GetOverlappedResult(w->pipe,&op,&bytes,FALSE)?0:GetLastError();
        else {error=wait==WAIT_TIMEOUT?ERROR_TIMEOUT:GetLastError();CancelIoEx(w->pipe,&op);GetOverlappedResult(w->pipe,&op,&bytes,TRUE);}
    }
    CloseHandle(op.hEvent);return error;
}
static DWORD read_request(Worker *w) {
    BYTE header[SSD_WIRE_HEADER_BYTES];DWORD error=io(w->pipe,FALSE,header,sizeof(header),w->deadline);
    if(error || (error=ssd_request_header(&w->request,header,sizeof(header))) || (error=authenticate(w)))return error;
    for(DWORD i=0;i<w->request.root_count;i++) {
        BYTE fields[12];if((error=io(w->pipe,FALSE,fields,sizeof(fields),w->deadline)))return error;
        ULONGLONG id;DWORD units;memcpy(&id,fields,8);memcpy(&units,fields+8,4);
        if(units<3 || units>32760)return ERROR_INVALID_DATA;
        BYTE *path=HeapAlloc(GetProcessHeap(),0,(SIZE_T)units*2);if(!path)return ERROR_NOT_ENOUGH_MEMORY;
        error=io(w->pipe,FALSE,path,units*2,w->deadline);
        if(!error)error=ssd_request_add_root(&w->request,i,id,path,units);
        SecureZeroMemory(path,(SIZE_T)units*2);HeapFree(GetProcessHeap(),0,path);if(error)return error;
    }
    if(w->request.operation==2) {
        if(w->request.root_count!=1)return ERROR_INVALID_DATA;
        if((error=io(w->pipe,FALSE,(BYTE*)&w->request.journal_maximum,8,w->deadline)))return error;
        if(!w->request.journal_maximum || w->request.journal_maximum>8589934592ULL)return ERROR_INVALID_DATA;
    }
    return ssd_admit_roots(w->request.roots,w->request.root_count,w->client_token,&w->admission);
}
/* Root admission is mandatory. Only grow an observed existing journal; an external
   administrator can race deletion, so verify the journal identity after the change too. */
static DWORD grow_journal(Worker *w) {
    WCHAR mount[50],fs[32];DWORD serial=0,component=0,flags=0,bytes=0,error=0;
    HANDLE volume=CreateFileW(ssd_admission_volume(w->admission),GENERIC_READ|GENERIC_WRITE,
        FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,NULL,OPEN_EXISTING,0,NULL);
    if(volume==INVALID_HANDLE_VALUE){error=GetLastError();goto reply;}
    if(!GetVolumeInformationByHandleW(volume,NULL,0,&serial,&component,&flags,fs,ARRAYSIZE(fs)))
        {error=GetLastError();goto reply;}
    if(serial!=w->request.volume_serial || wcscmp(fs,L"NTFS")){error=ERROR_NOT_SUPPORTED;goto reply;}
    /* The admitted GUID, not an elevated-token drive-letter mapping. */
    if(FAILED(StringCchCopyW(mount,ARRAYSIZE(mount),ssd_admission_volume(w->admission))) ||
        FAILED(StringCchCatW(mount,ARRAYSIZE(mount),L"\\"))){error=ERROR_INVALID_DRIVE;goto reply;}
    ULARGE_INTEGER free_bytes;
    if(!GetDiskFreeSpaceExW(mount,&free_bytes,NULL,NULL)){error=GetLastError();goto reply;}
    USN_JOURNAL_DATA current={0};
    if(!DeviceIoControl(volume,FSCTL_QUERY_USN_JOURNAL,NULL,0,&current,sizeof(current),&bytes,NULL))error=GetLastError();
    else {
        ULONGLONG delta=current.AllocationDelta>536870912ULL?current.AllocationDelta:536870912ULL;
        ULONGLONG target=0;
        error=ssd_journal_target(w->request.journal_maximum,current.MaximumSize,current.AllocationDelta,free_bytes.QuadPart,&target);
        if(!error) {
            CREATE_USN_JOURNAL_DATA request={0};request.MaximumSize=target;request.AllocationDelta=delta;
            if(!DeviceIoControl(volume,FSCTL_CREATE_USN_JOURNAL,&request,sizeof(request),NULL,0,&bytes,NULL))error=GetLastError();
            else {
                USN_JOURNAL_DATA after={0};
                if(!DeviceIoControl(volume,FSCTL_QUERY_USN_JOURNAL,NULL,0,&after,sizeof(after),&bytes,NULL))error=GetLastError();
                else if(after.UsnJournalID!=current.UsnJournalID)error=ERROR_JOURNAL_ENTRY_DELETED;
            }
        }
    }
reply:
    if(volume!=INVALID_HANDLE_VALUE)CloseHandle(volume);
    BYTE reply[16]={0};DWORD length=12,kind=5;
    memcpy(reply,&length,4);memcpy(reply+4,&kind,4);memcpy(reply+8,&error,4);
    DWORD transport=io(w->pipe,TRUE,reply,sizeof(reply),w->deadline);if(transport)return transport;
    BYTE ack=0;transport=io(w->pipe,FALSE,&ack,1,w->deadline);
    return transport?transport:(ack==0xa5?0:ERROR_INVALID_DATA);
}
static DWORD WINAPI watch_client(void *context) {
    Worker *w=context;
    while(WaitForSingleObject(w->done,100)==WAIT_TIMEOUT) {
        DWORD available=0;
        ULONGLONG now=GetTickCount64(),last=(ULONGLONG)InterlockedCompareExchange64(&w->last_progress,0,0);
        /* The four stop conditions are preserved exactly, including the order in
           which PeekNamedPipe is reached, and the first one that fires is recorded
           so the exit code distinguishes observed triggers instead of collapsing to 1223. */
        DWORD origin=0;
        if(now>=w->deadline)origin=SSD_STOP_ORIGIN_DEADLINE;
        else if(now-last>=120000)origin=SSD_STOP_ORIGIN_IDLE;
        else if(!PeekNamedPipe(w->pipe,NULL,0,NULL,&available,NULL))origin=SSD_STOP_ORIGIN_PEER;
        else if(available)origin=SSD_STOP_ORIGIN_CLIENT_INPUT;
        if(origin) {if(!w->cancel_origin)InterlockedCompareExchange(&w->cancel_origin,(LONG)origin,0);SetEvent(w->cancel);break;}
    }
    return 0;
}
static BOOL emit_progress(void *context,DWORD stage,ULONGLONG records_read) {
    Worker *w=context;ULONGLONG now=GetTickCount64();
    InterlockedExchange64(&w->last_progress,(LONG64)now);
    w->progress_sequence++;
    if(stage==w->last_stage && now-w->last_sent<1000)return TRUE;
    BYTE bytes[28];DWORD count=ssd_reply_progress(stage,w->progress_sequence,records_read,bytes,sizeof(bytes));
    DWORD error=count ? io(w->pipe,TRUE,bytes,count,w->deadline) : ERROR_INVALID_DATA;
    if(error) {if(!w->cancel_origin)InterlockedCompareExchange(&w->cancel_origin,(LONG)(error==ERROR_TIMEOUT?SSD_STOP_ORIGIN_PIPE_TIMEOUT:SSD_STOP_ORIGIN_PIPE_BROKEN),0);return FALSE;}
    w->last_sent=now;w->last_stage=stage;return TRUE;
}
static BOOL emit_item(void *context,const SsdItem *item) {
    Worker *w=context;BYTE bytes[SSD_WIRE_MAX_FRAME+4];DWORD count=ssd_reply_item(item,bytes,sizeof(bytes));
    DWORD error=count ? io(w->pipe,TRUE,bytes,count,w->deadline) : ERROR_INVALID_DATA;
    if(error) {if(!w->cancel_origin)InterlockedCompareExchange(&w->cancel_origin,(LONG)(error==ERROR_TIMEOUT?SSD_STOP_ORIGIN_PIPE_TIMEOUT:SSD_STOP_ORIGIN_PIPE_BROKEN),0);return FALSE;}
    InterlockedExchange64(&w->last_progress,(LONG64)GetTickCount64());w->emitted++;return TRUE;
}
static DWORD run(Worker *w) {
    DWORD error=ordinary_linked_token(w);if(error)return error;
    if(setup_incomplete())return ERROR_BUSY;
    WCHAR actual[4096];DWORD n=GetModuleFileNameW(NULL,actual,ARRAYSIZE(actual));
    if(!n || n>=ARRAYSIZE(actual) || CompareStringOrdinal(actual,-1,worker_path,-1,TRUE)!=CSTR_EQUAL)return ERROR_ACCESS_DENIED;
    if((error=ssd_hold_protected_file(worker_path,w->medium,&w->protected_worker,&w->worker_handles)) ||
        (error=ssd_hold_protected_file(manifest_path,w->medium,&w->protected_manifest,&w->manifest_handles)) ||
        (error=ssd_load_trust_manifest(manifest_path,&w->manifest,&w->manifest_file)))return error;
    if(setup_incomplete())return ERROR_BUSY;
    if(wcscmp(w->manifest.user_sid,w->user))return ERROR_ACCESS_DENIED;
    HANDLE self=w->protected_worker[w->worker_handles-1];BYTE hash[32];
    if((error=ssd_hash_file(self,hash)))return error;
    if(memcmp(hash,w->manifest.worker_sha256,32))return ERROR_INVALID_IMAGE_HASH;
    if((error=ssd_verify_product_signature(worker_path,self)) || (error=connect_client(w)) || (error=read_request(w)))return error;
    if(w->request.operation==2)return grow_journal(w);
    // The longer scan allowance starts only after authenticated, bounded root
    // admission. It does not extend the original request/handshake timeout.
    w->deadline=GetTickCount64()+SSD_MAX_DURATION_MS;
    w->cancel=CreateEventW(NULL,TRUE,FALSE,NULL);w->done=CreateEventW(NULL,TRUE,FALSE,NULL);
    if(!w->cancel || !w->done)return GetLastError();
    InterlockedExchange64(&w->last_progress,(LONG64)GetTickCount64());
    w->watcher=CreateThread(NULL,0,watch_client,w,0,NULL);if(!w->watcher)return GetLastError();
    w->volume=CreateFileW(ssd_admission_volume(w->admission),GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,
        NULL,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,NULL);
    if(w->volume==INVALID_HANDLE_VALUE)return GetLastError();
    w->catalog=ssd_catalog_create(w->cancel,w->deadline);if(!w->catalog)return ssd_catalog_failure_code(NULL,ERROR_NOT_ENOUGH_MEMORY);
    BYTE bytes[128];DWORD count=ssd_reply_header(&w->request,bytes,sizeof(bytes));
    if(!count || (error=io(w->pipe,TRUE,bytes,count,w->deadline)))return error?error:ERROR_INVALID_DATA;
    ssd_catalog_set_progress(w->catalog,emit_progress,w);
    USN_JOURNAL_DATA before={0},after={0};
    /* A cancellation keeps stage, category and Win32 exactly where they were and
       adds the first observed trigger above them; every other error is unchanged. */
    if((error=ssd_catalog_read_volume(w->catalog,w->volume,w->request.roots,w->request.root_count,&before,&after)))return ssd_catalog_stop_code(w->catalog,error,(DWORD)w->cancel_origin);
    if((error=ssd_catalog_project(w->catalog,w->request.roots,w->request.root_count,w->client_token,emit_item,w)))return ssd_catalog_stop_code(w->catalog,error,(DWORD)w->cancel_origin);
    if(WaitForSingleObject(w->cancel,0)==WAIT_OBJECT_0)return ssd_catalog_stop_code(w->catalog,ERROR_CANCELLED,(DWORD)w->cancel_origin);
    SetEvent(w->done);
    count=ssd_reply_end(w->emitted,&before,&after,bytes,sizeof(bytes));
    if(!count || (error=io(w->pipe,TRUE,bytes,count,w->deadline)))return error?error:ERROR_INVALID_DATA;
    /* The client acknowledges only after reading/validating the terminal frame.
       This avoids an unbounded FlushFileBuffers and unread-data loss on close. */
    if((error=io(w->pipe,FALSE,bytes,1,w->deadline)))return error;
    return bytes[0]==0xa5?ERROR_SUCCESS:ERROR_INVALID_DATA;
}
static void close_worker(Worker *w) {
    if(w->done)SetEvent(w->done);if(w->cancel)SetEvent(w->cancel);
    if(w->watcher){WaitForSingleObject(w->watcher,INFINITE);CloseHandle(w->watcher);}
    if(w->pipe && w->pipe!=INVALID_HANDLE_VALUE){CancelIoEx(w->pipe,NULL);DisconnectNamedPipe(w->pipe);CloseHandle(w->pipe);}
    if(w->volume && w->volume!=INVALID_HANDLE_VALUE)CloseHandle(w->volume);
    ssd_catalog_destroy(w->catalog);ssd_admission_close(w->admission);ssd_request_free(&w->request);
    ssd_release_trusted_app(w->app,w->app_handles);
    ssd_release_trusted_app(w->protected_manifest,w->manifest_handles);ssd_release_trusted_app(w->protected_worker,w->worker_handles);
    HANDLE handles[]={w->cancel,w->done,w->client_token,w->medium,w->high,w->manifest_file};
    for(DWORD i=0;i<ARRAYSIZE(handles);i++)if(handles[i] && handles[i]!=INVALID_HANDLE_VALUE)CloseHandle(handles[i]);
    SecureZeroMemory(w,sizeof(*w));HeapFree(GetProcessHeap(),0,w);
}
int WINAPI wWinMain(HINSTANCE instance,HINSTANCE previous,LPWSTR arguments,int show) {
    /* FoD pairs are excluded from discovery and these records are only path hints that the exposed
       ordinary scanner revalidates. TODO: call RtlSetProcessPlaceholderCompatibilityMode(PHCM_EXPOSE_PLACEHOLDERS) here before file I/O if that changes. */
    (void)instance;(void)previous;(void)arguments;(void)show;
    int argc=0;WCHAR **argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    if(!argv)return ERROR_INVALID_PARAMETER;
    DWORD setupPid=0;
    if(argc!=1) {
        if(argc!=3 || (wcscmp(argv[1],L"Configure") && wcscmp(argv[1],L"Restore") && wcscmp(argv[1],L"Remove") &&
            wcscmp(argv[1],L"RuntimeInstall") && wcscmp(argv[1],L"RuntimeRemove") && wcscmp(argv[1],L"RuntimeRollback")) || !argv[2][0]){LocalFree(argv);return ERROR_INVALID_PARAMETER;}
        for(const WCHAR *at=argv[2];*at;at++) {if(*at<L'0' || *at>L'9' || setupPid>214748364 || (setupPid==214748364 && *at>L'7')){LocalFree(argv);return ERROR_INVALID_PARAMETER;}setupPid=setupPid*10+(*at-L'0');}
        if(!setupPid){LocalFree(argv);return ERROR_INVALID_PARAMETER;}
    }
    if(!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32) || !SetDllDirectoryW(L""))return ERROR_ACCESS_DENIED;
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX);
    Worker *w=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(*w));if(!w)return ERROR_NOT_ENOUGH_MEMORY;
    w->deadline=GetTickCount64()+120000;DWORD error;
    if(argc==1)error=run(w);
    else {error=ordinary_linked_token(w);if(!error)error=ssd_setup_run(argv[1],setupPid,w->medium,w->session);}
    LocalFree(argv);close_worker(w);return (int)error;
}
