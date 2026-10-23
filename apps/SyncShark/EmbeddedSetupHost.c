#include "EmbeddedSetupHost.h"
#include <wincrypt.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

static DWORD resource(WORD id,const BYTE **bytes,DWORD *length) {
    HRSRC found=FindResourceW(NULL,MAKEINTRESOURCEW(id),MAKEINTRESOURCEW(10));
    if(!found)return ERROR_RESOURCE_DATA_NOT_FOUND;
    *length=SizeofResource(NULL,found);HGLOBAL loaded=LoadResource(NULL,found);
    *bytes=loaded?LockResource(loaded):NULL;
    return !*bytes || *length<1 || *length>524288?ERROR_INVALID_DATA:0;
}
static DWORD write_pipe(HANDLE pipe,const char *value,DWORD size) {
    while(size){DWORD sent=0;if(!WriteFile(pipe,value,size,&sent,NULL))return GetLastError();if(!sent)return ERROR_BROKEN_PIPE;value+=sent;size-=sent;}return 0;
}
static DWORD text(HANDLE pipe,const char *value){return write_pipe(pipe,value,(DWORD)strlen(value));}
static DWORD base64(HANDLE pipe,const BYTE *value,DWORD length) {
    DWORD size=0;if(!CryptBinaryToStringA(value,length,CRYPT_STRING_BASE64|CRYPT_STRING_NOCRLF,NULL,&size))return GetLastError();
    char *encoded=HeapAlloc(GetProcessHeap(),0,size);if(!encoded)return ERROR_NOT_ENOUGH_MEMORY;
    DWORD error=CryptBinaryToStringA(value,length,CRYPT_STRING_BASE64|CRYPT_STRING_NOCRLF,encoded,&size)?text(pipe,encoded):GetLastError();
    SecureZeroMemory(encoded,size);HeapFree(GetProcessHeap(),0,encoded);return error;
}
DWORD ssd_run_embedded_setup(const WCHAR *app,const char *invocation,DWORD timeout,BOOL terminate_on_timeout) {
    const BYTE *assembly,*script;DWORD assembly_size,script_size,error;
    if((error=resource(201,&assembly,&assembly_size)) || (error=resource(202,&script,&script_size)))return error;
    HANDLE input_read=NULL,input_write=NULL,sink=INVALID_HANDLE_VALUE;
    PROCESS_INFORMATION child={0};LPPROC_THREAD_ATTRIBUTE_LIST attributes=NULL;BOOL attributes_ready=FALSE;
    SECURITY_ATTRIBUTES security={sizeof(security),NULL,TRUE};
    if(!CreatePipe(&input_read,&input_write,&security,65536) || !SetHandleInformation(input_write,HANDLE_FLAG_INHERIT,0)){error=GetLastError();goto done;}
    sink=CreateFileW(L"NUL",GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,&security,OPEN_EXISTING,0,NULL);
    if(sink==INVALID_HANDLE_VALUE){error=GetLastError();goto done;}
    WCHAR shell[4096],command[4300],system[4096],environment[18000],windows[4096];
    DWORD n=GetSystemDirectoryW(system,ARRAYSIZE(system));if(!n || n>=ARRAYSIZE(system)-64){error=ERROR_INVALID_NAME;goto done;}
    swprintf(shell,ARRAYSIZE(shell),L"%ls\\WindowsPowerShell\\v1.0\\powershell.exe",system);
    swprintf(command,ARRAYSIZE(command),L"\"%ls\" -NoLogo -NoProfile -NonInteractive -Command -",shell);
    n=GetWindowsDirectoryW(windows,ARRAYSIZE(windows));if(!n || n>=ARRAYSIZE(windows)){error=ERROR_INVALID_NAME;goto done;}
    // Preserve the established native clean-environment and handle allowlists.
    int used=swprintf(environment,ARRAYSIZE(environment),L"PATH=%ls%cSystemRoot=%ls%cTEMP=%ls\\Temp%cTMP=%ls\\Temp%cwindir=%ls%c",system,0,windows,0,windows,0,windows,0,windows,0);
    if(used<0 || (size_t)used+1>=ARRAYSIZE(environment)){error=ERROR_BUFFER_OVERFLOW;goto done;}environment[used]=0;environment[used+1]=0;
    SIZE_T required=0;InitializeProcThreadAttributeList(NULL,1,0,&required);attributes=HeapAlloc(GetProcessHeap(),0,required);
    if(!attributes || !InitializeProcThreadAttributeList(attributes,1,0,&required)){error=GetLastError();goto done;}attributes_ready=TRUE;
    HANDLE inherited[]={input_read,sink};
    if(!UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,inherited,sizeof(inherited),NULL,NULL)){error=GetLastError();goto done;}
    STARTUPINFOEXW start={0};start.StartupInfo.cb=sizeof(start);start.StartupInfo.dwFlags=STARTF_USESTDHANDLES|STARTF_USESHOWWINDOW;
    start.StartupInfo.hStdInput=input_read;start.StartupInfo.hStdOutput=sink;start.StartupInfo.hStdError=sink;start.StartupInfo.wShowWindow=SW_HIDE;start.lpAttributeList=attributes;
    if(!CreateProcessW(shell,command,NULL,NULL,TRUE,CREATE_NO_WINDOW|CREATE_UNICODE_ENVIRONMENT|EXTENDED_STARTUPINFO_PRESENT,environment,system,&start.StartupInfo,&child)){error=GetLastError();goto done;}
    CloseHandle(input_read);input_read=NULL;
    error=text(input_write,"& { $ErrorActionPreference='Stop'; $env:PSModulePath=$PSHOME+'\\Modules'; try { [void][Reflection.Assembly]::Load([Convert]::FromBase64String('");
    if(!error)error=base64(input_write,assembly,assembly_size);
    if(!error)error=text(input_write,"')); . ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('");
    if(!error)error=base64(input_write,script,script_size);
    if(!error)error=text(input_write,"')))); ");
    if(app) {
        if(!error)error=text(input_write,"$app=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('");
        if(!error)error=base64(input_write,(const BYTE*)app,(DWORD)wcslen(app)*2);
        if(!error)error=text(input_write,"')); ");
    }
    if(!error)error=text(input_write,invocation);
    if(!error)error=text(input_write,"; exit 0 } catch { try { exit (Get-SyncSharkDiscoverySetupFailureCode $_.Exception) } catch { exit 1 } } }\r\n");
    CloseHandle(input_write);input_write=NULL;
    if(error) {
        // An incomplete, single-line script cannot enter its invocation. Keep the
        // previous setup behaviour; updater uses the non-terminating profile.
        if(terminate_on_timeout){TerminateProcess(child.hProcess,error);WaitForSingleObject(child.hProcess,10000);}
        goto done;
    }
    DWORD waited=WaitForSingleObject(child.hProcess,timeout);
    if(waited!=WAIT_OBJECT_0){error=ERROR_TIMEOUT;if(terminate_on_timeout){TerminateProcess(child.hProcess,error);WaitForSingleObject(child.hProcess,10000);}}
    else if(!GetExitCodeProcess(child.hProcess,&error))error=GetLastError();
done:
    if(attributes){if(attributes_ready)DeleteProcThreadAttributeList(attributes);HeapFree(GetProcessHeap(),0,attributes);}
    HANDLE handles[]={input_read,input_write,sink,child.hThread,child.hProcess};
    for(DWORD i=0;i<ARRAYSIZE(handles);i++)if(handles[i] && handles[i]!=INVALID_HANDLE_VALUE)CloseHandle(handles[i]);
    return error;
}
