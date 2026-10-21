#define COBJMACROS
#define CINTERFACE
#define CONST_VTABLE
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <shellapi.h>
#include <wchar.h>
#include <string.h>
#include "DiscoveryTrust.h"

/* Registered product COM class. This DLL never opens a provider, database, credential,
   or recursively enumerates a directory. The signed application revalidates every action. */
static const GUID command_class = {0xb2251d9a,0x7803,0x4f1f,{0x8f,0xe8,0xcb,0xda,0x28,0x4b,0xd5,0xe9}};
static HMODULE module_handle;
static LONG live_objects, server_locks;
static const WCHAR *labels[] = {L"Sync Shark",L"Open in Sync Shark",L"Open online",L"Share and Manage Links",
    L"Make Available Offline",L"Make Online Only",L"Sync This Folder Pair"};
static const WCHAR *verbs[] = {L"",L"open",L"web",L"share",L"offline",L"online-only",L"sync"};
typedef struct Command { IExplorerCommand iface; LONG references; unsigned index; } Command;
typedef struct Commands { IEnumExplorerCommand iface; LONG references; unsigned next; } Commands;
typedef struct Factory { IClassFactory iface; LONG references; } Factory;
static HRESULT make_command(unsigned index, IExplorerCommand **output);
static HRESULT make_enumerator(unsigned next, IEnumExplorerCommand **output);
static HRESULT copy_text(const WCHAR *text, LPWSTR *output) {
    if (!output) return E_POINTER;
    *output = NULL; size_t length = wcslen(text) + 1;
    *output = CoTaskMemAlloc(length * sizeof(WCHAR));
    if (!*output) return E_OUTOFMEMORY;
    memcpy(*output, text, length * sizeof(WCHAR)); return S_OK;
}
static BOOL executable_path(WCHAR *output, DWORD capacity, HANDLE *held) {
    *held=INVALID_HANDLE_VALUE;PWSTR local=NULL;
    if(FAILED(SHGetKnownFolderPath(&FOLDERID_LocalAppData,KF_FLAG_DEFAULT,NULL,&local)))return FALSE;
    WCHAR path[32768],prefix[32768],actual[32768];BOOL ok=FALSE;
    if(wcslen(local)+64>=ARRAYSIZE(path))goto done;
    wcscpy(path,local);wcscat(path,L"\\Massdex\\SyncShark\\active\\SyncShark.exe");
    wcscpy(prefix,L"C:\\Program Files\\Massdex\\SyncShark\\versions\\");
    *held=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,0,NULL);
    if(*held==INVALID_HANDLE_VALUE)goto done;
    DWORD length=GetFinalPathNameByHandleW(*held,actual,ARRAYSIZE(actual),FILE_NAME_NORMALIZED|VOLUME_NAME_DOS);
    BY_HANDLE_FILE_INFORMATION info;
    if(!length || length>=ARRAYSIZE(actual) || wcsncmp(actual,L"\\\\?\\",4) ||
        !GetFileInformationByHandle(*held,&info) || info.nNumberOfLinks!=1 ||
        (info.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_OFFLINE|FILE_ATTRIBUTE_REPARSE_POINT)))goto done;
    const WCHAR *resolved=actual+4;size_t base=wcslen(prefix);
    if(wcslen(resolved)<=base || CompareStringOrdinal(resolved,(int)base,prefix,(int)base,TRUE)!=CSTR_EQUAL)goto done;
    const WCHAR *version=resolved+base,*end=wcschr(version,L'\\');
    if(!end || end==version || _wcsicmp(end,L"\\SyncShark.exe"))goto done;
    unsigned dots=0;BOOL digit=FALSE;
    for(const WCHAR *at=version;at<end;at++) {
        if(*at==L'.'){if(!digit || ++dots>2)goto done;digit=FALSE;}
        else if(*at>=L'0' && *at<=L'9')digit=TRUE;else goto done;
    }
    if(dots!=2 || !digit || wcslen(resolved)+1>capacity || ssd_verify_product_signature(resolved,*held))goto done;
    // An active-pointer redirect must not make an old loaded extension dispatch
    // into another release, even when that release also has a valid signer.
    WCHAR module[32768];DWORD moduleLength=GetModuleFileNameW(module_handle,module,ARRAYSIZE(module));
    if(!moduleLength || moduleLength>=ARRAYSIZE(module))goto done;
    WCHAR *moduleEnd=wcsrchr(module,L'\\');
    if(!moduleEnd || _wcsicmp(moduleEnd,L"\\SyncShark.Explorer.dll"))goto done;
    *moduleEnd=0;size_t directoryLength=(size_t)(end-resolved);
    if(wcslen(module)!=directoryLength || CompareStringOrdinal(module,(int)directoryLength,resolved,(int)directoryLength,TRUE)!=CSTR_EQUAL)goto done;
    HANDLE token=NULL,medium=NULL,*protectedHandles=NULL;DWORD protectedCount=0;
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY|TOKEN_DUPLICATE,&token))goto done;
    BOOL duplicated=DuplicateTokenEx(token,TOKEN_QUERY,NULL,SecurityIdentification,TokenImpersonation,&medium);
    CloseHandle(token);if(!duplicated)goto done;
    DWORD protection=ssd_hold_protected_file(resolved,medium,&protectedHandles,&protectedCount);
    CloseHandle(medium);ssd_release_trusted_app(protectedHandles,protectedCount);
    if(protection)goto done;
    wcscpy(output,resolved);ok=TRUE;
