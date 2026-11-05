// Mirrors lib_utils/_diagnostics_demo.py.
//
// Same rationale as lib_core/src/diagnostics_demo.cu: that file is a
// deliberate sandbox of broken code (generic mis-parameterization,
// swapped Pair arguments, wrong-shaped iterator-helper inputs, wrong
// TypeVar counts) meant to trigger the Python analyzer's PY6003
// diagnostics. A real C++ compiler already rejects every one of those
// categories at compile time, so reproducing them verbatim would break
// the build. Left as a documentation-only stub, matching the Rust
// sibling's lib_utils/src/diagnostics_demo.rs. See the Python source for
// exactly which diagnostic each snippet was meant to trigger.
