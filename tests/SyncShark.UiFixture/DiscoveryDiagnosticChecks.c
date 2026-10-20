#include "../../apps/SyncShark/DiscoveryInventory.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Controlled allocation/IO failures feed the unchanged production guards.
   No real volume, protected task, pipe or user directory is accessed. */
static DWORD fail_allocation,allocation_number,io_mode,io_number,passes;
static DWORD cancel_after,wait_number,progress_calls,progress_cancel_after;
static ULONGLONG progress_records;
static ULONGLONG pending_now,pending_last_activity;
static HANDLE pending_event,pending_cancellation;
static DWORD pending_waits,pending_finish_after,pending_cancel_after,pending_cancel_calls;
static BOOL pending_idle_guard;
static ULONGLONG test_tick(void){return pending_now ? pending_now : GetTickCount64();}
static BOOL pending_progress(void *context,DWORD stage,ULONGLONG records) {
    (void)context;
    if(stage!=2 || records!=42)return FALSE;
    pending_last_activity=pending_now;progress_calls++;return TRUE;
}
static BOOL progress(void *context,DWORD stage,ULONGLONG records) {
    (void)context;
    if(stage<1 || stage>7 || records<progress_records)return FALSE;
    progress_records=records;progress_calls++;
    return !progress_cancel_after || progress_calls<progress_cancel_after;
}
static DWORD test_wait(HANDLE handle,DWORD milliseconds) {
    if(pending_now && handle==pending_event && milliseconds==100) {
        pending_now+=50000;pending_waits++;
        if((pending_idle_guard && pending_now-pending_last_activity>=120000) ||
            (pending_cancel_after && pending_waits>=pending_cancel_after))SetEvent(pending_cancellation);
        if(pending_waits>=pending_finish_after){SetEvent(pending_event);return WAIT_OBJECT_0;}
        return WAIT_TIMEOUT;
    }
    if(cancel_after && ++wait_number==cancel_after)return WAIT_OBJECT_0;
    return WaitForSingleObject(handle,milliseconds);
}
static void *test_alloc(HANDLE heap,DWORD flags,SIZE_T bytes) {
    if(fail_allocation && ++allocation_number==fail_allocation)return NULL;
    return HeapAlloc(heap,flags,bytes);
}
static void *test_realloc(HANDLE heap,DWORD flags,void *memory,SIZE_T bytes) {
    if(fail_allocation && ++allocation_number==fail_allocation)return NULL;
    return HeapReAlloc(heap,flags,memory,bytes);
}
static BOOL test_control(HANDLE handle,DWORD code,void *input,DWORD input_size,
    void *output,DWORD output_size,DWORD *returned,OVERLAPPED *operation) {
    (void)handle;(void)input;(void)input_size;(void)operation;
    io_number++;
    if(io_mode==4){pending_event=operation->hEvent;SetLastError(ERROR_IO_PENDING);return FALSE;}
    if(io_mode==1){SetLastError(0x8007000eu);return FALSE;}
    if(code==FSCTL_QUERY_USN_JOURNAL && io_number==1) {
        if(output_size<sizeof(USN_JOURNAL_DATA))return FALSE;
        USN_JOURNAL_DATA value={.UsnJournalID=1,.NextUsn=10};
        memcpy(output,&value,sizeof(value));*returned=sizeof(value);return TRUE;
    }
    if(io_mode==2 || io_mode==3){SetLastError(ERROR_NOT_ENOUGH_MEMORY);return FALSE;}
    SetLastError(ERROR_INVALID_FUNCTION);return FALSE;
}
static BOOL test_result(HANDLE handle,OVERLAPPED *operation,DWORD *returned,BOOL wait) {
    if(pending_now && operation->hEvent==pending_event) {
        (void)handle;(void)wait;*returned=0;
        if(pending_cancel_calls){SetLastError(ERROR_OPERATION_ABORTED);return FALSE;}
        *returned=8;return TRUE;
    }
    return GetOverlappedResult(handle,operation,returned,wait);
}
static BOOL test_cancel(HANDLE handle,OVERLAPPED *operation) {
    if(pending_now && operation->hEvent==pending_event){pending_cancel_calls++;SetEvent(pending_event);return TRUE;}
    return CancelIoEx(handle,operation);
}
#define HeapAlloc test_alloc
#define HeapReAlloc test_realloc
#define DeviceIoControl test_control
#define WaitForSingleObject test_wait
#define GetTickCount64 test_tick
#define GetOverlappedResult test_result
#define CancelIoEx test_cancel
#include "../../apps/SyncShark/DiscoveryInventory.c"
#undef HeapAlloc
#undef HeapReAlloc
#undef DeviceIoControl
#undef WaitForSingleObject
#undef GetTickCount64
#undef GetOverlappedResult
#undef CancelIoEx

