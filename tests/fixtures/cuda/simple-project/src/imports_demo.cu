#include "imports_demo.cuh"

// Mirrors the Python original's `import os` (unused) / `import sys` (used,
// for sys.argv) / `from typing import List, Dict` (List unused, Dict used
// via the bare reference `d = Dict`): <cstdlib> below is never referenced —
// the "unused #include" this demo exists to have flagged and removed — while
// <string> genuinely is referenced in show_args.
#include <cstdlib> // unused: no symbol from here is referenced below
#include <string>

namespace pythonsimple::imports_demo {

void show_args(int argc, char **argv) {
    for (int i = 0; i < argc; ++i) {
        std::string arg(argv[i]); // uses <string>, analog of `print(sys.argv)`
        (void)arg;
    }
}

} // namespace pythonsimple::imports_demo
