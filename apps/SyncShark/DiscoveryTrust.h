#ifndef SYNC_SHARK_DISCOVERY_TRUST_H
#define SYNC_SHARK_DISCOVERY_TRUST_H
#include "DiscoveryInventory.h"
#define SSD_TRUST_MAX_MEMBERS 512
typedef struct { WCHAR path[256];BYTE sha256[32]; } SsdTrustedMember;
typedef struct {
    WCHAR version[64],user_sid[192],app_directory[2048];
    BYTE worker_sha256[32];DWORD count;
    SsdTrustedMember members[SSD_TRUST_MAX_MEMBERS];
} SsdTrustManifest;
DWORD ssd_hash_file(HANDLE file,BYTE sha256[32]);
DWORD ssd_verify_product_signature(const WCHAR *path,HANDLE file);
DWORD ssd_parse_trust_manifest(const BYTE *bytes,DWORD length,SsdTrustManifest *manifest);
DWORD ssd_load_trust_manifest(const WCHAR *path,SsdTrustManifest *manifest,HANDLE *held_file);
DWORD ssd_hold_trusted_app(const SsdTrustManifest *manifest,DWORD client_pid,HANDLE **held,DWORD *count);
/* Read-only Discovery caller: image signed by a listed publisher, any path. */
DWORD ssd_hold_signed_caller(DWORD client_pid,HANDLE **held,DWORD *count);
void ssd_release_trusted_app(HANDLE *held,DWORD count);
DWORD ssd_hold_protected_file(const WCHAR *path,HANDLE medium_impersonation_token,HANDLE **held,DWORD *count);
#endif
