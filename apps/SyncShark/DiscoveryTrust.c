#include "DiscoveryTrust.h"
#include <bcrypt.h>
#include <wincrypt.h>
#include <wintrust.h>
#include <softpub.h>
#include <aclapi.h>
#include <string.h>
#include <wchar.h>
#include "SyncSharkSignerIdentity.h"

DWORD ssd_hash_file(HANDLE file,BYTE digest[32]) {
    BCRYPT_ALG_HANDLE algorithm=NULL;BCRYPT_HASH_HANDLE hash=NULL;BYTE buffer[65536];DWORD bytes,error=0;
    LARGE_INTEGER zero={0},length;
    if(!GetFileSizeEx(file,&length) || length.QuadPart<1 || length.QuadPart>512LL*1024*1024 ||
        !SetFilePointerEx(file,zero,NULL,FILE_BEGIN))return ERROR_INVALID_DATA;
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,NULL,0)<0 ||
        BCryptCreateHash(algorithm,&hash,NULL,0,NULL,0,0)<0){error=ERROR_INVALID_DATA;goto done;}
    for(;;) {
        if(!ReadFile(file,buffer,sizeof(buffer),&bytes,NULL)){error=GetLastError();break;}
        if(!bytes)break;
        if(BCryptHashData(hash,buffer,bytes,0)<0){error=ERROR_INVALID_DATA;break;}
    }
    if(!error && BCryptFinishHash(hash,digest,32,0)<0)error=ERROR_INVALID_DATA;
done:
    SecureZeroMemory(buffer,sizeof(buffer));if(hash)BCryptDestroyHash(hash);
    if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);return error;
}
static DWORD verify_signature(const WCHAR *path,HANDLE file,BOOL timestamp_required) {
    /* Executable role only. The legacy certificate remains assigned to the
       MSIX container; it must not authorize a current executable or caller.
       Any listed publisher thumbprint verifies during a signer switch. */
    WINTRUST_FILE_INFO info={.cbStruct=sizeof(info),.pcwszFilePath=path,.hFile=file};
    WINTRUST_DATA data={.cbStruct=sizeof(data),.dwUIChoice=WTD_UI_NONE,.fdwRevocationChecks=WTD_REVOKE_NONE,
        .dwUnionChoice=WTD_CHOICE_FILE,.pFile=&info,.dwStateAction=WTD_STATEACTION_VERIFY,.dwProvFlags=WTD_CACHE_ONLY_URL_RETRIEVAL};
    GUID action=WINTRUST_ACTION_GENERIC_VERIFY_V2;DWORD error=ERROR_INVALID_IMAGE_HASH;
    LONG status=WinVerifyTrust(INVALID_HANDLE_VALUE,&action,&data);
    /* This product's existing self-issued identity is locally pinned, not added
       to Windows trust. Only the isolated untrusted-root condition is allowed. */
    if(status!=0 && status!=CERT_E_UNTRUSTEDROOT)goto done;
    HMODULE module=GetModuleHandleW(L"wintrust.dll");
    CRYPT_PROVIDER_DATA *(WINAPI *provider)(HANDLE)=(void*)GetProcAddress(module,"WTHelperProvDataFromStateData");
    CRYPT_PROVIDER_SGNR *(WINAPI *signer)(CRYPT_PROVIDER_DATA*,DWORD,BOOL,DWORD)=(void*)GetProcAddress(module,"WTHelperGetProvSignerFromChain");
    if(!provider || !signer)goto done;
    CRYPT_PROVIDER_DATA *state=provider(data.hWVTStateData);if(!state)goto done;
    CRYPT_PROVIDER_SGNR *primary=signer(state,0,FALSE,0);
    if(!primary || !primary->csCertChain || !primary->pChainContext || !primary->psSigner ||
        primary->pChainContext->TrustStatus.dwErrorStatus & ~CERT_TRUST_IS_UNTRUSTED_ROOT || !primary->csCounterSigners)goto done;
    BYTE actual[20];DWORD size=sizeof(actual);
    if(!CertGetCertificateContextProperty(primary->pasCertChain[0].pCert,CERT_SHA1_HASH_PROP_ID,actual,&size) ||
        size!=sizeof(actual))goto done;
    BOOL thumbprint_match=FALSE;
    for(DWORD index=0;index<SSD_PUBLISHER_THUMBPRINT_COUNT;index++)
        if(!memcmp(actual,ssd_publisher_thumbprints[index],sizeof(actual))){thumbprint_match=TRUE;break;}
    if(!thumbprint_match)goto done;
    if(!timestamp_required){error=ERROR_SUCCESS;goto done;}
    BOOL rfc3161=FALSE;
    for(DWORD i=0;i<primary->psSigner->UnauthAttrs.cAttr;i++)
        if(!strcmp(primary->psSigner->UnauthAttrs.rgAttr[i].pszObjId,"1.3.6.1.4.1.311.3.3.1"))rfc3161=TRUE;
    if(!rfc3161)goto done;
    for(DWORD i=0;i<primary->csCounterSigners;i++) {
        CRYPT_PROVIDER_SGNR *timestamp=signer(state,0,TRUE,i);
        if(timestamp && !timestamp->dwError && timestamp->pChainContext &&
            !timestamp->pChainContext->TrustStatus.dwErrorStatus){error=ERROR_SUCCESS;break;}
    }
done:
    data.dwStateAction=WTD_STATEACTION_CLOSE;WinVerifyTrust(INVALID_HANDLE_VALUE,&action,&data);
    return error;
}
DWORD ssd_verify_product_signature(const WCHAR *path,HANDLE file){return verify_signature(path,file,TRUE);}
/* Deliberately narrow JSON: setup emits this ordered, ASCII-escaped schema.
   No generic object lookup, permissive duplicate keys or ignored fields. */
