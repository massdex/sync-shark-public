#include "../../apps/SyncShark/DiscoverySetup.c"
#include <stdlib.h>
static unsigned checks;
#define CHECK(x,label) do{if(!(x)){fprintf(stderr,"FAIL %s at %d\n",label,__LINE__);return 1;}checks++;printf("PASS %s\n",label);}while(0)
int wmain(int argc,WCHAR **argv){
    CHECK(argc==3,"Only the exact installed manifest and genuine application PID are supplied");
    DWORD pid=wcstoul(argv[2],NULL,10),session=0;HANDLE primary=NULL,medium=NULL,held=NULL,manifest_file=NULL;
    CHECK(pid>0 && ProcessIdToSessionId(GetCurrentProcessId(),&session),"Resolve the actual current interactive session");
    CHECK(OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY|TOKEN_DUPLICATE,&primary),"Read the fixture's actual ordinary token");
    TOKEN_ELEVATION elevation;DWORD bytes;
    CHECK(GetTokenInformation(primary,TokenElevation,&elevation,sizeof(elevation),&bytes) && !elevation.TokenIsElevated,"No elevation is used by these admission checks");
    CHECK(DuplicateTokenEx(primary,TOKEN_QUERY,NULL,SecurityIdentification,TokenImpersonation,&medium),"Use an actual ordinary identity token");CloseHandle(primary);
    SsdTrustManifest *manifest=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(*manifest));CHECK(manifest!=NULL,"Bound the fixture's manifest storage");
    CHECK(ssd_load_trust_manifest(argv[1],manifest,&manifest_file)==0,"Load the actual protected installed manifest through its native parser");
    CHECK(setup_caller(pid,medium,session,manifest->app_directory,&held)==0 && held,"Real native setup caller admission accepts the genuine installed medium process");CloseHandle(held);held=NULL;
    CHECK(setup_caller(pid,medium,session+1,manifest->app_directory,&held)==ERROR_ACCESS_DENIED && !held,"Real native caller admission rejects a different session before child execution");
    CHECK(setup_caller(0,medium,session,manifest->app_directory,&held)!=0 && !held,"An absent caller cannot enter native setup");
    CHECK(setup_caller(GetCurrentProcessId(),medium,session,manifest->app_directory,&held)==0 && held,"The runtime-contract route can authenticate a same-session ordinary installer host identity");CloseHandle(held);held=NULL;
    HANDLE *files=NULL;DWORD count=0;
    CHECK(ssd_hold_trusted_app(manifest,pid,&files,&count)==0 && count>manifest->count,"Actual native app admission holds and verifies every installed member for the genuine caller");ssd_release_trusted_app(files,count);files=NULL;count=0;
    CHECK(ssd_hold_trusted_app(manifest,GetCurrentProcessId(),&files,&count)!=0 && !files,"The app-caller route rejects an unrelated executable even with the same session token");
    manifest->members[0].sha256[0]^=1;
    CHECK(ssd_hold_trusted_app(manifest,pid,&files,&count)!=0 && !files,"A changed bound source-member hash is rejected by the real held-file verifier");manifest->members[0].sha256[0]^=1;
    WCHAR original[ARRAYSIZE(manifest->app_directory)];wcscpy(original,manifest->app_directory);wcscpy(manifest->app_directory,L"C:\\Windows\\System32");
    CHECK(ssd_hold_trusted_app(manifest,pid,&files,&count)!=0 && !files,"A foreign source directory cannot authorize the genuine caller");wcscpy(manifest->app_directory,original);
    WCHAR path[4096];swprintf(path,ARRAYSIZE(path),L"%ls\\SyncShark.exe",manifest->app_directory);
    HANDLE file=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    CHECK(file!=INVALID_HANDLE_VALUE && ssd_verify_product_signature(path,file)==0,"The actual installed executable signer and timestamp pass the production native verifier");CloseHandle(file);
    CHECK(GetModuleFileNameW(NULL,path,ARRAYSIZE(path))>0,"Resolve only the fixture executable for the unsigned control");
    file=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    CHECK(file!=INVALID_HANDLE_VALUE && ssd_verify_product_signature(path,file)==ERROR_INVALID_IMAGE_HASH,"Unsigned source fails the actual native product-signature decision");CloseHandle(file);
    CloseHandle(manifest_file);CloseHandle(medium);HeapFree(GetProcessHeap(),0,manifest);
    printf("PASS %u real native setup caller/source/signature decisions; no privileged setup, child shell, task or file effect.\n",checks);return 0;
}
