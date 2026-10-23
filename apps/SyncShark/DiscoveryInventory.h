#ifndef SYNC_SHARK_DISCOVERY_INVENTORY_H
#define SYNC_SHARK_DISCOVERY_INVENTORY_H
#ifndef UNICODE
#define UNICODE
#endif
#include <windows.h>
#include <winioctl.h>
#include <stdint.h>

/* Private native component API, not an installed/privileged activation surface.
   The authenticated worker supplies the client token and held read-only volume.
   Callbacks contain admitted-root relative metadata only. An MFT result is a
   discovery hint: it never authorizes deletion or proves all hard-link names. */
typedef struct SsdCatalog SsdCatalog;
typedef struct SsdAdmission SsdAdmission;
typedef struct {
    const WCHAR *path;
    DWORD volume_serial;
    ULONGLONG file_id;
} SsdRoot;
typedef struct {
    DWORD root_index;
    const WCHAR *relative_path;
    ULONGLONG file_id, parent_id;
    LONGLONG usn;
    DWORD attributes, link_count;
    ULONGLONG size;
    FILETIME modified;
} SsdItem;
typedef BOOL (*SsdEmit)(void *context, const SsdItem *item);
/* Progress describes completed work only, never filenames or success. The fixed
   worker still limits the whole request to thirty minutes and idle work to two. */
#define SSD_MAX_DURATION_MS (30u * 60u * 1000u)
typedef BOOL (*SsdProgress)(void *context, DWORD stage, ULONGLONG records_read);

/* Retain this lease before opening the privileged volume and until projection
   finishes. Admission runs with the real medium client token, never the worker. */
DWORD ssd_admit_roots(const SsdRoot *roots, DWORD root_count, HANDLE client_token,
    SsdAdmission **admission);
const WCHAR *ssd_admission_volume(const SsdAdmission *admission);
void ssd_admission_close(SsdAdmission *admission);

SsdCatalog *ssd_catalog_create(HANDLE cancellation, ULONGLONG deadline);
void ssd_catalog_set_progress(SsdCatalog *catalog, SsdProgress progress, void *context);
void ssd_catalog_destroy(SsdCatalog *catalog);
DWORD ssd_catalog_add_page(SsdCatalog *catalog, const BYTE *page, DWORD length);
/* Two bounded passes retain the directory topology, then files beneath the
   admitted roots. Whole-volume topology permits at most1572864 directories
   within256MiB. In-place scope compaction precedes the unchanged750000-result
   limit. Unrelated volume-wide files never consume catalogue memory. */
DWORD ssd_catalog_begin_directories(SsdCatalog *catalog);
DWORD ssd_catalog_begin_selected_files(SsdCatalog *catalog, const SsdRoot *roots, DWORD root_count);
DWORD ssd_catalog_finish_selected_files(SsdCatalog *catalog);
DWORD ssd_catalog_read_volume(SsdCatalog *catalog, HANDLE overlapped_read_only_volume,
    const SsdRoot *roots, DWORD root_count, USN_JOURNAL_DATA *before, USN_JOURNAL_DATA *after);
DWORD ssd_catalog_project(SsdCatalog *catalog, const SsdRoot *roots, DWORD root_count,
    HANDLE client_impersonation_token, SsdEmit emit, void *context);
DWORD ssd_catalog_count(const SsdCatalog *catalog);
/* Failed worker exit: 0x30SKeeee, S=stage, K=allocation category, eeee=Win32.
   Stage:0 create,1 journal,2 directories,3 scope,4 files,5 reconcile,
   6 final journal,7 projection. Kind:0 underlying operation,1 record cap,
   2 name bound,3 total byte bound,4 node allocation,5 name allocation,
   6 buffer/catalogue allocation. Extended OS status is returned unchanged.
   Numeric-only diagnostic; no new wire frame. */
DWORD ssd_catalog_failure_code(const SsdCatalog *catalog, DWORD error);
/* Cancellation keeps stage, category and Win32 in their exact existing positions,
   so a reader that masks the low 24 bits decodes every code unchanged. The family
   is marked by bit 30 together with bits 27:24, which are zero in every legacy
   code. Bits 29:28, which the legacy 0x3 prefix held constant at 11, are reused
   for the pending bucket, so the top-level prefix deliberately moves from 0x3 to
   0x4 through 0x7 and a reader that requires the 0x3 prefix does not match a
   cancellation code at all and must fall back rather than mis-decode it:
     0x40MMMM: bit30 marks the cancellation envelope (0x40000000..0x7fffffff);
     [29:28] longest single pending Windows metadata call in the current work
       window (0 none, 1 under10s, 2 under60s, 3 at least60s);
     [27] a Windows call was still outstanding when the stop was observed;
     [26:24] first observed stop trigger, which is not necessarily the underlying
       cause. Stage [23:20], category [19:16] and Win32 [15:0] keep their exact
       legacy positions and meaning.
   This adds no wire frame, no log store, no path and no admission or deadline
   change; it rides the existing worker exit code that the fixed task already
   records as its last task result. */
#define SSD_STOP_ORIGIN_UNKNOWN 0u
#define SSD_STOP_ORIGIN_IDLE 1u
#define SSD_STOP_ORIGIN_DEADLINE 2u
#define SSD_STOP_ORIGIN_PEER 3u
#define SSD_STOP_ORIGIN_CLIENT_INPUT 4u
#define SSD_STOP_ORIGIN_PIPE_BROKEN 5u
#define SSD_STOP_ORIGIN_PIPE_TIMEOUT 6u
/* First observed stop trigger plus bounded pending-call timing for a stop that is
   a cancellation. Any other error returns the unchanged failure code. */
DWORD ssd_catalog_stop_code(const SsdCatalog *catalog, DWORD error, DWORD origin);
#endif
