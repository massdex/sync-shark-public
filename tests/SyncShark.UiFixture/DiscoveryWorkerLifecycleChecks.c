#include <windows.h>
#include <stdio.h>
#include <string.h>

/* The production worker control flow is compiled in unchanged: the real watcher,
   the real overlapped transport (io), the real progress/item emission and the real
   first-cause recording. Only the clock is injected, and the peer is a real named
   pipe created with the exact production flags instead of the earlier anonymous
   pipe with a faked WriteFile. Real wall time is resolved explicitly through
   GetProcAddress so transport bounds can still be measured. Nothing privileged is
   touched and no volume is read. */
static volatile LONG64 fixture_now=1000;
static ULONGLONG (WINAPI *real_ticks)(void);
static ULONGLONG fixture_tick(void){return (ULONGLONG)InterlockedCompareExchange64(&fixture_now,0,0);}
#define GetTickCount64 fixture_tick
#define wWinMain fixture_unused_worker_entry
#include "../../apps/SyncShark/DiscoveryWorker.c"
#undef wWinMain
#undef GetTickCount64

static unsigned checks;
/* Flush each line so a crash cannot hide how far the run reached. */
#define CHECK(x,label) do{if(!(x)){fprintf(stderr,"FAIL %s at %d\n",label,__LINE__);fflush(stderr);fflush(stdout);return 1;}checks++;printf("PASS %s\n",label);fflush(stdout);}while(0)

typedef struct { Worker worker; HANDLE server,client; } Fixture;

/* The server handle mirrors DiscoveryWorker.c connect_client: duplex, overlapped,
   first instance only, one instance, byte mode, no remote clients, 64 KiB buffers.
   The fixture supplies the peer with a plain synchronous handle; that only affects
   the peer's own handle, never the production handle semantics under test. */
