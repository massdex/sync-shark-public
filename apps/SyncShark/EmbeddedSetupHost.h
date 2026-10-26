#ifndef SYNC_SHARK_EMBEDDED_SETUP_HOST_H
#define SYNC_SHARK_EMBEDDED_SETUP_HOST_H
#include <windows.h>
// Called only by authenticated native entry points with their compiled invocation.
// No command line, file or registry value supplies executable script text.
DWORD ssd_run_embedded_setup(const WCHAR *app,const char *invocation,DWORD timeout,BOOL terminate_on_timeout);
#endif
