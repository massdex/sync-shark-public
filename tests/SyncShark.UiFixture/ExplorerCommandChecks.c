#define COBJMACROS
#define CINTERFACE
#ifndef UNICODE
#define UNICODE
#endif
#include <windows.h>
#include <shobjidl.h>
#include <shlobj.h>
#include <stdio.h>
#include <wchar.h>
static int checks;
#define CHECK(test) do { if (!(test)) { wprintf(L"FAILED line %d\n", __LINE__); return 1; } ++checks; } while(0)
typedef HRESULT (WINAPI *ClassObject)(REFCLSID, REFIID, void**);
typedef HRESULT (WINAPI *CanUnload)(void);
int wmain(int argc, wchar_t **argv) {
    CHECK(argc == 2 || argc == 3); CHECK(SUCCEEDED(CoInitializeEx(NULL, COINIT_APARTMENTTHREADED)));
    GUID clsid = {0xb2251d9a,0x7803,0x4f1f,{0x8f,0xe8,0xcb,0xda,0x28,0x4b,0xd5,0xe9}};
    if (argc == 3 && (!wcscmp(argv[1], L"--registered-selection") || !wcscmp(argv[1], L"--registered-rejection"))) {
        // Activate only our registered class, then inspect its read-only command
        // contract with a real Shell selection. Rejection mode also invokes each
        // command with the out-of-root selection, which must refuse dispatch.
        const BOOL reject = !wcscmp(argv[1], L"--registered-rejection");
        IExplorerCommand *registered = NULL;
        CHECK(SUCCEEDED(CoCreateInstance(&clsid,NULL,CLSCTX_LOCAL_SERVER,&IID_IExplorerCommand,(void**)&registered)));
        IShellItem *item = NULL; IShellItemArray *selection = NULL;
        CHECK(SUCCEEDED(SHCreateItemFromParsingName(argv[2],NULL,&IID_IShellItem,(void**)&item)));
        CHECK(SUCCEEDED(SHCreateShellItemArrayFromShellItem(item,&IID_IShellItemArray,(void**)&selection)));
        IShellItem_Release(item);
        EXPCMDSTATE state; CHECK(SUCCEEDED(IExplorerCommand_GetState(registered,selection,FALSE,&state)));
        CHECK(state == (reject ? ECS_HIDDEN : ECS_ENABLED));
        LPWSTR title = NULL; CHECK(SUCCEEDED(IExplorerCommand_GetTitle(registered,selection,&title)) && !wcscmp(title,L"Sync Shark")); CoTaskMemFree(title);
        LPWSTR icon = NULL; CHECK(SUCCEEDED(IExplorerCommand_GetIcon(registered,selection,&icon)) && icon && *icon);
        wprintf(L"Registered Explorer icon binding: %ls\n", icon); CoTaskMemFree(icon);
        IEnumExplorerCommand *children = NULL; CHECK(SUCCEEDED(IExplorerCommand_EnumSubCommands(registered,&children)));
        ULONG count = 0; IExplorerCommand *child = NULL; unsigned commands = 0;
        while (IEnumExplorerCommand_Next(children,1,&child,&count) == S_OK && count == 1) {
            CHECK(++commands <= 6);
            CHECK(SUCCEEDED(IExplorerCommand_GetState(child,selection,FALSE,&state)) && state == (reject ? ECS_HIDDEN : ECS_ENABLED));
            if (reject) CHECK(IExplorerCommand_Invoke(child,selection,NULL) == E_ACCESSDENIED);
            IExplorerCommand_Release(child);
        }
        CHECK(commands == 6); IEnumExplorerCommand_Release(children);
        IShellItemArray_Release(selection); IExplorerCommand_Release(registered); CoUninitialize();
        wprintf(L"PASS %d registered Explorer %ls checks; no accepted dispatch.\n",checks,reject?L"unsafe-selection rejection":L"selection"); return 0;
    }
    const BOOL uninstalled = argc == 3 && !wcscmp(argv[1], L"--uninstalled");
    CHECK(argc == 2 || uninstalled);
    HMODULE module = LoadLibraryExW(argv[uninstalled ? 2 : 1], NULL, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32); CHECK(module != NULL);
    ClassObject get = (ClassObject)(void*)GetProcAddress(module,"DllGetClassObject");
    CanUnload unload = (CanUnload)(void*)GetProcAddress(module,"DllCanUnloadNow"); CHECK(get && unload); CHECK(unload() == S_OK);
    IClassFactory *factory = NULL; CHECK(SUCCEEDED(get(&clsid,&IID_IClassFactory,(void**)&factory))); CHECK(unload() == S_FALSE);
    IExplorerCommand *command = NULL; CHECK(SUCCEEDED(IClassFactory_CreateInstance(factory,NULL,&IID_IExplorerCommand,(void**)&command)));
    LPWSTR title = NULL; CHECK(SUCCEEDED(IExplorerCommand_GetTitle(command,NULL,&title)) && !wcscmp(title,L"Sync Shark")); CoTaskMemFree(title);
    LPWSTR icon = NULL; HRESULT icon_result = IExplorerCommand_GetIcon(command,NULL,&icon);
    if (uninstalled) {
        // An uninstalled DLL must not bind itself to a different installed release.
        CHECK(icon_result == E_FAIL && icon == NULL);
    } else {
        CHECK(SUCCEEDED(icon_result) && icon != NULL);
        const wchar_t *icon_name = wcsrchr(icon,L'\\');
        CHECK(icon_name && !_wcsicmp(icon_name,L"\\SyncShark.exe,0")); CoTaskMemFree(icon);
    }
    EXPCMDFLAGS flags; CHECK(SUCCEEDED(IExplorerCommand_GetFlags(command,&flags)) && flags == ECF_HASSUBCOMMANDS);
    EXPCMDSTATE state; CHECK(SUCCEEDED(IExplorerCommand_GetState(command,NULL,FALSE,&state)) && state == ECS_HIDDEN);
    IEnumExplorerCommand *items = NULL; CHECK(SUCCEEDED(IExplorerCommand_EnumSubCommands(command,&items)));
    const wchar_t *expected[] = {L"Open in Sync Shark",L"Open online",L"Share and Manage Links",L"Make Available Offline",L"Make Online Only",L"Sync This Folder Pair"};
    GUID prior = clsid;
    for (int i = 0; i < 6; ++i) {
        IExplorerCommand *child = NULL; ULONG count = 0; CHECK(IEnumExplorerCommand_Next(items,1,&child,&count) == S_OK && count == 1);
        CHECK(SUCCEEDED(IExplorerCommand_GetTitle(child,NULL,&title)) && !wcscmp(title,expected[i])); CoTaskMemFree(title);
        GUID name; CHECK(SUCCEEDED(IExplorerCommand_GetCanonicalName(child,&name)) && !IsEqualGUID(&name,&prior)); prior = name;
        CHECK(IExplorerCommand_Invoke(child,NULL,NULL) == E_INVALIDARG); IExplorerCommand_Release(child);
    }
    IExplorerCommand *child = NULL; ULONG count = 10; CHECK(IEnumExplorerCommand_Next(items,1,&child,&count) == S_FALSE && count == 0);
    CHECK(SUCCEEDED(IEnumExplorerCommand_Reset(items))); IEnumExplorerCommand *copy = NULL; CHECK(SUCCEEDED(IEnumExplorerCommand_Clone(items,&copy)));
    CHECK(IEnumExplorerCommand_Skip(copy,100) == S_FALSE); IEnumExplorerCommand_Release(copy); IEnumExplorerCommand_Release(items);
    IExplorerCommand_Release(command); IClassFactory_Release(factory); CHECK(unload() == S_OK);
    factory = NULL; CHECK(get(&IID_IUnknown,&IID_IClassFactory,(void**)&factory) == CLASS_E_CLASSNOTAVAILABLE && factory == NULL);
    FreeLibrary(module); CoUninitialize(); wprintf(L"PASS %d real native Explorer COM checks; no registration or actions executed.\n",checks); return 0;
}
