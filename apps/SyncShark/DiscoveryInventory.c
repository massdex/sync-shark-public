#include "DiscoveryInventory.h"
#include <stddef.h>
#include <string.h>
#include <wchar.h>

#define SSD_MAX_RECORDS 750000u
#define SSD_MAX_BYTES (256u * 1024u * 1024u)
#define SSD_HASH_SIZE (1u << 21)
#define SSD_MAX_TOPOLOGY_RECORDS (SSD_HASH_SIZE / 4u * 3u)
#define SSD_MAX_DEPTH 512u
#define SSD_MAX_PATH 32760u
#define SSD_NONE 0xffffffffu
#define SSD_UNSAFE_ATTRIBUTES (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_OFFLINE | 0x00400000u | 0x00040000u)
/* The bundled Windows headers retain the original names for these V0 layouts. */
_Static_assert(sizeof(USN_JOURNAL_DATA)==56,"USN V0 layout changed");
_Static_assert(sizeof(MFT_ENUM_DATA)==24,"MFT V0 layout changed");
typedef struct {
    ULONGLONG id, parent;
    LONGLONG usn;
    DWORD attributes, name_offset, name_length, first_child, next_child, visit, scope;
} SsdNode;
typedef struct { DWORD next, length; HANDLE handle; } SsdFrame;
struct SsdCatalog {
    SsdNode *nodes;
    WCHAR *names;
    DWORD *slots;
    DWORD count, node_capacity, name_count, name_capacity, phase, directory_count;
    DWORD diagnostic_stage, diagnostic_kind;
    ULONGLONG cursor, deadline, records_read;
    ULONGLONG pending_started, pending_longest, stop_pending;
    BOOL pending_in_flight, stop_outstanding, stop_recorded;
    SsdProgress progress;
    void *progress_context;
    HANDLE cancellation;
    BOOL failed, sealed;
};
/* First stop observation wins. Recording whether a Windows metadata call was
   outstanding when the stop was seen, and how long it had been outstanding, is
   evidence about that call's elapsed time, not proof of a kernel or driver stall.
   It never changes the stop decision, the deadline or any returned value. */
