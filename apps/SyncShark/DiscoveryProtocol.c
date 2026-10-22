#include "DiscoveryProtocol.h"
#include <string.h>
#include <wchar.h>
static const BYTE magic[8]={'S','S','D','I','S','C','0','1'};
static DWORD u32(const BYTE *p){DWORD n;memcpy(&n,p,4);return n;}
static void put32(BYTE *p,DWORD n){memcpy(p,&n,4);}
static void put64(BYTE *p,ULONGLONG n){memcpy(p,&n,8);}
DWORD ssd_request_header(SsdRequest *r,const BYTE *header,DWORD length) {
    if(!r || !header || length!=SSD_WIRE_HEADER_BYTES || memcmp(header,magic,8) || (u32(header+8)!=1 && u32(header+8)!=2) ||
        !u32(header+44) || u32(header+44)>128)return ERROR_INVALID_DATA;
    memset(r,0,sizeof(*r));memcpy(r->nonce,header+12,32);r->operation=u32(header+8);r->root_count=u32(header+44);r->volume_serial=u32(header+48);return 0;
}
DWORD ssd_request_add_root(SsdRequest *r,DWORD index,ULONGLONG id,const BYTE *utf16,DWORD units) {
    if(!r || index>=r->root_count || !id || !utf16 || units<3 || units>32760-512 || r->roots[index].path)return ERROR_INVALID_DATA;
    for(DWORD i=0;i<r->root_count;i++)if(r->roots[i].file_id==id)return ERROR_DUP_NAME;
    WCHAR *path=HeapAlloc(GetProcessHeap(),0,(units+1)*sizeof(WCHAR));if(!path)return ERROR_NOT_ENOUGH_MEMORY;
    memcpy(path,utf16,units*2);path[units]=0;
    if(!((path[0]>=L'A'&&path[0]<=L'Z')||(path[0]>=L'a'&&path[0]<=L'z'))||path[1]!=L':'||path[2]!=L'\\')goto invalid;
    for(DWORD i=0;i<units;i++) {
        WCHAR ch=path[i];if(ch<32 || (i!=1 && ch==L':') || ch==L'/' || ch==L'"'||ch==L'<'||ch==L'>'||ch==L'|'||ch==L'*'||ch==L'?')goto invalid;
        if(ch>=0xd800&&ch<=0xdbff){if(++i>=units||path[i]<0xdc00||path[i]>0xdfff)goto invalid;}
        else if(ch>=0xdc00&&ch<=0xdfff)goto invalid;
    }
    r->roots[index]=(SsdRoot){path,r->volume_serial,id};return 0;
invalid:
    HeapFree(GetProcessHeap(),0,path);return ERROR_INVALID_NAME;
}
void ssd_request_free(SsdRequest *r) {
    if(!r)return;
    for(DWORD i=0;i<min(r->root_count,128u);i++)if(r->roots[i].path) {
        SecureZeroMemory((void*)r->roots[i].path,wcslen(r->roots[i].path)*2);HeapFree(GetProcessHeap(),0,(void*)r->roots[i].path);
    }
    SecureZeroMemory(r,sizeof(*r));
}
DWORD ssd_reply_header(const SsdRequest *r,BYTE *output,DWORD capacity) {
    if(!r||!output||capacity<SSD_WIRE_HEADER_BYTES||!r->root_count||r->root_count>128)return 0;
    memcpy(output,magic,8);put32(output+8,1);memcpy(output+12,r->nonce,32);put32(output+44,r->root_count);put32(output+48,r->volume_serial);
    return SSD_WIRE_HEADER_BYTES;
}
DWORD ssd_reply_item(const SsdItem *item,BYTE *output,DWORD capacity) {
    if(!item||!item->relative_path||!output||!item->file_id||!item->parent_id||item->root_index>=128||item->usn<0||
        !item->link_count||item->size>0x7fffffffffffffffULL)return 0;
    size_t units=wcslen(item->relative_path);
    if(!units||units>32760||capacity<64+units*2)return 0;
    put32(output,(DWORD)(60+units*2));put32(output+4,1);put32(output+8,item->root_index);
    put64(output+12,item->file_id);put64(output+20,item->parent_id);put64(output+28,(ULONGLONG)item->usn);
    put32(output+36,item->attributes);put32(output+40,item->link_count);put64(output+44,item->size);
    put64(output+52,((ULONGLONG)item->modified.dwHighDateTime<<32)|item->modified.dwLowDateTime);
    put32(output+60,(DWORD)units);memcpy(output+64,item->relative_path,units*2);return (DWORD)(64+units*2);
}
DWORD ssd_reply_progress(DWORD stage,ULONGLONG sequence,ULONGLONG records_read,BYTE *output,DWORD capacity) {
    if(!output||capacity<28||stage<1||stage>7||!sequence)return 0;
    put32(output,24);put32(output+4,4);put32(output+8,stage);
    put64(output+12,sequence);put64(output+20,records_read);return 28;
}
DWORD ssd_reply_end(DWORD count,const USN_JOURNAL_DATA *before,const USN_JOURNAL_DATA *after,BYTE *output,DWORD capacity) {
    if(!before||!after||!output||capacity<76||count>750000||!before->UsnJournalID||before->UsnJournalID!=after->UsnJournalID||
        before->FirstUsn<0||before->LowestValidUsn<0||before->NextUsn<before->FirstUsn||before->NextUsn<before->LowestValidUsn||
        after->NextUsn<before->NextUsn||after->FirstUsn<0||after->LowestValidUsn<0||
        after->FirstUsn>before->NextUsn||after->LowestValidUsn>before->NextUsn)return 0;
    put32(output,72);put32(output+4,2);put32(output+8,count);
    const USN_JOURNAL_DATA *points[]={before,after};
    for(DWORD i=0;i<2;i++) {
        BYTE *p=output+12+i*32;put64(p,points[i]->UsnJournalID);put64(p+8,(ULONGLONG)points[i]->FirstUsn);
        put64(p+16,(ULONGLONG)points[i]->NextUsn);put64(p+24,(ULONGLONG)points[i]->LowestValidUsn);
    }
    return 76;
}

/* Pure grow-only sizing policy, including the allocation overhead in both caps. */
DWORD ssd_journal_target(ULONGLONG requested,ULONGLONG current_max,ULONGLONG current_delta,ULONGLONG free_bytes,ULONGLONG *target) {
    if(!target || !requested || requested>8589934592ULL)return ERROR_INVALID_PARAMETER;
    *target=current_max;
    if(requested<=current_max && current_delta>=536870912ULL)return ERROR_ALREADY_EXISTS;
    ULONGLONG cap=free_bytes/20;
    if(cap>8589934592ULL)cap=8589934592ULL;
    ULONGLONG delta=current_delta>536870912ULL?current_delta:536870912ULL;
    cap=cap>delta?cap-delta:0;
    if(cap<current_max || (cap==current_max && requested>current_max))return ERROR_DISK_FULL;
    *target=requested<cap?requested:cap;
    if(*target<current_max)*target=current_max;
    return ERROR_SUCCESS;
}