done:
    CoTaskMemFree(local);if(!ok && *held!=INVALID_HANDLE_VALUE){CloseHandle(*held);*held=INVALID_HANDLE_VALUE;}return ok;
}
static BOOL ordinary_path(const WCHAR *path) {
    size_t length = wcslen(path);
    if (length < 3 || length > 32700 || !((path[0] >= L'A' && path[0] <= L'Z') ||
        (path[0] >= L'a' && path[0] <= L'z')) || path[1] != L':' || path[2] != L'\\') return FALSE;
    for (size_t i = 2; i < length; ++i)
        if (path[i] < 32 || path[i] == L'"' || path[i] == L':' || path[i] == L'/') return FALSE;
    return TRUE;
}
static HRESULT selected_path(IShellItemArray *items, LPWSTR *path) {
    if (!path) return E_POINTER; *path = NULL;
    if (!items) return E_INVALIDARG;
    DWORD count = 0; HRESULT hr = IShellItemArray_GetCount(items, &count);
    if (FAILED(hr) || count != 1) return E_INVALIDARG;
    IShellItem *item = NULL; hr = IShellItemArray_GetItemAt(items, 0, &item);
    if (SUCCEEDED(hr)) { hr = IShellItem_GetDisplayName(item, SIGDN_FILESYSPATH, path); IShellItem_Release(item); }
    if (SUCCEEDED(hr) && (!*path || !ordinary_path(*path))) {
        CoTaskMemFree(*path); *path = NULL; return E_INVALIDARG;
    }
    return hr;
}
static BOOL registered_path(const WCHAR *path) {
    HKEY key = NULL;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Classes\\*\\shell\\Massdex.SyncShark", 0, KEY_QUERY_VALUE, &key)) return FALSE;
    WCHAR owner[64] = {0}; DWORD bytes = sizeof(owner), type = 0;
    BOOL ours = RegQueryValueExW(key, L"SyncSharkOwner", NULL, &type, (BYTE*)owner, &bytes) == ERROR_SUCCESS &&
        type == REG_SZ && bytes >= 2 && bytes <= sizeof(owner) && owner[ARRAYSIZE(owner)-1] == 0 && !wcscmp(owner, L"Massdex.SyncShark");
    WCHAR *applies = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 65538); bytes = 65536;
    BOOL matches = FALSE;
    if (ours && applies && RegQueryValueExW(key, L"AppliesTo", NULL, &type, (BYTE*)applies, &bytes) == ERROR_SUCCESS && type == REG_SZ && bytes % 2 == 0) {
        /* Read only exact-root clauses written by the product's existing bounded registration.
           Match descendants on a separator boundary, never text prefix alone. */
        const WCHAR marker[] = L"System.ItemPathDisplay:=\"";
        WCHAR *at = applies;
        while ((at = wcsstr(at, marker)) != NULL) {
            at += ARRAYSIZE(marker)-1; WCHAR *end = wcschr(at, L'"'); if (!end) break;
            size_t length = (size_t)(end-at), selected = wcslen(path);
            if (length >= 3 && selected >= length && CompareStringOrdinal(path, (int)length, at, (int)length, TRUE) == CSTR_EQUAL &&
                (selected == length || path[length] == L'\\')) { matches = TRUE; break; }
            at = end + 1;
        }
    }
    if (applies) HeapFree(GetProcessHeap(), 0, applies); RegCloseKey(key); return matches;
}
static HRESULT STDMETHODCALLTYPE command_query(IExplorerCommand *self, REFIID iid, void **output) {
    if (!output) return E_POINTER; *output = NULL;
    if (!IsEqualIID(iid, &IID_IUnknown) && !IsEqualIID(iid, &IID_IExplorerCommand)) return E_NOINTERFACE;
    *output = self; IExplorerCommand_AddRef(self); return S_OK;
}
static ULONG STDMETHODCALLTYPE command_add(IExplorerCommand *self) { return InterlockedIncrement(&((Command*)self)->references); }
static ULONG STDMETHODCALLTYPE command_release(IExplorerCommand *self) {
    LONG remaining = InterlockedDecrement(&((Command*)self)->references);
    if (!remaining) { HeapFree(GetProcessHeap(), 0, self); InterlockedDecrement(&live_objects); } return remaining;
}
static HRESULT STDMETHODCALLTYPE command_title(IExplorerCommand *self, IShellItemArray *items, LPWSTR *output) {
    (void)items; return copy_text(labels[((Command*)self)->index], output);
}
static HRESULT STDMETHODCALLTYPE command_icon(IExplorerCommand *self, IShellItemArray *items, LPWSTR *output) {
    (void)self; (void)items; if (!output) return E_POINTER; *output = NULL;
    WCHAR path[32768];HANDLE held; if (!executable_path(path, ARRAYSIZE(path)-2,&held)) return E_FAIL;
    CloseHandle(held);
    wcscat(path, L",0"); return copy_text(path, output);
}
static HRESULT STDMETHODCALLTYPE command_tip(IExplorerCommand *self, IShellItemArray *items, LPWSTR *output) {
    return command_title(self, items, output);
}
static HRESULT STDMETHODCALLTYPE command_name(IExplorerCommand *self, GUID *output) {
    if (!output) return E_POINTER; *output = command_class;
    output->Data4[7] ^= (unsigned char)((Command*)self)->index; return S_OK;
}
static HRESULT STDMETHODCALLTYPE command_state(IExplorerCommand *self, IShellItemArray *items, BOOL slow, EXPCMDSTATE *state) {
    (void)self; (void)slow; if (!state) return E_POINTER; *state = ECS_HIDDEN;
    LPWSTR path = NULL; if (SUCCEEDED(selected_path(items, &path))) {
        if (registered_path(path)) *state = ECS_ENABLED; CoTaskMemFree(path);
    }
    return S_OK;
}
static HRESULT STDMETHODCALLTYPE command_invoke(IExplorerCommand *self, IShellItemArray *items, IBindCtx *context) {
    (void)context; unsigned index = ((Command*)self)->index; if (!index || index >= ARRAYSIZE(verbs)) return E_NOTIMPL;
    LPWSTR path = NULL; HRESULT hr = selected_path(items, &path); if (FAILED(hr)) return hr;
    WCHAR executable[32768];
    HANDLE held;
    if (!registered_path(path) || !executable_path(executable, ARRAYSIZE(executable),&held)) { CoTaskMemFree(path); return E_ACCESSDENIED; }
    size_t length = wcslen(path) + wcslen(verbs[index]) + 32;
    WCHAR *arguments = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, length * sizeof(WCHAR));
    if (!arguments) { CloseHandle(held);CoTaskMemFree(path); return E_OUTOFMEMORY; }
    wcscpy(arguments, L"--explorer "); wcscat(arguments, verbs[index]); wcscat(arguments, L" \""); wcscat(arguments, path); wcscat(arguments, L"\"");
    SHELLEXECUTEINFOW start = {0}; start.cbSize = sizeof(start); start.fMask = SEE_MASK_FLAG_NO_UI;
    start.lpFile = executable; start.lpParameters = arguments; start.nShow = SW_SHOWNORMAL;
    BOOL launched = ShellExecuteExW(&start); hr = launched ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    CloseHandle(held);HeapFree(GetProcessHeap(), 0, arguments); CoTaskMemFree(path); return hr;
}
static HRESULT STDMETHODCALLTYPE command_flags(IExplorerCommand *self, EXPCMDFLAGS *flags) {
    if (!flags) return E_POINTER; *flags = ((Command*)self)->index ? ECF_DEFAULT : ECF_HASSUBCOMMANDS; return S_OK;
}
static HRESULT STDMETHODCALLTYPE command_children(IExplorerCommand *self, IEnumExplorerCommand **output) {
    if (!output) return E_POINTER; *output = NULL; return ((Command*)self)->index ? E_NOTIMPL : make_enumerator(1, output);
}
static const IExplorerCommandVtbl command_vtable = {command_query,command_add,command_release,command_title,command_icon,command_tip,command_name,command_state,command_invoke,command_flags,command_children};
static HRESULT make_command(unsigned index, IExplorerCommand **output) {
    if (!output) return E_POINTER; *output = NULL;
    Command *value = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(*value)); if (!value) return E_OUTOFMEMORY;
    value->iface.lpVtbl = &command_vtable; value->references = 1; value->index = index;
    InterlockedIncrement(&live_objects); *output = &value->iface; return S_OK;
}
static HRESULT STDMETHODCALLTYPE enum_query(IEnumExplorerCommand *self, REFIID iid, void **output) {
    if (!output) return E_POINTER; *output = NULL;
    if (!IsEqualIID(iid,&IID_IUnknown) && !IsEqualIID(iid,&IID_IEnumExplorerCommand)) return E_NOINTERFACE;
    *output = self; IEnumExplorerCommand_AddRef(self); return S_OK;
}
static ULONG STDMETHODCALLTYPE enum_add(IEnumExplorerCommand *self) { return InterlockedIncrement(&((Commands*)self)->references); }
static ULONG STDMETHODCALLTYPE enum_release(IEnumExplorerCommand *self) {
    LONG remaining = InterlockedDecrement(&((Commands*)self)->references);
    if (!remaining) { HeapFree(GetProcessHeap(),0,self); InterlockedDecrement(&live_objects); } return remaining;
}
static HRESULT STDMETHODCALLTYPE enum_next(IEnumExplorerCommand *self, ULONG count, IExplorerCommand **output, ULONG *fetched) {
    if (!output || (!fetched && count != 1)) return E_POINTER;
    ULONG done = 0; Commands *value = (Commands*)self; if (fetched) *fetched = 0;
    if (count > 6) count = 7; /* Never allocate caller-controlled unbounded collections. */
    for (; done < count && value->next < ARRAYSIZE(labels); ++done) {
        HRESULT hr = make_command(value->next, &output[done]); if (FAILED(hr)) { if (fetched) *fetched = done; return hr; } ++value->next;
    }
    if (fetched) *fetched = done; return done == count ? S_OK : S_FALSE;
}
static HRESULT STDMETHODCALLTYPE enum_skip(IEnumExplorerCommand *self, ULONG count) {
    Commands *value = (Commands*)self; unsigned available = (unsigned)ARRAYSIZE(labels)-value->next;
    if (count > available) { value->next = ARRAYSIZE(labels); return S_FALSE; } value->next += count; return S_OK;
}
static HRESULT STDMETHODCALLTYPE enum_reset(IEnumExplorerCommand *self) { ((Commands*)self)->next = 1; return S_OK; }
static HRESULT STDMETHODCALLTYPE enum_clone(IEnumExplorerCommand *self, IEnumExplorerCommand **output) { return make_enumerator(((Commands*)self)->next, output); }
static const IEnumExplorerCommandVtbl enum_vtable = {enum_query,enum_add,enum_release,enum_next,enum_skip,enum_reset,enum_clone};
static HRESULT make_enumerator(unsigned next, IEnumExplorerCommand **output) {
    if (!output) return E_POINTER; *output = NULL;
    Commands *value = HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(*value)); if (!value) return E_OUTOFMEMORY;
    value->iface.lpVtbl = &enum_vtable; value->references = 1; value->next = next;
    InterlockedIncrement(&live_objects); *output = &value->iface; return S_OK;
}
static HRESULT STDMETHODCALLTYPE factory_query(IClassFactory *self, REFIID iid, void **output) {
    if (!output) return E_POINTER; *output = NULL;
    if (!IsEqualIID(iid,&IID_IUnknown) && !IsEqualIID(iid,&IID_IClassFactory)) return E_NOINTERFACE;
    *output = self; IClassFactory_AddRef(self); return S_OK;
}
static ULONG STDMETHODCALLTYPE factory_add(IClassFactory *self) { return InterlockedIncrement(&((Factory*)self)->references); }
static ULONG STDMETHODCALLTYPE factory_release(IClassFactory *self) {
    LONG remaining = InterlockedDecrement(&((Factory*)self)->references);
    if (!remaining) { HeapFree(GetProcessHeap(),0,self); InterlockedDecrement(&live_objects); } return remaining;
}
static HRESULT STDMETHODCALLTYPE factory_create(IClassFactory *self, IUnknown *outer, REFIID iid, void **output) {
    (void)self; if (!output) return E_POINTER; *output = NULL; if (outer) return CLASS_E_NOAGGREGATION;
    IExplorerCommand *command = NULL; HRESULT hr = make_command(0,&command);
    if (SUCCEEDED(hr)) { hr = IExplorerCommand_QueryInterface(command,iid,output); IExplorerCommand_Release(command); } return hr;
}
static HRESULT STDMETHODCALLTYPE factory_lock(IClassFactory *self, BOOL lock) { (void)self; if (lock) InterlockedIncrement(&server_locks); else InterlockedDecrement(&server_locks); return S_OK; }
static const IClassFactoryVtbl factory_vtable = {factory_query,factory_add,factory_release,factory_create,factory_lock};
HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void **output) {
    if (!output) return E_POINTER; *output = NULL; if (!IsEqualCLSID(clsid,&command_class)) return CLASS_E_CLASSNOTAVAILABLE;
    Factory *value = HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(*value)); if (!value) return E_OUTOFMEMORY;
    value->iface.lpVtbl = &factory_vtable; value->references = 1; InterlockedIncrement(&live_objects);
    HRESULT hr = IClassFactory_QueryInterface(&value->iface,iid,output); IClassFactory_Release(&value->iface); return hr;
}
HRESULT WINAPI DllCanUnloadNow(void) { return !live_objects && !server_locks ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    (void)reserved; if (reason == DLL_PROCESS_ATTACH) { module_handle = instance; DisableThreadLibraryCalls(instance); } return TRUE;
}