typedef struct { const BYTE *next,*end; } Json;
static void whitespace(Json *j){while(j->next<j->end && (*j->next==' ' || *j->next=='\t' || *j->next=='\r' || *j->next=='\n'))j->next++;}
static BOOL punctuation(Json *j,BYTE value){whitespace(j);if(j->next==j->end || *j->next!=value)return FALSE;j->next++;return TRUE;}
static int hex(BYTE value){return value>='0' && value<='9'?value-'0':value>='a' && value<='f'?value-'a'+10:value>='A' && value<='F'?value-'A'+10:-1;}
static BOOL string(Json *j,WCHAR *out,DWORD capacity) {
    if(!punctuation(j,'"'))return FALSE;DWORD used=0;
    while(j->next<j->end) {
        WCHAR ch=*j->next++;
        if(ch=='"'){out[used]=0;return TRUE;}
        if(ch<32 || ch>126 || used+1>=capacity)return FALSE;
        if(ch=='\\') {
            if(j->next==j->end)return FALSE;ch=*j->next++;
            if(ch=='u') {
                if(j->end-j->next<4)return FALSE;ch=0;
                for(DWORD n=0;n<4;n++){int digit=hex(*j->next++);if(digit<0)return FALSE;ch=(WCHAR)((ch<<4)|digit);}
                if(ch<32)return FALSE;
            } else if(ch!='\\' && ch!='"' && ch!='/')return FALSE;
        }
        out[used++]=ch;
    }
    return FALSE;
}
static BOOL key(Json *j,const WCHAR *name){WCHAR value[64];return string(j,value,ARRAYSIZE(value))&&!wcscmp(value,name)&&punctuation(j,':');}
static BOOL hash(Json *j,BYTE out[32]) {
    WCHAR value[65];if(!string(j,value,ARRAYSIZE(value)) || wcslen(value)!=64)return FALSE;
    for(DWORD i=0;i<32;i++){int a=value[i*2]<=127?hex((BYTE)value[i*2]):-1,b=value[i*2+1]<=127?hex((BYTE)value[i*2+1]):-1;if(a<0 || b<0)return FALSE;out[i]=(BYTE)((a<<4)|b);}
    return TRUE;
}
static BOOL relative_member(const WCHAR *name) {
    size_t length=wcslen(name);if(!length || length>=256)return FALSE;
    for(size_t start=0;start<length;) {
        size_t end=start;while(end<length && name[end]!=L'\\')end++;
        if(end==start || name[end-1]==L'.' || name[end-1]==L' ')return FALSE;
        for(size_t i=start;i<end;i++) {
            WCHAR ch=name[i];if(ch<32 || ch==L'/' || ch==L':' || ch==L'"' || ch==L'<' || ch==L'>' || ch==L'|' || ch==L'*' || ch==L'?')return FALSE;
            if(ch>=0xd800 && ch<=0xdbff){if(++i>=end || name[i]<0xdc00 || name[i]>0xdfff)return FALSE;}
            else if(ch>=0xdc00 && ch<=0xdfff)return FALSE;
        }
        start=end+1;if(start==length)return FALSE;
    }
    return TRUE;
}
DWORD ssd_parse_trust_manifest(const BYTE *bytes,DWORD length,SsdTrustManifest *m) {
    if(!bytes || !m || length<100 || length>524288)return ERROR_INVALID_DATA;memset(m,0,sizeof(*m));Json j={bytes,bytes+length};
    if(!punctuation(&j,'{') || !key(&j,L"schemaVersion") || !punctuation(&j,'1') || !punctuation(&j,',') ||
        !key(&j,L"version") || !string(&j,m->version,ARRAYSIZE(m->version)) || !m->version[0] || !punctuation(&j,',') ||
        !key(&j,L"userSid") || !string(&j,m->user_sid,ARRAYSIZE(m->user_sid)) || wcsncmp(m->user_sid,L"S-1-5-21-",9) || !punctuation(&j,',') ||
        !key(&j,L"appDirectory") || !string(&j,m->app_directory,ARRAYSIZE(m->app_directory)) || !punctuation(&j,',') ||
        !key(&j,L"workerSha256") || !hash(&j,m->worker_sha256) || !punctuation(&j,',') ||
        !key(&j,L"members") || !punctuation(&j,'['))return ERROR_INVALID_DATA;
    if(m->app_directory[1]!=L':' || m->app_directory[2]!=L'\\' || !relative_member(m->app_directory+3))return ERROR_INVALID_DATA;
    do {
        if(m->count==SSD_TRUST_MAX_MEMBERS)return ERROR_INVALID_DATA;SsdTrustedMember *member=m->members+m->count;
        if(!punctuation(&j,'{') || !key(&j,L"path") || !string(&j,member->path,ARRAYSIZE(member->path)) ||
            !relative_member(member->path) || !punctuation(&j,',') || !key(&j,L"sha256") || !hash(&j,member->sha256) || !punctuation(&j,'}'))return ERROR_INVALID_DATA;
        for(DWORD i=0;i<m->count;i++)if(CompareStringOrdinal(member->path,-1,m->members[i].path,-1,TRUE)==CSTR_EQUAL)return ERROR_DUP_NAME;
        m->count++;
    } while(punctuation(&j,','));
    if(!punctuation(&j,']') || !punctuation(&j,'}'))return ERROR_INVALID_DATA;whitespace(&j);
    if(j.next!=j.end)return ERROR_INVALID_DATA;
    const WCHAR *required[]={L"SyncShark.exe",L"SyncShark.dll",L"SyncShark.Core.dll",L"SyncShark.deps.json",L"SyncShark.runtimeconfig.json"};
    for(DWORD r=0;r<ARRAYSIZE(required);r++) {
        BOOL found=FALSE;for(DWORD i=0;i<m->count;i++)if(!wcscmp(m->members[i].path,required[r]))found=TRUE;
        if(!found)return ERROR_INVALID_DATA;
    }
    return ERROR_SUCCESS;
}
static HANDLE ordinary_file(const WCHAR *path,BOOL directory) {
    HANDLE file=CreateFileW(path,FILE_READ_ATTRIBUTES|READ_CONTROL|(directory?FILE_LIST_DIRECTORY:FILE_READ_DATA),
        FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    if(file==INVALID_HANDLE_VALUE)return file;BY_HANDLE_FILE_INFORMATION info;
    if(!GetFileInformationByHandle(file,&info) || (info.dwFileAttributes&(FILE_ATTRIBUTE_REPARSE_POINT|FILE_ATTRIBUTE_OFFLINE)) ||
        !!(info.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)!=!!directory || (!directory && info.nNumberOfLinks!=1)) {
        CloseHandle(file);SetLastError(ERROR_FILE_INVALID);return INVALID_HANDLE_VALUE;
    }
    return file;
}
DWORD ssd_load_trust_manifest(const WCHAR *path,SsdTrustManifest *manifest,HANDLE *held) {
    if(!held)return ERROR_INVALID_PARAMETER;*held=INVALID_HANDLE_VALUE;
    HANDLE file=ordinary_file(path,FALSE);if(file==INVALID_HANDLE_VALUE)return GetLastError();
    LARGE_INTEGER size;DWORD error=ERROR_INVALID_DATA,read;
    if(!GetFileSizeEx(file,&size) || size.QuadPart<100 || size.QuadPart>524288)goto done;
    BYTE *bytes=HeapAlloc(GetProcessHeap(),0,(SIZE_T)size.QuadPart);if(!bytes){error=ERROR_NOT_ENOUGH_MEMORY;goto done;}
    if(ReadFile(file,bytes,(DWORD)size.QuadPart,&read,NULL) && read==size.QuadPart)error=ssd_parse_trust_manifest(bytes,read,manifest);
    SecureZeroMemory(bytes,(SIZE_T)size.QuadPart);HeapFree(GetProcessHeap(),0,bytes);
done:
    if(error)CloseHandle(file);else *held=file;return error;
}
void ssd_release_trusted_app(HANDLE *held,DWORD count) {
    if(!held)return;for(DWORD i=0;i<count;i++)CloseHandle(held[i]);HeapFree(GetProcessHeap(),0,held);
}
static DWORD hold_directories(WCHAR *path,HANDLE *held,DWORD *count,DWORD limit) {
    for(DWORD i=3;path[i];i++)if(path[i]==L'\\') {
        if(*count>=limit)return ERROR_NOT_ENOUGH_MEMORY;
        path[i]=0;HANDLE h=ordinary_file(path,TRUE);path[i]=L'\\';
        if(h==INVALID_HANDLE_VALUE)return GetLastError();held[(*count)++]=h;
    }
    return ERROR_SUCCESS;
}
DWORD ssd_hold_trusted_app(const SsdTrustManifest *manifest,DWORD pid,HANDLE **result,DWORD *count) {
    if(!manifest || !result || !count)return ERROR_INVALID_PARAMETER;*result=NULL;*count=0;
    const WCHAR prefix[]=L"C:\\Program Files\\Massdex\\SyncShark\\versions\\";
    size_t base=ARRAYSIZE(prefix)-1;
    if(wcslen(manifest->app_directory)<=base || CompareStringOrdinal(manifest->app_directory,(int)base,prefix,(int)base,TRUE)!=CSTR_EQUAL)return ERROR_ACCESS_DENIED;
    unsigned dots=0;BOOL digit=FALSE;
    for(const WCHAR *at=manifest->app_directory+base;*at;at++) {
        if(*at==L'.'){if(!digit || ++dots>2)return ERROR_ACCESS_DENIED;digit=FALSE;}
        else if(*at>=L'0' && *at<=L'9')digit=TRUE;else return ERROR_ACCESS_DENIED;
    }
    if(dots!=2 || !digit)return ERROR_ACCESS_DENIED;
    const DWORD maximum=4096;HANDLE *held=HeapAlloc(GetProcessHeap(),0,maximum*sizeof(HANDLE));
    if(!held)return ERROR_NOT_ENOUGH_MEMORY;DWORD used=0,error=0;
    HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,pid);
    if(!process){error=GetLastError();goto done;}held[used++]=process;
    WCHAR executable[4096],actual[4096],path[4096];DWORD length=ARRAYSIZE(executable);
    if(!QueryFullProcessImageNameW(process,0,executable,&length)){error=GetLastError();goto done;}
    HANDLE image=ordinary_file(executable,FALSE);if(image==INVALID_HANDLE_VALUE){error=GetLastError();goto done;}held[used++]=image;
    length=GetFinalPathNameByHandleW(image,actual,ARRAYSIZE(actual),FILE_NAME_NORMALIZED|VOLUME_NAME_DOS);
    if(!length || length>=ARRAYSIZE(actual)){error=ERROR_INVALID_NAME;goto done;}
    wcscpy(path,manifest->app_directory);wcscat(path,L"\\SyncShark.exe");
    if(wcsncmp(actual,L"\\\\?\\",4) || CompareStringOrdinal(actual+4,-1,path,-1,TRUE)!=CSTR_EQUAL){error=ERROR_ACCESS_DENIED;goto done;}
    for(DWORD i=0;i<manifest->count && !error;i++) {
        wcscpy(path,manifest->app_directory);wcscat(path,L"\\");wcscat(path,manifest->members[i].path);
        if((error=hold_directories(path,held,&used,maximum)))break;
        if(used==maximum){error=ERROR_NOT_ENOUGH_MEMORY;break;}
        HANDLE h=ordinary_file(path,FALSE);if(h==INVALID_HANDLE_VALUE){error=GetLastError();break;}held[used++]=h;
        BYTE hash[32];error=ssd_hash_file(h,hash);
        if(!error && memcmp(hash,manifest->members[i].sha256,32))error=ERROR_INVALID_IMAGE_HASH;
        if(!error && (!wcscmp(manifest->members[i].path,L"SyncShark.exe") || !wcscmp(manifest->members[i].path,L"SyncShark.dll") ||
            !wcscmp(manifest->members[i].path,L"SyncShark.Core.dll")))error=ssd_verify_product_signature(path,h);
    }
    if(!error && WaitForSingleObject(process,0)!=WAIT_TIMEOUT)error=ERROR_PROCESS_ABORTED;
done:
    if(error)ssd_release_trusted_app(held,used);else {*result=held;*count=used;}return error;
}
DWORD ssd_hold_signed_caller(DWORD pid,HANDLE **result,DWORD *count) {
    /* Any path, timestamp optional. Safe only because Discovery is read-only and
       answers per root under the caller's own impersonated access. */
    if(!result || !count)return ERROR_INVALID_PARAMETER;*result=NULL;*count=0;
    HANDLE *held=HeapAlloc(GetProcessHeap(),0,2*sizeof(HANDLE));if(!held)return ERROR_NOT_ENOUGH_MEMORY;DWORD used=0,error=0;
    HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,pid);
    if(!process){error=GetLastError();goto done;}held[used++]=process;
    WCHAR executable[4096];DWORD length=ARRAYSIZE(executable);
    if(!QueryFullProcessImageNameW(process,0,executable,&length)){error=GetLastError();goto done;}
    HANDLE image=ordinary_file(executable,FALSE);if(image==INVALID_HANDLE_VALUE){error=GetLastError();goto done;}held[used++]=image;
    error=verify_signature(executable,image,FALSE);
    if(!error && WaitForSingleObject(process,0)!=WAIT_TIMEOUT)error=ERROR_PROCESS_ABORTED;