static void check(BOOL condition,const char *name) {
    if(!condition){fprintf(stderr,"FAIL %s\n",name);exit(1);}
    printf("PASS %s\n",name);passes++;
}
static SsdCatalog *fresh(void) {
    fail_allocation=allocation_number=cancel_after=wait_number=0;
    SsdCatalog *c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(c!=NULL && !ssd_catalog_begin_directories(c),"Create actual bounded catalogue");return c;
}
static SsdCatalog *topology(void) {
    SsdCatalog *c=fresh();BYTE page[8+4*64]={0};ULONGLONG cursor=1;
    memcpy(page,&cursor,8);
    const ULONGLONG ids[]={10,21,20,11},parents[]={10,20,10,10};
    for(DWORD i=0;i<4;i++) {
        BYTE *raw=page+8+i*64;DWORD length=64,attributes=FILE_ATTRIBUTE_DIRECTORY;
        WORD version=2,namesize=2,nameoffset=60;LONGLONG usn=5;WCHAR name=(WCHAR)(L'a'+i);
        memcpy(raw,&length,4);memcpy(raw+4,&version,2);memcpy(raw+8,ids+i,8);memcpy(raw+16,parents+i,8);
        memcpy(raw+24,&usn,8);memcpy(raw+52,&attributes,4);memcpy(raw+56,&namesize,2);memcpy(raw+58,&nameoffset,2);
        memcpy(raw+60,&name,2);
    }
    check(!ssd_catalog_add_page(c,page,sizeof(page)),"Parse interleaved directories with child before parent");return c;
}
int main(void) {
    check(!ssd_catalog_create(NULL,GetTickCount64()+SSD_MAX_DURATION_MS+10000),
        "Requests beyond the fixed scan safety ceiling are rejected");
    SsdCatalog *long_scan=ssd_catalog_create(NULL,GetTickCount64()+10*60*1000);
    check(long_scan!=NULL,"Healthy scans may legitimately run beyond five minutes");ssd_catalog_destroy(long_scan);
    SsdCatalog *c=fresh();c->count=SSD_MAX_TOPOLOGY_RECORDS;
    check(reserve(c,1)==ERROR_NOT_ENOUGH_MEMORY && ssd_catalog_failure_code(c,8)==0x30210008u,
        "Topology-cap guard has a distinct fixed code");ssd_catalog_destroy(c);
    c=fresh();c->phase=2;c->count=SSD_MAX_RECORDS;c->diagnostic_stage=4;
    check(reserve(c,1)==ERROR_NOT_ENOUGH_MEMORY && ssd_catalog_failure_code(c,8)==0x30410008u,
        "Selected-result capacity is not increased with topology capacity");ssd_catalog_destroy(c);
    c=fresh();c->name_count=SSD_MAX_BYTES/2;
    check(reserve(c,1)==ERROR_NOT_ENOUGH_MEMORY && ssd_catalog_failure_code(c,8)==0x30220008u,
        "Name-length guard has its own fixed code");ssd_catalog_destroy(c);
    c=fresh();c->name_count=SSD_MAX_BYTES/2-1;c->name_capacity=SSD_MAX_BYTES/4+1;
    check(reserve(c,1)==ERROR_NOT_ENOUGH_MEMORY && ssd_catalog_failure_code(c,8)==0x30220008u,
        "Name-arena growth guard executes before allocation");ssd_catalog_destroy(c);
    c=fresh();c->name_count=c->name_capacity=SSD_MAX_BYTES/2;
    check(reserve(c,0)==ERROR_NOT_ENOUGH_MEMORY && ssd_catalog_failure_code(c,8)==0x30230008u,
        "Aggregate allocation budget is distinct from the record cap");ssd_catalog_destroy(c);
    c=fresh();fail_allocation=1;allocation_number=0;
    check(reserve(c,1)==ERROR_NOT_ENOUGH_MEMORY && ssd_catalog_failure_code(c,8)==0x30240008u,
        "Actual node allocation failure is not mislabelled as a cap");ssd_catalog_destroy(c);
    c=fresh();fail_allocation=2;allocation_number=0;
    check(reserve(c,1)==ERROR_NOT_ENOUGH_MEMORY && ssd_catalog_failure_code(c,8)==0x30250008u,
        "Actual name allocation failure is distinct");ssd_catalog_destroy(c);
    fail_allocation=1;allocation_number=0;c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    check(!c && ssd_catalog_failure_code(c,8)==0x30060008u,"Catalogue creation allocation failure retains its cause");
    fail_allocation=allocation_number=0;c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    SsdRoot root={L"C:\\fixture",1,5};USN_JOURNAL_DATA before={0},after={0};
    io_mode=3;io_number=0;fail_allocation=1;allocation_number=0;
    check(ssd_catalog_read_volume(c,INVALID_HANDLE_VALUE,&root,1,&before,&after)==8 &&
        ssd_catalog_failure_code(c,8)==0x30160008u,"Volume buffer allocation fails at its actual guard");ssd_catalog_destroy(c);
    fail_allocation=allocation_number=0;c=ssd_catalog_create(NULL,GetTickCount64()+30000);
    io_mode=2;io_number=0;
    check(ssd_catalog_read_volume(c,INVALID_HANDLE_VALUE,&root,1,&before,&after)==8 &&
        ssd_catalog_failure_code(c,8)==0x30200008u,"Real production IO-error propagation is distinct from every internal cap");ssd_catalog_destroy(c);
    c=ssd_catalog_create(NULL,GetTickCount64()+30000);io_mode=1;io_number=0;
    DWORD error=ssd_catalog_read_volume(c,INVALID_HANDLE_VALUE,&root,1,&before,&after);
    check(error==0x8007000eu && ssd_catalog_failure_code(c,error)==error,"Full extended OS status survives without truncation");ssd_catalog_destroy(c);
    c=fresh();check(!reserve(c,2),"Successful reservation is unchanged");
    c->nodes[0]=(SsdNode){.id=5,.parent=5,.attributes=FILE_ATTRIBUTE_DIRECTORY};c->count=1;c->slots[slot_for(5)]=0;c->diagnostic_stage=7;
    fail_allocation=1;allocation_number=0;
    check(project_root(c,&root,0,NULL,NULL)==8 && ssd_catalog_failure_code(c,8)==0x30760008u,
        "Projection allocation is diagnosed before any filesystem access");ssd_catalog_destroy(c);
    root=(SsdRoot){L"C:\\fixture",1,20};c=topology();fail_allocation=1;allocation_number=0;
    check(!ssd_catalog_begin_selected_files(c,&root,1) && !allocation_number,
        "Production scope compaction needs no allocation or duplicate catalogue");
    check(c->count==2 && c->directory_count==2 && c->name_count==4 && c->nodes[0].id==21 &&
        c->nodes[0].parent==20 && c->nodes[1].id==20 && find(c,21)==0 && find(c,20)==1 &&
        find(c,10)==SSD_NONE && find(c,11)==SSD_NONE && c->names[0]==L'b' && c->names[2]==L'c' &&
        !c->names[4] && !c->names[6] && c->nodes[0].first_child==SSD_NONE && c->nodes[0].next_child==SSD_NONE,
        "In-place remap retains exact names and parent IDs and removes outside slots/name bytes");ssd_catalog_destroy(c);
    c=topology();c->node_capacity=SSD_MAX_BYTES;
    check(ssd_catalog_begin_selected_files(c,&root,1)==8 && !ssd_catalog_count(c) &&
        ssd_catalog_failure_code(c,8)==0x30330008u,"Compaction includes retained capacity in the unchanged byte budget");ssd_catalog_destroy(c);
    c=topology();c->nodes[1].name_offset=MAXDWORD;
    check(ssd_catalog_begin_selected_files(c,&root,1)==ERROR_INVALID_DATA && !ssd_catalog_count(c),
        "Compaction refuses an out-of-bounds name before touching it");ssd_catalog_destroy(c);
    c=topology();c->nodes[2].name_offset=0;
    check(ssd_catalog_begin_selected_files(c,&root,1)==ERROR_INVALID_DATA && !ssd_catalog_count(c),
        "Nonmonotonic names cannot overwrite a later unread value");ssd_catalog_destroy(c);
    for(DWORD point=0;point<3;point++) {
        c=topology();HANDLE cancellation=CreateEventW(NULL,TRUE,FALSE,NULL);check(cancellation!=NULL,"Create bounded cancellation fixture");
        c->cancellation=cancellation;cancel_after=point==0?1:(point==1?2:4);wait_number=0;
        check(ssd_catalog_begin_selected_files(c,&root,1)==ERROR_CANCELLED && !ssd_catalog_count(c),
            point==0?"Classification cancellation discards all output":
            (point==1?"Cancellation during node/name compaction discards all output":"Cancellation during slot rebuild discards all output"));
        ssd_catalog_destroy(c);CloseHandle(cancellation);
    }
    c=topology();c->deadline=GetTickCount64();
    check(compact_selected_directories(c)==ERROR_TIMEOUT,"In-place compaction retains the existing deadline");ssd_catalog_destroy(c);
    c=topology();for(DWORD i=0;i<SSD_HASH_SIZE;i++)c->slots[i]=0;
    check(insert(c,1)==ERROR_INVALID_DATA,"A saturated hash cannot cause unbounded insertion");ssd_catalog_destroy(c);
    c=topology();c->progress=progress;progress_calls=0;progress_records=0;progress_cancel_after=2;
    check(ssd_catalog_begin_selected_files(c,&root,1)==ERROR_CANCELLED && !ssd_catalog_count(c) && progress_calls==2,
        "A cancelled real progress sink discards the incomplete catalogue");ssd_catalog_destroy(c);
    check(ssd_catalog_failure_code(NULL,0)==0,"Success is unchanged");
    /* Supply only kernel completion and time; the actual control loop and real
       cancellation event decide whether an outstanding read survives or stops. */
    for(DWORD scenario=0;scenario<3;scenario++) {
        pending_now=pending_last_activity=1000;pending_waits=pending_cancel_calls=progress_calls=0;
        pending_finish_after=4;pending_cancel_after=scenario==1 ? 2 : 0;pending_idle_guard=scenario!=2;
        pending_cancellation=CreateEventW(NULL,TRUE,FALSE,NULL);
        check(pending_cancellation!=NULL,"Create actual pending-read cancellation event");
        c=ssd_catalog_create(pending_cancellation,pending_now+(scenario==2?125000:600000));
        check(c!=NULL,"Create bounded pending-I/O catalogue");
        c->diagnostic_stage=2;c->records_read=42;ssd_catalog_set_progress(c,pending_progress,NULL);
        io_mode=4;DWORD returned=0;BYTE output[8]={0};
        DWORD result=control(c,INVALID_HANDLE_VALUE,FSCTL_ENUM_USN_DATA,NULL,0,output,sizeof(output),&returned);
        if(scenario==0)check(result==ERROR_CANCELLED && returned==0 && pending_waits==3 && !progress_calls && pending_cancel_calls==1,
            "A pending read with no returned metadata cannot manufacture progress or evade idle cancellation");
        else if(scenario==1)check(result==ERROR_CANCELLED && pending_cancel_calls==1 && pending_waits==2,
            "Explicit cancellation still cancels the exact pending operation and returns no result");
        else check(result==ERROR_TIMEOUT && pending_cancel_calls==1 && pending_waits==3,
            "Pending-read activity cannot extend the fixed hard deadline");
        ssd_catalog_destroy(c);CloseHandle(pending_cancellation);pending_cancellation=pending_event=NULL;
    }
    /* Cancellation diagnostics: stage, category and Win32 keep their exact legacy
       positions and a previously unused marker above them carries the first causal
       stop source with bounded pending-call timing. No existing code changes. */
    pending_now=0;pending_last_activity=0;io_mode=0;
    c=fresh();
    check(c->diagnostic_stage==2 && !c->diagnostic_kind,"A fresh catalogue starts in the directory stage with no category");
    DWORD idle_code=ssd_catalog_stop_code(c,ERROR_CANCELLED,SSD_STOP_ORIGIN_IDLE);
    check((idle_code&0xc0000000u)==0x40000000u,"A cancellation uses the distinct marked diagnostic envelope");
    check(idle_code==(0x40000000u|((DWORD)SSD_STOP_ORIGIN_IDLE<<24)|(2u<<20)|(ERROR_CANCELLED&0xffffu)),
        "An idle stop with no outstanding call records cause, stage, category and Win32 exactly");
    check((idle_code&0x00ffffffu)==(ssd_catalog_failure_code(c,ERROR_CANCELLED)&0x00ffffffu),
        "Stage, category and Win32 occupy the same bits as the legacy failure shape");
    check((idle_code&0x00ffffffu)==0x002004c7u,
        "The recorded cancellation decodes to the exact observed historical low bits");
    check((ssd_catalog_failure_code(c,8)&0xc0000000u)==0,"Legacy failure codes never set the cancellation marker");
    check(ssd_catalog_stop_code(c,ERROR_NOT_ENOUGH_MEMORY,SSD_STOP_ORIGIN_IDLE)==ssd_catalog_failure_code(c,ERROR_NOT_ENOUGH_MEMORY),
        "A non-cancellation error still returns the unchanged failure code");
    for(DWORD origin=SSD_STOP_ORIGIN_UNKNOWN;origin<=SSD_STOP_ORIGIN_PIPE_TIMEOUT;origin++)
        check(((ssd_catalog_stop_code(c,ERROR_CANCELLED,origin)>>24)&7u)==origin,"Every recorded stop source survives the numeric diagnostic");
    c->diagnostic_stage=7;
    check(((ssd_catalog_stop_code(c,ERROR_CANCELLED,SSD_STOP_ORIGIN_PEER)>>20)&15u)==7,
        "A stop in any later stage retains that stage");
    ssd_catalog_destroy(c);
    /* A stop observed while a Windows metadata call was outstanding is the only
       in-code discriminator between a stalled kernel call and in-process work. */
    pending_now=pending_last_activity=1000;pending_waits=pending_cancel_calls=progress_calls=0;
    pending_finish_after=4;pending_cancel_after=0;pending_idle_guard=TRUE;
    pending_cancellation=CreateEventW(NULL,TRUE,FALSE,NULL);
    check(pending_cancellation!=NULL,"Create the pending-call cancellation event");
    c=ssd_catalog_create(pending_cancellation,pending_now+600000);
    check(c!=NULL,"Create the pending-call catalogue");
    c->diagnostic_stage=2;c->records_read=42;
    io_mode=4;DWORD pending_returned=0;BYTE pending_output[8]={0};
    check(control(c,INVALID_HANDLE_VALUE,FSCTL_ENUM_USN_DATA,NULL,0,pending_output,sizeof(pending_output),&pending_returned)==ERROR_CANCELLED,
        "The production control loop stops the outstanding call on the real cancellation event");
    DWORD pending_code=ssd_catalog_stop_code(c,ERROR_CANCELLED,SSD_STOP_ORIGIN_UNKNOWN);
    check(((pending_code>>27)&1u)==1u,"A stop seen while a Windows call was outstanding records that fact");
    check(((pending_code>>28)&3u)==3u,"A call outstanding for at least a minute is reported in the top bucket");
    check((pending_code&0x00ffffffu)==0x002004c7u,"The pending-call stop retains the historical stage, category and Win32 bits");
    ssd_catalog_destroy(c);CloseHandle(pending_cancellation);pending_cancellation=pending_event=NULL;
    pending_now=0;io_mode=0;
    printf("%lu production diagnostic assertions passed; no live IO or privileged effect.\n",passes);return 0;
}
