// Mirrors lib_core/_diagnostics_demo.py.
//
// That file is a deliberate sandbox of broken code for HelixStudio's Python
// diagnostics engine: an unknown module import, real-module-missing-
// attribute access, wrong-type call sites (PY6003), unreachable code, a
// non-exhaustive `match`, and a pile of unused imports. Every one of those
// categories is something a real C++ compiler already rejects outright at
// compile time (an unresolvable #include, a call to a nonexistent member,
// a type that doesn't convert) -- so a byte-for-byte translation would
// simply fail to build, defeating both this fixture's purpose *and* the
// requirement that the workspace build cleanly end to end.
//
// This stub documents that deliberate omission rather than attempting a
// lossy/broken translation, matching the precedent set by this
// workspace's Rust sibling (lib_core/src/diagnostics_demo.rs). See the
// Python source for exactly which diagnostic codes (PY6003, PY7001,
// PY7003, unused-import, unreachable-code) each snippet was meant to
// trigger in the Python analyzer.