static BOOL open_fixture(Fixture *f) {
    static LONG counter;
    WCHAR name[256];
    LONG n=InterlockedIncrement(&counter);
    if(swprintf(name,ARRAYSIZE(name),L"\\\\.\\pipe\\SyncSharkDiscoveryFixture-%lu-%ld",(unsigned long)GetCurrentProcessId(),(long)n)<0)return FALSE;
    f->server=CreateNamedPipeW(name,PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED|FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,65536,65536,0,NULL);
    if(f->server==INVALID_HANDLE_VALUE)return FALSE;
    f->client=CreateFileW(name,GENERIC_READ|GENERIC_WRITE,0,NULL,OPEN_EXISTING,0,NULL);
    if(f->client==INVALID_HANDLE_VALUE){CloseHandle(f->server);f->server=INVALID_HANDLE_VALUE;return FALSE;}
    OVERLAPPED op={0};op.hEvent=CreateEventW(NULL,TRUE,FALSE,NULL);
    if(!op.hEvent)return FALSE;
    DWORD error=ConnectNamedPipe(f->server,&op)?0:GetLastError();
    if(error==ERROR_PIPE_CONNECTED)error=0;
    else if(error==ERROR_IO_PENDING){
        DWORD bytes=0;
        if(WaitForSingleObject(op.hEvent,15000)!=WAIT_OBJECT_0||!GetOverlappedResult(f->server,&op,&bytes,FALSE))error=GetLastError();
        else error=0;
    }
    CloseHandle(op.hEvent);
    return error==0;
}
static BOOL begin(Fixture *f) {
    memset(&f->worker,0,sizeof(f->worker));
    InterlockedExchange64(&fixture_now,1000);
    f->worker.pipe=f->server;
    f->worker.cancel=CreateEventW(NULL,TRUE,FALSE,NULL);
    f->worker.done=CreateEventW(NULL,TRUE,FALSE,NULL);
    f->worker.deadline=1000+SSD_MAX_DURATION_MS;
    InterlockedExchange64(&f->worker.last_progress,1000);
    f->worker.watcher=CreateThread(NULL,0,watch_client,&f->worker,0,NULL);
    return f->worker.cancel&&f->worker.done&&f->worker.watcher;
}
static void park(Fixture *f) {
    if(!f->worker.done)return;
    SetEvent(f->worker.done);
    if(f->worker.watcher)WaitForSingleObject(f->worker.watcher,2000);
}
static void end(Fixture *f) {
    park(f);
    if(f->worker.watcher){CloseHandle(f->worker.watcher);f->worker.watcher=NULL;}
    HANDLE handles[]={f->worker.cancel,f->worker.done,f->server,f->client};
    for(unsigned i=0;i<ARRAYSIZE(handles);i++)if(handles[i]&&handles[i]!=INVALID_HANDLE_VALUE)CloseHandle(handles[i]);
    f->worker.cancel=f->worker.done=NULL;f->server=f->client=INVALID_HANDLE_VALUE;
}
static BOOL pipe_bytes(HANDLE handle,DWORD *available,DWORD *error) {
    if(available)*available=0;if(error)*error=0;
    if(PeekNamedPipe(handle,NULL,0,NULL,available,NULL))return TRUE;
    if(error)*error=GetLastError();return FALSE;
}
/* Peek-then-read so a byte-mode read cannot block on a partial message. */
/* Overlapped read on the production server handle, as the worker reads the request. */
static BOOL server_read(HANDLE server,BYTE *bytes,DWORD count,DWORD timeout_ms) {
    OVERLAPPED op={0};op.hEvent=CreateEventW(NULL,TRUE,FALSE,NULL);
    if(!op.hEvent)return FALSE;
    DWORD got=0;BOOL ok=ReadFile(server,bytes,count,&got,&op);
    DWORD error=ok?0:GetLastError();
    if(error==ERROR_IO_PENDING){
        if(WaitForSingleObject(op.hEvent,timeout_ms)==WAIT_OBJECT_0&&GetOverlappedResult(server,&op,&got,FALSE))error=0;
        else {error=ERROR_TIMEOUT;CancelIoEx(server,&op);}
    }
    CloseHandle(op.hEvent);
    return error==0&&got==count;
}
static DWORD client_read_exact(HANDLE client,BYTE *bytes,DWORD count,DWORD timeout_ms) {
    DWORD total=0;ULONGLONG start=real_ticks();
    while(total<count) {
        DWORD available=0;
        if(!pipe_bytes(client,&available,NULL))return (DWORD)-1;
        if(!available){
            if((DWORD)(real_ticks()-start)>=timeout_ms)return total;
            Sleep(1);continue;
        }
        DWORD want=count-total;if(want>available)want=available;
        DWORD got=0;
        if(!ReadFile(client,bytes+total,want,&got,NULL))return (DWORD)-1;
        total+=got;
    }
    return total;
}
static BOOL client_write_bytes(HANDLE client,const BYTE *bytes,DWORD count) {
    DWORD sent=0;return WriteFile(client,bytes,count,&sent,NULL)&&sent==count;
}
static BOOL fresh_pair(Fixture *f,const char *label) {
    if(!open_fixture(f)||!begin(f)){fprintf(stderr,"FAIL %s setup at %d\n",label,__LINE__);return FALSE;}
    return TRUE;
}