static void note_stop(SsdCatalog *c) {
    if(c->stop_recorded)return;
    c->stop_recorded=TRUE;c->stop_outstanding=c->pending_in_flight;
    c->stop_pending=c->pending_in_flight && c->pending_started ? GetTickCount64()-c->pending_started : 0;
}
static DWORD stopped(SsdCatalog *c) {
    if(c->cancellation && WaitForSingleObject(c->cancellation,0)==WAIT_OBJECT_0) {note_stop(c);return ERROR_CANCELLED;}
    return GetTickCount64()>=c->deadline ? ERROR_TIMEOUT : ERROR_SUCCESS;
}
static DWORD progressed(SsdCatalog *c) {
    DWORD error=stopped(c);if(error)return error;
    c->pending_longest=0;
    return c->progress && !c->progress(c->progress_context,c->diagnostic_stage,c->records_read)
        ? ERROR_CANCELLED : ERROR_SUCCESS;
}
static DWORD slot_for(ULONGLONG id) {
    id^=id>>33; id*=0xff51afd7ed558ccdULL; id^=id>>33;
    return (DWORD)id & (SSD_HASH_SIZE-1);
}
static DWORD find(const SsdCatalog *c, ULONGLONG id) {
    DWORD slot=slot_for(id);
    for(DWORD n=0;n<SSD_HASH_SIZE;n++,slot=(slot+1)&(SSD_HASH_SIZE-1)) {
        DWORD index=c->slots[slot];
        if(index==SSD_NONE || c->nodes[index].id==id) return index;
    }
    return SSD_NONE;
}
static DWORD insert(SsdCatalog *c,DWORD index) {
    DWORD slot=slot_for(c->nodes[index].id);
    for(DWORD n=0;n<SSD_HASH_SIZE;n++,slot=(slot+1)&(SSD_HASH_SIZE-1)) {
        if(n && !(n&1023u)){DWORD error=stopped(c);if(error)return error;}
        if(c->slots[slot]==SSD_NONE){c->slots[slot]=index;return ERROR_SUCCESS;}
        if(c->nodes[c->slots[slot]].id==c->nodes[index].id)return ERROR_INVALID_DATA;
    }
    return ERROR_INVALID_DATA;
}
static BOOL within_budget(DWORD nodes,DWORD names) {
    /* Includes the fixed hash and the existing buffer/projection/stack reserve.
       Compaction uses only scalar locals, not a second catalogue or remap table. */
    return (ULONGLONG)nodes*sizeof(SsdNode)+(ULONGLONG)names*sizeof(WCHAR)+
        SSD_HASH_SIZE*sizeof(DWORD)+16*1024*1024<=SSD_MAX_BYTES;
}
static BOOL valid_name(const WCHAR *name,DWORD length,BOOL root_dot) {
    if(!length || length>255 || (length==1 && name[0]==L'.' && !root_dot) ||
        (length==2 && name[0]==L'.' && name[1]==L'.')) return FALSE;
    for(DWORD i=0;i<length;i++) {
        WCHAR ch=name[i];
        if(ch<32 || ch==L'/' || ch==L'\\' || ch==L':' || ch==L'"' || ch==L'<' || ch==L'>' || ch==L'|' || ch==L'*' || ch==L'?') return FALSE;
        if(ch>=0xd800 && ch<=0xdbff) {
            if(++i>=length || name[i]<0xdc00 || name[i]>0xdfff) return FALSE;
        } else if(ch>=0xdc00 && ch<=0xdfff) return FALSE;
    }
    return TRUE;
}
static DWORD allocation_failure(SsdCatalog *c,DWORD kind) {
    c->diagnostic_kind=kind;return ERROR_NOT_ENOUGH_MEMORY;
}
static DWORD reserve(SsdCatalog *c,DWORD names) {
    DWORD nc=c->node_capacity,sc=c->name_capacity;
    DWORD limit=c->phase==1 ? SSD_MAX_TOPOLOGY_RECORDS : SSD_MAX_RECORDS;
    if(c->count>=limit) return allocation_failure(c,1);
    if(c->count==nc) nc=nc ? min(nc*2,limit) : 4096;
    if(names>SSD_MAX_BYTES/2-c->name_count) return allocation_failure(c,2);
    while(sc<c->name_count+names) {
        if(sc>SSD_MAX_BYTES/4) return allocation_failure(c,2);
        sc=sc ? sc*2 : 16384;
    }
    if(!within_budget(nc,sc))
        return allocation_failure(c,3);
    if(nc!=c->node_capacity) {
        void *next=c->nodes ? HeapReAlloc(GetProcessHeap(),0,c->nodes,(SIZE_T)nc*sizeof(SsdNode)) : HeapAlloc(GetProcessHeap(),0,(SIZE_T)nc*sizeof(SsdNode));
        if(!next) return allocation_failure(c,4);
        c->nodes=next;c->node_capacity=nc;
    }
    if(sc!=c->name_capacity) {
        void *next=c->names ? HeapReAlloc(GetProcessHeap(),0,c->names,(SIZE_T)sc*sizeof(WCHAR)) : HeapAlloc(GetProcessHeap(),0,(SIZE_T)sc*sizeof(WCHAR));
        if(!next) return allocation_failure(c,5);
        c->names=next;c->name_capacity=sc;
    }
    return ERROR_SUCCESS;
}
SsdCatalog *ssd_catalog_create(HANDLE cancellation,ULONGLONG deadline) {
    ULONGLONG now=GetTickCount64();
    if(deadline<=now || deadline-now>SSD_MAX_DURATION_MS) return NULL;
    SsdCatalog *c=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(*c));
    if(!c)return NULL;
    c->slots=HeapAlloc(GetProcessHeap(),0,SSD_HASH_SIZE*sizeof(DWORD));
    if(!c->slots){HeapFree(GetProcessHeap(),0,c);return NULL;}
    memset(c->slots,0xff,SSD_HASH_SIZE*sizeof(DWORD));
    c->cancellation=cancellation;c->deadline=deadline;return c;
}
void ssd_catalog_set_progress(SsdCatalog *c,SsdProgress progress,void *context) {
    if(c && !c->count && !c->phase && !c->failed && !c->sealed){c->progress=progress;c->progress_context=context;}
}
void ssd_catalog_destroy(SsdCatalog *c) {
    if(!c)return;
    if(c->nodes)HeapFree(GetProcessHeap(),0,c->nodes);
    if(c->names){SecureZeroMemory(c->names,(SIZE_T)c->name_capacity*sizeof(WCHAR));HeapFree(GetProcessHeap(),0,c->names);}
    HeapFree(GetProcessHeap(),0,c->slots);HeapFree(GetProcessHeap(),0,c);
}
DWORD ssd_catalog_count(const SsdCatalog *c){return c && !c->failed ? c->count : 0;}
DWORD ssd_catalog_failure_code(const SsdCatalog *c,DWORD error) {
    if(!error)return ERROR_SUCCESS;
    if(error>0xffffu)return error; /* Never truncate an extended OS status. */
    /* Fixed numeric diagnostics only: no filenames, IDs, paths or payloads.
       The original Win32 error remains in the low16 bits. No limit or result
       acceptance changes; partial inventory is still discarded. */
    DWORD stage=c ? c->diagnostic_stage : 0,kind=c ? c->diagnostic_kind : 6;
    return 0x30000000u|((stage&15u)<<20)|((kind&15u)<<16)|(error&0xffffu);
}
static DWORD pending_bucket(ULONGLONG milliseconds) {
    return milliseconds>=60000u ? 3u : milliseconds>=10000u ? 2u : milliseconds ? 1u : 0u;
}
DWORD ssd_catalog_stop_code(const SsdCatalog *c,DWORD error,DWORD origin) {
    if(error!=ERROR_CANCELLED)return ssd_catalog_failure_code(c,error);
    ULONGLONG longest=c ? c->pending_longest : 0;
    if(c && c->stop_outstanding && c->stop_pending>longest)longest=c->stop_pending;
    DWORD outstanding=c && c->stop_outstanding ? 1u : 0u;
    DWORD stage=c ? c->diagnostic_stage : 0,kind=c ? c->diagnostic_kind : 6;
    return 0x40000000u|(pending_bucket(longest)<<28)|(outstanding<<27)|
        ((origin&7u)<<24)|((stage&15u)<<20)|((kind&15u)<<16)|(error&0xffffu);
}
DWORD ssd_catalog_begin_directories(SsdCatalog *c) {
    if(!c || c->failed || c->sealed || c->count || c->cursor || c->phase)return ERROR_INVALID_STATE;
    c->phase=1;c->diagnostic_stage=2;return ERROR_SUCCESS;
}
static DWORD compact_selected_directories(SsdCatalog *c) {
    DWORD error,kept=0,names=0,previous_end=0,old_names=c->name_count;
    if(c->count>SSD_MAX_TOPOLOGY_RECORDS || c->count>c->node_capacity || old_names>c->name_capacity)
        return ERROR_INVALID_DATA;
    if(!within_budget(c->node_capacity,c->name_capacity))return allocation_failure(c,3);
    for(DWORD i=0;i<c->count;i++) {
        if(!(i&1023u) && (error=progressed(c)))return error;
        SsdNode node=c->nodes[i];
        if(node.scope!=1 && node.scope!=2)return ERROR_INVALID_DATA;
        if(node.name_offset<previous_end || node.name_offset>old_names ||
            node.name_length>=old_names-node.name_offset || c->names[node.name_offset+node.name_length])
            return ERROR_INVALID_DATA;
        previous_end=node.name_offset+node.name_length+1;
        if(node.scope!=1)continue;
        if(kept==SSD_MAX_RECORDS)return allocation_failure(c,1);
        /* Input names were appended in node order. Moving left cannot destroy
           a later unread name. Parent links remain stable file IDs; all index
           links are reset and the hash is rebuilt against compacted indices. */
        memmove(c->names+names,c->names+node.name_offset,(node.name_length+1)*sizeof(WCHAR));
        node.name_offset=names;names+=node.name_length+1;
        node.first_child=node.next_child=SSD_NONE;node.visit=0;
        c->nodes[kept++]=node;
    }
    if(!kept)return ERROR_INVALID_DATA;
    for(DWORD offset=names;offset<old_names;) {
        if((error=stopped(c)))return error;
        DWORD length=min(old_names-offset,32768u);
        SecureZeroMemory(c->names+offset,(SIZE_T)length*sizeof(WCHAR));offset+=length;
    }
    c->count=kept;c->name_count=names;
    for(DWORD offset=0;offset<SSD_HASH_SIZE;offset+=16384u) {
        if((error=stopped(c)))return error;
        memset(c->slots+offset,0xff,16384u*sizeof(DWORD));
    }
    for(DWORD i=0;i<kept;i++) {
        if(!(i&1023u) && (error=progressed(c)))return error;
        if((error=insert(c,i)))return error;
    }
    return stopped(c);
}
DWORD ssd_catalog_begin_selected_files(SsdCatalog *c,const SsdRoot *roots,DWORD count) {
    if(!c || c->failed || c->sealed || c->phase!=1 || !roots || !count || count>128)return ERROR_INVALID_STATE;
    c->diagnostic_stage=3;DWORD error=ERROR_INVALID_DATA;
    for(DWORD r=0;r<count;r++) {
        if(!roots[r].file_id || roots[r].volume_serial!=roots[0].volume_serial)goto fail;
        for(DWORD j=0;j<r;j++)if(roots[r].file_id==roots[j].file_id)goto fail;
        DWORD root=find(c,roots[r].file_id);
        if(root==SSD_NONE || !(c->nodes[root].attributes&FILE_ATTRIBUTE_DIRECTORY))goto fail;
        c->nodes[root].scope=1;
    }
    for(DWORD i=0;i<c->count;i++) {
        if(!(i&1023u) && (error=progressed(c)))goto fail;
        DWORD trail[SSD_MAX_DEPTH],length=0,index=i,scope=2;error=ERROR_INVALID_DATA;
        while(index!=SSD_NONE) {
            SsdNode *node=c->nodes+index;
            if(node->scope){scope=node->scope;break;}
            if(length==SSD_MAX_DEPTH)goto fail;
            for(DWORD j=0;j<length;j++)if(trail[j]==index)goto fail;
            trail[length++]=index;
            if(node->parent==node->id)break;
            index=find(c,node->parent);
        }
        for(DWORD j=0;j<length;j++)c->nodes[trail[j]].scope=scope;
    }
    if((error=compact_selected_directories(c)))goto fail;
    c->directory_count=c->count;c->cursor=0;c->phase=2;c->diagnostic_stage=4;return ERROR_SUCCESS;
fail:
    c->failed=TRUE;return error;
}
DWORD ssd_catalog_finish_selected_files(SsdCatalog *c) {
    if(!c || c->failed || c->sealed || c->phase!=2)return ERROR_INVALID_STATE;
    c->diagnostic_stage=5;
    for(DWORD i=0;i<c->directory_count;i++) {
        DWORD error=!(i&1023u) ? progressed(c) : 0;if(error){c->failed=TRUE;return error;}
        if(c->nodes[i].scope==1 && c->nodes[i].visit!=1){c->failed=TRUE;return ERROR_FILE_INVALID;}
        c->nodes[i].visit=0;
    }
    DWORD error=progressed(c);if(error){c->failed=TRUE;return error;}
    c->phase=3;return ERROR_SUCCESS;
}
DWORD ssd_catalog_add_page(SsdCatalog *c,const BYTE *page,DWORD length) {
    DWORD error=ERROR_INVALID_DATA;
    if(!c || c->failed || c->sealed || c->phase==3)return ERROR_INVALID_STATE;
    if(!page || length<8 || length>1024*1024)goto fail;
    ULONGLONG next;memcpy(&next,page,8);
    if(next<=c->cursor)goto fail;
    DWORD offset=8,processed=0;
    while(offset<length) {
        // Check in bounded CPU batches rather than one kernel wait per record.
        // Framing, names, admission and every retained record are still checked.
        if(!(processed++&1023u) && (error=stopped(c)))goto fail;
        error=ERROR_INVALID_DATA;
        if(length-offset<60)goto fail;
        const BYTE *raw=page+offset;
        DWORD record,attrs;WORD major,minor,namesize,nameoffset;ULONGLONG id,parent;LONGLONG usn;
        memcpy(&record,raw,4);memcpy(&major,raw+4,2);memcpy(&minor,raw+6,2);
        memcpy(&id,raw+8,8);memcpy(&parent,raw+16,8);memcpy(&usn,raw+24,8);
        memcpy(&attrs,raw+52,4);memcpy(&namesize,raw+56,2);memcpy(&nameoffset,raw+58,2);
        if(record<64 || record>4096 || (record&7) || record>length-offset || major!=2 || minor || !id || !parent || usn<0 ||
            namesize<2 || namesize>510 || (namesize&1) || nameoffset<60 || (nameoffset&1) || nameoffset+namesize>record)goto fail;
        WCHAR name[256];memcpy(name,raw+nameoffset,namesize);name[namesize/2]=0;
        if(!valid_name(name,namesize/2,id==parent && (attrs&FILE_ATTRIBUTE_DIRECTORY)))goto fail;
        if(c->phase==1 && !(attrs&FILE_ATTRIBUTE_DIRECTORY)){offset+=record;continue;}
        if(c->phase==2) {
            if(attrs&FILE_ATTRIBUTE_DIRECTORY) {
                DWORD existing=find(c,id);
                DWORD directory=find(c,parent);
                if(existing==SSD_NONE || existing>=c->directory_count || c->nodes[existing].scope!=1) {
                    // Unrelated temporary/system directories may change while
                    // reading the drive. A new/moved directory entering an
                    // admitted subtree, however, invalidates that snapshot.
                    if(directory!=SSD_NONE && c->nodes[directory].scope==1)goto fail;
                    offset+=record;continue;
                }
                SsdNode *node=c->nodes+existing;
                if(node->visit || node->parent!=parent || node->usn!=usn || node->attributes!=attrs ||
                    node->name_length!=namesize/2 || memcmp(c->names+node->name_offset,name,namesize))goto fail;
                node->visit=1;offset+=record;continue;
            }
            DWORD directory=find(c,parent);
            if(directory==SSD_NONE || c->nodes[directory].scope!=1){offset+=record;continue;}
        }
        if(find(c,id)!=SSD_NONE)goto fail;
        if((error=reserve(c,namesize/2+1)))goto fail;
        DWORD index=c->count++;
        c->nodes[index]=(SsdNode){.id=id,.parent=parent,.usn=usn,.attributes=attrs,.name_offset=c->name_count,
            .name_length=namesize/2,.first_child=SSD_NONE,.next_child=SSD_NONE,.visit=0};
        memcpy(c->names+c->name_count,name,namesize+2);c->name_count+=namesize/2+1;
        if((error=insert(c,index)))goto fail;
        offset+=record;
    }
    c->records_read+=processed;
    if((error=progressed(c)))goto fail;
    c->cursor=next;return ERROR_SUCCESS;
fail:
    c->failed=TRUE;return error;
}
static DWORD control(SsdCatalog *c,HANDLE volume,DWORD code,void *input,DWORD input_size,void *output,DWORD output_size,DWORD *returned) {
    DWORD error=stopped(c);if(error)return error;
    OVERLAPPED op={0};op.hEvent=CreateEventW(NULL,TRUE,FALSE,NULL);if(!op.hEvent)return GetLastError();
    c->pending_started=GetTickCount64();c->pending_in_flight=TRUE;
    BOOL ok=DeviceIoControl(volume,code,input,input_size,output,output_size,returned,&op);
    error=ok ? ERROR_SUCCESS : GetLastError();
    if(error==ERROR_IO_PENDING) {
        for(;;) {
            DWORD wait=WaitForSingleObject(op.hEvent,100);
            if(wait==WAIT_OBJECT_0){error=GetOverlappedResult(volume,&op,returned,FALSE) ? 0 : GetLastError();break;}
            error=wait==WAIT_FAILED ? GetLastError() : stopped(c);
            if(error){CancelIoEx(volume,&op);DWORD ignored;GetOverlappedResult(volume,&op,&ignored,TRUE);break;}
        }
    }
    c->pending_in_flight=FALSE;
    {ULONGLONG elapsed=GetTickCount64()-c->pending_started;if(elapsed>c->pending_longest)c->pending_longest=elapsed;}
    CloseHandle(op.hEvent);return error;
}
static DWORD read_index(SsdCatalog *c,HANDLE volume,BYTE *buffer,LONGLONG high_usn) {
    MFT_ENUM_DATA request={0};request.HighUsn=high_usn;DWORD returned=0,error=0;
    for(;;) {
        error=control(c,volume,FSCTL_ENUM_USN_DATA,&request,sizeof(request),buffer,1024*1024,&returned);
        if(error==ERROR_HANDLE_EOF){error=0;break;}
        if(error || (error=ssd_catalog_add_page(c,buffer,returned)))break;
        request.StartFileReferenceNumber=c->cursor;
    }
    return error;
}
DWORD ssd_catalog_read_volume(SsdCatalog *c,HANDLE volume,const SsdRoot *roots,DWORD count,USN_JOURNAL_DATA *before,USN_JOURNAL_DATA *after) {
    if(!c || c->count || c->failed || c->sealed || !roots || !count || count>128 || !before || !after)return ERROR_INVALID_STATE;
    c->diagnostic_stage=1;
    DWORD returned=0,error=control(c,volume,FSCTL_QUERY_USN_JOURNAL,NULL,0,before,sizeof(*before),&returned);
    if(error || returned<sizeof(*before) || !before->UsnJournalID){c->failed=TRUE;return error ? error : ERROR_INVALID_DATA;}
    BYTE *buffer=HeapAlloc(GetProcessHeap(),0,1024*1024);if(!buffer){c->failed=TRUE;return allocation_failure(c,6);}
    error=ssd_catalog_begin_directories(c);
    if(!error)error=read_index(c,volume,buffer,before->NextUsn);
    if(!error)error=ssd_catalog_begin_selected_files(c,roots,count);
    if(!error)error=read_index(c,volume,buffer,before->NextUsn);
    if(!error)error=ssd_catalog_finish_selected_files(c);
    SecureZeroMemory(buffer,1024*1024);HeapFree(GetProcessHeap(),0,buffer);
    if(!error){c->diagnostic_stage=6;error=control(c,volume,FSCTL_QUERY_USN_JOURNAL,NULL,0,after,sizeof(*after),&returned);}
    if(!error && (returned<sizeof(*after) || before->UsnJournalID!=after->UsnJournalID ||
        after->NextUsn<before->NextUsn || after->FirstUsn>before->NextUsn || after->LowestValidUsn>before->NextUsn)) error=ERROR_JOURNAL_DELETE_IN_PROGRESS;
    if(error)c->failed=TRUE;
    return error;
}
static DWORD ordinary_root(const WCHAR *path) {
    if(!path)return ERROR_INVALID_NAME;
    size_t n=wcslen(path);
    if(n<3 || n>SSD_MAX_PATH-512 || !((path[0]>=L'A' && path[0]<=L'Z') || (path[0]>=L'a' && path[0]<=L'z')) || path[1]!=L':' || path[2]!=L'\\')return ERROR_INVALID_NAME;
    for(size_t begin=3;begin<n;) {
        size_t end=begin;while(end<n && path[end]!=L'\\')end++;
        if(!valid_name(path+begin,(DWORD)(end-begin),FALSE) || path[end-1]==L'.' || path[end-1]==L' ')return ERROR_INVALID_NAME;
        begin=end+1;if(begin==n)return ERROR_INVALID_NAME;
    }
    return ERROR_SUCCESS;
}
static HANDLE open_metadata(const WCHAR *path,BOOL directory) {
    return CreateFileW(path,FILE_READ_ATTRIBUTES|(directory?FILE_LIST_DIRECTORY:0),FILE_SHARE_READ|FILE_SHARE_WRITE,
        NULL,OPEN_EXISTING,FILE_FLAG_BACKUP_SEMANTICS|FILE_FLAG_OPEN_REPARSE_POINT,NULL);
}
static DWORD identity(HANDLE file,DWORD volume,ULONGLONG id,BOOL directory,BY_HANDLE_FILE_INFORMATION *info) {
    if(!GetFileInformationByHandle(file,info))return GetLastError();
    if(info->dwVolumeSerialNumber!=volume || (((ULONGLONG)info->nFileIndexHigh<<32)|info->nFileIndexLow)!=id ||
        !!(info->dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY)!=!!directory || (info->dwFileAttributes&SSD_UNSAFE_ATTRIBUTES))return ERROR_FILE_INVALID;
    return ERROR_SUCCESS;
}
static DWORD safe_client_token(HANDLE token) {
    TOKEN_TYPE type;SECURITY_IMPERSONATION_LEVEL level;DWORD size;
    if(!token || !GetTokenInformation(token,TokenType,&type,sizeof(type),&size) || type!=TokenImpersonation ||
        !GetTokenInformation(token,TokenImpersonationLevel,&level,sizeof(level),&size) || level<SecurityImpersonation)return ERROR_BAD_IMPERSONATION_LEVEL;
    TOKEN_ELEVATION elevation;
    if(!GetTokenInformation(token,TokenElevation,&elevation,sizeof(elevation),&size) || elevation.TokenIsElevated)return ERROR_ACCESS_DENIED;
    const WCHAR *unsafe_privileges[]={SE_BACKUP_NAME,SE_RESTORE_NAME,SE_SECURITY_NAME,SE_TAKE_OWNERSHIP_NAME,SE_DEBUG_NAME};
    for(DWORD i=0;i<ARRAYSIZE(unsafe_privileges);i++) {
        PRIVILEGE_SET privilege={.PrivilegeCount=1,.Control=PRIVILEGE_SET_ALL_NECESSARY};BOOL enabled=FALSE;
        if(!LookupPrivilegeValueW(NULL,unsafe_privileges[i],&privilege.Privilege[0].Luid) ||
            !PrivilegeCheck(token,&privilege,&enabled) || enabled)return ERROR_ACCESS_DENIED;
    }
    return ERROR_SUCCESS;
}
struct SsdAdmission { HANDLE handles[4096];DWORD count;WCHAR volume[50]; };
void ssd_admission_close(SsdAdmission *admission) {
    if(!admission)return;
    for(DWORD i=0;i<admission->count;i++)CloseHandle(admission->handles[i]);
    SecureZeroMemory(admission,sizeof(*admission));HeapFree(GetProcessHeap(),0,admission);
}
const WCHAR *ssd_admission_volume(const SsdAdmission *admission) {return admission?admission->volume:NULL;}
DWORD ssd_admit_roots(const SsdRoot *roots,DWORD count,HANDLE token,SsdAdmission **result) {
    if(!result)return ERROR_INVALID_PARAMETER;*result=NULL;
    if(!roots || !count || count>128)return ERROR_INVALID_PARAMETER;
    DWORD error=safe_client_token(token);if(error)return error;
    for(DWORD i=0;i<count;i++) {
        if((error=ordinary_root(roots[i].path)))return error;
        if(!roots[i].file_id || roots[i].volume_serial!=roots[0].volume_serial)return ERROR_INVALID_PARAMETER;
        for(DWORD j=0;j<i;j++)if(roots[i].file_id==roots[j].file_id)return ERROR_DUP_NAME;
    }
    SsdAdmission *admission=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(*admission));
    WCHAR *path=HeapAlloc(GetProcessHeap(),0,(SSD_MAX_PATH+1)*sizeof(WCHAR));
    WCHAR *final=HeapAlloc(GetProcessHeap(),0,(SSD_MAX_PATH+1)*sizeof(WCHAR));
    if(!admission || !path || !final){error=ERROR_NOT_ENOUGH_MEMORY;goto done;}
    if(!ImpersonateLoggedOnUser(token)){error=GetLastError();goto done;}
    for(DWORD r=0;r<count && !error;r++) {
        wcscpy(path,roots[r].path);DWORD length=(DWORD)wcslen(path);HANDLE last=NULL;
        for(DWORD i=3;i<=length;i++)if(i==length || path[i]==L'\\') {
            if(admission->count==ARRAYSIZE(admission->handles)){error=ERROR_NOT_ENOUGH_MEMORY;break;}
            WCHAR saved=path[i];path[i]=0;HANDLE h=open_metadata(path,TRUE);path[i]=saved;
            if(h==INVALID_HANDLE_VALUE){error=GetLastError();break;}
            admission->handles[admission->count++]=h;last=h;BY_HANDLE_FILE_INFORMATION info;
            if(!GetFileInformationByHandle(h,&info)){error=GetLastError();break;}
            if(info.dwVolumeSerialNumber!=roots[r].volume_serial || !(info.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY) ||
                (info.dwFileAttributes&SSD_UNSAFE_ATTRIBUTES)){error=ERROR_FILE_INVALID;break;}
        }
        if(error)break;BY_HANDLE_FILE_INFORMATION info;
        if((error=identity(last,roots[r].volume_serial,roots[r].file_id,TRUE,&info)))break;
        DWORD n=GetFinalPathNameByHandleW(last,final,SSD_MAX_PATH+1,VOLUME_NAME_GUID);
        if(!n || n>SSD_MAX_PATH || n<49 || wcsncmp(final,L"\\\\?\\Volume{",11) || final[47]!=L'}' || final[48]!=L'\\') {error=ERROR_INVALID_DRIVE;break;}
        final[48]=0;
        if(r && CompareStringOrdinal(final,-1,admission->volume,-1,TRUE)!=CSTR_EQUAL){error=ERROR_NOT_SAME_DEVICE;break;}
        wcscpy(admission->volume,final);
        WCHAR filesystem[32];
        if(!GetVolumeInformationByHandleW(last,NULL,0,NULL,NULL,NULL,filesystem,ARRAYSIZE(filesystem)) || wcscmp(filesystem,L"NTFS"))
            {error=ERROR_NOT_SUPPORTED;break;}
    }
    if(!RevertToSelf())TerminateProcess(GetCurrentProcess(),ERROR_CANNOT_IMPERSONATE);