done:
    if(error)ssd_release_trusted_app(held,used);else {*result=held;*count=used;}return error;
}
DWORD ssd_hold_protected_file(const WCHAR *path,HANDLE medium,HANDLE **result,DWORD *count) {
    if(!path || !medium || !result || !count || wcslen(path)>=4096)return ERROR_INVALID_PARAMETER;*result=NULL;*count=0;
    HANDLE *held=HeapAlloc(GetProcessHeap(),0,256*sizeof(HANDLE));if(!held)return ERROR_NOT_ENOUGH_MEMORY;
    WCHAR copy[4096];wcscpy(copy,path);DWORD used=0,error=hold_directories(copy,held,&used,255);
    if(!error) {
        HANDLE file=ordinary_file(path,FALSE);
        if(file==INVALID_HANDLE_VALUE)error=GetLastError();else held[used++]=file;
    }
    for(DWORD i=0;i<used && !error;i++) {
        PSECURITY_DESCRIPTOR descriptor=NULL;
        error=GetSecurityInfo(held[i],SE_FILE_OBJECT,OWNER_SECURITY_INFORMATION|GROUP_SECURITY_INFORMATION|DACL_SECURITY_INFORMATION,
            NULL,NULL,NULL,NULL,&descriptor);
        if(error)break;
        GENERIC_MAPPING mapping={FILE_GENERIC_READ,FILE_GENERIC_WRITE,FILE_GENERIC_EXECUTE,FILE_ALL_ACCESS};
        BYTE privileges[4096];DWORD privilege_size=sizeof(privileges),granted=0;BOOL allowed=FALSE;
        if(!AccessCheck(descriptor,medium,MAXIMUM_ALLOWED,&mapping,(PRIVILEGE_SET*)privileges,&privilege_size,&granted,&allowed))error=GetLastError();
        LocalFree(descriptor);
        DWORD unsafe=FILE_WRITE_DATA|FILE_APPEND_DATA|FILE_WRITE_EA|FILE_WRITE_ATTRIBUTES|DELETE|WRITE_DAC|WRITE_OWNER;
        if(i+1<used)unsafe|=FILE_DELETE_CHILD;
        if(!error && (!allowed || (granted&unsafe)))error=ERROR_ACCESS_DENIED;
    }
    if(error)ssd_release_trusted_app(held,used);else {*result=held;*count=used;}return error;
}
