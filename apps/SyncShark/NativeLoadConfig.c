#include <windows.h>

// COFF's recognised load-configuration symbol is emitted directly because the
// registered Zig driver's linker-argument parser does not expose the equivalent
// /DEPENDENTLOADFLAG switch. Verify the final PE data directory, not this source.
__attribute__((used)) const IMAGE_LOAD_CONFIG_DIRECTORY64 _load_config_used = {
    .Size = sizeof(IMAGE_LOAD_CONFIG_DIRECTORY64),
    .DependentLoadFlags = LOAD_LIBRARY_SEARCH_SYSTEM32
};