done:
    if(path){SecureZeroMemory(path,(SSD_MAX_PATH+1)*sizeof(WCHAR));HeapFree(GetProcessHeap(),0,path);}
    if(final){SecureZeroMemory(final,(SSD_MAX_PATH+1)*sizeof(WCHAR));HeapFree(GetProcessHeap(),0,final);}
    if(error)ssd_admission_close(admission);else *result=admission;
    return error;
}
static DWORD project_root(SsdCatalog *c,const SsdRoot *root,DWORD ordinal,SsdEmit emit,void *context) {
    DWORD error=ordinary_root(root->path);if(error)return error;
    DWORD anchor=find(c,root->file_id);if(anchor==SSD_NONE || !(c->nodes[anchor].attributes&FILE_ATTRIBUTE_DIRECTORY))return ERROR_FILE_INVALID;
    WCHAR *path=HeapAlloc(GetProcessHeap(),0,(SSD_MAX_PATH+1)*sizeof(WCHAR));
    SsdFrame *frames=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,(SSD_MAX_DEPTH+1)*sizeof(SsdFrame));
    HANDLE *ancestors=HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,(SSD_MAX_DEPTH+1)*sizeof(HANDLE));
    if(!path || !frames || !ancestors){error=allocation_failure(c,6);goto free_memory;}
    wcscpy(path,root->path);DWORD root_length=(DWORD)wcslen(path),ancestor_count=0,depth=0;
    // Hold every ordinary ancestor against replacement while the selected root is inspected.
    for(DWORD i=3;i<=root_length;i++)if(i==root_length || path[i]==L'\\') {
        if(ancestor_count>=SSD_MAX_DEPTH){error=ERROR_BUFFER_OVERFLOW;goto close_ancestors;}
        WCHAR saved=path[i];path[i]=0;HANDLE h=open_metadata(path,TRUE);path[i]=saved;
        if(h==INVALID_HANDLE_VALUE){error=GetLastError();goto close_ancestors;}
        ancestors[ancestor_count++]=h;BY_HANDLE_FILE_INFORMATION info;
        if(!GetFileInformationByHandle(h,&info)){error=GetLastError();goto close_ancestors;}
        if(info.dwVolumeSerialNumber!=root->volume_serial || !(info.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY) ||
            (info.dwFileAttributes&SSD_UNSAFE_ATTRIBUTES)){error=ERROR_FILE_INVALID;goto close_ancestors;}
    }
    BY_HANDLE_FILE_INFORMATION root_info;
    error=identity(ancestors[ancestor_count-1],root->volume_serial,root->file_id,TRUE,&root_info);if(error)goto close_ancestors;
    frames[0]=(SsdFrame){.next=c->nodes[anchor].first_child,.length=root_length,.handle=NULL};
    c->nodes[anchor].visit=ordinal+1;
    for(;;) {
        if((error=stopped(c)))break;
        SsdFrame *frame=frames+depth;
        if(frame->next==SSD_NONE){if(!depth)break;CloseHandle(frame->handle);frame->handle=NULL;depth--;continue;}
        DWORD index=frame->next;SsdNode *node=c->nodes+index;frame->next=node->next_child;
        if(node->visit==ordinal+1 || frame->length+node->name_length+1>SSD_MAX_PATH){error=ERROR_INVALID_DATA;break;}
        node->visit=ordinal+1;
        if(node->attributes&SSD_UNSAFE_ATTRIBUTES){error=ERROR_REPARSE_TAG_INVALID;break;}
        DWORD length=frame->length;
        if(path[length-1]!=L'\\')path[length++]=L'\\';
        memcpy(path+length,c->names+node->name_offset,(node->name_length+1)*sizeof(WCHAR));
        BOOL directory=!!(node->attributes&FILE_ATTRIBUTE_DIRECTORY);
        HANDLE file=open_metadata(path,directory);if(file==INVALID_HANDLE_VALUE){error=GetLastError();break;}
        BY_HANDLE_FILE_INFORMATION info;error=identity(file,root->volume_serial,node->id,directory,&info);
        if(error){CloseHandle(file);break;}
        const WCHAR *relative=path+root_length;while(*relative==L'\\')relative++;
        SsdItem item={.root_index=ordinal,.relative_path=relative,.file_id=node->id,.parent_id=node->parent,.usn=node->usn,
            .attributes=info.dwFileAttributes,.link_count=info.nNumberOfLinks,.size=((ULONGLONG)info.nFileSizeHigh<<32)|info.nFileSizeLow,.modified=info.ftLastWriteTime};
        if(!emit(context,&item)){CloseHandle(file);error=ERROR_CANCELLED;break;}
        if(directory) {
            if(depth+1>=SSD_MAX_DEPTH){CloseHandle(file);error=ERROR_BUFFER_OVERFLOW;break;}
            frames[++depth]=(SsdFrame){.next=node->first_child,.length=length+node->name_length,.handle=file};
        } else CloseHandle(file);
    }
    for(DWORD i=1;i<=depth;i++)if(frames[i].handle)CloseHandle(frames[i].handle);
    if(!error)error=identity(ancestors[ancestor_count-1],root->volume_serial,root->file_id,TRUE,&root_info);
