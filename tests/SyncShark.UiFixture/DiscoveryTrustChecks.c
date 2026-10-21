#include "../../apps/SyncShark/DiscoveryTrust.h"
#include <stdio.h>
#include <string.h>
#include <sddl.h>
static DWORD checks;
static void check(BOOL value,const char *name){if(!value){fprintf(stderr,"FAIL %s\n",name);ExitProcess(1);}checks++;printf("PASS %s\n",name);}
int wmain(int argc,WCHAR **argv) {
    if(argc==3 && !wcscmp(argv[1],L"--reject-signature-only")) {
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);
        HANDLE candidate=CreateFileW(argv[2],GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,NULL);
        if(candidate==INVALID_HANDLE_VALUE)return 3;
        check(ssd_verify_product_signature(argv[2],candidate)==ERROR_INVALID_IMAGE_HASH,
            "Actual previous-certificate executable is refused by the new native executable-role verifier");
        CloseHandle(candidate);return 0;
    }
    if(argc!=2)return 2;SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);
    HANDLE file=CreateFileW(argv[1],GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    if(file==INVALID_HANDLE_VALUE)return 3;
    BYTE first[32],second[32];check(!ssd_hash_file(file,first)&&!ssd_hash_file(file,second)&&!memcmp(first,second,32),"Native SHA-256 rebinds the same held file");
    DWORD signature=ssd_verify_product_signature(argv[1],file);
    if(signature)fprintf(stderr,"Signature error %lu\n",signature);
    check(signature==0,"Actual installed product signer and RFC3161 timestamp verify without trust-store mutation");CloseHandle(file);
    WCHAR self[4096];check(GetModuleFileNameW(NULL,self,ARRAYSIZE(self))>0,"Resolve only this fixture executable");
    file=CreateFileW(self,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    check(file!=INVALID_HANDLE_VALUE && ssd_verify_product_signature(self,file)!=0,"Unsigned native code is refused by the real product signature verifier");CloseHandle(file);
    HANDLE primary=NULL,medium=NULL;
    check(OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY|TOKEN_DUPLICATE,&primary)&&
        DuplicateTokenEx(primary,TOKEN_QUERY|TOKEN_IMPERSONATE,NULL,SecurityImpersonation,TokenImpersonation,&medium),
        "Use the real medium token for the pipe permission check");CloseHandle(primary);
    BYTE groups_buffer[65536];DWORD group_size;WCHAR *logon=NULL;
    check(GetTokenInformation(medium,TokenGroups,groups_buffer,sizeof(groups_buffer),&group_size),"Inspect only token groups for the admission check");
    TOKEN_GROUPS *groups=(TOKEN_GROUPS*)groups_buffer;
    for(DWORD i=0;i<groups->GroupCount;i++)if((groups->Groups[i].Attributes&SE_GROUP_LOGON_ID)==SE_GROUP_LOGON_ID)
        ConvertSidToStringSidW(groups->Groups[i].Sid,&logon);
    check(logon!=NULL,"The current interactive logon identity is available");
    WCHAR sddl[768];swprintf(sddl,ARRAYSIZE(sddl),L"O:BAG:BAD:P(A;;GA;;;SY)(A;;0x0012019b;;;%ls)",logon);LocalFree(logon);
    PSECURITY_DESCRIPTOR descriptor=NULL;check(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl,SDDL_REVISION_1,&descriptor,NULL),
        "Parse the exact proposed pipe DACL without creating a pipe");
    GENERIC_MAPPING mapping={FILE_GENERIC_READ,FILE_GENERIC_WRITE,FILE_GENERIC_EXECUTE,FILE_ALL_ACCESS};
    BYTE privileges[4096];DWORD privilege_size=sizeof(privileges),granted;BOOL allowed;
    check(AccessCheck(descriptor,medium,MAXIMUM_ALLOWED,&mapping,(PRIVILEGE_SET*)privileges,&privilege_size,&granted,&allowed)&&allowed&&
        (granted&0x0012019b)==0x0012019b && !(granted&(FILE_CREATE_PIPE_INSTANCE|WRITE_DAC|WRITE_OWNER|DELETE)),
        "Real medium-token access allows pipe I/O but denies new instances, ACL edits, ownership and deletion");
    LocalFree(descriptor);
    HANDLE *held=NULL;DWORD held_count=0;
    check(ssd_hold_protected_file(argv[1],medium,&held,&held_count)==ERROR_ACCESS_DENIED && held==NULL,
        "A user-writable installed app is refused as a privileged protected worker");CloseHandle(medium);
    const char *zero="0000000000000000000000000000000000000000000000000000000000000000";
    char json[4096],item[400];strcpy(json,"{\"schemaVersion\":1,\"version\":\"0.3.18\",\"userSid\":\"S-1-5-21-1-2-3-1001\",\"appDirectory\":\"C:\\\\Fixture\",\"workerSha256\":\"");
    strcat(json,zero);strcat(json,"\",\"members\":[");
    const char *names[]={"SyncShark.exe","SyncShark.dll","SyncShark.Core.dll","SyncShark.deps.json","SyncShark.runtimeconfig.json"};
    for(DWORD i=0;i<ARRAYSIZE(names);i++){snprintf(item,sizeof(item),"%s{\"path\":\"%s\",\"sha256\":\"%s\"}",i?",":"",names[i],zero);strcat(json,item);}
    strcat(json,"]}");SsdTrustManifest *manifest=HeapAlloc(GetProcessHeap(),0,sizeof(*manifest));check(manifest!=NULL,"Manifest fixture is bounded");
    check(!ssd_parse_trust_manifest((const BYTE*)json,(DWORD)strlen(json),manifest)&&manifest->count==5,"Strict native manifest accepts the complete required app identity");
    check(ssd_parse_trust_manifest((const BYTE*)json,(DWORD)strlen(json)-1,manifest)!=0,"Truncated protected manifest is rejected");
    char *name=strstr(json,"SyncShark.dll");memcpy(name,"SYNCShark.exe",13);
    check(ssd_parse_trust_manifest((const BYTE*)json,(DWORD)strlen(json),manifest)==ERROR_DUP_NAME,"Case-colliding app members are rejected");
    HeapFree(GetProcessHeap(),0,manifest);
    printf("%lu native trust checks passed; read-only installed identity inspection, no helper activation.\n",checks);return 0;
}