int main(void) {
    setvbuf(stdout,NULL,_IONBF,0);setvbuf(stderr,NULL,_IONBF,0);
    real_ticks=(ULONGLONG(WINAPI*)(void))GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"GetTickCount64");
    if(!real_ticks){fprintf(stderr,"FAIL real clock unavailable\n");return 1;}
    Fixture f;f.server=INVALID_HANDLE_VALUE;f.client=INVALID_HANDLE_VALUE;memset(&f.worker,0,sizeof(f.worker));

    /* 1. Production transport and watcher over the real pipe. */
    CHECK(open_fixture(&f),"Create the production-flag duplex overlapped pipe and connect a real client");
    CHECK(begin(&f),"Start the real watcher over the real pipe");
    for(unsigned i=1;i<=7;i++) {
        InterlockedExchange64(&fixture_now,1000+(LONG64)i*60000);
        CHECK(emit_progress(&f.worker,2,(ULONGLONG)i*1024),"Actual progress callback emits over the real pipe");
        CHECK(WaitForSingleObject(f.worker.cancel,50)==WAIT_TIMEOUT,"Advancing work remains live beyond the original two-minute total limit");
    }
    CHECK(f.worker.progress_sequence==7&&f.worker.last_stage==2&&f.worker.cancel_origin==0,
        "Seven advancing frames leave no stop source recorded");
    {BYTE frames[7*28];
     CHECK(client_read_exact(f.client,frames,sizeof(frames),2000)==(DWORD)sizeof(frames),"Real client receives seven complete 28-byte progress frames");
     BOOL exact=TRUE;
     for(unsigned i=0;i<7;i++){
         const BYTE *frame=frames+i*28;DWORD length=0,kind=0,stage=0;ULONGLONG sequence=0,records=0;
         memcpy(&length,frame,4);memcpy(&kind,frame+4,4);memcpy(&stage,frame+8,4);
         memcpy(&sequence,frame+12,8);memcpy(&records,frame+20,8);
         if(length!=24||kind!=4||stage!=2||sequence!=i+1||records!=(ULONGLONG)(i+1)*1024)exact=FALSE;
     }
     CHECK(exact,"Frames carry the exact bounded progress encoding in order");}

    /* 2. First causal stop source for each real watcher condition. */
    InterlockedExchange64(&fixture_now,(LONG64)f.worker.last_progress+120000);
    CHECK(WaitForSingleObject(f.worker.cancel,2000)==WAIT_OBJECT_0,"Two minutes of real inactivity stops the watcher");
    CHECK(f.worker.cancel_origin==SSD_STOP_ORIGIN_IDLE,"Idle stop is attributed to the idle guard");
    end(&f);
    CHECK(fresh_pair(&f,"deadline"),"Fresh real watcher for the deadline control");
    InterlockedExchange64(&fixture_now,(LONG64)f.worker.deadline);
    CHECK(WaitForSingleObject(f.worker.cancel,2000)==WAIT_OBJECT_0,"The hard ceiling stops the watcher");
    CHECK(f.worker.cancel_origin==SSD_STOP_ORIGIN_DEADLINE,"A ceiling stop is attributed to the deadline, not to idle");
    end(&f);
    CHECK(fresh_pair(&f,"peer"),"Fresh real watcher for the peer-closure control");
    CloseHandle(f.client);f.client=INVALID_HANDLE_VALUE;
    CHECK(WaitForSingleObject(f.worker.cancel,2000)==WAIT_OBJECT_0,"Real peer closure stops the watcher");
    CHECK(f.worker.cancel_origin==SSD_STOP_ORIGIN_PEER,"Peer closure is attributed to the closed pipe");
    end(&f);
    CHECK(fresh_pair(&f,"client input"),"Fresh real watcher for the unexpected-input control");
    {BYTE byte=0xA5;CHECK(client_write_bytes(f.client,&byte,1),"Harmless client sends one real pipe byte");}
    CHECK(WaitForSingleObject(f.worker.cancel,2000)==WAIT_OBJECT_0,"Unexpected post-request input stops the real watcher");
    CHECK(f.worker.cancel_origin==SSD_STOP_ORIGIN_CLIENT_INPUT,"Unexpected input is attributed to client bytes");
    end(&f);

    /* 3. The handle semantics the watcher depends on, on the production server handle. */
    CHECK(open_fixture(&f),"Create a pipe pair for handle semantics");
    {DWORD available=0,error=0;
     CHECK(pipe_bytes(f.server,&available,&error)&&available==0,"PeekNamedPipe reports a healthy idle peer with no bytes");
     BYTE byte=1;CHECK(client_write_bytes(f.client,&byte,1),"One client byte is written");
     CHECK(pipe_bytes(f.server,&available,&error)&&available==1,"PeekNamedPipe reports exactly one available byte");
     BYTE got=0;CHECK(server_read(f.server,&got,1,1000)&&got==1,"The server consumes that exact byte");}
    CloseHandle(f.client);f.client=INVALID_HANDLE_VALUE;
    {DWORD available=0,error=0;
     CHECK(!pipe_bytes(f.server,&available,&error)&&error==ERROR_BROKEN_PIPE,"A closed peer makes PeekNamedPipe fail with ERROR_BROKEN_PIPE");}
    end(&f);

    /* 4. The terminal sequence and the disputed acknowledgement ordering. */
    USN_JOURNAL_DATA before={0},after={0};before.UsnJournalID=1;before.NextUsn=100;after.UsnJournalID=1;after.NextUsn=120;
    BYTE end_frame[128];DWORD end_count=ssd_reply_end(0,&before,&after,end_frame,sizeof(end_frame));
    CHECK(end_count==76,"Production terminal frame builds with a valid journal pair");
    CHECK(fresh_pair(&f,"terminal"),"Fresh real watcher for the terminal sequence");
    CHECK(WaitForSingleObject(f.worker.cancel,0)==WAIT_TIMEOUT,"The single pre-completion stop check passes on a healthy run");
    SetEvent(f.worker.done);
    CHECK(WaitForSingleObject(f.worker.watcher,2000)==WAIT_OBJECT_0,"Signalling completion stops the watcher");
    CHECK(io(f.worker.pipe,TRUE,end_frame,end_count,f.worker.deadline)==0,"Production transport writes the terminal frame");
    {BYTE terminal[128];
     CHECK(client_read_exact(f.client,terminal,end_count,2000)==end_count,"Real client receives the complete terminal frame");
     DWORD length=0,kind=0;memcpy(&length,terminal,4);memcpy(&kind,terminal+4,4);
     CHECK(length==72&&kind==2,"Terminal frame carries the exact end encoding");
     BYTE ack=0xA5;CHECK(client_write_bytes(f.client,&ack,1),"Client acknowledges only the validated terminal frame");
     BYTE read=0;
     CHECK(io(f.worker.pipe,FALSE,&read,1,f.worker.deadline)==0&&read==0xA5,"Production transport reads the acknowledgement and the success condition holds");
     CHECK(WaitForSingleObject(f.worker.cancel,0)==WAIT_TIMEOUT&&f.worker.cancel_origin==0,"The terminal exchange produced no stop source");}
    end(&f);
    CHECK(fresh_pair(&f,"late stop"),"Fresh real watcher for the late-stop control");
    CHECK(WaitForSingleObject(f.worker.cancel,0)==WAIT_TIMEOUT,"Pre-completion check passes before the control stop");
    SetEvent(f.worker.done);
    CHECK(WaitForSingleObject(f.worker.watcher,2000)==WAIT_OBJECT_0,"Watcher exits on completion");
    SetEvent(f.worker.cancel);
    CHECK(io(f.worker.pipe,TRUE,end_frame,end_count,f.worker.deadline)==0,"Terminal frame still writes after a late stop");
    {BYTE terminal[128];
     CHECK(client_read_exact(f.client,terminal,end_count,2000)==end_count,"Client still receives the terminal frame");
     BYTE ack=0xA5;CHECK(client_write_bytes(f.client,&ack,1),"Client still acknowledges");
     BYTE read=0;
     CHECK(io(f.worker.pipe,FALSE,&read,1,f.worker.deadline)==0&&read==0xA5,
         "The transport never reads the stop event, so a late stop cannot change the terminal return");}
    end(&f);
    CHECK(fresh_pair(&f,"early stop"),"Fresh real watcher for the early-stop control");
    {BYTE byte=0xA5;CHECK(client_write_bytes(f.client,&byte,1),"Client sends input before completion");}
    CHECK(WaitForSingleObject(f.worker.cancel,2000)==WAIT_OBJECT_0,"The watcher observes that input");
    CHECK(f.worker.cancel_origin==SSD_STOP_ORIGIN_CLIENT_INPUT,"The early stop records the client-input cause");
    CHECK(WaitForSingleObject(f.worker.cancel,0)==WAIT_OBJECT_0,
        "A stop observed before the check is the rejection path, so no result is published");
    end(&f);

    /* 5. Transport bounds, so a reporting gap can be attributed instead of assumed. */
    CHECK(fresh_pair(&f,"progress transport"),"Fresh real watcher for transport bounds");
    {ULONGLONG start=real_ticks();
     CHECK(emit_progress(&f.worker,3,1024),"A progress frame still completes with a non-reading peer");
     CHECK((DWORD)(real_ticks()-start)<1000,"A 28-byte progress frame cannot block on a non-reading peer");}
    park(&f);
    {BYTE chunk[4096];memset(chunk,0,sizeof(chunk));unsigned timeouts=0;ULONGLONG start=real_ticks();
     for(unsigned i=0;i<64;i++){
         DWORD error=io(f.worker.pipe,TRUE,chunk,sizeof(chunk),fixture_tick()+300);
         if(error==ERROR_TIMEOUT){timeouts++;break;}
         if(error){fprintf(stderr,"FAIL unexpected transport error %lu\n",(unsigned long)error);return 1;}
     }
     CHECK(timeouts==1,"A full pipe buffer makes the production transport time out instead of stalling");
     CHECK((DWORD)(real_ticks()-start)>=250&&(DWORD)(real_ticks()-start)<5000,"The transport bound comes from the supplied deadline");}
    /* A cancelled blocked write can leave a few kilobytes of residual space, so the
       refusal is driven with full-size item frames inside a bounded attempt count. */
    {static WCHAR long_path[15001];
     for(unsigned i=0;i<15000;i++)long_path[i]=L'a';long_path[15000]=0;
     SsdItem item={.root_index=0,.relative_path=long_path,.file_id=1,.parent_id=2,.usn=1,.attributes=0,.link_count=1,.size=0};
     unsigned attempts=0;BOOL refused=FALSE;
     for(;attempts<8&&!refused;attempts++){
         f.worker.cancel_origin=0;f.worker.deadline=fixture_tick()+300;
         if(!emit_item(&f.worker,&item))refused=TRUE;
     }
     CHECK(refused,"A non-reading peer eventually refuses an item emission");
     CHECK(f.worker.cancel_origin==SSD_STOP_ORIGIN_PIPE_TIMEOUT,"A transport timeout is recorded as its own cause");
     CHECK(attempts<8,"The refusal is reached inside the bounded attempt count");}
    end(&f);
    CHECK(fresh_pair(&f,"closed peer transport"),"Fresh real watcher for the closed-peer transport cause");
    CloseHandle(f.client);f.client=INVALID_HANDLE_VALUE;park(&f);
    {BOOL broke=FALSE,refused=FALSE;
     for(unsigned i=0;i<2&&!broke;i++){
         f.worker.cancel_origin=0;
         if(!emit_progress(&f.worker,5u+i,4096u+i*1024u))refused=TRUE;
         if(f.worker.cancel_origin==SSD_STOP_ORIGIN_PIPE_BROKEN)broke=TRUE;
     }
     CHECK(refused,"Emission against a closed peer is refused");
     CHECK(broke,"A closed peer is recorded as a broken-pipe cause");}
    end(&f);

    printf("PASS %u real worker lifecycle and transport checks; production watcher, transport, first-cause recording and terminal sequence over a real production-flag overlapped named pipe, controlled clock, no elevation and no volume read.\n",checks);
    return 0;
}