close_ancestors:
    for(DWORD i=0;i<ancestor_count;i++)CloseHandle(ancestors[i]);
free_memory:
    if(path){SecureZeroMemory(path,(SSD_MAX_PATH+1)*sizeof(WCHAR));HeapFree(GetProcessHeap(),0,path);}
    if(frames)HeapFree(GetProcessHeap(),0,frames);if(ancestors)HeapFree(GetProcessHeap(),0,ancestors);
    return error;
}
DWORD ssd_catalog_project(SsdCatalog *c,const SsdRoot *roots,DWORD count,HANDLE token,SsdEmit emit,void *context) {
    if(!c || c->failed || c->sealed || c->phase==1 || c->phase==2 || !roots || !count || count>128 || !token || !emit)return ERROR_INVALID_PARAMETER;
    c->diagnostic_stage=7;
    DWORD cancelled=stopped(c);if(cancelled){c->failed=TRUE;return cancelled;}
    c->sealed=TRUE;
    for(DWORD i=0;i<count;i++) {
        if(!roots[i].file_id || roots[i].volume_serial!=roots[0].volume_serial)return ERROR_INVALID_PARAMETER;
        for(DWORD j=0;j<i;j++)if(roots[i].file_id==roots[j].file_id)return ERROR_DUP_NAME;
    }
    DWORD token_error=safe_client_token(token);if(token_error)return token_error;
    // Do not grant the worker's volume privilege to filesystem ACL probes.
    if(!ImpersonateLoggedOnUser(token))return GetLastError();
    DWORD error=0;
    for(DWORD i=0;i<c->count;i++) {
        if(!(i&1023u) && (error=progressed(c)))break;
        DWORD parent=find(c,c->nodes[i].parent);
        if(parent==SSD_NONE || parent==i)continue;
        c->nodes[i].next_child=c->nodes[parent].first_child;c->nodes[parent].first_child=i;
    }
    for(DWORD i=0;i<count && !error;i++)error=project_root(c,roots+i,i,emit,context);
    if(!RevertToSelf())TerminateProcess(GetCurrentProcess(),ERROR_CANNOT_IMPERSONATE);
    if(error)c->failed=TRUE;
    return error;
}
