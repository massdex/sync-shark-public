#include "../../apps/SyncShark/DiscoveryInventory.h"
#include "../../apps/SyncShark/DiscoveryProtocol.h"
#include <aclapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

static DWORD passes;
static WCHAR paths[8][2048];
static BYTE page[8192];
static DWORD used;
static HANDLE token;
typedef struct { DWORD count,path_mask;BOOL outside,denied_child,multiple_links;HANDLE output; } Seen;
static void check(BOOL good,const char *name) {
    if(!good){fprintf(stderr,"FAIL %s (Windows %lu)\n",name,GetLastError());exit(1);}
    printf("PASS %s\n",name);passes++;
}
static void make_path(DWORD index,const WCHAR *base,const WCHAR *tail) {
    check(wcslen(base)+wcslen(tail)+2<ARRAYSIZE(paths[index]),"Fixture path is bounded");
    wcscpy(paths[index],base);wcscat(paths[index],L"\\");wcscat(paths[index],tail);
}
static BY_HANDLE_FILE_INFORMATION info(const WCHAR *path) {
    HANDLE h=CreateFileW(path,FILE_READ_ATTRIBUTES,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,NULL,OPEN_EXISTING,FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT,NULL);
    BY_HANDLE_FILE_INFORMATION value={0};
    check(h!=INVALID_HANDLE_VALUE && GetFileInformationByHandle(h,&value),"Fixture native identity is readable");CloseHandle(h);return value;
}
static ULONGLONG id(BY_HANDLE_FILE_INFORMATION value){return ((ULONGLONG)value.nFileIndexHigh<<32)|value.nFileIndexLow;}
static void file(const WCHAR *path) {
    HANDLE h=CreateFileW(path,GENERIC_WRITE,0,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);DWORD written;
    check(h!=INVALID_HANDLE_VALUE && WriteFile(h,"fixture",7,&written,NULL) && written==7,"Only the fresh fixture file is created");CloseHandle(h);
}
static void record(ULONGLONG file_id,ULONGLONG parent,const WCHAR *name,DWORD attrs) {
    WORD bytes=(WORD)(wcslen(name)*2),offset=60,version=2;DWORD length=(60+bytes+7)&~7;LONGLONG usn=5;
    check(used+length<=sizeof(page),"Synthetic MFT input fits its fixed page");
    BYTE *raw=page+used;memset(raw,0,length);memcpy(raw,&length,4);memcpy(raw+4,&version,2);
    memcpy(raw+8,&file_id,8);memcpy(raw+16,&parent,8);memcpy(raw+24,&usn,8);memcpy(raw+52,&attrs,4);
    memcpy(raw+56,&bytes,2);memcpy(raw+58,&offset,2);memcpy(raw+60,name,bytes);used+=length;
}
static BOOL emit(void *context,const SsdItem *item) {
    Seen *seen=context;seen->count++;
    if(wcsstr(item->relative_path,L"Outside"))seen->outside=TRUE;
    if(wcsstr(item->relative_path,L"Reports\\Today"))seen->denied_child=TRUE;
    if(item->link_count>1 && !(item->attributes&FILE_ATTRIBUTE_DIRECTORY))seen->multiple_links=TRUE;
    if(item->root_index==0 && !wcscmp(item->relative_path,L"Ä.txt"))seen->path_mask|=1;
    if(item->root_index==0 && !wcscmp(item->relative_path,L"Reports"))seen->path_mask|=2;
    if(item->root_index==0 && !wcscmp(item->relative_path,L"Reports\\Today.txt"))seen->path_mask|=4;
    if(item->root_index==1 && !wcscmp(item->relative_path,L"Second.txt"))seen->path_mask|=8;
    if(item->root_index==2 && !wcscmp(item->relative_path,L"Today.txt"))seen->path_mask|=16;
    if(seen->output) {
        BYTE buffer[SSD_WIRE_MAX_FRAME+4];DWORD length=ssd_reply_item(item,buffer,sizeof(buffer)),written;
        if(!length || !WriteFile(seen->output,buffer,length,&written,NULL) || written!=length)return FALSE;
    }
    return TRUE;
}
static SsdCatalog *catalog(HANDLE cancel) {
    SsdCatalog *c=ssd_catalog_create(cancel,GetTickCount64()+30000);
    check(c!=NULL && !ssd_catalog_add_page(c,page,used),"Actual native parser accepts the complete bounded input");return c;
}
static SsdCatalog *filtered_catalog(const SsdRoot *roots,DWORD count) {
    SsdCatalog *c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(c && !ssd_catalog_begin_directories(c) && !ssd_catalog_add_page(c,page,used) &&
        !ssd_catalog_begin_selected_files(c,roots,count) && !ssd_catalog_add_page(c,page,used) &&
        !ssd_catalog_finish_selected_files(c),"Use actual two-pass compaction before filesystem safety checks");return c;
}
static DWORD typed_pages(SsdCatalog *c,DWORD total,ULONGLONG parent,DWORD attrs) {
    BYTE input[8192];DWORD count=0;ULONGLONG cursor=1000;
    while(count<total) {
        DWORD bytes=8;
        while(count<total && bytes+64<=sizeof(input)) {
            BYTE *raw=input+bytes;memset(raw,0,64);
            DWORD length=64;WORD version=2,namesize=2,nameoffset=60;
            ULONGLONG file_id=0x1000000000000000ULL+count;LONGLONG usn=5;WCHAR name=L'x';
            memcpy(raw,&length,4);memcpy(raw+4,&version,2);memcpy(raw+8,&file_id,8);memcpy(raw+16,&parent,8);
            memcpy(raw+24,&usn,8);memcpy(raw+52,&attrs,4);memcpy(raw+56,&namesize,2);memcpy(raw+58,&nameoffset,2);
            memcpy(raw+60,&name,2);bytes+=64;count++;
        }
        cursor++;memcpy(input,&cursor,8);
        DWORD error=ssd_catalog_add_page(c,input,bytes);if(error)return error;
    }
    return ERROR_SUCCESS;
}
static DWORD file_pages(SsdCatalog *c,DWORD total,ULONGLONG parent) {
    return typed_pages(c,total,parent,FILE_ATTRIBUTE_NORMAL);
}
static void journal_sizing_checks(void) {
    ULONGLONG target=0,gb=1073741824ULL,delta=134217728ULL;
    check(ssd_journal_target(gb,gb,delta,100*gb,&target)==ERROR_ALREADY_EXISTS && target==gb,"Journal already large enough");
    check(ssd_journal_target(2*gb,gb,delta,20*gb,&target)==ERROR_DISK_FULL && target==gb,"Journal cap includes allocation delta");
    check(!ssd_journal_target(2*gb,gb,delta,20*(2*gb+delta),&target) && target==2*gb,"Journal target exactly at cap");
    check(!ssd_journal_target(8*gb,gb,delta,1000*gb,&target) && target==8*gb-delta,"Journal absolute cap includes delta");
    check(ssd_journal_target(2*gb,gb,8*gb,100*gb,&target)==ERROR_DISK_FULL,"Oversized delta cannot underflow");
    check(!ssd_journal_target(2*gb,gb,2*delta,20*(2*gb+delta),&target) && target==2*gb-delta,"Existing allocation delta retained");
}
int wmain(int argc,WCHAR **argv) {
    journal_sizing_checks();
    if(argc==2 && !wcscmp(argv[1],L"--journal-sizing-only")){printf("%lu journal sizing assertions passed.\n",passes);return 0;}
    if(argc!=2 || wcslen(argv[1])>1800)return 2;
    /* The caller allocates one fresh directory inside its registered build root.
       No raw volume, pipe, task, protected install, provider or user file is touched. */
    DWORD existing=GetFileAttributesW(argv[1]);
    if(existing==INVALID_FILE_ATTRIBUTES || !(existing&FILE_ATTRIBUTE_DIRECTORY) || (existing&FILE_ATTRIBUTE_REPARSE_POINT))return 3;
    make_path(0,argv[1],L"A");make_path(1,argv[1],L"B");
    make_path(2,paths[0],L"Ä.txt");make_path(3,paths[0],L"Reports");make_path(4,paths[3],L"Today.txt");
    make_path(5,paths[1],L"Second.txt");make_path(6,argv[1],L"Outside.txt");make_path(7,argv[1],L"Alias.txt");
    check(CreateDirectoryW(paths[0],NULL)&&CreateDirectoryW(paths[1],NULL)&&CreateDirectoryW(paths[3],NULL),"Create only fresh disposable fixture folders");
    file(paths[2]);file(paths[4]);file(paths[5]);file(paths[6]);
    HANDLE primary;
    check(OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY|TOKEN_DUPLICATE,&primary)&&
        DuplicateTokenEx(primary,TOKEN_QUERY|TOKEN_IMPERSONATE,NULL,SecurityImpersonation,TokenImpersonation,&token),"Use the actual ordinary user's impersonation token");
    BY_HANDLE_FILE_INFORMATION a=info(paths[0]),b=info(paths[1]),one=info(paths[2]),reports=info(paths[3]),today=info(paths[4]),two=info(paths[5]),outside=info(paths[6]);
    ULONGLONG cursor=1000;memcpy(page,&cursor,8);used=8;
    record(id(a),5,L"A",FILE_ATTRIBUTE_DIRECTORY);record(id(b),5,L"B",FILE_ATTRIBUTE_DIRECTORY);
    record(id(one),id(a),L"Ä.txt",FILE_ATTRIBUTE_NORMAL);record(id(reports),id(a),L"Reports",FILE_ATTRIBUTE_DIRECTORY);
    record(id(today),id(reports),L"Today.txt",FILE_ATTRIBUTE_NORMAL);record(id(two),id(b),L"Second.txt",FILE_ATTRIBUTE_NORMAL);
    record(id(outside),5,L"Outside.txt",FILE_ATTRIBUTE_NORMAL);
    SsdRoot roots[]={{paths[0],a.dwVolumeSerialNumber,id(a)},{paths[1],b.dwVolumeSerialNumber,id(b)}};
    SsdAdmission *admission=NULL;
    check(!ssd_admit_roots(roots,2,token,&admission) && admission && wcslen(ssd_admission_volume(admission))==48,
        "Both ordinary roots are admitted against one actual volume GUID before enumeration");
    WCHAR moved[2048];wcscpy(moved,argv[1]);wcscat(moved,L"\\MovedA");
    check(!MoveFileW(paths[0],moved) && GetLastError()==ERROR_SHARING_VIOLATION,
        "Held admission prevents a real root rename during volume discovery");
    ssd_admission_close(admission);admission=NULL;
    SsdRoot rejected=roots[0];rejected.file_id^=1;
    check(ssd_admit_roots(&rejected,1,token,&admission)!=0 && !admission,
        "A replaced root identity is rejected before any privileged volume read");
    SsdCatalog *c=catalog(NULL);Seen seen={0};
    check(ssd_catalog_project(c,roots,2,token,emit,&seen)==0 && seen.count==4 && !seen.outside,"One native catalogue serves two real roots without outside names");
    check(ssd_catalog_project(c,roots,2,token,emit,&seen)!=0,"A consumed native catalogue cannot be replayed");ssd_catalog_destroy(c);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && !ssd_catalog_add_page(c,page,used) && ssd_catalog_count(c)==3,
        "Directory pass retains topology, not unrelated volume-wide files");
    check(!file_pages(c,4000000,5) && ssd_catalog_count(c)==3,
        "Four million unrelated files do not consume the bounded directory catalogue");
    check(!ssd_catalog_begin_selected_files(c,roots,2) && !ssd_catalog_add_page(c,page,used),
        "Second pass revalidates directory identities and retains selected-root files");
    check(!file_pages(c,4000000,5) && ssd_catalog_count(c)==6 && !ssd_catalog_finish_selected_files(c),
        "Second four-million-file pass still retains only three directories and three selected files");
    seen=(Seen){0};
    check(!ssd_catalog_project(c,roots,2,token,emit,&seen) && seen.count==4 && !seen.outside,
        "Filtered real parser feeds the same actual ACL-checked two-root projection");ssd_catalog_destroy(c);
    BYTE outside_directory[72];memcpy(outside_directory,page,sizeof(outside_directory));
    ULONGLONG outside_cursor=2000,outside_id=0x2000000000000000ULL,outside_parent=5;
    memcpy(outside_directory,&outside_cursor,8);memcpy(outside_directory+8+8,&outside_id,8);
    memcpy(outside_directory+8+16,&outside_parent,8);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && !ssd_catalog_add_page(c,page,used) &&
        !ssd_catalog_add_page(c,outside_directory,sizeof(outside_directory)) && !ssd_catalog_begin_selected_files(c,roots,2) &&
        !ssd_catalog_add_page(c,page,used),"Prepare concurrent unrelated directory change");
    LONGLONG outside_usn=6;memcpy(outside_directory+8+24,&outside_usn,8);
    check(!ssd_catalog_add_page(c,outside_directory,sizeof(outside_directory)) && !ssd_catalog_finish_selected_files(c),
        "An unrelated directory update does not invalidate the selected-root snapshot");ssd_catalog_destroy(c);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && !ssd_catalog_add_page(c,page,used) &&
        !ssd_catalog_begin_selected_files(c,roots,2),"Prepare an entering-directory rejection");
    ULONGLONG admitted_parent=roots[0].file_id;memcpy(outside_directory+8+16,&admitted_parent,8);
    check(ssd_catalog_add_page(c,outside_directory,sizeof(outside_directory))==ERROR_INVALID_DATA && !ssd_catalog_count(c),
        "A new directory entering an admitted root fails closed");ssd_catalog_destroy(c);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && !ssd_catalog_add_page(c,page,used) && !ssd_catalog_begin_selected_files(c,roots,2),
        "Prepare bounded selected-root capacity check");
    check(file_pages(c,750001,roots[0].file_id)==ERROR_NOT_ENOUGH_MEMORY && !ssd_catalog_count(c),
        "Selected-root retention still enforces its original cap and discards partial data");
    check(ssd_catalog_failure_code(c,ERROR_NOT_ENOUGH_MEMORY)==0x30410008u,
        "Fixed diagnostic distinguishes the selected-file record cap without exposing metadata");ssd_catalog_destroy(c);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && typed_pages(c,1572865,5,FILE_ATTRIBUTE_DIRECTORY)==ERROR_NOT_ENOUGH_MEMORY && !ssd_catalog_count(c),
        "Actual topology parser rejects beyond seventy-five-percent hash occupancy");
    check(ssd_catalog_failure_code(c,ERROR_NOT_ENOUGH_MEMORY)==0x30210008u,
        "Fixed diagnostic distinguishes the directory record cap from selected-file retention");ssd_catalog_destroy(c);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && !typed_pages(c,750001,5,FILE_ATTRIBUTE_DIRECTORY),
        "Production parser accepts more than750000 unrelated directories within its topology budget");
    ULONGLONG later_cursor=1000000;memcpy(page,&later_cursor,8);
    check(!ssd_catalog_add_page(c,page,used) && ssd_catalog_count(c)==750004,
        "Real selected-root identities enter after the large unrelated topology");
    SsdRoot overlapping[]={roots[0],roots[1],{paths[3],reports.dwVolumeSerialNumber,id(reports)}};
    check(!ssd_catalog_begin_selected_files(c,overlapping,3) && ssd_catalog_count(c)==3,
        "Production classifier compacts in place to the three admitted directories including overlapping roots");
    memcpy(page,&cursor,8);
    check(!ssd_catalog_add_page(c,page,used) && !ssd_catalog_finish_selected_files(c) && ssd_catalog_count(c)==6,
        "Rebuilt slots find compacted parents and admit only the selected files");
    seen=(Seen){0};
    check(!ssd_catalog_project(c,overlapping,3,token,emit,&seen) && seen.count==5 && seen.path_mask==31 && !seen.outside,
        "Compacted names indices parents and overlapping roots survive actual ACL-checked projection");ssd_catalog_destroy(c);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && !ssd_catalog_add_page(c,page,used) &&
        !typed_pages(c,750000,roots[0].file_id,FILE_ATTRIBUTE_DIRECTORY),
        "Topology pass may contain more selected directories than the result cap");
    check(ssd_catalog_begin_selected_files(c,roots,2)==ERROR_NOT_ENOUGH_MEMORY && !ssd_catalog_count(c) &&
        ssd_catalog_failure_code(c,8)==0x30310008u,
        "Compaction rejects selected-directory overflow before any result or file pass");ssd_catalog_destroy(c);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);check(!ssd_catalog_begin_directories(c),"Prepare underlying enumeration error diagnostic");
    check(ssd_catalog_failure_code(c,ERROR_NOT_ENOUGH_MEMORY)==0x30200008u,
        "An underlying Windows memory error is not falsely labelled as an internal cap");ssd_catalog_destroy(c);
    check(ssd_catalog_failure_code(NULL,ERROR_NOT_ENOUGH_MEMORY)==0x30060008u && !ssd_catalog_failure_code(NULL,0),
        "Creation failure is distinct and diagnostics never turn success into failure");
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && !ssd_catalog_add_page(c,page,used) &&
        !ssd_catalog_begin_selected_files(c,roots,2),"Prepare directory-change rejection through the real filter");
    BYTE changed[8192];memcpy(changed,page,used);LONGLONG changed_usn=6;memcpy(changed+8+24,&changed_usn,8);
    check(ssd_catalog_add_page(c,changed,used)==ERROR_INVALID_DATA && !ssd_catalog_count(c),
        "Changed directory between passes discards the entire result");ssd_catalog_destroy(c);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && !ssd_catalog_add_page(c,page,used) &&
        !ssd_catalog_begin_selected_files(c,roots,2),"Prepare selected-directory movement after compaction");
    memcpy(changed,page,used);ULONGLONG changed_parent=6;memcpy(changed+8+16,&changed_parent,8);
    check(ssd_catalog_add_page(c,changed,used)==ERROR_INVALID_DATA && !ssd_catalog_count(c),
        "A compacted root changing parent between passes fails closed");ssd_catalog_destroy(c);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && !ssd_catalog_add_page(c,page,used) &&
        !ssd_catalog_begin_selected_files(c,roots,2) && ssd_catalog_finish_selected_files(c)==ERROR_FILE_INVALID,
        "Missing directories in the second pass cannot yield a partial success");ssd_catalog_destroy(c);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && !ssd_catalog_add_page(c,page,used),"Prepare incomplete-pass rejection");
    seen=(Seen){0};check(ssd_catalog_project(c,roots,2,token,emit,&seen)==ERROR_INVALID_PARAMETER && !seen.count,
        "A directory-only partial catalogue cannot be consumed");ssd_catalog_destroy(c);
    WCHAR wire_path[2048];wcscpy(wire_path,argv[1]);wcscat(wire_path,L"\\request.bin");
    HANDLE wire_request=CreateFileW(wire_path,GENERIC_WRITE,0,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);
    check(wire_request!=INVALID_HANDLE_VALUE,"Create the bounded native protocol input fixture");
    SsdRequest request={.root_count=2,.volume_serial=a.dwVolumeSerialNumber};for(DWORD i=0;i<32;i++)request.nonce[i]=(BYTE)i;
    BYTE wire_buffer[256];DWORD wire_length=ssd_reply_header(&request,wire_buffer,sizeof(wire_buffer)),written;
    check(wire_length==SSD_WIRE_HEADER_BYTES&&WriteFile(wire_request,wire_buffer,wire_length,&written,NULL)&&written==wire_length,"Native request header uses the exact shared format");
    SsdRequest decoded={0};check(!ssd_request_header(&decoded,wire_buffer,wire_length),"Native request header decoder binds both roots");
    check(decoded.operation==1,"Inventory operation is recorded");
    DWORD operation=2;memcpy(wire_buffer+8,&operation,4);
    check(!ssd_request_header(&decoded,wire_buffer,wire_length)&&decoded.operation==2,"Grow operation is accepted and recorded");
    operation=0;memcpy(wire_buffer+8,&operation,4);
    check(ssd_request_header(&decoded,wire_buffer,wire_length)==ERROR_INVALID_DATA,"Operation zero is refused");
    operation=3;memcpy(wire_buffer+8,&operation,4);
    check(ssd_request_header(&decoded,wire_buffer,wire_length)==ERROR_INVALID_DATA,"Unknown operation is refused");
    operation=1;memcpy(wire_buffer+8,&operation,4);
    check(!ssd_request_header(&decoded,wire_buffer,wire_length),"Restore inventory header for projection checks");
    for(DWORD i=0;i<2;i++) {
        DWORD units=(DWORD)wcslen(roots[i].path);ULONGLONG root_id=roots[i].file_id;
        check(WriteFile(wire_request,&root_id,8,&written,NULL)&&written==8&&WriteFile(wire_request,&units,4,&written,NULL)&&written==4&&
            WriteFile(wire_request,roots[i].path,units*2,&written,NULL)&&written==units*2,"Serialize only the admitted root path and identity");
        check(!ssd_request_add_root(&decoded,i,root_id,(const BYTE*)roots[i].path,units),"Native decoder retains the exact admitted root");
    }
    CloseHandle(wire_request);wcscpy(wire_path,argv[1]);wcscat(wire_path,L"\\response.bin");
    HANDLE wire_response=CreateFileW(wire_path,GENERIC_WRITE,0,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);
    check(wire_response!=INVALID_HANDLE_VALUE&&WriteFile(wire_response,wire_buffer,wire_length,&written,NULL)&&written==wire_length,"Start one native metadata response");
    wire_length=ssd_reply_progress(2,1,4000000,wire_buffer,sizeof(wire_buffer));
    check(wire_length==28 && WriteFile(wire_response,wire_buffer,wire_length,&written,NULL) && written==wire_length,
        "Native producer reports bounded numeric progress without file names or partial success");
    check(!ssd_reply_progress(0,1,0,wire_buffer,sizeof(wire_buffer)) &&
        !ssd_reply_progress(8,1,0,wire_buffer,sizeof(wire_buffer)) &&
        !ssd_reply_progress(2,0,0,wire_buffer,sizeof(wire_buffer)) &&
        !ssd_reply_progress(2,1,0,wire_buffer,27),"Invalid native progress cannot be serialized");
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!ssd_catalog_begin_directories(c) && !ssd_catalog_add_page(c,page,used) &&
        !ssd_catalog_begin_selected_files(c,decoded.roots,decoded.root_count) && !ssd_catalog_add_page(c,page,used) &&
        !ssd_catalog_finish_selected_files(c),"Cross-language response uses the production two-pass retention path");
    seen=(Seen){.output=wire_response};
    DWORD projection=ssd_catalog_project(c,decoded.roots,decoded.root_count,token,emit,&seen);ssd_catalog_destroy(c);
    USN_JOURNAL_DATA before={.UsnJournalID=7,.FirstUsn=1,.NextUsn=20,.LowestValidUsn=1},after=before;after.NextUsn=25;
    wire_length=ssd_reply_end(seen.count,&before,&after,wire_buffer,sizeof(wire_buffer));
    BOOL finalized=!projection&&wire_length&&WriteFile(wire_response,wire_buffer,wire_length,&written,NULL)&&written==wire_length;
    CloseHandle(wire_response);ssd_request_free(&decoded);
    check(finalized,"Real native metadata producer completes the framed response for the managed-reader check");
    c=catalog(NULL);SsdRoot swapped=roots[0];swapped.file_id^=1;seen=(Seen){0};
    check(ssd_catalog_project(c,&swapped,1,token,emit,&seen)!=0 && !seen.count,"Changed root file reference discloses no result");ssd_catalog_destroy(c);
    HANDLE identification;
    check(DuplicateTokenEx(primary,TOKEN_QUERY|TOKEN_IMPERSONATE,NULL,SecurityIdentification,TokenImpersonation,&identification),"Create a bounded identification-only rejection input");
    c=catalog(NULL);seen=(Seen){0};
    check(ssd_catalog_project(c,roots,2,identification,emit,&seen)==ERROR_BAD_IMPERSONATION_LEVEL && !seen.count,"Identification-only clients cannot inspect filesystem metadata");
    ssd_catalog_destroy(c);CloseHandle(identification);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(ssd_catalog_add_page(c,page,used-1)==ERROR_INVALID_DATA && !ssd_catalog_count(c),"Truncated native input is discarded");ssd_catalog_destroy(c);
    c=catalog(NULL);
    check(ssd_catalog_add_page(c,page,used)==ERROR_INVALID_DATA && !ssd_catalog_count(c),"Replayed enumeration cursor invalidates the whole catalogue");ssd_catalog_destroy(c);
    HANDLE cancel=CreateEventW(NULL,TRUE,FALSE,NULL);c=catalog(cancel);SetEvent(cancel);seen=(Seen){0};
    check(ssd_catalog_project(c,roots,2,token,emit,&seen)==ERROR_CANCELLED && !seen.count,"Cancellation produces no partial root result");ssd_catalog_destroy(c);CloseHandle(cancel);
    check(CreateHardLinkW(paths[7],paths[2],NULL),"Create a hard link only within the disposable fixture");
    c=filtered_catalog(roots,2);seen=(Seen){0};
    check(!ssd_catalog_project(c,roots,2,token,emit,&seen)&&seen.multiple_links,"Real link counts are exposed as uncertainty, never complete-name proof");ssd_catalog_destroy(c);
    PACL old_dacl=NULL,new_dacl=NULL;PSECURITY_DESCRIPTOR security=NULL;
    check(GetNamedSecurityInfoW(paths[3],SE_FILE_OBJECT,DACL_SECURITY_INFORMATION,NULL,NULL,&old_dacl,NULL,&security)==ERROR_SUCCESS,"Retain exact fixture ACL before the denied-access test");
    BYTE user_buffer[512];DWORD user_size;
    check(GetTokenInformation(token,TokenUser,user_buffer,sizeof(user_buffer),&user_size),"Read only the test token SID for its temporary ACL");
    EXPLICIT_ACCESSW deny={0};deny.grfAccessPermissions=FILE_LIST_DIRECTORY|FILE_READ_ATTRIBUTES;deny.grfAccessMode=DENY_ACCESS;
    deny.Trustee.TrusteeForm=TRUSTEE_IS_SID;deny.Trustee.TrusteeType=TRUSTEE_IS_USER;deny.Trustee.ptstrName=(WCHAR*)((TOKEN_USER*)user_buffer)->User.Sid;
    check(SetEntriesInAclW(1,&deny,old_dacl,&new_dacl)==ERROR_SUCCESS &&
        SetNamedSecurityInfoW(paths[3],SE_FILE_OBJECT,DACL_SECURITY_INFORMATION,NULL,NULL,new_dacl,NULL)==ERROR_SUCCESS,"Deny metadata access only on the disposable Reports folder");
    c=filtered_catalog(roots,2);seen=(Seen){0};DWORD denied=ssd_catalog_project(c,roots,2,token,emit,&seen);ssd_catalog_destroy(c);
    DWORD restored=SetNamedSecurityInfoW(paths[3],SE_FILE_OBJECT,DACL_SECURITY_INFORMATION,NULL,NULL,old_dacl,NULL);
    LocalFree(new_dacl);LocalFree(security);
    check(restored==ERROR_SUCCESS,"Restore the exact temporary folder ACL before cleanup");
    check(denied==ERROR_ACCESS_DENIED&&!seen.denied_child,"Actual denied-directory ACL prevents child metadata disclosure");
    /* A reparse-marked candidate must be rejected without following it. Only
       synthetic input changes; no real junction or reparse point is created. */
    DWORD reparse_offset=0;
    for(DWORD offset=8;offset<used;) {
        ULONGLONG current;DWORD length;memcpy(&current,page+offset+8,8);memcpy(&length,page+offset,4);
        if(current==id(one)){reparse_offset=offset;break;}offset+=length;
    }
    check(reparse_offset!=0,"Locate the selected-file record for the reparse rejection");
    DWORD reparse_attributes=FILE_ATTRIBUTE_NORMAL|FILE_ATTRIBUTE_REPARSE_POINT;
    memcpy(page+reparse_offset+52,&reparse_attributes,4);c=filtered_catalog(roots,2);seen=(Seen){0};
    check(ssd_catalog_project(c,roots,2,token,emit,&seen)==ERROR_REPARSE_TAG_INVALID && !ssd_catalog_count(c),
        "Compacted selected-root projection preserves fail-closed reparse handling");ssd_catalog_destroy(c);
    reparse_attributes=FILE_ATTRIBUTE_NORMAL;memcpy(page+reparse_offset+52,&reparse_attributes,4);
    CloseHandle(token);CloseHandle(primary);
    for(DWORD i=0;i<8;i++)if(i!=0&&i!=1&&i!=3)check(DeleteFileW(paths[i]),"Remove only a file created by this fixture");
    check(RemoveDirectoryW(paths[3])&&RemoveDirectoryW(paths[0])&&RemoveDirectoryW(paths[1]),"Remove only the empty fixture folders");
    printf("%lu native inventory assertions passed (including setup/cleanup). Real ACL/identity tests; synthetic MFT input. No elevated enumeration or speed claim.\n",passes);
    return 0;
}
