#ifndef SYNC_SHARK_DISCOVERY_PROTOCOL_H
#define SYNC_SHARK_DISCOVERY_PROTOCOL_H
#include "DiscoveryInventory.h"
#define SSD_WIRE_HEADER_BYTES 52u
#define SSD_WIRE_MAX_FRAME (60u+32760u*2u)
typedef struct {
    BYTE nonce[32];
    DWORD root_count, volume_serial, operation;
    ULONGLONG journal_maximum;
    SsdRoot roots[128];
} SsdRequest;
/* Buffer encoding is shared by the real pipe producer and its non-privileged
   fixture. Authentication and pipe creation are deliberately separate. */
DWORD ssd_journal_target(ULONGLONG requested,ULONGLONG current_max,ULONGLONG current_delta,ULONGLONG free_bytes,ULONGLONG *target);
DWORD ssd_request_header(SsdRequest *request,const BYTE *header,DWORD length);
DWORD ssd_request_add_root(SsdRequest *request,DWORD index,ULONGLONG id,const BYTE *utf16,DWORD units);
void ssd_request_free(SsdRequest *request);
DWORD ssd_reply_header(const SsdRequest *request,BYTE *output,DWORD capacity);
DWORD ssd_reply_item(const SsdItem *item,BYTE *output,DWORD capacity);
DWORD ssd_reply_progress(DWORD stage,ULONGLONG sequence,ULONGLONG records_read,BYTE *output,DWORD capacity);
DWORD ssd_reply_end(DWORD count,const USN_JOURNAL_DATA *before,const USN_JOURNAL_DATA *after,BYTE *output,DWORD capacity);
#endif
